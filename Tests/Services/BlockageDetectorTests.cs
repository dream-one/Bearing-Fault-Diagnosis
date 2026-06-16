using BearingFaultDiagnosis.Models;
using Xunit;

namespace BearingFaultDiagnosis.Tests.Services;

public class BlockageDetectorTests
{
    private const double Fs = 5000.0;
    private const int N = 1024;
    /// <summary>模拟真实 ProcessLoop 迭代周期 (~3.277s)</summary>
    private const double MonitoringPeriod = 3.277;

    #region 辅助方法

    /// <summary>生成 base + noise 的模拟电流信号</summary>
    private static double[] GenerateCurrentSignal(double baseAmp, double noiseAmp, int n, double fs)
    {
        var data = new double[n];
        var rng = new Random(42); // 固定种子，确保可重复
        for (int i = 0; i < n; i++)
            data[i] = baseAmp + (rng.NextDouble() - 0.5) * noiseAmp;
        return data;
    }

    /// <summary>生成纯正弦叠加直流的信号</summary>
    private static double[] GenerateSineOnDc(double dc, double amp, double freq, int n, double fs)
    {
        var data = new double[n];
        for (int i = 0; i < n; i++)
            data[i] = dc + amp * Math.Sin(2 * Math.PI * freq * i / fs);
        return data;
    }

    /// <summary>用恒定基线信号完成标定（快速填充 720 期，σ≈0 由 MinSigma=0.1 保护）</summary>
    private static void CompleteCalibration(BlockageDetector detector, double baseAmp = 15.0)
    {
        var signal = Enumerable.Repeat(baseAmp, N).ToArray();
        for (int i = 0; i < BlockageDetector.CalibrationPeriods; i++)
            detector.Process(signal, Fs, MonitoringPeriod);
    }

    #endregion

    #region 标定阶段测试

    [Fact]
    public void Process_First719Periods_ReturnsCalibrating()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector);
        // 重置后重新计数到 719
        detector.ResetCalibration();
        var signal = Enumerable.Repeat(15.0, N).ToArray();

        for (int i = 0; i < 719; i++)
        {
            var result = detector.Process(signal, Fs, MonitoringPeriod);
            Assert.Equal(BlockageAlarmState.Calibrating, result.AlarmState);
            Assert.False(result.IsCalibrated);
        }

        Assert.Equal(719, detector.CalibrationCount);
    }

    [Fact]
    public void Process_After720Periods_IsCalibrated()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector);

        Assert.True(detector.IsCalibrated);
        Assert.Equal(720, detector.CalibrationCount);
    }

    #endregion

    #region RMS 计算测试

    [Fact]
    public void ComputeRms_ConstantSignal_ReturnsAmplitude()
    {
        var data = Enumerable.Repeat(15.0, 1000).ToArray();
        double rms = BlockageDetector.ComputeRms(data);
        Assert.Equal(15.0, rms, precision: 6);
    }

    [Fact]
    public void ComputeRms_SineOnDc_ReturnsCorrectRms()
    {
        // DC=15, sin amp=2, RMS = sqrt(15^2 + 2^2/2) = sqrt(227)
        var data = GenerateSineOnDc(15.0, 2.0, 50.0, 10000, Fs);
        double rms = BlockageDetector.ComputeRms(data);
        double expected = Math.Sqrt(15.0 * 15.0 + 2.0 * 2.0 / 2.0); // sqrt(227) ≈ 15.066
        Assert.True(Math.Abs(rms - expected) < 0.1,
            $"RMS={rms:F4} 应接近 {expected:F4}");
    }

    [Fact]
    public void ComputeRms_ZeroSignal_ReturnsZero()
    {
        var data = new double[100];
        double rms = BlockageDetector.ComputeRms(data);
        Assert.Equal(0.0, rms, precision: 10);
    }

    [Fact]
    public void ComputeRms_EmptySignal_ReturnsZero()
    {
        var data = Array.Empty<double>();
        double rms = BlockageDetector.ComputeRms(data);
        Assert.Equal(0.0, rms);
    }

    #endregion

    #region 正常状态测试

    [Fact]
    public void Process_NormalCurrent_ReturnsNormal()
    {
        var detector = new BlockageDetector();
        // 标定：常值信号 → μ=15.0, σ≈0 (MinSigma=0.1 保护)
        CompleteCalibration(detector);

        // 正常电流：与标定基线一致 → ΔI ≈ 0
        var normalSignal = Enumerable.Repeat(15.0, N).ToArray();
        var result = detector.Process(normalSignal, Fs, MonitoringPeriod);

        Assert.True(result.IsCalibrated);
        Assert.Equal(BlockageAlarmState.Normal, result.AlarmState);
        Assert.Equal(0, result.ConsecutiveCount);
    }

    [Fact]
    public void Process_SlightlyAboveBaseline_StaysNormal()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector); // μ=15.0, effectiveSigma=0.1

        // 电流偏移 0.25A → ΔI = 0.25/0.1 = 2.5 < 3 → Normal
        var signal = Enumerable.Repeat(15.25, N).ToArray();
        var result = detector.Process(signal, Fs, MonitoringPeriod);

        Assert.True(result.IsCalibrated);
        Assert.NotEqual(BlockageAlarmState.Alarm, result.AlarmState);
    }

    #endregion

    #region 报警检测测试

    [Fact]
    public void Process_SustainedHighCurrent_ReturnsAlarmAfter60s()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector); // μ=15.0, effectiveSigma=0.1

        // 注入高电流：base=18A → EWMA 首帧 ≈ 15.45, ΔI ≈ 4.5 >> 3
        var highSignal = Enumerable.Repeat(18.0, N).ToArray();
        int consecutiveRequired = -1;
        BlockageResult? lastResult = null;

        // consecutiveRequired = ceil(60/3.277) = 19
        for (int i = 0; i < 25; i++)
        {
            lastResult = detector.Process(highSignal, Fs, MonitoringPeriod);
            consecutiveRequired = lastResult.ConsecutiveRequired;
        }

        Assert.NotNull(lastResult);
        Assert.True(consecutiveRequired >= 18, $"consecutiveRequired={consecutiveRequired} 应接近 19");
        Assert.Equal(BlockageAlarmState.Alarm, lastResult.AlarmState);
        Assert.True(lastResult.ExceedDurationSeconds >= 60.0,
            $"超限时长={lastResult.ExceedDurationSeconds:F1}s 应 ≥ 60s");
    }

    [Fact]
    public void Process_TransientSpike_ReturnsWarningNotAlarm()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector); // μ=15.0

        // 注入高电流 5 期 (< 19 期要求), base=18A 确保首帧即超限
        var highSignal = Enumerable.Repeat(18.0, N).ToArray();
        BlockageResult? lastResult = null;
        for (int i = 0; i < 5; i++)
            lastResult = detector.Process(highSignal, Fs, MonitoringPeriod);

        Assert.NotNull(lastResult);
        Assert.True(lastResult.ConsecutiveCount >= 4,
            $"连续计数={lastResult.ConsecutiveCount} 应 ≥ 4 (EWMA 延迟可能丢失首帧)");
        Assert.NotEqual(BlockageAlarmState.Alarm, lastResult.AlarmState);
    }

    #endregion

    #region 连续计数测试

    [Fact]
    public void Process_ConditionBreak_ResetsConsecutiveCount()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector); // μ=15.0

        // 注入高电流 10 期 → Warning (base=18A 确保每帧超限)
        var highSignal = Enumerable.Repeat(18.0, N).ToArray();
        for (int i = 0; i < 10; i++)
            detector.Process(highSignal, Fs, MonitoringPeriod);

        Assert.True(detector.ConsecutiveCount >= 9,
            $"连续计数={detector.ConsecutiveCount} 应 ≥ 9");

        // 恢复正常电流：EWMA 从 ~17.4 回落到 <15.3 需要多期
        // EWMA(n) = 15 + 2.41*0.85^n, 需要 0.85^n < 0.1245 → n > 36
        var normalSignal = Enumerable.Repeat(15.0, N).ToArray();
        BlockageResult result = default!;
        for (int i = 0; i < 50; i++)
            result = detector.Process(normalSignal, Fs, MonitoringPeriod);

        Assert.Equal(0, result.ConsecutiveCount);
        Assert.Equal(BlockageAlarmState.Normal, result.AlarmState);
    }

    [Fact]
    public void Process_ExceedDuration_IncrementsCorrectly()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector); // μ=15.0

        // base=18A 确保首帧即超限
        var highSignal = Enumerable.Repeat(18.0, N).ToArray();

        // 第 1 期超限
        var r1 = detector.Process(highSignal, Fs, MonitoringPeriod);
        Assert.Equal(1, r1.ConsecutiveCount);
        Assert.True(Math.Abs(r1.ExceedDurationSeconds - MonitoringPeriod) < 0.01,
            $"超限时长={r1.ExceedDurationSeconds:F3} 应接近 {MonitoringPeriod}");

        // 第 2 期
        var r2 = detector.Process(highSignal, Fs, MonitoringPeriod);
        Assert.Equal(2, r2.ConsecutiveCount);
        Assert.True(Math.Abs(r2.ExceedDurationSeconds - 2 * MonitoringPeriod) < 0.01);

        // 第 6 期
        for (int i = 0; i < 4; i++)
            detector.Process(highSignal, Fs, MonitoringPeriod);
        var r6 = detector.Process(highSignal, Fs, MonitoringPeriod);
        Assert.Equal(7, r6.ConsecutiveCount); // 2 + 4 + 1 = 7
    }

    #endregion

    #region Welford 基线测试

    [Fact]
    public void Process_WelfordBaseline_ConvergesToMeanAndStdDev()
    {
        var detector = new BlockageDetector();
        // 使用有变化性的信号：每期基线略有不同
        for (int i = 0; i < BlockageDetector.CalibrationPeriods; i++)
        {
            double baseVal = 15.0 + (i % 5 - 2) * 0.1; // 14.8, 14.9, 15.0, 15.1, 15.2 循环
            var signal = Enumerable.Repeat(baseVal, N).ToArray();
            detector.Process(signal, Fs, MonitoringPeriod);
        }

        Assert.True(detector.IsCalibrated);
        // 发送最后一帧获取结果
        var lastSignal = Enumerable.Repeat(15.0, N).ToArray();
        var result = detector.Process(lastSignal, Fs, MonitoringPeriod);

        // 均值应接近 15.0
        Assert.True(Math.Abs(result.BaselineMean - 15.0) < 0.5,
            $"μ={result.BaselineMean:F4} 应接近 15.0");
        // 标准差应 > 0（基线有变化性）
        Assert.True(result.BaselineStdDev > 0.01,
            $"σ={result.BaselineStdDev:F4} 应 > 0");
    }

    #endregion

    #region 重置测试

    [Fact]
    public void ResetCalibration_ClearsAllState()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector);

        Assert.True(detector.IsCalibrated);

        // 重置
        detector.ResetCalibration();

        Assert.False(detector.IsCalibrated);
        Assert.Equal(0, detector.CalibrationCount);
        Assert.Equal(0, detector.ConsecutiveCount);
        Assert.Equal(BlockageAlarmState.Calibrating, detector.CurrentState);

        // 重置后第一次调用应返回 Calibrating
        var signal = GenerateCurrentSignal(15.0, 0.3, N, Fs);
        var result = detector.Process(signal, Fs, MonitoringPeriod);
        Assert.Equal(BlockageAlarmState.Calibrating, result.AlarmState);
    }

    #endregion

    #region 双侧检测测试

    [Fact]
    public void Process_CurrentDropsBelow_ReturnsWarningOrAlarm()
    {
        var detector = new BlockageDetector();
        CompleteCalibration(detector); // μ=15.0, effectiveSigma=0.1

        // 电流大幅下降：base=12.0A → EWMA 首帧 ≈ 14.55, ΔI ≈ -4.5, |ΔI|=4.5 > 3
        var lowSignal = Enumerable.Repeat(12.0, N).ToArray();
        var result = detector.Process(lowSignal, Fs, MonitoringPeriod);

        Assert.True(Math.Abs(result.DeviationDeltaI) > 3.0,
            $"|ΔI|={Math.Abs(result.DeviationDeltaI):F2} 应 > 3");
        Assert.True(result.ConsecutiveCount >= 1);
    }

    #endregion

    #region 最小标准差保护测试

    [Fact]
    public void Process_MinSigmaProtection_PreventsOverSensitivity()
    {
        var detector = new BlockageDetector();
        // 用完全恒定信号标定 → σ_I 理论为 0
        var constantSignal = Enumerable.Repeat(15.0, N).ToArray();
        for (int i = 0; i < BlockageDetector.CalibrationPeriods; i++)
            detector.Process(constantSignal, Fs, MonitoringPeriod);

        Assert.True(detector.IsCalibrated);

        // 微小偏移 0.2A → ΔI = 0.2/0.1(MinSigma) = 2.0 < 3 → Normal
        var slightlyOff = Enumerable.Repeat(15.2, N).ToArray();
        var result = detector.Process(slightlyOff, Fs, MonitoringPeriod);

        // |ΔI| = 2.0 < 3 → 不超限
        Assert.True(Math.Abs(result.DeviationDeltaI) < 3.0,
            $"ΔI={result.DeviationDeltaI:F2} 应 < 3 (MinSigma 保护生效)");
    }

    #endregion
}
