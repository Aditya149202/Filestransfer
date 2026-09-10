using OrderService.Enums;

namespace OrderService.Entities;

public class PaymentIntent
{
    public int Id { get; set; }
    public int DoctorId { get; set; } // logical ref to UsersDB, not FK-enforced
    public string RazorpayOrderId { get; set; } = default!;
    public string? RazorpayPaymentId { get; set; }
    public decimal Amount { get; set; }
    public PaymentIntentStatus Status { get; set; }
    public string ItemsSnapshot { get; set; } = default!; // JSON: drugId+quantity+unitPrice at initiation time
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Order? Order { get; set; } // one-to-one, populated only after successful payment
}
