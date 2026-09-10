namespace OrderService.DTOs;

// Matches the locked pagination response shape: {items, page, size, totalItems, totalPages}
public record PagedResponse<T>(
    List<T> Items,
    int Page,
    int Size,
    int TotalItems,
    int TotalPages
);
