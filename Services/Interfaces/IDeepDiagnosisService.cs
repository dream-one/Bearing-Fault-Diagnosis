using System.Collections.Generic;

namespace BearingFaultDiagnosis.Services.Interfaces
{
    public interface IDeepDiagnosisService
    {
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