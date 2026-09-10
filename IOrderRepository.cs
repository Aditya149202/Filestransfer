using OrderService.Entities;
using OrderService.Enums;
using OrderService.Repositories.Filters;

namespace OrderService.Repositories.Interfaces;

public interface IOrderRepository
{
    // Does NOT call SaveChangesAsync internally — caller must save explicitly.
    Task AddAsync(Order order);

    // Tracked entity with Items included — used by verify/pickup/cancel, which mutate
    // the returned entity and call SaveChangesAsync(), not a separate Update() method.
    Task<Order?> GetByIdAsync(int id);

    // Read-only listing for GET /orders. No-tracking, no Items include (list view doesn't need line items).
    // Returns (page of results, total matching count) for the {items, page, size, totalItems, totalPages} shape.
    Task<(List<Order> Items, int TotalCount)> GetFilteredAsync(OrderFilter filter, int page, int size);

    // For the stale-order auto-cancel job. Threshold hours is not locked yet, so the cutoff
    // is computed by the caller and passed in — this repo method has no opinion on the duration.
    Task<List<Order>> GetStaleOrdersAsync(DateTime cutoff, IReadOnlyCollection<OrderStatus> statuses);

    Task SaveChangesAsync();
}
