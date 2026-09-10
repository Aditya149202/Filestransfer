namespace OrderService.Enums;

public enum OrderStatus
{
    NEW,
    VERIFIED,
    PICKED_UP,
    COMPLETED,
    CANCELLED
}

public enum PaymentIntentStatus
{
    CREATED,
    PAID,
    FAILED,
    EXPIRED
}

public enum CancelledBy
{
    ADMIN,
    SYSTEM
}
