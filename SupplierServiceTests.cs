using Moq;
using NUnit.Framework;
using SupplierInventoryService.DTOs;
using SupplierInventoryService.Entities;
using SupplierInventoryService.ExceptionMiddleware;
using SupplierInventoryService.Repositories.Interfaces;
using SupplierInventoryService.Services.Implementations;

namespace SupplierInventoryService.Tests.Services;

[TestFixture]
public class SupplierServiceTests
{
    private Mock<ISupplierRepository> _supplierRepository;
    private SupplierService _supplierService;

    [SetUp]
    public void SetUp()
    {
        _supplierRepository = new Mock<ISupplierRepository>();
        _supplierService = new SupplierService(_supplierRepository.Object);
    }

    private static Supplier MakeSupplier(int id = 1, string name = "Supplier1", string contactInfo = "1234556889",
        string? address = "jdfkn gdh", bool isActive = true) => new()
    {
        Id = id,
        Name = name,
        ContactInfo = contactInfo,
        Address = address,
        IsActive = isActive,
        Drugs = new List<Drug>()
    };

    // ---------- CreateSupplierAsync ----------

    [Test]
    public async Task CreateSupplierAsync_Should_Create_Supplier_As_Active()
    {
        var request = new CreateSupplierRequest(Name: "Supplier1", ContactInfo: "1234556889", Address: "jdfkn gdh");

        var response = await _supplierService.CreateSupplierAsync(request);

        Assert.That(response.Name, Is.EqualTo(request.Name));
        Assert.That(response.ContactInfo, Is.EqualTo(request.ContactInfo));
        Assert.That(response.Address, Is.EqualTo(request.Address));
        Assert.That(response.IsActive, Is.True); // new suppliers are always created active

        _supplierRepository.Verify(x => x.AddAsync(It.Is<Supplier>(s =>
            s.Name == request.Name && s.ContactInfo == request.ContactInfo && s.IsActive)), Times.Once);
        _supplierRepository.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    // ---------- GetAllSuppliersAsync ----------

    [Test]
    public async Task GetAllSuppliersAsync_Should_Return_All_Suppliers_Mapped()
    {
        var suppliers = new List<Supplier>
        {
            MakeSupplier(id: 1, isActive: true),
            MakeSupplier(id: 2, isActive: false) // no active/inactive filtering here — unlike DrugService.GetAllDrugsAsync
        };
        _supplierRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(suppliers);

        var result = (await _supplierService.GetAllSuppliersAsync()).ToList();

        Assert.That(result.Select(s => s.Id), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task GetAllSuppliersAsync_Should_Return_Empty_When_No_Suppliers()
    {
        _supplierRepository.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<Supplier>());

        var result = await _supplierService.GetAllSuppliersAsync();

        Assert.That(result, Is.Empty);
    }

    // ---------- GetSupplierByIdAsync ----------

    [Test]
    public void GetSupplierByIdAsync_Should_Throw_NotFound_When_Supplier_Does_Not_Exist()
    {
        _supplierRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync((Supplier?)null);

        Assert.ThrowsAsync<NotFoundException>(() => _supplierService.GetSupplierByIdAsync(1));
    }

    [Test]
    public async Task GetSupplierByIdAsync_Should_Return_Supplier_When_Exists()
    {
        _supplierRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(MakeSupplier(id: 1));

        var result = await _supplierService.GetSupplierByIdAsync(1);

        Assert.That(result.Id, Is.EqualTo(1));
    }

    // ---------- UpdateSupplierAsync ----------

    [Test]
    public void UpdateSupplierAsync_Should_Throw_NotFound_When_Supplier_Does_Not_Exist()
    {
        _supplierRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync((Supplier?)null);
        var request = new UpdateSupplierRequest(Name: "new", ContactInfo: "999", Address: "new addr", IsActive: true);

        Assert.ThrowsAsync<NotFoundException>(() => _supplierService.UpdateSupplierAsync(1, request));

        _supplierRepository.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public async Task UpdateSupplierAsync_Should_Update_Fields_And_Save_When_Supplier_Exists()
    {
        var supplier = MakeSupplier(id: 1, name: "old", contactInfo: "111", address: "old addr", isActive: true);
        _supplierRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(supplier);
        var request = new UpdateSupplierRequest(Name: "new", ContactInfo: "999", Address: "new addr", IsActive: false);

        var response = await _supplierService.UpdateSupplierAsync(1, request);

        Assert.That(supplier.Name, Is.EqualTo("new"));
        Assert.That(supplier.ContactInfo, Is.EqualTo("999"));
        Assert.That(supplier.Address, Is.EqualTo("new addr"));
        Assert.That(supplier.IsActive, Is.False);
        Assert.That(response.Name, Is.EqualTo("new"));
        _supplierRepository.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    // ---------- DeactivateSupplierAsync ----------

    [Test]
    public void DeactivateSupplierAsync_Should_Throw_NotFound_When_Supplier_Does_Not_Exist()
    {
        _supplierRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync((Supplier?)null);

        Assert.ThrowsAsync<NotFoundException>(() => _supplierService.DeactivateSupplierAsync(1));

        _supplierRepository.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Test]
    public async Task DeactivateSupplierAsync_Should_Set_IsActive_False_And_Save_When_Supplier_Exists()
    {
        var supplier = MakeSupplier(id: 1, isActive: true);
        _supplierRepository.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(supplier);

        await _supplierService.DeactivateSupplierAsync(1);

        Assert.That(supplier.IsActive, Is.False);
        _supplierRepository.Verify(x => x.SaveChangesAsync(), Times.Once);
    }
}
