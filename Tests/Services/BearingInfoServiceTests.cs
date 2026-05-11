using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Services.Implements;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BearingFaultDiagnosis.Tests.Services;

public class BearingInfoServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BearingInfoServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // 创建数据库架构（含 HasData 种子数据），然后清除种子数据使测试从空库开始
        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
        context.BearingInfos.RemoveRange(context.BearingInfos);
        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    private BearingInfoService CreateService()
    {
        var factory = new TestDbContextFactory(_options);
        return new BearingInfoService(factory);
    }

    [Fact]
    public async Task GetAllAsync_EmptyDatabase_ReturnsEmptyList()
    {
        var service = CreateService();
        var result = await service.GetAllAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task AddAsync_ValidBearing_ReturnsBearingWithId()
    {
        var service = CreateService();
        var bearing = new BearingInfo
        {
            Manufacturer = "TestMfr",
            Model = "TEST-001",
            RollerCount_n = 9,
            RollerDiameter_d = 7.94,
            PitchDiameter_D = 39.04,
            ContactAngle_alpha = 0,
            BPFO_Multiplier = 3.585,
            BPFI_Multiplier = 5.415,
            BSF_Multiplier = 2.311,
            FTF_Multiplier = 0.398
        };

        var result = await service.AddAsync(bearing);

        Assert.True(result.Id > 0);
        Assert.Equal("TestMfr", result.Manufacturer);
        Assert.Equal("TEST-001", result.Model);
    }

    [Fact]
    public async Task AddAsync_MissingModel_ThrowsException()
    {
        var service = CreateService();
        var bearing = new BearingInfo
        {
            Model = "",
            RollerCount_n = 9
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(bearing));
    }

    [Fact]
    public async Task GetAllAsync_AfterAdd_ReturnsList()
    {
        var service = CreateService();
        await SeedBearing(service);

        var result = await service.GetAllAsync();

        Assert.Single(result);
        Assert.Equal("TEST-001", result[0].Model);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsBearing()
    {
        var service = CreateService();
        var added = await SeedBearing(service);

        var result = await service.GetByIdAsync(added.Id);

        Assert.NotNull(result);
        Assert.Equal(added.Id, result.Id);
        Assert.Equal("TEST-001", result.Model);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistingId_ReturnsNull()
    {
        var service = CreateService();

        var result = await service.GetByIdAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_ExistingBearing_UpdatesFields()
    {
        var service = CreateService();
        var added = await SeedBearing(service);

        added.Manufacturer = "NSK";
        added.RollerCount_n = 10;
        var result = await service.UpdateAsync(added);

        Assert.NotNull(result);
        Assert.Equal("NSK", result.Manufacturer);
        Assert.Equal(10, result.RollerCount_n);

        // 验证持久化
        var fetched = await service.GetByIdAsync(added.Id);
        Assert.NotNull(fetched);
        Assert.Equal("NSK", fetched.Manufacturer);
    }

    [Fact]
    public async Task UpdateAsync_NonExistingId_ReturnsNull()
    {
        var service = CreateService();
        var bearing = new BearingInfo
        {
            Id = 999,
            Model = "6205",
            RollerCount_n = 9
        };

        var result = await service.UpdateAsync(bearing);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_ExistingId_ReturnsTrue()
    {
        var service = CreateService();
        var added = await SeedBearing(service);

        var result = await service.DeleteAsync(added.Id);

        Assert.True(result);
        Assert.Null(await service.GetByIdAsync(added.Id));
    }

    [Fact]
    public async Task DeleteAsync_NonExistingId_ReturnsFalse()
    {
        var service = CreateService();

        var result = await service.DeleteAsync(999);

        Assert.False(result);
    }

    [Fact]
    public async Task GetAllAsync_MultipleBearings_OrderedByManufacturerThenModel()
    {
        var service = CreateService();

        await service.AddAsync(new BearingInfo { Model = "B", RollerCount_n = 9, Manufacturer = "Alpha" });
        await service.AddAsync(new BearingInfo { Model = "A", RollerCount_n = 9, Manufacturer = "Beta" });
        await service.AddAsync(new BearingInfo { Model = "A", RollerCount_n = 8, Manufacturer = "Alpha" });

        var result = await service.GetAllAsync();

        Assert.Equal(3, result.Count);
        // 排序按 Manufacturer 升序，再按 Model 升序
        Assert.Equal("Alpha", result[0].Manufacturer);
        Assert.Equal("A", result[0].Model);              // Alpha-A
        Assert.Equal("B", result[1].Model);              // Alpha-B
        Assert.Equal("Beta", result[2].Manufacturer);    // Beta-A
    }

    private static async Task<BearingInfo> SeedBearing(BearingInfoService service)
    {
        return await service.AddAsync(new BearingInfo
        {
            Manufacturer = "TestMfr",
            Model = "TEST-001",
            RollerCount_n = 9,
            RollerDiameter_d = 7.94,
            PitchDiameter_D = 39.04
        });
    }
}

/// <summary>
/// 测试用 IDbContextFactory，直接传入已配置的选项
/// </summary>
public class TestDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDbContextFactory(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    public AppDbContext CreateDbContext()
    {
        return new AppDbContext(_options);
    }

    public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(new AppDbContext(_options));
    }
}
