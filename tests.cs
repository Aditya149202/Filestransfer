using Moq;
using OrderService.Clients;
using OrderService.Entities;
using OrderService.Enums;
using OrderService.ExceptionMiddleware;
using OrderService.Repositories.Interfaces;
using OrderServiceUnderTest = OrderService.Services.OrderService;

namespace OrderService.Tests.Services;

[TestFixture]
public class OrderServiceTests
{
    private Mock<IOrderRepository> _orderRepo;
    private Mock<ISupplierInventoryClient> _supplierInventoryClient;
    private OrderServiceUnderTest _sut;

    [SetUp]
    public void Setup()
    {
        _orderRepo = new Mock<IOrderRepository>();
        _supplierInventoryClient = new Mock<ISupplierInventoryClient>();
        _sut = new OrderServiceUnderTest(_orderRepo.Object, _supplierInventoryClient.Object);
    }

    private static Order MakeOrder(int id, OrderStatus status, int doctorId = 1) => new()
    {
        Id = id,
        DoctorId = doctorId,
        DoctorNameSnapshot = "Dr. Test",
        Status = status.ToString(),
        TotalAmount = 100m,
        PaymentIntentId = 50,
        CreatedAt = DateTime.UtcNow,
        OrderItems = new List<OrderItem>()
    };

    [Test]
    public async Task VerifyOrderAsync_Should_Verify_When_Status_Is_New()
    {
        var order = MakeOrder(1, OrderStatus.NEW);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        var result = await _sut.VerifyOrderAsync(1);

        Assert.That(result.Status, Is.EqualTo(OrderStatus.VERIFIED));
        Assert.That(order.VerifiedAt, Is.Not.Null);
        _orderRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    [Test]
    public void VerifyOrderAsync_Should_Throw_InvalidOrderStateException_When_Not_New()
    {
        var order = MakeOrder(1, OrderStatus.VERIFIED);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        Assert.ThrowsAsync<InvalidOrderStateException>(() => _sut.VerifyOrderAsync(1));
    }

    // This is the exact regression test for the bug you fixed — before the fix, this
    // threw AppValidationException (400) instead of InvalidOrderStateException (409).
    [Test]
    public void PickupOrderAsync_On_NotVerified_Order_Should_Throw_InvalidOrderStateException_Not_AppValidationException()
    {
        var order = MakeOrder(1, OrderStatus.NEW);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        Assert.ThrowsAsync<InvalidOrderStateException>(() => _sut.PickupOrderAsync(1));
        _supplierInventoryClient.Verify(
            c => c.CommitSaleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()), Times.Never);
    }

    [Test]
    public async Task PickupOrderAsync_Should_Complete_And_Call_CommitSale_When_Verified()
    {
        var order = MakeOrder(1, OrderStatus.VERIFIED);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        var result = await _sut.PickupOrderAsync(1);

        Assert.That(result.Status, Is.EqualTo(OrderStatus.COMPLETED));
        _supplierInventoryClient.Verify(c => c.CommitSaleAsync(1, order.PaymentIntentId, order.TotalAmount), Times.Once);
        _orderRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    [Test]
    public async Task PickupOrderAsync_When_CommitSaleAsync_Throws_Should_Not_Change_Order_Status()
    {
        // Confirms the "stays VERIFIED, safe to retry" guarantee described in the code comment.
        var order = MakeOrder(1, OrderStatus.VERIFIED);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);
        _supplierInventoryClient
            .Setup(c => c.CommitSaleAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>()))
            .ThrowsAsync(new HttpRequestException("downstream failure"));

        Assert.ThrowsAsync<HttpRequestException>(() => _sut.PickupOrderAsync(1));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.VERIFIED.ToString()));
        _orderRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
        await Task.CompletedTask;
    }

    [Test]
    public void CancelOrderAsync_Doctor_Should_Throw_When_Order_Already_Verified()
    {
        // Doctors can only cancel NEW orders — VERIFIED is Admin-only to cancel.
        var order = MakeOrder(1, OrderStatus.VERIFIED, doctorId: 7);

        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        Assert.ThrowsAsync<InvalidOrderStateException>(
            () => _sut.CancelOrderAsync(1, CancelledBy.DOCTOR, requestingDoctorId: 7));
    }

    [Test]
    public async Task CancelOrderAsync_Admin_Should_Cancel_Verified_Order_And_Release_Reservation()
    {
        var order = MakeOrder(1, OrderStatus.VERIFIED);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        var result = await _sut.CancelOrderAsync(1, CancelledBy.ADMIN, requestingDoctorId: null);

        Assert.That(result.Status, Is.EqualTo(OrderStatus.CANCELLED));
        _supplierInventoryClient.Verify(c => c.ReleaseReservationAsync(order.PaymentIntentId), Times.Once);
    }

    [Test]
    public void GetOrderByIdAsync_Should_Throw_NotFound_When_Doctor_Does_Not_Own_Order()
    {
        // Anti-enumeration: a mismatched owner gets the SAME exception as a missing id.
        var order = MakeOrder(1, OrderStatus.NEW, doctorId: 7);
        _orderRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(order);

        Assert.ThrowsAsync<NotFoundException>(() => _sut.GetOrderByIdAsync(1, requestingDoctorId: 99));
    }
}




using Moq;
using OrderService.Clients;
using OrderService.DTOs;
using OrderService.Entities;
using OrderService.Enums;
using OrderService.ExceptionMiddleware;
using OrderService.Repositories.Interfaces;
using OrderService.Services;

namespace OrderService.Tests.Services;

[TestFixture]
public class PaymentServiceTests
{
    private Mock<IPaymentIntentRepository> _paymentIntentRepo;
    private Mock<IOrderRepository> _orderRepo;
    private Mock<ISupplierInventoryClient> _supplierInventoryClient;
    private Mock<IPaymentGatewayClient> _paymentGatewayClient;
    private PaymentService _sut;

    [SetUp]
    public void Setup()
    {
        _paymentIntentRepo = new Mock<IPaymentIntentRepository>();
        _orderRepo = new Mock<IOrderRepository>();
        _supplierInventoryClient = new Mock<ISupplierInventoryClient>();
        _paymentGatewayClient = new Mock<IPaymentGatewayClient>();

        _sut = new PaymentService(
            _paymentIntentRepo.Object,
            _orderRepo.Object,
            _supplierInventoryClient.Object,
            _paymentGatewayClient.Object);
    }

    [Test]
    public void InitiatePaymentAsync_Should_Throw_AppValidationException_When_No_Items()
    {
        var request = new PaymentInitiateRequest(new List<PaymentInitiateRequestItem>());

        Assert.ThrowsAsync<AppValidationException>(
            () => _sut.InitiatePaymentAsync(doctorId: 1, doctorName: "Dr. Test", request));
    }

    // This is the actual point of the abstraction: PaymentService never knows or cares
    // whether IPaymentGatewayClient is the Mock or the real Razorpay implementation —
    // it only calls the interface. This test proves that boundary holds.
    [Test]
    public async Task InitiatePaymentAsync_Should_Only_Depend_On_IPaymentGatewayClient_Abstraction()
    {
        var request = new PaymentInitiateRequest(new List<PaymentInitiateRequestItem> { new(DrugId: 1, Quantity: 2) });

        _supplierInventoryClient
            .Setup(c => c.ReserveStockAsync(It.IsAny<int>(), It.IsAny<List<(int DrugId, int Quantity)>>()))
            .ReturnsAsync(new List<(int, string, decimal)> { (1, "Paracetamol", 10m) });

        _paymentGatewayClient
            .Setup(g => g.CreateOrderAsync(It.IsAny<decimal>(), "INR", It.IsAny<string>()))
            .ReturnsAsync(new PaymentGatewayOrder("gw_order_123", "gw_key_abc"));

        var result = await _sut.InitiatePaymentAsync(doctorId: 1, doctorName: "Dr. Test", request);

        Assert.That(result.RazorpayOrderId, Is.EqualTo("gw_order_123"));
        Assert.That(result.RazorpayKeyId, Is.EqualTo("gw_key_abc"));
        Assert.That(result.Amount, Is.EqualTo(20m)); // 2 * 10
        _paymentGatewayClient.Verify(g => g.CreateOrderAsync(20m, "INR", It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void ConfirmPaymentAsync_Should_Throw_InvalidPaymentSignatureException_When_Signature_Invalid()
    {
        _paymentGatewayClient
            .Setup(g => g.VerifySignature(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var request = new PaymentConfirmRequest("gw_order_123", "gw_pay_456", "bad_signature");

        Assert.ThrowsAsync<InvalidPaymentSignatureException>(
            () => _sut.ConfirmPaymentAsync(doctorId: 1, doctorName: "Dr. Test", request));

        // Must fail BEFORE any lookup happens — signature check is the first gate.
        _paymentIntentRepo.Verify(r => r.GetByRazorpayOrderIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ConfirmPaymentAsync_Should_Be_Idempotent_When_Already_Paid()
    {
        _paymentGatewayClient
            .Setup(g => g.VerifySignature(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var existingOrder = new Order { Id = 99 };
        var paymentIntent = new PaymentIntent
        {
            Id = 5,
            DoctorId = 1,
            RazorpayOrderId = "gw_order_123",
            Status = PaymentIntentStatus.PAID.ToString(), // already processed
            ItemsSnapshot = "[]",
            Order = existingOrder
        };
        _paymentIntentRepo.Setup(r => r.GetByRazorpayOrderIdAsync("gw_order_123")).ReturnsAsync(paymentIntent);

        var request = new PaymentConfirmRequest("gw_order_123", "gw_pay_456", "any_signature");
        var result = await _sut.ConfirmPaymentAsync(doctorId: 1, doctorName: "Dr. Test", request);

        Assert.That(result.Status, Is.EqualTo(PaymentIntentStatus.PAID));
        Assert.That(result.OrderId, Is.EqualTo(99));
        // Must NOT create a second order on a retried confirm.
        _orderRepo.Verify(r => r.AddAsync(It.IsAny<Order>()), Times.Never);
    }

    [Test]
    public async Task ConfirmPaymentAsync_Should_Create_Order_When_Valid_And_Not_Yet_Processed()
    {
        _paymentGatewayClient
            .Setup(g => g.VerifySignature(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var paymentIntent = new PaymentIntent
        {
            Id = 5,
            DoctorId = 1,
            RazorpayOrderId = "gw_order_123",
            Status = PaymentIntentStatus.CREATED.ToString(),
            Amount = 20m,
            ItemsSnapshot = "[{\"DrugId\":1,\"DrugName\":\"Paracetamol\",\"Quantity\":2,\"UnitPrice\":10}]"
        };
        _paymentIntentRepo.Setup(r => r.GetByRazorpayOrderIdAsync("gw_order_123")).ReturnsAsync(paymentIntent);

        var request = new PaymentConfirmRequest("gw_order_123", "gw_pay_456", "valid_signature");
        var result = await _sut.ConfirmPaymentAsync(doctorId: 1, doctorName: "Dr. Test", request);

        Assert.That(result.Status, Is.EqualTo(PaymentIntentStatus.PAID));
        _orderRepo.Verify(r => r.AddAsync(It.Is<Order>(o => o.DoctorId == 1 && o.TotalAmount == 20m)), Times.Once);
        _orderRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }
}





using Moq;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Enums;
using SupplierInventoryService.ExceptionMiddleware;
using SupplierInventoryService.Repositories.Interfaces;
using SupplierInventoryService.Services;

namespace SupplierInventoryService.Tests.Services;

[TestFixture]
public class InternalServiceTests
{
    private Mock<IStockReservationRepository> _reservationRepo;
    private Mock<ISalesRepository> _salesRepo;
    private Mock<IProcessedEventRepository> _processedEventRepo;
    private Mock<IDrugRepository> _drugRepo;
    private InternalService _sut;

    [SetUp]
    public void Setup()
    {
        _reservationRepo = new Mock<IStockReservationRepository>();
        _salesRepo = new Mock<ISalesRepository>();
        _processedEventRepo = new Mock<IProcessedEventRepository>();
        _drugRepo = new Mock<IDrugRepository>();

        _sut = new InternalService(
            _reservationRepo.Object, _salesRepo.Object, _processedEventRepo.Object, _drugRepo.Object);
    }

    // The core idempotency test: a retried commit for an already-processed order must be
    // a silent no-op — not a duplicate Sale, not a duplicate ProcessedEvent insert (which
    // is exactly what threw the identity-column error before the ValueGeneratedNever fix).
    [Test]
    public async Task CommitSaleAsync_Should_ReturnEarly_When_OrderId_Already_Processed()
    {
        _processedEventRepo.Setup(r => r.ExistsAsync(42)).ReturnsAsync(true);

        await _sut.CommitSaleAsync(orderId: 42, paymentIntentId: 5, amount: 100m);

        _reservationRepo.Verify(r => r.GetActiveByPaymentIntentIdAsync(It.IsAny<int>()), Times.Never);
        _salesRepo.Verify(r => r.AddAsync(It.IsAny<Sale>()), Times.Never);
        _processedEventRepo.Verify(r => r.AddAsync(It.IsAny<ProcessedEvent>()), Times.Never);
    }

    [Test]
    public void CommitSaleAsync_Should_Throw_NotFound_When_No_Active_Reservations()
    {
        _processedEventRepo.Setup(r => r.ExistsAsync(42)).ReturnsAsync(false);
        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(new List<StockReservation>());

        Assert.ThrowsAsync<NotFoundException>(
            () => _sut.CommitSaleAsync(orderId: 42, paymentIntentId: 5, amount: 100m));
    }

    [Test]
    public async Task CommitSaleAsync_Should_Commit_Reservations_Create_Sale_And_ProcessedEvent_When_Valid()
    {
        _processedEventRepo.Setup(r => r.ExistsAsync(42)).ReturnsAsync(false);

        var reservations = new List<StockReservation>
        {
            new() { Id = 1, PaymentIntentId = 5, DrugId = 10, Quantity = 2, Status = StockReservationStatus.ACTIVE.ToString() }
        };
        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(reservations);

        await _sut.CommitSaleAsync(orderId: 42, paymentIntentId: 5, amount: 100m);

        Assert.That(reservations[0].Status, Is.EqualTo(StockReservationStatus.COMMITTED.ToString()));
        _salesRepo.Verify(r => r.AddAsync(It.Is<Sale>(s => s.OrderId == 42 && s.Amount == 100m)), Times.Once);
        _processedEventRepo.Verify(r => r.AddAsync(It.Is<ProcessedEvent>(pe => pe.OrderId == 42)), Times.Once);
        // Single shared-context save, per the code's own comment — not once per repo.
        _processedEventRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
        _salesRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }
}







