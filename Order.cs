public async Task<List<(int, string, decimal)>> ReserveStockAsync(
    int paymentIntentId, List<(int DrugId, int Quantity)> items)
{
    var drugs = new List<(Drug Drug, int Quantity)>();

    foreach (var (drugId, quantity) in items)
    {
        var drug = await _drugRepository.GetByIdAsync(drugId)
            ?? throw new NotFoundException($"Drug {drugId} not found.");

        if (!drug.IsActive)
            throw new AppValidationException("drugId", $"Drug {drugId} is no longer available.");

        if (drug.QuantityInStock < quantity)
            throw new InsufficientStockException($"Insufficient stock for drug {drugId}.");

        drugs.Add((drug, quantity));
    }

    var results = new List<(int, string, decimal)>();
    foreach (var (drug, quantity) in drugs)
    {
        drug.QuantityInStock -= quantity;

        await _reservationRepository.AddAsync(new StockReservation
        {
            PaymentIntentId = paymentIntentId,
            DrugId = drug.Id,
            Quantity = quantity,
            Status = StockReservationStatus.ACTIVE.ToString(),
            ReservedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15) // placeholder, same as the never-locked stale-order threshold
        });

        results.Add((drug.Id, drug.Name, drug.Price));
    }

    await _reservationRepository.SaveChangesAsync();
    return results;
}
