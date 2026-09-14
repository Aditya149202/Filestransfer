using Moq;
using NUnit.Framework;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Repositories.Interfaces;

namespace SupplierInventoryService.Tests.Services;

[TestFixture]
public class ReportServiceTests
{
    private Mock<ISalesRepository> _salesRepository;
    private ReportService _reportService;

    [SetUp]
    public void SetUp()
    {
        _salesRepository = new Mock<ISalesRepository>();
        _reportService = new ReportService(_salesRepository.Object);
    }

    private static Sale MakeSale(int id, int orderId, decimal amount) => new()
    {
        Id = id,
        OrderId = orderId,
        Amount = amount,
        SaleDate = DateTime.UtcNow
    };

    [Test]
    public async Task GetSalesReportAsync_Should_Return_Zero_Totals_When_No_Sales()
    {
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;
        _salesRepository.Setup(r => r.GetByDateRangeAsync(from, to)).ReturnsAsync(new List<Sale>());

        var report = await _reportService.GetSalesReportAsync(from, to);

        Assert.That(report.TotalOrders, Is.EqualTo(0));
        Assert.That(report.TotalAmount, Is.EqualTo(0m));
        Assert.That(report.DateFrom, Is.EqualTo(from));
        Assert.That(report.DateTo, Is.EqualTo(to));
    }

    [Test]
    public async Task GetSalesReportAsync_Should_Sum_Amount_And_Count_Orders_When_Sales_Exist()
    {
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;
        var sales = new List<Sale>
        {
            MakeSale(1, orderId: 101, amount: 100m),
            MakeSale(2, orderId: 102, amount: 250.50m),
            MakeSale(3, orderId: 103, amount: 49.50m)
        };
        _salesRepository.Setup(r => r.GetByDateRangeAsync(from, to)).ReturnsAsync(sales);

        var report = await _reportService.GetSalesReportAsync(from, to);

        Assert.That(report.TotalOrders, Is.EqualTo(3));
        Assert.That(report.TotalAmount, Is.EqualTo(400m));
    }

    [Test]
    public async Task GetSalesReportAsync_Should_Pass_Null_Dates_Through_When_No_Range_Given()
    {
        var sales = new List<Sale> { MakeSale(1, orderId: 101, amount: 75m) };
        _salesRepository.Setup(r => r.GetByDateRangeAsync(null, null)).ReturnsAsync(sales);

        var report = await _reportService.GetSalesReportAsync(from: null, to: null);

        Assert.That(report.DateFrom, Is.Null);
        Assert.That(report.DateTo, Is.Null);
        Assert.That(report.TotalOrders, Is.EqualTo(1));
        _salesRepository.Verify(r => r.GetByDateRangeAsync(null, null), Times.Once);
    }
}
