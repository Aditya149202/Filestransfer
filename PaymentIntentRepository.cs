using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Entities;
using OrderService.Repositories.Interfaces;

namespace OrderService.Repositories.Implementations;

public class PaymentIntentRepository : IPaymentIntentRepository
{
    private readonly OrdersDbContext _context;

    public PaymentIntentRepository(OrdersDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(PaymentIntent paymentIntent)
    {
        await _context.PaymentIntents.AddAsync(paymentIntent);
    }

    public async Task<PaymentIntent?> GetByIdAsync(int id)
    {
        return await _context.PaymentIntents
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<PaymentIntent?> GetByRazorpayOrderIdAsync(string razorpayOrderId)
    {
        return await _context.PaymentIntents
            .FirstOrDefaultAsync(p => p.RazorpayOrderId == razorpayOrderId);
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}
