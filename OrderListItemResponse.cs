using OrderService.Enums;

namespace OrderService.DTOs;

// Deliberately lighter than OrderResponse — no Items list. Listing is a display/scan surface;
// full item breakdown belongs to the single-order detail view (GET /orders/{id}).
public record OrderListItemResponse(
    int Id,
    int DoctorId,
    string DoctorNameSnapshot,
    OrderStatus Status,
    decimal TotalAmount,
    DateTime CreatedAt
);
