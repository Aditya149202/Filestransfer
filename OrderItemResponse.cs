namespace OrderService.DTOs;

public record OrderItemResponse(int DrugId, int Quantity, decimal UnitPriceAtOrder);
