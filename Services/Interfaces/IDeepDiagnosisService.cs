using System.Collections.Generic;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Models;

namespace BearingFaultDiagnosis.Services.Interfaces
{
    public interface IDeepDiagnosisService
    {
        /// <summary>
        /// 获取轴承型号列表
        /// </summary>
        Task<List<BearingInfo>> GetBearingListAsync();

        /// <summary>
        /// 计算轴承故障频率：优先使用数据库系数，否则根据几何参数推算
        /// </summary>
        BearingFaultResult CalculateFaultFrequencies(BearingInfo bearing, double rpm);

        /// <summary>
        /// 处理一块数据
        /// </summary>
        /// <param name="rawData">原生数据</param>
        /// <param name="fs">采样频率</param>
        /// <param name="rpm">转速</param>
        /// <param name="targetSpr">目标转速</param>
        /// <returns>处理后的数据</returns>
        double[] ProcessChunk(double[] rawData, double fs, double rpm, int targetSpr);

        /// <summary>
        /// 计算频谱幅度
        /// </summary>
        double[] ComputeSpectrum(double[] inData);

        /// <summary>
        /// 计算 CWT 时频矩阵（行=频率尺度，列=时间）
        /// </summary>
        double[] ComputeCWT(double[] rawData, double fs, double freqLow, double freqHigh,
                            int numScales, int numTimeBins);
    }
}