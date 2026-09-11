using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Razorpay.Api;
using OrderService.Clients;
using OrderService.Entities;
using OrderService.Enums;
using OrderService.ExceptionMiddleware;
using OrderService.Repositories.Interfaces;

namespace OrderService.Services;

public class PaymentService : IPaymentService
{
    private readonly IPaymentIntentRepository _paymentIntentRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly ISupplierInventoryClient _supplierInventoryClient;
    private readonly IConfiguration _config;

    public PaymentService(
        IPaymentIntentRepository paymentIntentRepository,
        IOrderRepository orderRepository,
        ISupplierInventoryClient supplierInventoryClient,
        IConfiguration config)
    {
        _paymentIntentRepository = paymentIntentRepository;
        _orderRepository = orderRepository;
        _supplierInventoryClient = supplierInventoryClient;
        _config = config;
    }

    public async Task<PaymentInitiateResponse> InitiatePaymentAsync(
        int doctorId, string doctorName, PaymentInitiateRequest request)
    {
        if (request.Items.Count == 0)
            throw new AppValidationException(new Dictionary<string, string[]>
                { ["items"] = new[] { "Order must contain at least one item." } });

        // SAVE #1 — deliberate exception to the one-save-per-unit-of-work rule (see earlier
        // discussion): a real DB-generated Id is needed before SupplierInventoryService can be
        // called, before the real Razorpay order/amount are known. RazorpayOrderId here is a
        // throwaway GUID purely to satisfy NOT NULL + unique, overwritten below.
        var paymentIntent = new PaymentIntent
        {
            DoctorId = doctorId,
            DoctorNameSnapshot = doctorName,
            RazorpayOrderId = Guid.NewGuid().ToString(),
            Amount = 0,
            Status = PaymentIntentStatus.CREATED.ToString(),
            ItemsSnapshot = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _paymentIntentRepository.AddAsync(paymentIntent);
        await _paymentIntentRepository.SaveChangesAsync();

        List<(int DrugId, string DrugName, decimal UnitPrice)> reserved;
        try
        {
            var items = request.Items.Select(i => (i.DrugId, i.Quantity)).ToList();
            reserved = await _supplierInventoryClient.ReserveStockAsync(paymentIntent.Id, items);
        }
        catch
        {
            paymentIntent.Status = PaymentIntentStatus.FAILED.ToString();
            await _paymentIntentRepository.SaveChangesAsync();
            throw;
        }

        var quantityByDrugId = request.Items.ToDictionary(i => i.DrugId, i => i.Quantity);
        var snapshotItems = reserved.Select(r => new
        {
            r.DrugId,
            r.DrugName,
            Quantity = quantityByDrugId[r.DrugId],
            UnitPrice = r.UnitPrice
        }).ToList();

        var totalAmount = snapshotItems.Sum(i => i.Quantity * i.UnitPrice);

        var client = new RazorpayClient(_config["Razorpay:KeyId"], _config["Razorpay:KeySecret"]);
        var options = new Dictionary<string, object>
        {
            { "amount", (int)(totalAmount * 100) }, // paise, not rupees
            { "currency", "INR" },
            { "receipt", paymentIntent.Id.ToString() }
        };
        Order razorpayOrder = client.Order.Create(options);

        // SAVE #2 — real Razorpay order id, final amount, and server-side-priced item
        // snapshot are all known now.
        paymentIntent.RazorpayOrderId = razorpayOrder["id"].ToString();
        paymentIntent.Amount = totalAmount;
        paymentIntent.ItemsSnapshot = JsonSerializer.Serialize(snapshotItems);
        paymentIntent.UpdatedAt = DateTime.UtcNow;
        await _paymentIntentRepository.SaveChangesAsync();

        return new PaymentInitiateResponse(
            paymentIntent.Id, paymentIntent.RazorpayOrderId, totalAmount, "INR", _config["Razorpay:KeyId"]!);
    }

    public async Task HandleWebhookAsync(string rawBody, string signature)
    {
        if (!VerifySignature(rawBody, signature))
            throw new InvalidPaymentSignatureException();

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        var eventType = root.GetProperty("event").GetString();
        var paymentEntity = root.GetProperty("payload").GetProperty("payment").GetProperty("entity");
        var razorpayOrderId = paymentEntity.GetProperty("order_id").GetString()!;
        var razorpayPaymentId = paymentEntity.GetProperty("id").GetString()!;

        var paymentIntent = await _paymentIntentRepository.GetByRazorpayOrderIdAsync(razorpayOrderId)
            ?? throw new NotFoundException($"PaymentIntent for Razorpay order {razorpayOrderId} not found.");

        // Idempotency — Razorpay retries webhook delivery.
        if (paymentIntent.Status != PaymentIntentStatus.CREATED.ToString())
            return;

        if (eventType == "payment.captured")
        {
            paymentIntent.Status = PaymentIntentStatus.PAID.ToString();
            paymentIntent.RazorpayPaymentId = razorpayPaymentId;
            paymentIntent.UpdatedAt = DateTime.UtcNow;

            var snapshotItems = JsonSerializer.Deserialize<List<SnapshotItem>>(paymentIntent.ItemsSnapshot)!;

            var order = new Order
            {
                PaymentIntentId = paymentIntent.Id,
                DoctorId = paymentIntent.DoctorId,
                DoctorNameSnapshot = paymentIntent.DoctorNameSnapshot,
                Status = OrderStatus.NEW.ToString(),
                TotalAmount = paymentIntent.Amount,
                CreatedAt = DateTime.UtcNow,
                OrderItems = snapshotItems.Select(i => new OrderItem
                {
                    DrugId = i.DrugId,
                    Quantity = i.Quantity,
                    UnitPriceAtOrder = i.UnitPrice
                }).ToList()
            };

            await _orderRepository.AddAsync(order);
            await _orderRepository.SaveChangesAsync();
        }
        else if (eventType == "payment.failed")
        {
            paymentIntent.Status = PaymentIntentStatus.FAILED.ToString();
            paymentIntent.UpdatedAt = DateTime.UtcNow;
            await _paymentIntentRepository.SaveChangesAsync();
        }
    }

    public async Task<PaymentStatusResponse> GetPaymentStatusAsync(int paymentIntentId, int? requestingDoctorId)
    {
        var paymentIntent = await _paymentIntentRepository.GetByIdAsync(paymentIntentId)
            ?? throw new NotFoundException($"PaymentIntent {paymentIntentId} not found.");

        if (requestingDoctorId.HasValue && paymentIntent.DoctorId != requestingDoctorId.Value)
            throw new NotFoundException($"PaymentIntent {paymentIntentId} not found.");

        return new PaymentStatusResponse(
            paymentIntent.Id,
            Enum.Parse<PaymentIntentStatus>(paymentIntent.Status, ignoreCase: true),
            paymentIntent.Order?.Id);
    }

    private bool VerifySignature(string rawBody, string signature)
    {
        var secret = _config["Razorpay:WebhookSecret"]!;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
        var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedSignature), Encoding.UTF8.GetBytes(signature));
    }

    private record SnapshotItem(int DrugId, string DrugName, int Quantity, decimal UnitPrice);
}


namespace OrderService.Services;

public interface IPaymentService
{
    Task<PaymentInitiateResponse> InitiatePaymentAsync(int doctorId, string doctorName, PaymentInitiateRequest request);
    Task HandleWebhookAsync(string rawBody, string signature);
    Task<PaymentStatusResponse> GetPaymentStatusAsync(int paymentIntentId, int? requestingDoctorId);
}
