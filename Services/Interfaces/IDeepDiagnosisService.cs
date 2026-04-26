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
    }
}