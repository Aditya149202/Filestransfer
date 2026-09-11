public async Task<OrderResponse> CancelOrderAsync(int id, CancelledBy cancelledBy, int? requestingDoctorId)
{
    var order = await _orderRepository.GetByIdAsync(id)
        ?? throw new NotFoundException($"Order {id} not found.");

    // Anti-enumeration — same NotFoundException as a genuinely missing id, same
    // convention as GetOrderByIdAsync, so a Doctor can't distinguish "not mine" from "doesn't exist."
    if (requestingDoctorId.HasValue && order.DoctorId != requestingDoctorId.Value)
        throw new NotFoundException($"Order {id} not found.");

    // If you go with "Doctor can only cancel pre-verification":
    var allowedStatuses = cancelledBy == CancelledBy.DOCTOR
        ? new[] { OrderStatus.NEW }
        : new[] { OrderStatus.NEW, OrderStatus.VERIFIED };

    if (!allowedStatuses.Contains(order.Status))
        throw new AppValidationException(
            $"Order {id} cannot be cancelled from status {order.Status} by {cancelledBy}.");

    await _supplierInventoryClient.ReleaseReservationAsync(order.PaymentIntentId);

    order.Status = OrderStatus.CANCELLED;
    order.CancelledAt = DateTime.UtcNow;
    order.CancelledBy = cancelledBy;
    await _orderRepository.SaveChangesAsync();

    return ToOrderResponse(order);
}
