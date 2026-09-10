using OrderService.Enums;

namespace OrderService.Repositories.Filters;

// Repository-layer filter, kept separate from the controller-facing OrderFilterRequest DTO
// so the repo signature doesn't depend on the DTO namespace.
public class OrderFilter
{
    public int? DoctorId { get; set; }
    public OrderStatus? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? DrugId { get; set; }
}
