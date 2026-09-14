using Moq;
using NUnit.Framework;
using SupplierInventoryService.DTOs;
using SupplierInventoryService.Entities;
using SupplierInventoryService.ExceptionMiddleware;
using SupplierInventoryService.Repositories.Interfaces;
using SupplierInventoryService.Services.Implementations;

namespace SupplierInventoryService.Tests.Services;

[TestFixture]
public class DrugServiceTests
{
    private Mock<IDrugRepository> _drugRepository;
    private Mock<ISupplierRepository> _supplierRepository;
    private DrugService _drugService;

    [SetUp]
    public void SetUp()
    {
        _drugRepository = new Mock<IDrugRepository>();
        _supplierRepository = new Mock<ISupplierRepository>();

        _drugService = new DrugService(_drugRepository.Object, _supplierRepository.Object);
    }

    private static Supplier MakeSupplier(int id = 1, bool isActive = true) => new()
    {
        Id = id,
        Name = "Supplier1",
        ContactInfo = "1234556889",
        Address = "jdfkn gdh",
        IsActive = isActive,
        Drugs = new List<Drug>()
    };

    private static Drug MakeDrug(int id = 1, string name = "drug1", decimal price = 2000,
        int quantityInStock = 30, bool isActive = true, int supplierId = 1) => new()
    {
        Id = id,
        Name = name,
        Price = price,
        QuantityInStock = quantityInStock,
        IsActive = isActive,
        SupplierId = supplierId
    };

    // ---------- CreateDrugAsync ----------

    [Test]
    public async Task CreateDrugAsync_Should_Create_Drug_When_Supplier_Exists()
    {
        var request = new CreateDrugRequest(Name: "drug1", Price: 2000, QuantityInStock: 30, SupplierId: 1);
        _supplierRepository.Setup(x => x.GetByIdAsync(request.SupplierId)).ReturnsAsync(MakeSupplier(id: request.SupplierId));

        var response = await _drugService.CreateDrugAsync(request);

        // Assert on the fields CreateDrugAsync actually sets, rather than a hand-built
        // expected object — Id/IsActive are the service's own decisions, not the caller's.
        Assert.That(response.Name, Is.EqualTo(request.Name));
        Assert.That(response.Price, Is.EqualTo(request.Price));
        Assert.That(response.QuantityInStock, Is.EqualTo(request.QuantityInStock));
        Assert.That(response.SupplierId, Is.EqualTo(request.SupplierId));
        Assert.That(response.IsActive, Is.True); // new drugs are always created active

        _drugRepository.Verify(x => x.AddAsync(It.Is<Drug>(d =>
            d.Name == request.Name && d.SupplierId == request.SupplierId)), Times.Once);
        _drugRepository.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Test]
    public void CreateDrugAsync_Should_Throw_NotFound_When_Supplier_Does_Not_Exist()
    {
        var request = new CreateDrugRequest(Name: "drug1", Price: 2000, QuantityInStock: 30, SupplierId: 99);
        _supplierRepository.Setup(x => x.GetByIdAsync(99)).ReturnsAsync((Supplier?)null);

        Assert.ThrowsAsync<NotFoundException>(() => _drugService.CreateDrugAsync(request));

        _drugRepository.Verify(x => x.AddAsync(It.IsAny<Drug>()), Times.Never);
        _drugRepository.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    // ---------- GetAllDrugsAsync ----------

    [Test]
    public async Task GetAllDrugsAsync_Should_Exclude_Inactive_Drugs_When_IncludeInactive_Is_False()
    {
        var drugs = new List<Drug> { MakeDrug(id: 1, isActive: true), MakeDrug(id: 2, isActive: false) };
        _drugRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(drugs);

        var result = await _drugService.GetAllDrugsAsync(includeInactive: false);

        Assert.That(result.Select(d => d.Id), Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public async Task GetAllDrugsAsync_Should_Include_Inactive_Drugs_When_IncludeInactive_Is_True()
    {
        var drugs = new List<Drug> { MakeDrug(id: 1, isActive: true), MakeDrug(id: 2, isActive: false) };
        _drugRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(drugs);

        var result = await _drugService.GetAllDrugsAsync(includeInactive: true);

        Assert.That(result.Select(d => d.Id), Is.EquivalentTo(new[] { 1, 2 }));
    }

    // ---------- GetDrugByIdAsync ----------

    [Test]
    public void GetDrugByIdAsync_Should_Throw_NotFound_When_Drug_Does_Not_Exist()
    {
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync((Drug?)null);

        Assert.ThrowsAsync<NotFoundException>(() => _drugService.GetDrugByIdAsync(1, includeInactive: false));
    }

    [Test]
    public void GetDrugByIdAsync_Should_Throw_NotFound_When_Drug_Is_Inactive_And_IncludeInactive_Is_False()
    {
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(MakeDrug(id: 1, isActive: false));

        Assert.ThrowsAsync<NotFoundException>(() => _drugService.GetDrugByIdAsync(1, includeInactive: false));
    }

    [Test]
    public async Task GetDrugByIdAsync_Should_Return_Inactive_Drug_When_IncludeInactive_Is_True()
    {
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(MakeDrug(id: 1, isActive: false));

        var result = await _drugService.GetDrugByIdAsync(1, includeInactive: true);

        Assert.That(result.Id, Is.EqualTo(1));
        Assert.That(result.IsActive, Is.False);
    }

    [Test]
    public async Task GetDrugByIdAsync_Should_Return_Drug_When_Active()
    {
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(MakeDrug(id: 1, isActive: true));

        var result = await _drugService.GetDrugByIdAsync(1, includeInactive: false);

        Assert.That(result.Id, Is.EqualTo(1));
    }

    // ---------- UpdateDrugAsync ----------

    [Test]
    public void UpdateDrugAsync_Should_Throw_NotFound_When_Drug_Does_Not_Exist()
    {
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync((Drug?)null);
        var request = new UpdateDrugRequest(Name: "new", Price: 10, QuantityInStock: 5, IsActive: true);

        Assert.ThrowsAsync<NotFoundException>(() => _drugService.UpdateDrugAsync(1, request));

        _drugRepository.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public async Task UpdateDrugAsync_Should_Update_Fields_And_Save_When_Drug_Exists()
    {
        var drug = MakeDrug(id: 1, name: "old", price: 100, quantityInStock: 10, isActive: true);
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(drug);
        var request = new UpdateDrugRequest(Name: "new", Price: 250, QuantityInStock: 40, IsActive: false);

        var response = await _drugService.UpdateDrugAsync(1, request);

        Assert.That(drug.Name, Is.EqualTo("new"));
        Assert.That(drug.Price, Is.EqualTo(250));
        Assert.That(drug.QuantityInStock, Is.EqualTo(40));
        Assert.That(drug.IsActive, Is.False);
        Assert.That(response.Name, Is.EqualTo("new"));
        _drugRepository.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    // ---------- DeactivateDrugAsync ----------

    [Test]
    public void DeactivateDrugAsync_Should_Throw_NotFound_When_Drug_Does_Not_Exist()
    {
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync((Drug?)null);

        Assert.ThrowsAsync<NotFoundException>(() => _drugService.DeactivateDrugAsync(1));

        _drugRepository.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public async Task DeactivateDrugAsync_Should_Set_IsActive_False_And_Save_When_Drug_Exists()
    {
        var drug = MakeDrug(id: 1, isActive: true);
        _drugRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(drug);

        await _drugService.DeactivateDrugAsync(1);

        Assert.That(drug.IsActive, Is.False);
        _drugRepository.Verify(x => x.SaveChangesAsync(), Times.Once);
    }
}
