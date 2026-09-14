using Moq;
using NUnit.Framework;
using SupplierInventoryService.Data;
using SupplierInventoryService.DTOs;
using SupplierInventoryService.Repositories.Interfaces;
using SupplierInventoryService.Entities;
using SupplierInventoryService.Services.Interfaces;
using SupplierInventoryService.Services.Implementations;
using SupplierInventoryService.Repositories.Interfaces;
using SupplierInventoryService.ExceptionMiddleware;
using SupplierInventoryService.Controllers;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace SupplierInventoryService.Tests.Services;

[TestFixture]

public class DrugServiceTests
{
    #region Mocks

    private Mock<IDrugRepository> _drugRepository;
    private Mock<ISupplierRepository> _supplierRepository;

    private DrugService _drugService;
    #endregion


    [SetUp]
    public void SetUp()
    {
        _drugRepository = new Mock<IDrugRepository>();
        _supplierRepository = new Mock<ISupplierRepository>();

        _drugService = new DrugService(_drugRepository.Object, _supplierRepository.Object);
    }

[Test]
public async Task CreateDrugAsync_Should_Create_Drug()
    {
        var request=new CreateDrugRequest(
            Name:"drug1",
            Price:2000,
            QuantityInStock:30,
            SupplierId:1
        );
        _supplierRepository.Setup(x=>x.GetByIdAsync(request.SupplierId))
        .ReturnsAsync(new Supplier
        {
            Id=request.SupplierId,
            Name="Supplier1",
            ContactInfo="1234556889",
            Address="jdfkn gdh",
            IsActive=true,
            Drugs=new List<Drug>{}
        });
        var d=new Drug
        {
            Name=request.Name,
            Price=request.Price,
            QuantityInStock=request.QuantityInStock,
            SupplierId=request.SupplierId,
            IsActive=true
        };
        var response=await _drugService.CreateDrugAsync(request);
        Assert.That(response,Is.EqualTo(new DrugResponse(d.Id, d.Name, d.Price, d.QuantityInStock, d.IsActive, d.SupplierId)));

        _drugRepository.Verify(x=>x.AddAsync(It.IsAny<Drug>()),Times.Once);
        _drugRepository.Verify(x=>x.SaveChangesAsync(),Times.Once);

    }

}
    
