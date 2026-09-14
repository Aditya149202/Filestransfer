using Moq;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Enums;
using SupplierInventoryService.ExceptionMiddleware;
using SupplierInventoryService.Repositories.Interfaces;
using SupplierInventoryService.Services;

namespace SupplierInventoryService.Tests.Services;

[TestFixture]
public class InternalServiceTests
{
    private Mock<IStockReservationRepository> _reservationRepo;
    private Mock<ISalesRepository> _salesRepo;
    private Mock<IProcessedEventRepository> _processedEventRepo;
    private Mock<IDrugRepository> _drugRepo;
    private InternalService _sut;

    [SetUp]
    public void Setup()
    {
        _reservationRepo = new Mock<IStockReservationRepository>();
        _salesRepo = new Mock<ISalesRepository>();
        _processedEventRepo = new Mock<IProcessedEventRepository>();
        _drugRepo = new Mock<IDrugRepository>();

        _sut = new InternalService(
            _reservationRepo.Object, _salesRepo.Object, _processedEventRepo.Object, _drugRepo.Object);
    }

    private static Drug MakeDrug(int id = 10, string name = "Paracetamol", decimal price = 5m,
        int quantityInStock = 100, bool isActive = true) => new()
    {
        Id = id,
        Name = name,
        Price = price,
        QuantityInStock = quantityInStock,
        IsActive = isActive
    };

    // ---------- CommitSaleAsync (existing) ----------

    // The core idempotency test: a retried commit for an already-processed order must be
    // a silent no-op — not a duplicate Sale, not a duplicate ProcessedEvent insert (which
    // is exactly what threw the identity-column error before the ValueGeneratedNever fix).
    [Test]
    public async Task CommitSaleAsync_Should_ReturnEarly_When_OrderId_Already_Processed()
    {
        _processedEventRepo.Setup(r => r.ExistsAsync(42)).ReturnsAsync(true);

        await _sut.CommitSaleAsync(orderId: 42, paymentIntentId: 5, amount: 100m);

        _reservationRepo.Verify(r => r.GetActiveByPaymentIntentIdAsync(It.IsAny<int>()), Times.Never);
        _salesRepo.Verify(r => r.AddAsync(It.IsAny<Sale>()), Times.Never);
        _processedEventRepo.Verify(r => r.AddAsync(It.IsAny<ProcessedEvent>()), Times.Never);
    }

    [Test]
    public void CommitSaleAsync_Should_Throw_NotFound_When_No_Active_Reservations()
    {
        _processedEventRepo.Setup(r => r.ExistsAsync(42)).ReturnsAsync(false);
        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(new List<StockReservation>());

        Assert.ThrowsAsync<NotFoundException>(
            () => _sut.CommitSaleAsync(orderId: 42, paymentIntentId: 5, amount: 100m));
    }

    [Test]
    public async Task CommitSaleAsync_Should_Commit_Reservations_Create_Sale_And_ProcessedEvent_When_Valid()
    {
        _processedEventRepo.Setup(r => r.ExistsAsync(42)).ReturnsAsync(false);

        var reservations = new List<StockReservation>
        {
            new() { Id = 1, PaymentIntentId = 5, DrugId = 10, Quantity = 2, Status = StockReservationStatus.ACTIVE.ToString() }
        };
        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(reservations);

        await _sut.CommitSaleAsync(orderId: 42, paymentIntentId: 5, amount: 100m);

        Assert.That(reservations[0].Status, Is.EqualTo(StockReservationStatus.COMMITTED.ToString()));
        _salesRepo.Verify(r => r.AddAsync(It.Is<Sale>(s => s.OrderId == 42 && s.Amount == 100m)), Times.Once);
        _processedEventRepo.Verify(r => r.AddAsync(It.Is<ProcessedEvent>(pe => pe.OrderId == 42)), Times.Once);
        // Single shared-context save, per the code's own comment — not once per repo.
        _processedEventRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
        _salesRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }

    // ---------- ReleaseReservationAsync (new) ----------

    [Test]
    public void ReleaseReservationAsync_Should_Throw_NotFound_When_No_Active_Reservations()
    {
        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(new List<StockReservation>());

        Assert.ThrowsAsync<NotFoundException>(() => _sut.ReleaseReservationAsync(paymentIntentId: 5));

        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public void ReleaseReservationAsync_Should_Throw_NotFound_When_Reservation_References_Missing_Drug()
    {
        var reservations = new List<StockReservation>
        {
            new() { Id = 1, PaymentIntentId = 5, DrugId = 10, Quantity = 3, Status = StockReservationStatus.ACTIVE.ToString() }
        };
        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(reservations);
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync((Drug?)null);

        Assert.ThrowsAsync<NotFoundException>(() => _sut.ReleaseReservationAsync(paymentIntentId: 5));

        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public async Task ReleaseReservationAsync_Should_Mark_Released_And_Restock_Drug_When_Valid()
    {
        var reservation = new StockReservation
        {
            Id = 1, PaymentIntentId = 5, DrugId = 10, Quantity = 3, Status = StockReservationStatus.ACTIVE.ToString()
        };
        var drug = MakeDrug(id: 10, quantityInStock: 20);

        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5)).ReturnsAsync(new List<StockReservation> { reservation });
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(drug);

        await _sut.ReleaseReservationAsync(paymentIntentId: 5);

        Assert.That(reservation.Status, Is.EqualTo(StockReservationStatus.RELEASED.ToString()));
        Assert.That(drug.QuantityInStock, Is.EqualTo(23)); // 20 + 3 released back
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    [Test]
    public async Task ReleaseReservationAsync_Should_Restock_Each_Drug_Independently_For_Multiple_Reservations()
    {
        var reservationA = new StockReservation { Id = 1, PaymentIntentId = 5, DrugId = 10, Quantity = 2, Status = StockReservationStatus.ACTIVE.ToString() };
        var reservationB = new StockReservation { Id = 2, PaymentIntentId = 5, DrugId = 20, Quantity = 4, Status = StockReservationStatus.ACTIVE.ToString() };
        var drugA = MakeDrug(id: 10, quantityInStock: 5);
        var drugB = MakeDrug(id: 20, quantityInStock: 8);

        _reservationRepo.Setup(r => r.GetActiveByPaymentIntentIdAsync(5))
            .ReturnsAsync(new List<StockReservation> { reservationA, reservationB });
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(drugA);
        _drugRepo.Setup(r => r.GetByIdAsync(20)).ReturnsAsync(drugB);

        await _sut.ReleaseReservationAsync(paymentIntentId: 5);

        Assert.That(drugA.QuantityInStock, Is.EqualTo(7));
        Assert.That(drugB.QuantityInStock, Is.EqualTo(12));
        Assert.That(reservationA.Status, Is.EqualTo(StockReservationStatus.RELEASED.ToString()));
        Assert.That(reservationB.Status, Is.EqualTo(StockReservationStatus.RELEASED.ToString()));
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    // ---------- ReserveStockAsync (new) ----------

    [Test]
    public void ReserveStockAsync_Should_Throw_NotFound_When_Drug_Does_Not_Exist()
    {
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync((Drug?)null);
        var items = new List<(int DrugId, int Quantity)> { (10, 2) };

        Assert.ThrowsAsync<NotFoundException>(() => _sut.ReserveStockAsync(paymentIntentId: 5, items: items));

        _reservationRepo.Verify(r => r.AddAsync(It.IsAny<StockReservation>()), Times.Never);
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public void ReserveStockAsync_Should_Throw_Validation_When_Drug_Is_Inactive()
    {
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(MakeDrug(id: 10, isActive: false));
        var items = new List<(int DrugId, int Quantity)> { (10, 2) };

        Assert.ThrowsAsync<AppValidationException>(() => _sut.ReserveStockAsync(paymentIntentId: 5, items: items));

        _reservationRepo.Verify(r => r.AddAsync(It.IsAny<StockReservation>()), Times.Never);
    }

    [Test]
    public void ReserveStockAsync_Should_Throw_InsufficientStock_When_Quantity_Exceeds_Stock()
    {
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(MakeDrug(id: 10, quantityInStock: 1));
        var items = new List<(int DrugId, int Quantity)> { (10, 2) };

        Assert.ThrowsAsync<InsufficientStockException>(() => _sut.ReserveStockAsync(paymentIntentId: 5, items: items));

        _reservationRepo.Verify(r => r.AddAsync(It.IsAny<StockReservation>()), Times.Never);
    }

    [Test]
    public async Task ReserveStockAsync_Should_Decrement_Stock_Create_Reservation_And_Return_DrugInfo_When_Valid()
    {
        var drug = MakeDrug(id: 10, name: "Ibuprofen", price: 12.5m, quantityInStock: 20);
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(drug);
        var items = new List<(int DrugId, int Quantity)> { (10, 5) };

        var result = await _sut.ReserveStockAsync(paymentIntentId: 5, items: items);

        Assert.That(drug.QuantityInStock, Is.EqualTo(15));
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo((10, "Ibuprofen", 12.5m)));
        _reservationRepo.Verify(r => r.AddAsync(It.Is<StockReservation>(sr =>
            sr.PaymentIntentId == 5 &&
            sr.DrugId == 10 &&
            sr.Quantity == 5 &&
            sr.Status == StockReservationStatus.ACTIVE.ToString())), Times.Once);
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    [Test]
    public async Task ReserveStockAsync_Should_Decrement_Each_Drug_Independently_For_Multiple_Items()
    {
        var drugA = MakeDrug(id: 10, name: "Paracetamol", price: 5m, quantityInStock: 20);
        var drugB = MakeDrug(id: 20, name: "Amoxicillin", price: 30m, quantityInStock: 10);
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(drugA);
        _drugRepo.Setup(r => r.GetByIdAsync(20)).ReturnsAsync(drugB);
        var items = new List<(int DrugId, int Quantity)> { (10, 3), (20, 4) };

        var result = await _sut.ReserveStockAsync(paymentIntentId: 5, items: items);

        Assert.That(drugA.QuantityInStock, Is.EqualTo(17));
        Assert.That(drugB.QuantityInStock, Is.EqualTo(6));
        Assert.That(result, Has.Count.EqualTo(2));
        _reservationRepo.Verify(r => r.AddAsync(It.IsAny<StockReservation>()), Times.Exactly(2));
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Once);
    }

    // Validates the two-pass design in ReserveStockAsync: ALL items are validated in a first
    // pass before ANY mutation happens in the second pass. If item #2 in the batch fails,
    // item #1 must be left completely untouched — no partial reservation, no partial decrement.
    [Test]
    public async Task ReserveStockAsync_Should_Not_Mutate_Any_Drug_When_A_Later_Item_Fails_Validation()
    {
        var drugA = MakeDrug(id: 10, quantityInStock: 20); // valid on its own
        var drugB = MakeDrug(id: 20, quantityInStock: 1);  // will fail: asking for more than in stock
        _drugRepo.Setup(r => r.GetByIdAsync(10)).ReturnsAsync(drugA);
        _drugRepo.Setup(r => r.GetByIdAsync(20)).ReturnsAsync(drugB);
        var items = new List<(int DrugId, int Quantity)> { (10, 3), (20, 5) };

        Assert.ThrowsAsync<InsufficientStockException>(() => _sut.ReserveStockAsync(paymentIntentId: 5, items: items));

        Assert.That(drugA.QuantityInStock, Is.EqualTo(20)); // untouched — validation pass hadn't started mutating yet
        _reservationRepo.Verify(r => r.AddAsync(It.IsAny<StockReservation>()), Times.Never);
        _reservationRepo.Verify(r => r.SaveChangesAsync(), Times.Never);
    }
}
