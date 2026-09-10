using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Entities;
using OrderService.Enums;
using OrderService.Repositories.Filters;
using OrderService.Repositories.Interfaces;

namespace OrderService.Repositories.Implementations;

public class OrderRepository : IOrderRepository
{
    private readonly OrdersDbContext _context;

    public OrderRepository(OrdersDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Order order)
    {
        await _context.Orders.AddAsync(order);
    }

    public async Task<Order?> GetByIdAsync(int id)
    {
        return await _context.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id);
    }

    public async Task<(List<Order> Items, int TotalCount)> GetFilteredAsync(
        OrderFilter filter, int page, int size)
    {
        var query = _context.Orders.AsNoTracking().AsQueryable();

        if (filter.DoctorId.HasValue)
            query = query.Where(o => o.DoctorId == filter.DoctorId.Value);

        if (filter.Status.HasValue)
            query = query.Where(o => o.Status == filter.Status.Value);

        if (filter.DateFrom.HasValue)
            query = query.Where(o => o.CreatedAt >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(o => o.CreatedAt <= filter.DateTo.Value);

        if (filter.DrugId.HasValue)
            query = query.Where(o => o.Items.Any(i => i.DrugId == filter.DrugId.Value));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<List<Order>> GetStaleOrdersAsync(DateTime cutoff, IReadOnlyCollection<OrderStatus> statuses)
    {
        return await _context.Orders
            .Where(o => statuses.Contains(o.Status) && o.CreatedAt < cutoff)
            .ToListAsync();
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}
