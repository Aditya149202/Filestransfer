using OrderService.Entities;

namespace OrderService.Repositories.Interfaces;

public interface IPaymentIntentRepository
{
    // Does NOT call SaveChangesAsync internally — caller must save explicitly.
    Task AddAsync(PaymentIntent paymentIntent);

    Task<PaymentIntent?> GetByIdAsync(int id);

    // Needed for the Razorpay webhook, which arrives keyed by razorpay_order_id, not our internal id.
    Task<PaymentIntent?> GetByRazorpayOrderIdAsync(string razorpayOrderId);

    Task SaveChangesAsync();
}
