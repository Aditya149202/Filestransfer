namespace OrderService.Clients;

public interface ISupplierInventoryClient
{
    // Replaces the old RabbitMQ OrderPickedUp event. Called synchronously from
    // OrderService.PickupOrderAsync. Must be idempotent on the receiving side —
    // reuse the already-locked ProcessedEvents(order_id PK) mechanism there,
    // just triggered by this REST call instead of a queued event.
    Task CommitSaleAsync(int orderId, int paymentIntentId, decimal totalAmount);

    // Called from CancelOrderAsync for NEW/VERIFIED cancels. Restores stock by
    // releasing the ACTIVE StockReservation tied to this PaymentIntent.
    Task ReleaseReservationAsync(int paymentIntentId);
}


using System.Net.Http.Json;
using OrderService.Exceptions;

namespace OrderService.Clients;

public class SupplierInventoryClient : ISupplierInventoryClient
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SupplierInventoryClient(HttpClient httpClient, IHttpContextAccessor httpContextAccessor)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
    }

    // Forwards the caller's own bearer token so SupplierInventoryService's
    // [Authorize(Roles="ADMIN")] on the internal endpoint validates normally.
    // See flag above — this is a real decision, not an oversight.
    private void ForwardAuthHeader()
    {
        var authHeader = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            _httpClient.DefaultRequestHeaders.Add("Authorization", authHeader);
        }
    }

    public async Task CommitSaleAsync(int orderId, int paymentIntentId, decimal totalAmount)
    {
        ForwardAuthHeader();

        var payload = new { OrderId = orderId, PaymentIntentId = paymentIntentId, Amount = totalAmount };
        var response = await _httpClient.PostAsJsonAsync("/internal/sales/commit", payload);

        if (response.IsSuccessStatusCode) return;

        switch ((int)response.StatusCode)
        {
            case 404:
                throw new NotFoundException($"StockReservation for PaymentIntent {paymentIntentId} not found.");
            case 409:
                throw new StockUnavailableException($"Could not commit sale for order {orderId} — reservation conflict.");
            case 400:
                throw new AppValidationException($"Invalid sale-commit request for order {orderId}.");
            default:
                // Genuinely unexpected (5xx, network-level) — do not swallow. Let the pickup
                // request fail loudly rather than silently marking an order COMPLETED
                // when the Sale record was never actually created.
                response.EnsureSuccessStatusCode();
                break;
        }
    }

    public async Task ReleaseReservationAsync(int paymentIntentId)
    {
        ForwardAuthHeader();

        var response = await _httpClient.PostAsync($"/internal/reservations/{paymentIntentId}/release", null);

        if (response.IsSuccessStatusCode) return;

        if ((int)response.StatusCode == 404)
        {
            // Reservation already released or never existed — treat as a no-op rather than
            // blocking the cancel. A cancel should always be able to complete on the OrderService
            // side even if inventory-side state is already consistent.
            return;
        }

        response.EnsureSuccessStatusCode();
    }
}

using OrderService.DTOs;
using OrderService.Enums;

namespace OrderService.Services;

public interface IOrderService
{
    Task<PagedResponse<OrderListItemResponse>> GetOrdersAsync(OrderFilterRequest request);
    Task<OrderResponse> GetOrderByIdAsync(int id, int? requestingDoctorId);
    Task<OrderResponse> VerifyOrderAsync(int id);
    Task<OrderResponse> PickupOrderAsync(int id);
    Task<OrderResponse> CancelOrderAsync(int id, CancelledBy cancelledBy);
}




using OrderService.Clients;
using OrderService.DTOs;
using OrderService.Entities;
using OrderService.Enums;
using OrderService.Exceptions;
using OrderService.Repositories.Filters;
using OrderService.Repositories.Interfaces;

namespace OrderService.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _orderRepository;
    private readonly ISupplierInventoryClient _supplierInventoryClient;

    public OrderService(IOrderRepository orderRepository, ISupplierInventoryClient supplierInventoryClient)
    {
        _orderRepository = orderRepository;
        _supplierInventoryClient = supplierInventoryClient;
    }

    public async Task<PagedResponse<OrderListItemResponse>> GetOrdersAsync(OrderFilterRequest request)
    {
        var filter = new OrderFilter
        {
            DoctorId = request.DoctorId,
            Status = request.Status,
            DateFrom = request.DateFrom,
            DateTo = request.DateTo,
            DrugId = request.DrugId
        };

        var (items, totalCount) = await _orderRepository.GetFilteredAsync(filter, request.Page, request.Size);
        var totalPages = (int)Math.Ceiling(totalCount / (double)request.Size);

        return new PagedResponse<OrderListItemResponse>(
            items.Select(ToListItemResponse).ToList(),
            request.Page,
            request.Size,
            totalCount,
            totalPages);
    }

    // requestingDoctorId is null for Admin callers (no ownership check), and set to the
    // caller's own id for Doctor callers. Mismatched ownership throws the SAME NotFoundException
    // as a genuinely missing id — anti-enumeration, same pattern already locked for inactive drugs.
    public async Task<OrderResponse> GetOrderByIdAsync(int id, int? requestingDoctorId)
    {
        var order = await _orderRepository.GetByIdAsync(id)
            ?? throw new NotFoundException($"Order {id} not found.");

        if (requestingDoctorId.HasValue && order.DoctorId != requestingDoctorId.Value)
            throw new NotFoundException($"Order {id} not found.");

        return ToOrderResponse(order);
    }

    public async Task<OrderResponse> VerifyOrderAsync(int id)
    {
        var order = await _orderRepository.GetByIdAsync(id)
            ?? throw new NotFoundException($"Order {id} not found.");

        if (order.Status != OrderStatus.NEW)
            throw new AppValidationException(
                $"Order {id} cannot be verified from status {order.Status}. Only NEW orders can be verified.");

        order.Status = OrderStatus.VERIFIED;
        order.VerifiedAt = DateTime.UtcNow;
        await _orderRepository.SaveChangesAsync();

        return ToOrderResponse(order);
    }

    // Collapsed pickup+complete per the synchronous-architecture decision. If CommitSaleAsync
    // throws, the status mutation below never runs — order stays VERIFIED, safe to retry.
    // If CommitSaleAsync succeeds but SaveChangesAsync then fails, the Sale record already
    // exists on the inventory side and a retry will call CommitSaleAsync again — this is
    // exactly why the ProcessedEvents idempotency table still matters even without RabbitMQ.
    public async Task<OrderResponse> PickupOrderAsync(int id)
    {
        var order = await _orderRepository.GetByIdAsync(id)
            ?? throw new NotFoundException($"Order {id} not found.");

        if (order.Status != OrderStatus.VERIFIED)
            throw new AppValidationException(
                $"Order {id} cannot be marked picked up from status {order.Status}. Only VERIFIED orders can be picked up.");

        await _supplierInventoryClient.CommitSaleAsync(order.Id, order.PaymentIntentId, order.TotalAmount);

        order.Status = OrderStatus.COMPLETED;
        order.CompletedAt = DateTime.UtcNow;
        await _orderRepository.SaveChangesAsync();

        return ToOrderResponse(order);
    }

    // Stock-restore only. Does NOT reverse the Razorpay charge — see flag above.
    public async Task<OrderResponse> CancelOrderAsync(int id, CancelledBy cancelledBy)
    {
        var order = await _orderRepository.GetByIdAsync(id)
            ?? throw new NotFoundException($"Order {id} not found.");

        if (order.Status != OrderStatus.NEW && order.Status != OrderStatus.VERIFIED)
            throw new AppValidationException(
                $"Order {id} cannot be cancelled from status {order.Status}. Only NEW or VERIFIED orders can be cancelled.");

        await _supplierInventoryClient.ReleaseReservationAsync(order.PaymentIntentId);

        order.Status = OrderStatus.CANCELLED;
        order.CancelledAt = DateTime.UtcNow;
        order.CancelledBy = cancelledBy;
        await _orderRepository.SaveChangesAsync();

        return ToOrderResponse(order);
    }

    private static OrderResponse ToOrderResponse(Order o) => new(
        o.Id, o.DoctorId, o.DoctorNameSnapshot, o.Status, o.TotalAmount, o.CreatedAt,
        o.VerifiedAt, o.CompletedAt, o.CancelledAt, o.CancelledBy,
        o.Items.Select(i => new OrderItemResponse(i.DrugId, i.Quantity, i.UnitPriceAtOrder)).ToList());

    private static OrderListItemResponse ToListItemResponse(Order o) => new(
        o.Id, o.DoctorId, o.DoctorNameSnapshot, o.Status, o.TotalAmount, o.CreatedAt);
}



using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderService.DTOs;
using OrderService.Enums;
using OrderService.Services;

namespace OrderService.Controllers;

[ApiController]
[Route("orders")]
[Authorize] // default: any authenticated role, same split pattern as DrugsController
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    // Doctor callers are force-scoped to their own DoctorId regardless of what's in the
    // query string — prevents a Doctor from viewing another doctor's orders by editing
    // ?doctorId=. Admin callers get whatever DoctorId filter they passed (or none).
    [HttpGet]
    public async Task<ActionResult<PagedResponse<OrderListItemResponse>>> GetOrders([FromQuery] OrderFilterRequest request)
    {
        if (!User.IsInRole("ADMIN"))
            request = request with { DoctorId = GetCallerId() };

        return Ok(await _orderService.GetOrdersAsync(request));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<OrderResponse>> GetOrderById(int id)
    {
        var requestingDoctorId = User.IsInRole("ADMIN") ? (int?)null : GetCallerId();
        return Ok(await _orderService.GetOrderByIdAsync(id, requestingDoctorId));
    }

    [HttpPut("{id}/verify")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult<OrderResponse>> VerifyOrder(int id)
        => Ok(await _orderService.VerifyOrderAsync(id));

    [HttpPut("{id}/pickup")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult<OrderResponse>> PickupOrder(int id)
        => Ok(await _orderService.PickupOrderAsync(id));

    [HttpPut("{id}/cancel")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult<OrderResponse>> CancelOrder(int id)
        => Ok(await _orderService.CancelOrderAsync(id, CancelledBy.ADMIN));

    // NOTE: assumes the "sub" claim maps to ClaimTypes.NameIdentifier, matching the
    // ASP.NET Core default JWT claim-type mapping. Verify this against whatever
    // TokenService.GenerateAccessToken actually issues — if that mapping was cleared
    // anywhere in AddJwtBearer setup, this throws instead of resolving the caller.
    private int GetCallerId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}



using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SupplierInventoryService.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "InternalApiKey";
}

// Separate scheme from the existing JWT bearer scheme — this one authenticates
// OrderService itself, not a human. No role claim, since "is this OrderService"
// is a yes/no question, not a role-based one.
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string HeaderName = "X-Internal-Api-Key";
    private readonly IConfiguration _config;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration config)
        : base(options, logger, encoder)
    {
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var providedKey))
            return Task.FromResult(AuthenticateResult.Fail($"Missing {HeaderName} header."));

        var expectedKey = _config["InternalApi:Key"];
        if (string.IsNullOrEmpty(expectedKey) || providedKey != expectedKey)
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal API key."));

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "OrderService") }, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}



[ApiController]
[Route("internal")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public class InternalController : ControllerBase
{
    [HttpPost("sales/commit")]
    public async Task<IActionResult> CommitSale([FromBody] CommitSaleRequest request)
    {
        // TODO: ProcessedEvents idempotency check (order_id PK), commit StockReservation,
        // insert Sale row. Not yet built — separate piece.
        throw new NotImplementedException();
    }

    [HttpPost("reservations/{paymentIntentId}/release")]
    public async Task<IActionResult> ReleaseReservation(int paymentIntentId)
    {
        // TODO: find ACTIVE StockReservation by paymentIntentId, restore Drugs.QuantityInStock, mark RELEASED.
        throw new NotImplementedException();
    }
}

using System.Net.Http.Json;
using OrderService.Exceptions;

namespace OrderService.Clients;

public class SupplierInventoryClient : ISupplierInventoryClient
{
    private readonly HttpClient _httpClient;

    public SupplierInventoryClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task CommitSaleAsync(int orderId, int paymentIntentId, decimal totalAmount)
    {
        var payload = new { OrderId = orderId, PaymentIntentId = paymentIntentId, Amount = totalAmount };
        var response = await _httpClient.PostAsJsonAsync("/internal/sales/commit", payload);

        if (response.IsSuccessStatusCode) return;

        switch ((int)response.StatusCode)
        {
            case 404: throw new NotFoundException($"StockReservation for PaymentIntent {paymentIntentId} not found.");
            case 409: throw new StockUnavailableException($"Could not commit sale for order {orderId} — reservation conflict.");
            case 400: throw new AppValidationException($"Invalid sale-commit request for order {orderId}.");
            default: response.EnsureSuccessStatusCode(); break;
        }
    }

    public async Task ReleaseReservationAsync(int paymentIntentId)
    {
        var response = await _httpClient.PostAsync($"/internal/reservations/{paymentIntentId}/release", null);

        if (response.IsSuccessStatusCode) return;
        if ((int)response.StatusCode == 404) return; // already released — treat as no-op

        response.EnsureSuccessStatusCode();
    }
}




