using BearingFaultDiagnosis.Models;
using Xunit;

namespace BearingFaultDiagnosis.Tests.Services;

public class BladeImbalanceDetectorTests
{
    private const double Fs = 5000.0;
    private const int N = 32768;
    private const double TargetFreq = BladeImbalanceDetector.TargetFreqHz; // 33.33 Hz

    #region 辅助方法

    /// <summary>生成纯正弦波时域信号</summary>
    private static double[] GeneratePureTone(double freq, double amplitude, int n, double fs)
    {
        var data = new double[n];
        for (int i = 0; i < n; i++)
            data[i] = amplitude * Math.Sin(2 * Math.PI * freq * i / fs);
        return data;
    }

    /// <summary>生成合成频谱 (单边幅值谱)，在指定 bin 处放置峰值</summary>
    private static double[] GenerateMockSpectrum(int n, params (int bin, double magnitude)[] peaks)
    {
        var spectrum = new double[n];
        foreach (var (bin, magnitude) in peaks)
        {
            if (bin >= 0 && bin < n)
                spectrum[bin] = magnitude;
        }
        return spectrum;
    }

    /// <summary>计算 1×fr 对应的 bin 索引</summary>
    private static int Bin1x() => (int)Math.Round(TargetFreq * N / Fs);

    /// <summary>计算 2×fr 对应的 bin 索引</summary>
    private static int Bin2x() => (int)Math.Round(TargetFreq * 2 * N / Fs);

    #endregion

    #region 标定阶段测试

    [Fact]
    public void Process_First30Periods_ReturnsCalibrating()
    {
        var detector = new BladeImbalanceDetector();
        var rawData = GeneratePureTone(TargetFreq, 1.0, N, Fs);
        var spectrum = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 1.0), (Bin2x(), 0.1));

        for (int i = 0; i < 29; i++)
        {
            var result = detector.Process(spectrum, rawData, Fs);
            Assert.Equal(BladeAlarmState.Calibrating, result.AlarmState);
            Assert.False(result.IsCalibrated);
        }

        Assert.Equal(29, detector.CalibrationCount);
    }

    [Fact]
    public void Process_After30Periods_IsCalibrated()
    {
        var detector = new BladeImbalanceDetector();
        var rawData = GeneratePureTone(TargetFreq, 1.0, N, Fs);
        var spectrum = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 1.0), (Bin2x(), 0.1));

        for (int i = 0; i < 30; i++)
            detector.Process(spectrum, rawData, Fs);

        Assert.True(detector.IsCalibrated);
        Assert.Equal(30, detector.CalibrationCount);
    }

    #endregion

    #region 正常状态测试

    [Fact]
    public void Process_NormalVibration_ReturnsNormal()
    {
        var detector = new BladeImbalanceDetector();
        // 标定数据：A1 有微小波动 (0.95~1.05)，确保 σ > 0，建立合理阈值带
        var rawData = GeneratePureTone(TargetFreq, 1.0, N, Fs);
        BladeImbalanceResult result = default!;
        for (int i = 0; i < 30; i++)
        {
            double amp = 1.0 + (i % 3 - 1) * 0.05; // 0.95, 1.0, 1.05 循环
            var spec = GenerateMockSpectrum(N / 2 + 1,
                (Bin1x(), amp),
                (Bin2x(), 0.4 * amp)); // R21 ≈ 0.4 > 0.3 阈值
            result = detector.Process(spec, rawData, Fs);
        }

        Assert.True(result.IsCalibrated);

        // 后续数据与标定基线一致：A1=1.0, R21=0.4 → 不应触发报警
        var normalSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 1.0), (Bin2x(), 0.4));
        result = detector.Process(normalSpec, rawData, Fs);

        Assert.True(result.IsCalibrated);
        Assert.NotEqual(BladeAlarmState.Alarm, result.AlarmState);
    }

    #endregion

    #region 报警检测测试

    [Fact]
    public void Process_ImbalanceDetected_ReturnsAlarmAfterConsecutivePeriods()
    {
        var detector = new BladeImbalanceDetector();
        var normalRaw = GeneratePureTone(TargetFreq, 0.5, N, Fs);
        var normalSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 0.5), (Bin2x(), 0.05));

        // 标定阶段：用低幅值数据
        for (int i = 0; i < 30; i++)
            detector.Process(normalSpec, normalRaw, Fs);

        // 注入不平衡数据：1×fr 幅值大幅增加，R21 < 0.3，相位稳定
        var faultRaw = GeneratePureTone(TargetFreq, 5.0, N, Fs);
        var faultSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 5.0), (Bin2x(), 0.5));

        BladeImbalanceResult? lastResult = null;
        int consecutiveRequired = -1;
        for (int i = 0; i < 10; i++)
        {
            lastResult = detector.Process(faultSpec, faultRaw, Fs);
            consecutiveRequired = lastResult.ConsecutiveRequired;
        }

        Assert.NotNull(lastResult);
        // 经过足够多周期后应达到 Alarm
        Assert.Equal(BladeAlarmState.Alarm, lastResult.AlarmState);
        Assert.True(lastResult.ConsecutiveCount >= consecutiveRequired);
    }

    [Fact]
    public void Process_HighR21_DoesNotAlarm()
    {
        var detector = new BladeImbalanceDetector();
        var normalRaw = GeneratePureTone(TargetFreq, 0.5, N, Fs);
        var normalSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 0.5), (Bin2x(), 0.05));

        for (int i = 0; i < 30; i++)
            detector.Process(normalSpec, normalRaw, Fs);

        // 高 R21 数据 (模拟不对中): A2/A1 > 0.3 → 不满足 R21 < 0.3 条件
        var misalignRaw = GeneratePureTone(TargetFreq, 5.0, N, Fs);
        var misalignSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 5.0), (Bin2x(), 3.0));

        for (int i = 0; i < 10; i++)
        {
            var result = detector.Process(misalignSpec, misalignRaw, Fs);
            Assert.NotEqual(BladeAlarmState.Alarm, result.AlarmState);
        }
    }

    #endregion

    #region 峰值搜索测试

    [Fact]
    public void PeakSearch_FindsMaxInRange()
    {
        var spectrum = new double[100];
        spectrum[20] = 1.0;
        spectrum[21] = 3.0;  // 峰值在 bin 21
        spectrum[22] = 2.0;

        double peak = BladeImbalanceDetector.PeakSearch(spectrum, 21, 2);
        Assert.Equal(3.0, peak);

        // 峰值在搜索范围边缘
        peak = BladeImbalanceDetector.PeakSearch(spectrum, 19, 2);
        Assert.Equal(3.0, peak);
    }

    [Fact]
    public void PeakSearch_BoundsCheck()
    {
        var spectrum = new double[10];
        spectrum[0] = 5.0;

        // centerIdx=0, range=2 → 搜索 bin 0~2
        double peak = BladeImbalanceDetector.PeakSearch(spectrum, 0, 2);
        Assert.Equal(5.0, peak);

        // centerIdx=8, range=5 → 搜索 bin 3~9，峰值在 bin 5
        spectrum[5] = 3.0;
        peak = BladeImbalanceDetector.PeakSearch(spectrum, 8, 5);
        Assert.Equal(3.0, peak);

        // 完全超出范围 → 搜索不到 bin 0
        peak = BladeImbalanceDetector.PeakSearch(spectrum, 9, 1);
        Assert.Equal(0.0, peak);
    }

    #endregion

    #region Goertzel 相位测试

    [Fact]
    public void ComputePhaseGoertzel_PureTone_ReturnsConsistentPhase()
    {
        var data = GeneratePureTone(TargetFreq, 1.0, N, Fs);

        double phase1 = BladeImbalanceDetector.ComputePhaseGoertzel(data, Fs, TargetFreq);
        double phase2 = BladeImbalanceDetector.ComputePhaseGoertzel(data, Fs, TargetFreq);

        // 相同数据应返回相同相位
        Assert.Equal(phase1, phase2, precision: 10);
    }

    [Fact]
    public void ComputePhaseGoertzel_PhaseShift_Detected()
    {
        // 信号 1: sin(2π·fr·t)
        var data1 = new double[N];
        for (int i = 0; i < N; i++)
            data1[i] = Math.Sin(2 * Math.PI * TargetFreq * i / Fs);

        // 信号 2: sin(2π·fr·t + π/2) → 相位偏移 90°
        var data2 = new double[N];
        for (int i = 0; i < N; i++)
            data2[i] = Math.Sin(2 * Math.PI * TargetFreq * i / Fs + Math.PI / 2);

        double phase1 = BladeImbalanceDetector.ComputePhaseGoertzel(data1, Fs, TargetFreq);
        double phase2 = BladeImbalanceDetector.ComputePhaseGoertzel(data2, Fs, TargetFreq);

        // 相位差应接近 90°
        double diff = Math.Abs(phase2 - phase1);
        if (diff > 180) diff = 360 - diff; // 处理环绕
        Assert.True(diff > 80 && diff < 100, $"相位差 {diff:F1}° 应接近 90°");
    }

    #endregion

    #region 圆周标准差测试

    [Fact]
    public void ComputeCircularStdDev_IdenticalPhases_ReturnsZero()
    {
        var phases = new Queue<double>(new[] { 45.0, 45.0, 45.0, 45.0, 45.0 });
        double std = BladeImbalanceDetector.ComputeCircularStdDev(phases);
        Assert.True(std < 0.1, $"相同相位的标准差应接近 0，实际 {std:F4}");
    }

    [Fact]
    public void ComputeCircularStdDev_WrapAround_HandledCorrectly()
    {
        // -179° 和 179° 实际相差 2°，不是 358°
        var phases = new Queue<double>(new[] { -179.0, 179.0, -178.0, 178.0 });
        double std = BladeImbalanceDetector.ComputeCircularStdDev(phases);
        // 圆周标准差应很小 (约 1°)
        Assert.True(std < 5.0, $"环绕情况下标准差应较小，实际 {std:F4}°");
    }

    [Fact]
    public void ComputeCircularStdDev_SmallSpread_ReturnsSmallValue()
    {
        // 相位在 30°±5° 范围波动
        var phases = new Queue<double>(new[] { 25.0, 28.0, 32.0, 35.0, 30.0 });
        double std = BladeImbalanceDetector.ComputeCircularStdDev(phases);
        Assert.True(std < 15.0, $"小波动标准差应 < 15°，实际 {std:F4}°");
    }

    [Fact]
    public void ComputeCircularStdDev_SingleValue_ReturnsZero()
    {
        var phases = new Queue<double>(new[] { 42.0 });
        double std = BladeImbalanceDetector.ComputeCircularStdDev(phases);
        Assert.Equal(0, std);
    }

    #endregion

    #region EWMA 测试

    [Fact]
    public void Process_Ewma_SmoothsCorrectly()
    {
        var detector = new BladeImbalanceDetector();
        var rawData = GeneratePureTone(TargetFreq, 1.0, N, Fs);

        // 用恒定幅值频谱完成标定
        var spec1 = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 1.0), (Bin2x(), 0.1));
        for (int i = 0; i < 30; i++)
            detector.Process(spec1, rawData, Fs);

        // 标定结束后再喂一帧，EWMA 应与 A₁ 接近
        var result = detector.Process(spec1, rawData, Fs);
        Assert.True(Math.Abs(result.EwmaAmplitude - 1.0) < 0.2,
            $"EWMA={result.EwmaAmplitude:F4} 应接近 1.0");
    }

    #endregion

    #region 连续计数与重置测试

    [Fact]
    public void Process_ConditionBreak_ResetsConsecutiveCount()
    {
        var detector = new BladeImbalanceDetector();
        // 正常数据：A1=0.5, A2=0.2 → R21=0.4 > 0.3 阈值，不满足不平衡条件
        var normalRaw = GeneratePureTone(TargetFreq, 0.5, N, Fs);
        var normalSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 0.5), (Bin2x(), 0.2));

        for (int i = 0; i < 30; i++)
            detector.Process(normalSpec, normalRaw, Fs);

        // 注入不平衡数据 (连续 2 期): A1=5.0, A2=0.5 → R21=0.1 < 0.3
        var faultRaw = GeneratePureTone(TargetFreq, 5.0, N, Fs);
        var faultSpec = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 5.0), (Bin2x(), 0.5));

        detector.Process(faultSpec, faultRaw, Fs);
        detector.Process(faultSpec, faultRaw, Fs);
        Assert.True(detector.ConsecutiveCount >= 1);

        // 恢复正常数据 → R21=0.4 > 0.3 阈值 → 条件打破 → consecutiveCount 归零
        var result = detector.Process(normalSpec, normalRaw, Fs);
        Assert.Equal(0, result.ConsecutiveCount);
    }

    [Fact]
    public void ResetCalibration_ClearsAllState()
    {
        var detector = new BladeImbalanceDetector();
        var rawData = GeneratePureTone(TargetFreq, 1.0, N, Fs);
        var spectrum = GenerateMockSpectrum(N / 2 + 1, (Bin1x(), 1.0), (Bin2x(), 0.1));

        // 完成标定
        for (int i = 0; i < 30; i++)
            detector.Process(spectrum, rawData, Fs);

        Assert.True(detector.IsCalibrated);

        // 重置
        detector.ResetCalibration();

        Assert.False(detector.IsCalibrated);
        Assert.Equal(0, detector.CalibrationCount);
        Assert.Equal(0, detector.ConsecutiveCount);
        Assert.Equal(BladeAlarmState.Calibrating, detector.CurrentState);

        // 重置后第一次调用应返回 Calibrating
        var result = detector.Process(spectrum, rawData, Fs);
        Assert.Equal(BladeAlarmState.Calibrating, result.AlarmState);
    }

    #endregion
}
