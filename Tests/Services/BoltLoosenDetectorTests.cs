using BearingFaultDiagnosis.Models;
using Xunit;

namespace BearingFaultDiagnosis.Tests.Services;

public class BoltLoosenDetectorTests
{
    private const int TiltDataLength = 500;

    #region 辅助方法

    /// <summary>生成模拟倾角数据：常值 + 噪声</summary>
    private static double[] GenerateTiltData(double baseDc, double noiseAmp = 0.001, int n = TiltDataLength)
    {
        var data = new double[n];
        var rng = new Random(42);
        for (int i = 0; i < n; i++)
            data[i] = baseDc + (rng.NextDouble() - 0.5) * noiseAmp;
        return data;
    }

    /// <summary>用常值基线完成标定</summary>
    private static void CompleteCalibration(BoltLoosenDetector detector,
        double baseDc = 0.05, double baseTemp = 25.0)
    {
        for (int i = 0; i < BoltLoosenDetector.CalibrationPeriods; i++)
        {
            // 温度在 24~26°C 范围变化，提供回归所需的方差
            double t = baseTemp + (i % 5 - 2) * 0.5;
            var tilt = GenerateTiltData(baseDc, 0.002);
            detector.Process(tilt, t);
        }
    }

    #endregion

    #region 标定阶段测试

    [Fact]
    public void Process_First199Periods_ReturnsCalibrating()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector);
        detector.ResetCalibration();

        for (int i = 0; i < 199; i++)
        {
            var tilt = GenerateTiltData(0.05);
            var result = detector.Process(tilt, 25.0);
            Assert.Equal(BoltAlarmState.Calibrating, result.AlarmState);
            Assert.False(result.IsCalibrated);
        }

        Assert.Equal(199, detector.CalibrationCount);
    }

    [Fact]
    public void Process_After200Periods_IsCalibrated()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector);

        Assert.True(detector.IsCalibrated);
        Assert.Equal(200, detector.CalibrationCount);
    }

    [Fact]
    public void Process_CalibrationCollectsTempPairs()
    {
        var detector = new BoltLoosenDetector();
        // 使用有温度相关性的信号标定
        for (int i = 0; i < BoltLoosenDetector.CalibrationPeriods; i++)
        {
            double temp = 20.0 + (i % 10);
            double theta = 0.002 * temp;
            var tilt = GenerateTiltData(theta, 0.0005);
            detector.Process(tilt, temp);
        }

        Assert.True(detector.IsTempCompensationActive,
            "温度补偿应在标定后激活");
    }

    #endregion

    #region 正常状态测试

    [Fact]
    public void Process_NoDrift_ReturnsNormal()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector, baseDc: 0.05);

        // 与标定基线一致的数据
        var tilt = GenerateTiltData(0.05, 0.001);
        var result = detector.Process(tilt, 25.0);

        Assert.True(result.IsCalibrated);
        Assert.Equal(BoltAlarmState.Normal, result.AlarmState);
        Assert.True(result.DriftDelta < BoltLoosenDetector.WarnThresholdDeg,
            $"Δθ={result.DriftDelta:F4} 应 < {BoltLoosenDetector.WarnThresholdDeg}°");
    }

    [Fact]
    public void Process_SmallDrift_StaysNormal()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector, baseDc: 0.05);

        // 漂移 0.08° < 0.1° 阈值 → Normal
        var tilt = GenerateTiltData(0.13, 0.001);
        var result = detector.Process(tilt, 25.0);

        Assert.True(result.IsCalibrated);
        Assert.NotEqual(BoltAlarmState.Alarm, result.AlarmState);
    }

    #endregion

    #region 报警检测测试

    [Fact]
    public void Process_LargeDriftAbove03_ReturnsAlarm()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector, baseDc: 0.05); // θ₀ ≈ 0.05°

        // 注入大幅偏移数据：θ_DC = 0.45° → Δθ = 0.40° > 0.3°
        // 需要填满滑动窗口 (183个值) 使窗口均值 > 0.35°
        var highTilt = GenerateTiltData(0.45, 0.001);
        BoltLoosenResult result = default!;
        for (int i = 0; i < 200; i++)
            result = detector.Process(highTilt, 25.0);

        Assert.Equal(BoltAlarmState.Alarm, result.AlarmState);
        Assert.True(result.DriftDelta > BoltLoosenDetector.AlarmThresholdDeg,
            $"Δθ={result.DriftDelta:F4} 应 > {BoltLoosenDetector.AlarmThresholdDeg}°");
    }

    #endregion

    #region 线性回归测试

    [Fact]
    public void ComputeLinearRegression_KnownSlope_ReturnsCorrectSlope()
    {
        // y = 0.5x + 2.0
        double[] x = { 0, 1, 2, 3, 4, 5 };
        double[] y = { 2.0, 2.5, 3.0, 3.5, 4.0, 4.5 };

        var (slope, intercept) = BoltLoosenDetector.ComputeLinearRegression(x, y);

        Assert.Equal(0.5, slope, precision: 6);
        Assert.Equal(2.0, intercept, precision: 6);
    }

    [Fact]
    public void ComputeLinearRegression_ConstantY_ReturnsZeroSlope()
    {
        double[] x = { 0, 1, 2, 3, 4 };
        double[] y = { 5.0, 5.0, 5.0, 5.0, 5.0 };

        var (slope, intercept) = BoltLoosenDetector.ComputeLinearRegression(x, y);

        Assert.Equal(0.0, slope, precision: 10);
        Assert.Equal(5.0, intercept, precision: 10);
    }

    [Fact]
    public void ComputeLinearRegression_SinglePoint_ReturnsZero()
    {
        double[] x = { 1.0 };
        double[] y = { 2.0 };

        var (slope, intercept) = BoltLoosenDetector.ComputeLinearRegression(x, y);

        Assert.Equal(0.0, slope);
        Assert.Equal(0.0, intercept);
    }

    [Fact]
    public void ComputeLinearRegression_NegativeSlope_ReturnsNegative()
    {
        // y = -0.3x + 10
        double[] x = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        double[] y = { 10, 9.7, 9.4, 9.1, 8.8, 8.5, 8.2, 7.9, 7.6, 7.3 };

        var (slope, intercept) = BoltLoosenDetector.ComputeLinearRegression(x, y);

        Assert.True(Math.Abs(slope - (-0.3)) < 1e-10, $"斜率={slope} 应 ≈ -0.3");
        Assert.True(Math.Abs(intercept - 10.0) < 1e-10, $"截距={intercept} 应 ≈ 10.0");
    }

    #endregion

    #region 滑动窗口测试

    [Fact]
    public void Process_SlidingWindowMean_CorrectValue()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector, baseDc: 0.05);

        // 注入恒定偏移数据，填满滑动窗口后 Δθ 应接近偏移量
        double offset = 0.05; // 从 0.05 偏移到 0.10 → Δθ ≈ 0.05
        var tilt = GenerateTiltData(0.05 + offset, 0.0001);

        BoltLoosenResult result = default!;
        for (int i = 0; i < 200; i++) // 200 > SlidingWindowSize(183)
            result = detector.Process(tilt, 25.0);

        // Δθ 应接近 0.05°
        Assert.True(Math.Abs(result.DriftDelta - 0.05) < 0.01,
            $"Δθ={result.DriftDelta:F4} 应接近 0.05°");
    }

    #endregion

    #region 温度补偿测试

    [Fact]
    public void Process_TempCompensation_ReducesDrift()
    {
        var detector = new BoltLoosenDetector();

        // 标定期：温度与倾角有线性关系 θ = 0.002·T + 0.0
        for (int i = 0; i < BoltLoosenDetector.CalibrationPeriods; i++)
        {
            double temp = 20.0 + (i % 10) * 1.0;
            double theta = 0.002 * temp;
            var tilt = GenerateTiltData(theta, 0.0005);
            detector.Process(tilt, temp);
        }

        Assert.True(detector.IsCalibrated);
        Assert.True(detector.IsTempCompensationActive, "温度补偿应已激活");

        // 注入温度升高的数据
        double hotTemp = 35.0;
        double hotTheta = 0.002 * hotTemp; // 0.07°
        var hotTilt = GenerateTiltData(hotTheta, 0.0005);
        var result = detector.Process(hotTilt, hotTemp);

        // 补偿后漂移应较小（温度变化被扣除）
        Assert.True(result.CompensatedDrift < 0.05,
            $"补偿漂移={result.CompensatedDrift:F4} 应较小 (温补生效)");
    }

    [Fact]
    public void Process_NoTemperature_SkipsCompensation()
    {
        var detector = new BoltLoosenDetector();

        // 标定时不提供温度 → 补偿不激活
        for (int i = 0; i < BoltLoosenDetector.CalibrationPeriods; i++)
        {
            var tilt = GenerateTiltData(0.05, 0.002);
            detector.Process(tilt, double.NaN);
        }

        Assert.True(detector.IsCalibrated);
        Assert.False(detector.IsTempCompensationActive,
            "无温度数据时补偿不应激活");

        // 后续也不提供温度 → 不补偿
        var result = detector.Process(GenerateTiltData(0.10), double.NaN);
        Assert.True(result.DriftDelta > 0.04,
            $"Δθ={result.DriftDelta:F4} 应反映真实偏移 (无温补)");
    }

    #endregion

    #region 重置测试

    [Fact]
    public void ResetCalibration_ClearsAllState()
    {
        var detector = new BoltLoosenDetector();
        CompleteCalibration(detector);
        Assert.True(detector.IsCalibrated);
        Assert.True(detector.IsTempCompensationActive);

        detector.ResetCalibration();

        Assert.False(detector.IsCalibrated);
        Assert.False(detector.IsTempCompensationActive);
        Assert.Equal(0, detector.CalibrationCount);
        Assert.Equal(BoltAlarmState.Calibrating, detector.CurrentState);

        var result = detector.Process(GenerateTiltData(0.05), 25.0);
        Assert.Equal(BoltAlarmState.Calibrating, result.AlarmState);
    }

    #endregion

    #region 7×24 运行测试

    [Fact]
    public void Process_FanStopped_StillRuns()
    {
        // BoltLoosenDetector.Process 不接受风机状态参数，
        // 内部无 _isFanRunning 检查，7×24 运行
        var detector = new BoltLoosenDetector();
        var result = detector.Process(GenerateTiltData(0.05), 25.0);

        Assert.Equal(BoltAlarmState.Calibrating, result.AlarmState);
        Assert.Equal(1, detector.CalibrationCount);
    }

    #endregion

    #region Welford 基线测试

    [Fact]
    public void Process_WelfordBaseline_ConvergesToMean()
    {
        var detector = new BoltLoosenDetector();

        // 用有变化性的信号标定：每期基线略有不同
        for (int i = 0; i < BoltLoosenDetector.CalibrationPeriods; i++)
        {
            double baseVal = 0.05 + (i % 5 - 2) * 0.005; // 0.04~0.06 循环
            var tilt = GenerateTiltData(baseVal, 0.001);
            double temp = 25.0 + (i % 3 - 1) * 0.5;
            detector.Process(tilt, temp);
        }

        Assert.True(detector.IsCalibrated);

        // 发送一帧获取结果
        var result = detector.Process(GenerateTiltData(0.05), 25.0);

        // 基线均值应接近 0.05°
        Assert.True(Math.Abs(result.BaselineTheta0 - 0.05) < 0.02,
            $"θ₀={result.BaselineTheta0:F4} 应接近 0.05°");
    }

    #endregion
}
