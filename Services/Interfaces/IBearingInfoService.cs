using BearingFaultDiagnosis.Entities;

namespace BearingFaultDiagnosis.Services.Interfaces;

public interface IBearingInfoService
{
    Task<List<BearingInfo>> GetAllAsync();
    Task<BearingInfo?> GetByIdAsync(long id);
    Task<BearingInfo> AddAsync(BearingInfo bearing);
    Task<BearingInfo?> UpdateAsync(BearingInfo bearing);
    Task<bool> DeleteAsync(long id);
}
