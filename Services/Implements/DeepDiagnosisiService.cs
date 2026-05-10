using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Models;
using BearingFaultDiagnosis.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BearingFaultDiagnosis.Services.Implements
{
    public class DeepDiagnosisService : IDeepDiagnosisService
    {
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

        public DeepDiagnosisService(IDbContextFactory<AppDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

         // 确保此处名称与 C++ 项目输出的 DLL 文件名完全一致
        private const string DllName = "HighPerformanceComputing.dll";

        // 1. 新增 FFTW 预初始化接口（必须调用一次，否则实时渲染必卡）
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void InitFFTW();

        // 2. 更新签名：增加 out_capacity 防止 C++ 端缓冲区溢出
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ComputeOrderTracking(
            IntPtr in_data,
            int in_len,
            double current_fs,
            double rpm,
            int target_spr,
            IntPtr out_data,
            out int out_len,
            int out_capacity // 新增安全参数
        );

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ComputeSpectrum(
            IntPtr in_data,
            int in_len,
            IntPtr out_mag
        );

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void InitCWT(int signal_len, int num_scales, int num_time_bins);

        [DllImport(DllName, EntryPoint = "ComputeCWT", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ComputeCWTNative(
            IntPtr signal,
            int signal_len,
            double fs,
            double freq_low,
            double freq_high,
            IntPtr out_matrix,
            int num_scales,
            int num_time_bins
        );

        // 静态构造函数：DLL 加载后自动执行一次，缓存 FFTW Plan
        static DeepDiagnosisService()
        {
            try
            {
                InitFFTW();
                InitCWT(32768, 128, 512);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("FFTW 初始化失败，请检查 DLL 路径或 libfftw3 依赖", ex);
            }
        }

        public double[] ProcessChunk(double[] rawData, double fs, double rpm, int targetSpr)
        {
            if (rawData == null || rawData.Length == 0 || rpm <= 1e-5)
                return Array.Empty<double>();

            int inLen = rawData.Length;

            // 动态计算实际需要的点数，替代硬编码 3000 RPM 的不安全估算
            double duration = inLen / fs;
            int estimatedPoints = (int)((rpm / 60.0) * duration * targetSpr) + 10;

            // 分配安全缓冲区（至少不小于输入长度，防极端工况溢出）
            int capacity = Math.Max(estimatedPoints, inLen);
            double[] outDataBuffer = new double[capacity];
            int actualOutLen = 0;

            unsafe
            {
                fixed (double* pIn = rawData)
                fixed (double* pOut = outDataBuffer)
                {
                    ComputeOrderTracking(
                        (IntPtr)pIn, inLen, fs, rpm, targetSpr,
                        (IntPtr)pOut, out actualOutLen, capacity
                    );
                }
            }

            if (actualOutLen <= 0) return Array.Empty<double>();

            // 截取有效数据（此处拷贝极小，兼容现有 ConcurrentQueue<double[]> 架构）
            double[] result = new double[actualOutLen];
            Array.Copy(outDataBuffer, result, actualOutLen);
            return result;
        }

        public double[] ComputeSpectrum(double[] inData)
        {
            if (inData == null || inData.Length == 0) return Array.Empty<double>();

            int inLen = inData.Length;
            // 修正：单边谱应包含 DC 和 Nyquist 点，长度为 N/2 + 1
            int outLen = (inLen / 2) + 1;
            double[] outMag = new double[outLen];

            unsafe
            {
                fixed (double* pIn = inData)
                fixed (double* pOut = outMag)
                {
                    ComputeSpectrum((IntPtr)pIn, inLen, (IntPtr)pOut);
                }
            }

            return outMag;
        }

        public double[] ComputeCWT(double[] rawData, double fs, double freqLow, double freqHigh,
                                    int numScales, int numTimeBins)
        {
            if (rawData == null || rawData.Length == 0)
                return Array.Empty<double>();

            double[] result = new double[numScales * numTimeBins];

            unsafe
            {
                fixed (double* pIn = rawData)
                fixed (double* pOut = result)
                {
                    ComputeCWTNative(
                        (IntPtr)pIn, rawData.Length, fs, freqLow, freqHigh,
                        (IntPtr)pOut, numScales, numTimeBins);
                }
            }

            return result;
        }
        public async Task<List<BearingInfo>> GetBearingListAsync()
        {
            using var context = await _dbContextFactory.CreateDbContextAsync();
            return await context.BearingInfos.OrderBy(x => x.Manufacturer).ThenBy(x => x.Model).ToListAsync();
        }

        public BearingFaultResult CalculateFaultFrequencies(BearingInfo bearing, double rpm)
        {
            double shaftHz = rpm / 60.0;

            // 优先使用数据库已存系数
            if (bearing.BPFO_Multiplier.HasValue && bearing.BPFI_Multiplier.HasValue)
            {
                return new BearingFaultResult
                {
                    BPFO_Hz = bearing.BPFO_Multiplier.Value * shaftHz,
                    BPFI_Hz = bearing.BPFI_Multiplier.Value * shaftHz,
                    BSF_Hz = (bearing.BSF_Multiplier ?? 0) * shaftHz,
                    FTF_Hz = (bearing.FTF_Multiplier ?? 0) * shaftHz,
                    IsCalculated = false
                };
            }

            // 无系数但有几何参数 → 物理公式推算
            if (bearing.RollerDiameter_d.HasValue && bearing.PitchDiameter_D.HasValue && bearing.PitchDiameter_D.Value > 0)
            {
                double d = bearing.RollerDiameter_d.Value;
                double D = bearing.PitchDiameter_D.Value;
                double n = bearing.RollerCount_n;
                double cosA = Math.Cos(bearing.ContactAngle_alpha * Math.PI / 180.0);
                double ratio = d / D * cosA;

                double ftf = (1.0 - ratio) / 2.0;
                double bpfo = n * ftf;
                double bpfi = n * (1.0 + ratio) / 2.0;
                double bsf = (D / d) * (1.0 - ratio * ratio) / 2.0;

                return new BearingFaultResult
                {
                    BPFO_Hz = bpfo * shaftHz,
                    BPFI_Hz = bpfi * shaftHz,
                    BSF_Hz = bsf * shaftHz,
                    FTF_Hz = ftf * shaftHz,
                    IsCalculated = true
                };
            }

            return new BearingFaultResult();
        }

    }
}