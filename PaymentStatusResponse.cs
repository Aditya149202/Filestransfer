using OrderService.Enums;

namespace OrderService.DTOs;

public record PaymentStatusResponse(
    int PaymentIntentId,
    PaymentIntentStatus Status,
    int? OrderId // populated only once status is PAID and the Order side-effect has run
);
