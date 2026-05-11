using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BearingFaultDiagnosis.Services.Implements;

public class BearingInfoService : IBearingInfoService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public BearingInfoService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<List<BearingInfo>> GetAllAsync()
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.BearingInfos
            .OrderBy(b => b.Manufacturer)
            .ThenBy(b => b.Model)
            .ToListAsync();
    }

    public async Task<BearingInfo?> GetByIdAsync(long id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.BearingInfos.FindAsync(id);
    }

    public async Task<BearingInfo> AddAsync(BearingInfo bearing)
    {
        ArgumentNullException.ThrowIfNull(bearing);
        if (string.IsNullOrWhiteSpace(bearing.Model))
            throw new ArgumentException("轴承型号不能为空", nameof(bearing.Model));

        using var context = await _dbContextFactory.CreateDbContextAsync();
        context.BearingInfos.Add(bearing);
        await context.SaveChangesAsync();
        return bearing;
    }

    public async Task<BearingInfo?> UpdateAsync(BearingInfo bearing)
    {
        ArgumentNullException.ThrowIfNull(bearing);
        if (string.IsNullOrWhiteSpace(bearing.Model))
            throw new ArgumentException("轴承型号不能为空", nameof(bearing.Model));

        using var context = await _dbContextFactory.CreateDbContextAsync();
        var existing = await context.BearingInfos.FindAsync(bearing.Id);
        if (existing == null)
            return null;

        context.Entry(existing).CurrentValues.SetValues(bearing);
        await context.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var context = await _dbContextFactory.CreateDbContextAsync();
        var existing = await context.BearingInfos.FindAsync(id);
        if (existing == null)
            return false;

        context.BearingInfos.Remove(existing);
        await context.SaveChangesAsync();
        return true;
    }
}
