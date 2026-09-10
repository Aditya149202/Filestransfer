using OrderService.Enums;

namespace OrderService.Entities;

public class Order
{
    public int Id { get; set; }
    public int PaymentIntentId { get; set; } // FK -> PaymentIntents, same DB
    public int DoctorId { get; set; } // logical ref to UsersDB
    public string DoctorNameSnapshot { get; set; } = default!;
    public OrderStatus Status { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? PickedUpAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public CancelledBy? CancelledBy { get; set; }

    public PaymentIntent PaymentIntent { get; set; } = default!;
    public List<OrderItem> Items { get; set; } = new();
}
