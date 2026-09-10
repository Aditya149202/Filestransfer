namespace OrderService.DTOs;

public record PaymentInitiateResponse(
    int PaymentIntentId,
    string RazorpayOrderId,
    decimal Amount,
    string Currency,
    string RazorpayKeyId
);
