using OrderService.Enums;

namespace OrderService.DTOs;

// Bound from query string on GET /orders. Filtering is by exact DoctorId (int), never by name —
// per the locked decision that name display uses the DoctorNameSnapshot, not a name-based filter.
public record OrderFilterRequest(
    int? DoctorId,
    OrderStatus? Status,
    DateTime? DateFrom,
    DateTime? DateTo,
    int? DrugId,
    int Page = 1,
    int Size = 20
);
