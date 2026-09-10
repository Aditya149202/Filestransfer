namespace SupplierInventoryService.Enums;

public enum StockReservationStatus
{
    ACTIVE,
    RELEASED,
    COMMITTED
}

using SupplierInventoryService.Enums;

namespace SupplierInventoryService.Entities;

public class StockReservation
{
    public int Id { get; set; }
    public int PaymentIntentId { get; set; } // logical ref to OrdersDB
    public int DrugId { get; set; } // FK -> Drugs, same DB
    public int Quantity { get; set; }
    public StockReservationStatus Status { get; set; }
    public DateTime ReservedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Drug Drug { get; set; } = default!;
}



namespace SupplierInventoryService.Entities;

public class Sale
{
    public int Id { get; set; }
    public int OrderId { get; set; } // unique, logical ref to OrdersDB
    public decimal Amount { get; set; }
    public DateTime SaleDate { get; set; }
}


namespace SupplierInventoryService.Entities;

// PK is OrderId itself, not a separate Id — matches the locked "order_id as PK" decision.
public class ProcessedEvent
{
    public int OrderId { get; set; }
    public DateTime ProcessedAt { get; set; }
}



using SupplierInventoryService.Entities;

namespace SupplierInventoryService.Repositories.Interfaces;

public interface IStockReservationRepository
{
    // Tracked entities — caller mutates Status directly and saves via the shared DbContext.
    // Returns all ACTIVE reservations for this PaymentIntent (one row per drug line item).
    Task<List<StockReservation>> GetActiveByPaymentIntentIdAsync(int paymentIntentId);

    Task SaveChangesAsync();
}


using Microsoft.EntityFrameworkCore;
using SupplierInventoryService.Data;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Enums;
using SupplierInventoryService.Repositories.Interfaces;

namespace SupplierInventoryService.Repositories.Implementations;

public class StockReservationRepository : IStockReservationRepository
{
    private readonly SupplierInventoryDbContext _context;

    public StockReservationRepository(SupplierInventoryDbContext context)
    {
        _context = context;
    }

    public async Task<List<StockReservation>> GetActiveByPaymentIntentIdAsync(int paymentIntentId)
    {
        return await _context.StockReservations
            .Where(r => r.PaymentIntentId == paymentIntentId && r.Status == StockReservationStatus.ACTIVE)
            .ToListAsync();
    }

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}




using SupplierInventoryService.Entities;

namespace SupplierInventoryService.Repositories.Interfaces;

public interface ISalesRepository
{
    // Does NOT call SaveChangesAsync internally — same convention as every other repo here.
    Task AddAsync(Sale sale);
    Task SaveChangesAsync();
}






using SupplierInventoryService.Data;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Repositories.Interfaces;

namespace SupplierInventoryService.Repositories.Implementations;

public class SalesRepository : ISalesRepository
{
    private readonly SupplierInventoryDbContext _context;

    public SalesRepository(SupplierInventoryDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Sale sale) => await _context.Sales.AddAsync(sale);

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}



using SupplierInventoryService.Entities;

namespace SupplierInventoryService.Repositories.Interfaces;

public interface IProcessedEventRepository
{
    // order_id is the PK — this is the idempotency check itself, not a generic lookup.
    Task<bool> ExistsAsync(int orderId);
    Task AddAsync(ProcessedEvent processedEvent);
    Task SaveChangesAsync();
}



using Microsoft.EntityFrameworkCore;
using SupplierInventoryService.Data;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Repositories.Interfaces;

namespace SupplierInventoryService.Repositories.Implementations;

public class ProcessedEventRepository : IProcessedEventRepository
{
    private readonly SupplierInventoryDbContext _context;

    public ProcessedEventRepository(SupplierInventoryDbContext context)
    {
        _context = context;
    }

    public async Task<bool> ExistsAsync(int orderId) =>
        await _context.ProcessedEvents.AnyAsync(e => e.OrderId == orderId);

    public async Task AddAsync(ProcessedEvent processedEvent) =>
        await _context.ProcessedEvents.AddAsync(processedEvent);

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}



namespace SupplierInventoryService.Services;

public interface IInternalService
{
    Task CommitSaleAsync(int orderId, int paymentIntentId, decimal amount);
    Task ReleaseReservationAsync(int paymentIntentId);
}




using SupplierInventoryService.Entities;
using SupplierInventoryService.Enums;
using SupplierInventoryService.Exceptions;
using SupplierInventoryService.Repositories.Interfaces;

namespace SupplierInventoryService.Services;

public class InternalService : IInternalService
{
    private readonly IStockReservationRepository _reservationRepository;
    private readonly ISalesRepository _salesRepository;
    private readonly IProcessedEventRepository _processedEventRepository;
    private readonly IDrugRepository _drugRepository;

    public InternalService(
        IStockReservationRepository reservationRepository,
        ISalesRepository salesRepository,
        IProcessedEventRepository processedEventRepository,
        IDrugRepository drugRepository)
    {
        _reservationRepository = reservationRepository;
        _salesRepository = salesRepository;
        _processedEventRepository = processedEventRepository;
        _drugRepository = drugRepository;
    }

    // Idempotent by design: if this orderId is already in ProcessedEvents, it's a retried
    // pickup call (e.g. OrderService's own SaveChangesAsync failed after this succeeded once
    // before) — return success without re-committing reservations or inserting a duplicate Sale.
    public async Task CommitSaleAsync(int orderId, int paymentIntentId, decimal amount)
    {
        if (await _processedEventRepository.ExistsAsync(orderId))
            return; // already processed — no-op, not an error

        var activeReservations = await _reservationRepository.GetActiveByPaymentIntentIdAsync(paymentIntentId);
        if (activeReservations.Count == 0)
            throw new NotFoundException($"No ACTIVE StockReservation found for PaymentIntent {paymentIntentId}.");

        foreach (var reservation in activeReservations)
            reservation.Status = StockReservationStatus.COMMITTED;

        await _salesRepository.AddAsync(new Sale
        {
            OrderId = orderId,
            Amount = amount,
            SaleDate = DateTime.UtcNow
        });

        await _processedEventRepository.AddAsync(new ProcessedEvent
        {
            OrderId = orderId,
            ProcessedAt = DateTime.UtcNow
        });

        // Single save across all three repos' pending changes (same DbContext instance).
        await _processedEventRepository.SaveChangesAsync();
    }

    // 404 here is deliberate, not swallowed — OrderService's ReleaseReservationAsync already
    // treats 404 as a no-op on its side, so "nothing ACTIVE to release" (already released,
    // or never existed) is safe to surface honestly rather than pretending to succeed here too.
    public async Task ReleaseReservationAsync(int paymentIntentId)
    {
        var activeReservations = await _reservationRepository.GetActiveByPaymentIntentIdAsync(paymentIntentId);
        if (activeReservations.Count == 0)
            throw new NotFoundException($"No ACTIVE StockReservation found for PaymentIntent {paymentIntentId}.");

        foreach (var reservation in activeReservations)
        {
            reservation.Status = StockReservationStatus.RELEASED;

            var drug = await _drugRepository.GetByIdAsync(reservation.DrugId)
                ?? throw new NotFoundException($"Drug {reservation.DrugId} referenced by reservation not found.");
            drug.QuantityInStock += reservation.Quantity;
        }

        await _reservationRepository.SaveChangesAsync();
    }
}




using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupplierInventoryService.Auth;
using SupplierInventoryService.DTOs;
using SupplierInventoryService.Services;

namespace SupplierInventoryService.Controllers;

[ApiController]
[Route("internal")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public class InternalController : ControllerBase
{
    private readonly IInternalService _internalService;

    public InternalController(IInternalService internalService)
    {
        _internalService = internalService;
    }

    [HttpPost("sales/commit")]
    public async Task<IActionResult> CommitSale([FromBody] CommitSaleRequest request)
    {
        await _internalService.CommitSaleAsync(request.OrderId, request.PaymentIntentId, request.Amount);
        return Ok();
    }

    [HttpPost("reservations/{paymentIntentId}/release")]
    public async Task<IActionResult> ReleaseReservation(int paymentIntentId)
    {
        await _internalService.ReleaseReservationAsync(paymentIntentId);
        return Ok();
    }
}








