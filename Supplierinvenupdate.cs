using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupplierInventoryService.Services.Interfaces;

namespace SupplierInventoryService.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize(Roles = "ADMIN")]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;

    public ReportsController(IReportService reportService)
    {
        _reportService = reportService;
    }

    [HttpGet("sales")]
    public async Task<IActionResult> GetSalesReport(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string format = "json")
    {
        var report = await _reportService.GetSalesReportAsync(dateFrom, dateTo);

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = new StringBuilder();
            csv.AppendLine("DateFrom,DateTo,TotalOrders,TotalAmount");
            csv.AppendLine($"{report.DateFrom:yyyy-MM-dd},{report.DateTo:yyyy-MM-dd},{report.TotalOrders},{report.TotalAmount}");

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return File(bytes, "text/csv", $"sales-report-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        }

        return Ok(report);
    }
}
