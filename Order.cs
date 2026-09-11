 public async Task<List<(int, string, decimal)>> ReserveStockAsync(
    int paymentIntentId, List<(int DrugId, int Quantity)> items)
{
    var payload = new
    {
        PaymentIntentId = paymentIntentId,
        Items = items.Select(i => new { DrugId = i.DrugId, Quantity = i.Quantity })
    };
    var response = await _httpClient.PostAsJsonAsync("/internal/stock/reserve", payload);

    if (!response.IsSuccessStatusCode)
    {
        switch ((int)response.StatusCode)
        {
            case 404:
                throw new NotFoundException("One or more drugs in the order were not found.");
            case 409:
                throw new StockUnavailableException("One or more items are out of stock.");
            case 400:
                throw new AppValidationException(new Dictionary<string, string[]>
                    { ["items"] = new[] { "Invalid stock reservation request." } });
            default:
                response.EnsureSuccessStatusCode();
                break;
        }
    }

    var result = await response.Content.ReadFromJsonAsync<ReserveStockResponseDto>();
    return result!.Items.Select(i => (i.DrugId, i.DrugName, i.UnitPrice)).ToList();
}

private record ReserveStockResponseDto(List<ReserveStockResponseItemDto> Items);
private record ReserveStockResponseItemDto(int DrugId, string DrugName, decimal UnitPrice);
