using System;
using System.Linq;

namespace BearingFaultDiagnosis.Models;

/// <summary>风口堵塞报警状态</summary>
public enum BlockageAlarmState
{
    Calibrating, // 基线收集中
    Normal,      // |ΔI| ≤ 阈值
    Warning,     // |ΔI| > 阈值但未达 60s 持续
    Alarm        // |ΔI| > 阈值且持续 ≥ 60s
}

/// <summary>单次风口堵塞监测结果快照</summary>
public sealed record BlockageResult
{
    /// <summary>I_rms — 本次电流 RMS 值</summary>
    public double CurrentRms { get; init; }
    /// <summary>基线均值 μ_I</summary>
    public double BaselineMean { get; init; }
    /// <summary>基线标准差 σ_I</summary>
    public double BaselineStdDev { get; init; }
    /// <summary>ΔI = (E_n - μ_I) / σ_I，归一化偏差</summary>
    public double DeviationDeltaI { get; init; }
    /// <summary>EWMA 平滑后的 RMS</summary>
    public double EwmaRms { get; init; }
    /// <summary>超限持续秒数 = consecutiveCount × monitoringPeriod</summary>
    public double ExceedDurationSeconds { get; init; }
    /// <summary>当前连续超限期数</summary>
    public int ConsecutiveCount { get; init; }
    /// <summary>要求的连续超限期数（动态计算）</summary>
    public int ConsecutiveRequired { get; init; }
    /// <summary>已收集标定期数</summary>
    public int CalibrationCount { get; init; }
    /// <summary>是否已完成基线标定</summary>
    public bool IsCalibrated { get; init; }
    /// <summary>当前报警状态</summary>
    public BlockageAlarmState AlarmState { get; init; }
}

/// <summary>
/// 风口堵塞监测算法核心检测器。
/// 基于电流 RMS 偏差检测：Welford 在线基线标定、EWMA 平滑、
/// |ΔI| &gt; 3 双侧判定与 60s 连续确认。
/// 线程安全：仅在 ProcessLoopAsync 的单一 Task 中调用，无并发。
/// </summary>
public sealed class BlockageDetector
{
    #region 配置常量

    /// <summary>自动标定周期数 (~2h at 10s/period)</summary>
    public const int CalibrationPeriods = 720;

    /// <summary>|ΔI| 阈值 (3σ 准则)</summary>
    private const double DeviationThreshold = 3.0;

    /// <summary>持续超限目标时长 (秒)</summary>
    private const double ConsecutiveTargetSeconds = 60.0;

    /// <summary>目标监测周期 (秒)</summary>
    private const double MonitoringTargetSeconds = 10.0;

    /// <summary>EWMA 衰减因子 λ ∈ [0.1, 0.2]</summary>
    private const double EwmaLambda = 0.15;

    /// <summary>最小连续确认周期数（兜底）</summary>
    private const int MinConsecutiveRequired = 3;

    /// <summary>最小标准差保护值 (A)，防止 σ 过小导致极度敏感</summary>
    private const double MinSigma = 0.1;

    #endregion

    #region 状态字段

    private double _ewma = double.NaN;

    // Welford 在线算法状态
    private int _calibrationCount;
    private double _baselineMu;
    private double _baselineSigma = 1e6;
    private double _baselineM2;

    // 连续判定
    private int _consecutiveCount;
    private double _lastMonitoringPeriod;

    #endregion

    #region 公共属性

    /// <summary>是否已完成基线标定</summary>
    public bool IsCalibrated => _calibrationCount >= CalibrationPeriods;

    /// <summary>当前报警状态</summary>
    public BlockageAlarmState CurrentState { get; private set; } = BlockageAlarmState.Calibrating;

    /// <summary>已收集标定期数</summary>
    public int CalibrationCount => _calibrationCount;

    /// <summary>标定目标周期数</summary>
    public int CalibrationPeriodsRequired => CalibrationPeriods;

    /// <summary>当前连续超限期数</summary>
    public int ConsecutiveCount => _consecutiveCount;

    #endregion

    /// <summary>
    /// 处理一帧电流数据，返回监测结果。
    /// 由 ViewModel 的 UpdateBlockageMonitor 每次 ProcessLoop 迭代调用一次。
    /// </summary>
    /// <param name="currentData">时域电流采样数据</param>
    /// <param name="fs">采样率 (Hz)</param>
    /// <param name="monitoringPeriodOverride">
    /// 实际监测周期覆盖值 (秒)。
    /// 传入 0 时按 currentData.Length / fs 计算；
    /// ViewModel 应传入 ProcessLoop 的实际迭代周期 (~3.277s)。
    /// </param>
    public BlockageResult Process(double[] currentData, double fs, double monitoringPeriodOverride = 0)
    {
        double monitoringPeriod = monitoringPeriodOverride > 0
            ? monitoringPeriodOverride
            : (double)currentData.Length / fs;
        _lastMonitoringPeriod = monitoringPeriod;

        int consecutiveRequired = Math.Max(MinConsecutiveRequired,
            (int)Math.Ceiling(ConsecutiveTargetSeconds / monitoringPeriod));

        // ── Step 1: RMS 计算 ──
        double rms = ComputeRms(currentData);

        // ── Step 2: EWMA 平滑 ──
        // E_n = (1-λ)·E_{n-1} + λ·I_rms
        if (double.IsNaN(_ewma))
            _ewma = rms;
        else
            _ewma = (1 - EwmaLambda) * _ewma + EwmaLambda * rms;

        // ── Step 3: 基线标定 (前 CalibrationPeriods 期, Welford 在线算法) ──
        if (_calibrationCount < CalibrationPeriods)
        {
            UpdateBaseline(rms);
            return MakeResult(rms, 0.0, consecutiveRequired, monitoringPeriod,
                BlockageAlarmState.Calibrating);
        }

        // ── Step 4: 偏差计算 ──
        double effectiveSigma = Math.Max(_baselineSigma, MinSigma);
        double deltaI = (_ewma - _baselineMu) / effectiveSigma;

        // ── Step 5: 持续性判定 ──
        bool conditionMet = Math.Abs(deltaI) > DeviationThreshold;
        if (conditionMet)
            _consecutiveCount++;
        else
            _consecutiveCount = 0;

        // ── Step 6: 状态机 ──
        BlockageAlarmState state;
        if (_consecutiveCount >= consecutiveRequired)
            state = BlockageAlarmState.Alarm;
        else if (_consecutiveCount > 0)
            state = BlockageAlarmState.Warning;
        else
            state = BlockageAlarmState.Normal;

        CurrentState = state;
        return MakeResult(rms, deltaI, consecutiveRequired, monitoringPeriod, state);
    }

    #region 私有辅助方法

    private BlockageResult MakeResult(double currentRms, double deltaI,
        int consecutiveRequired, double monitoringPeriod, BlockageAlarmState state)
    {
        return new BlockageResult
        {
            CurrentRms = currentRms,
            BaselineMean = _baselineMu,
            BaselineStdDev = _baselineSigma,
            DeviationDeltaI = deltaI,
            EwmaRms = double.IsNaN(_ewma) ? 0 : _ewma,
            ExceedDurationSeconds = _consecutiveCount * monitoringPeriod,
            ConsecutiveCount = _consecutiveCount,
            ConsecutiveRequired = consecutiveRequired,
            CalibrationCount = _calibrationCount,
            IsCalibrated = IsCalibrated,
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

    #endregion

    /// <summary>计算信号的 RMS（均方根值），保留直流分量</summary>
    internal static double ComputeRms(double[] data)
    {
        if (data.Length == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < data.Length; i++)
            sumSq += data[i] * data[i];
        return Math.Sqrt(sumSq / data.Length);
    }

    /// <summary>重置基线标定，清除所有状态</summary>
    public void ResetCalibration()
    {
        _calibrationCount = 0;
        _baselineMu = 0;
        _baselineSigma = 1e6;
        _baselineM2 = 0;
        _ewma = double.NaN;
        _consecutiveCount = 0;
        _lastMonitoringPeriod = 0;
        CurrentState = BlockageAlarmState.Calibrating;
    }
}
