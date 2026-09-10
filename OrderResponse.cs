using OrderService.Enums;

namespace OrderService.DTOs;

public record OrderResponse(
    int Id,
    int DoctorId,
    string DoctorNameSnapshot,
    OrderStatus Status,
    decimal TotalAmount,
    DateTime CreatedAt,
    DateTime? VerifiedAt,
    DateTime? PickedUpAt,
    DateTime? CompletedAt,
    DateTime? CancelledAt,
    CancelledBy? CancelledBy,
    List<OrderItemResponse> Items
);
