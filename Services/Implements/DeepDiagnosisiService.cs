using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Services.Interfaces;

namespace BearingFaultDiagnosis.Services.Implements
{
    public class DeepDiagnosisService: IDeepDiagnosisService
    {
        // 声明 C++ DLL 接口
        [DllImport("HighPerformanceComputing.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ComputeOrderTracking(
            IntPtr in_data,
            int in_len,
            double current_fs,
            double rpm,
            int target_spr,
            IntPtr out_data,
            out int out_len
        );
        public double[] ProcessChunk(double[] rawData, double fs, double rpm, int targetSpr)
        {
            int inLen = rawData.Length;
            // 估算输出最大长度：最高转速下的点数，分配一个足够大的缓冲区
            // 比如假设最高 3000 RPM (50Hz)，0.512秒最多转 25.6 圈，400点/圈，大约 10240 点。
            double maxDuration = inLen / fs;
            int maxPossiblePoints = (int)((3000.0 / 60.0) * maxDuration * targetSpr) + 1000;

            double[] outDataBuffer = new double[maxPossiblePoints];
            int actualOutLen = 0;

            // 使用 fixed 锁定内存，实现零拷贝传递
            unsafe
            {
                fixed (double* pIn = rawData)
                fixed (double* pOut = outDataBuffer)
                {
                    ComputeOrderTracking(
                        (IntPtr)pIn,
                        inLen,
                        fs,
                        rpm,
                        targetSpr,
                        (IntPtr)pOut,
                        out actualOutLen
                    );
                }
            }

            // 截取实际有效长度返回 (这里会发生一次极小的内存拷贝)
            // 也可以直接返回 Span<double> 彻底零拷贝
            double[] finalResult = new double[actualOutLen];
            Array.Copy(outDataBuffer, finalResult, actualOutLen);

            return finalResult;
        }

    }

}
