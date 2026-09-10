namespace OrderService.DTOs;

public record PaymentInitiateRequestItem(int DrugId, int Quantity);

public record PaymentInitiateRequest(List<PaymentInitiateRequestItem> Items);
