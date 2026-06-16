using System;
using System.Collections.Generic;
using System.Linq;

namespace BearingFaultDiagnosis.Models;

/// <summary>螺栓松动报警状态</summary>
public enum BoltAlarmState
{
    Calibrating, // 基线收集中
    Normal,      // Δθ ≤ Δθ₁
    Warning,     // Δθ > Δθ₁ 且 dθ/dt > 0
    Alarm        // Δθ > Δθ₂
}

/// <summary>单次螺栓松动监测结果快照</summary>
public sealed record BoltLoosenResult
{
    /// <summary>θ_DC — 本批次的静态倾角分量 (°)</summary>
    public double ThetaDc { get; init; }
    /// <summary>θ₀ — 标定基线均值 (°)</summary>
    public double BaselineTheta0 { get; init; }
    /// <summary>Δθ — 滑动窗口均值与基线的绝对偏差 (°)</summary>
    public double DriftDelta { get; init; }
    /// <summary>dθ/dt — 7 天线性回归斜率 (°/天)</summary>
    public double DriftRate { get; init; }
    /// <summary>温度补偿系数 a (°/°C)</summary>
    public double TempCoeffA { get; init; }
    /// <summary>温度补偿截距 b (°)</summary>
    public double TempCoeffB { get; init; }
    /// <summary>补偿后漂移 Δθ_real (°)</summary>
    public double CompensatedDrift { get; init; }
    /// <summary>滑动窗口内样本数</summary>
    public int SlidingWindowCount { get; init; }
    /// <summary>已收集标定期数</summary>
    public int CalibrationCount { get; init; }
    /// <summary>是否已完成基线标定</summary>
    public bool IsCalibrated { get; init; }
    /// <summary>温度补偿是否已激活</summary>
    public bool IsTempCompensationActive { get; init; }
    /// <summary>回归点数量 (小时均值)</summary>
    public int RegressionPointCount { get; init; }
    /// <summary>报警状态</summary>
    public BoltAlarmState AlarmState { get; init; }
}

/// <summary>
/// 安装螺栓松动监测算法核心检测器。
/// 基于倾角漂移检测：Welford 在线基线标定、10分钟滑动窗口均值、
/// 7 天小时降采样线性回归趋势、温度补偿线性回归、两级阈值判定。
/// 7×24 全时运行，不依赖风机运行状态。
/// 线程安全：仅在 ProcessLoopAsync 的单一 Task 中调用，无并发。
/// </summary>
public sealed class BoltLoosenDetector
{
    #region 配置常量

    /// <summary>自动标定周期数 (测试值 200, 生产环境 ~26000 对应 ≥24h)</summary>
    public const int CalibrationPeriods = 200;

    /// <summary>滑动窗口大小 (~10 min at 3.277s/iter = 183)</summary>
    private const int SlidingWindowSize = 183;

    /// <summary>每小时采样数 (~1099 iterations/hour at 3.277s)</summary>
    private const int SamplesPerHour = 1099;

    /// <summary>最大回归点数 (7 days × 24 hours = 168)</summary>
    private const int MaxRegressionPoints = 168;

    /// <summary>预警阈值 Δθ₁ (°)</summary>
    public const double WarnThresholdDeg = 0.1;

    /// <summary>报警阈值 Δθ₂ (°)</summary>
    public const double AlarmThresholdDeg = 0.3;

    /// <summary>启用温度补偿的最小标定样本数</summary>
    private const int MinCalibrationForCompensation = 50;

    #endregion

    #region 状态字段

    // Welford 在线基线标定
    private int _calibrationCount;
    private double _baselineMu;         // θ₀ 均值
    private double _baselineSigma = 1e6;
    private double _baselineM2;

    // 滑动窗口 (10 min)
    private readonly Queue<double> _slidingWindow = new();

    // 小时降采样 + 7天回归
    private int _hourSampleCounter;
    private double _hourSum;
    private readonly Queue<(double hourIndex, double value)> _hourlyHistory = new();
    private double _hourIndex;          // 全局小时计数器，用作回归 X 轴

    // 温度补偿线性回归
    private double _tempCoeffA;         // 斜率 a (°/°C)
    private double _tempCoeffB;         // 截距 b (°)
    private bool _tempCompensationReady;
    private readonly Queue<double> _calibTiltHistory = new();
    private readonly Queue<double> _calibTempHistory = new();

    #endregion

    #region 公共属性

    /// <summary>是否已完成基线标定</summary>
    public bool IsCalibrated => _calibrationCount >= CalibrationPeriods;

    /// <summary>当前报警状态</summary>
    public BoltAlarmState CurrentState { get; private set; } = BoltAlarmState.Calibrating;

    /// <summary>已收集标定期数</summary>
    public int CalibrationCount => _calibrationCount;

    /// <summary>标定目标周期数</summary>
    public int CalibrationPeriodsRequired => CalibrationPeriods;

    /// <summary>温度补偿是否激活</summary>
    public bool IsTempCompensationActive => _tempCompensationReady;

    #endregion

    /// <summary>
    /// 处理一帧倾角数据，返回监测结果。
    /// 由 ViewModel 的 UpdateBoltMonitor 每次 ProcessLoop 迭代调用一次。
    /// 7×24 运行，不依赖风机运行状态。
    /// </summary>
    /// <param name="tiltData">倾角传感器数据 (~500 点)</param>
    /// <param name="temperature">环境温度 (°C)，默认 NaN 表示无温度数据</param>
    public BoltLoosenResult Process(double[] tiltData, double temperature = double.NaN)
    {
        if (tiltData.Length == 0)
            return MakeResult(0.0, BoltAlarmState.Calibrating);

        // ── Step 1: 信号分解 — 取批次均值作为 θ_DC ──
        // ~500 点 @ ~150Hz 有效采样率 ≈ 3.3s 窗口，自然低通滤波
        double thetaDc = tiltData.Average();

        // ── Step 2: 基线标定 (前 CalibrationPeriods 期, Welford 在线算法) ──
        if (_calibrationCount < CalibrationPeriods)
        {
            // 收集温度配对数据用于补偿标定
            if (!double.IsNaN(temperature))
            {
                _calibTiltHistory.Enqueue(thetaDc);
                _calibTempHistory.Enqueue(temperature);
            }

            UpdateBaseline(thetaDc);

            // 标定完成时：计算温度补偿系数
            if (_calibrationCount == CalibrationPeriods &&
                _calibTiltHistory.Count >= MinCalibrationForCompensation)
            {
                ComputeTempCompensation();
            }

            return MakeResult(thetaDc, BoltAlarmState.Calibrating);
        }

        // ── Step 3: 温度补偿 (如已激活) ──
        double thetaCompensated = thetaDc;
        if (_tempCompensationReady && !double.IsNaN(temperature))
        {
            double predicted = _tempCoeffA * temperature + _tempCoeffB;
            thetaCompensated = thetaDc - (predicted - _baselineMu);
        }

        // ── Step 4: 滑动窗口均值 ──
        _slidingWindow.Enqueue(thetaCompensated);
        while (_slidingWindow.Count > SlidingWindowSize)
            _slidingWindow.Dequeue();

        double windowMean = _slidingWindow.Average();
        double driftDelta = Math.Abs(windowMean - _baselineMu);

        // ── Step 5: 小时降采样 + 7 天线性回归 ──
        _hourSum += thetaCompensated;
        _hourSampleCounter++;
        double driftRateDegPerDay = 0.0;

        if (_hourSampleCounter >= SamplesPerHour)
        {
            double hourlyMean = _hourSum / _hourSampleCounter;
            _hourIndex++;
            _hourlyHistory.Enqueue((_hourIndex, hourlyMean));
            if (_hourlyHistory.Count > MaxRegressionPoints)
                _hourlyHistory.Dequeue();

            _hourSampleCounter = 0;
            _hourSum = 0;

            // 线性回归计算 dθ/dt
            if (_hourlyHistory.Count >= 2)
            {
                var xArr = _hourlyHistory.Select(p => p.hourIndex).ToArray();
                var yArr = _hourlyHistory.Select(p => p.value).ToArray();
                var (slope, _) = ComputeLinearRegression(xArr, yArr);
                // slope 单位: °/hour → 转换为 °/day
                driftRateDegPerDay = slope * 24.0;
            }
        }

        // ── Step 6: 两级阈值状态机 ──
        BoltAlarmState state;
        if (driftDelta > AlarmThresholdDeg)
            state = BoltAlarmState.Alarm;
        else if (driftDelta > WarnThresholdDeg && driftRateDegPerDay > 0)
            state = BoltAlarmState.Warning;
        else
            state = BoltAlarmState.Normal;

        CurrentState = state;
        return MakeResult(thetaDc, state, driftDelta, driftRateDegPerDay, thetaCompensated);
    }

    #region 私有辅助方法

    private BoltLoosenResult MakeResult(double thetaDc, BoltAlarmState state,
        double driftDelta = 0, double driftRate = 0, double thetaCompensated = double.NaN)
    {
        double compensatedDrift = 0;
        if (!double.IsNaN(thetaCompensated))
            compensatedDrift = Math.Abs(thetaCompensated - _baselineMu);

        return new BoltLoosenResult
        {
            ThetaDc = thetaDc,
            BaselineTheta0 = _baselineMu,
            DriftDelta = driftDelta,
            DriftRate = driftRate,
            TempCoeffA = _tempCoeffA,
            TempCoeffB = _tempCoeffB,
            CompensatedDrift = compensatedDrift,
            SlidingWindowCount = _slidingWindow.Count,
            CalibrationCount = _calibrationCount,
            IsCalibrated = IsCalibrated,
            IsTempCompensationActive = _tempCompensationReady,
            RegressionPointCount = _hourlyHistory.Count,
            AlarmState = state
        };
    }

    /// <summary>Welford 在线算法：增量更新均值和标准差</summary>
    private void UpdateBaseline(double value)
    {
        _calibrationCount++;
        double delta = value - _baselineMu;
        _baselineMu += delta / _calibrationCount;
        double delta2 = value - _baselineMu;
        _baselineM2 += delta * delta2;
        if (_calibrationCount >= 2)
            _baselineSigma = Math.Sqrt(_baselineM2 / (_calibrationCount - 1));
    }

    /// <summary>标定完成时计算温度补偿系数 θ_DC = a·T + b</summary>
    private void ComputeTempCompensation()
    {
        if (_calibTiltHistory.Count < MinCalibrationForCompensation) return;

        var (slope, intercept) = ComputeLinearRegression(
            _calibTempHistory.ToArray(),
            _calibTiltHistory.ToArray());

        _tempCoeffA = slope;
        _tempCoeffB = intercept;
        _tempCompensationReady = true;

        // 释放标定缓冲区内存
        _calibTiltHistory.Clear();
        _calibTempHistory.Clear();
    }

    #endregion

    /// <summary>
    /// 最小二乘法线性回归 y = slope·x + intercept。
    /// </summary>
    /// <param name="x">自变量数组</param>
    /// <param name="y">因变量数组</param>
    /// <returns>(斜率, 截距)</returns>
    internal static (double slope, double intercept) ComputeLinearRegression(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2) return (0.0, 0.0);

        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += x[i];
            sumY += y[i];
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
        }

        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-15) return (0.0, sumY / n);

        double slope = (n * sumXY - sumX * sumY) / denom;
        double intercept = (sumY - slope * sumX) / n;
        return (slope, intercept);
    }

    /// <summary>重置基线标定，清除所有状态</summary>
    public void ResetCalibration()
    {
        _calibrationCount = 0;
        _baselineMu = 0;
        _baselineSigma = 1e6;
        _baselineM2 = 0;
        _slidingWindow.Clear();
        _hourSampleCounter = 0;
        _hourSum = 0;
        _hourIndex = 0;
        _hourlyHistory.Clear();
        _tempCoeffA = 0;
        _tempCoeffB = 0;
        _tempCompensationReady = false;
        _calibTiltHistory.Clear();
        _calibTempHistory.Clear();
        CurrentState = BoltAlarmState.Calibrating;
    }
}
