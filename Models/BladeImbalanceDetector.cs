using System;
using System.Collections.Generic;
using System.Linq;

namespace BearingFaultDiagnosis.Models;

/// <summary>叶片不平衡报警状态</summary>
public enum BladeAlarmState
{
    Normal,      // 正常
    Calibrating, // 标定中
    Watch,       // 观察 (部分条件满足)
    Warning,     // 预警 (全部条件满足但未达连续周期)
    Alarm        // 报警 (连续 N 周期全部满足)
}

/// <summary>单次叶片不平衡监测结果快照</summary>
public sealed record BladeImbalanceResult
{
    /// <summary>A₁ — 1×fr 幅值</summary>
    public double Amplitude1x { get; init; }
    /// <summary>A₂ — 2×fr 幅值</summary>
    public double Amplitude2x { get; init; }
    /// <summary>R₂₁ = A₂ / A₁</summary>
    public double HarmonicRatioR21 { get; init; }
    /// <summary>σφ — 相位标准差 (度)</summary>
    public double PhaseStdDev { get; init; }
    /// <summary>Sₙ — EWMA 平滑后 A₁</summary>
    public double EwmaAmplitude { get; init; }
    /// <summary>基线均值 μ</summary>
    public double BaselineMean { get; init; }
    /// <summary>基线标准差 σ</summary>
    public double BaselineStdDev { get; init; }
    /// <summary>阈值 μ + 3σ</summary>
    public double Threshold { get; init; }
    /// <summary>已收集标定期数</summary>
    public int CalibrationCount { get; init; }
    /// <summary>当前连续满足期数</summary>
    public int ConsecutiveCount { get; init; }
    /// <summary>连续确认要求期数</summary>
    public int ConsecutiveRequired { get; init; }
    /// <summary>是否已完成标定</summary>
    public bool IsCalibrated { get; init; }
    /// <summary>当前报警状态</summary>
    public BladeAlarmState AlarmState { get; init; }
}

/// <summary>
/// 叶片不平衡监测算法核心检测器。
/// 提取三特征 (A₁, R₂₁, σφ)，执行 EWMA 平滑、Welford 在线基线标定、
/// 三条件联合判定与连续周期确认。
/// 线程安全：仅在 ProcessLoopAsync 的单一 Task 中调用，无并发。
/// </summary>
public sealed class BladeImbalanceDetector
{
    #region 配置常量

    /// <summary>目标转频 (Hz), 2000 RPM / 60 ≈ 33.33 Hz</summary>
    public const double TargetFreqHz = 2000.0 / 60.0;

    /// <summary>EWMA 衰减因子 λ ∈ [0.1, 0.2]</summary>
    private const double EwmaLambda = 0.15;

    /// <summary>R₂₁ 上限阈值：纯不平衡时 < 0.3</summary>
    private const double R21Threshold = 0.3;

    /// <summary>相位标准差上限 (度)：不平衡时 σφ < 15°</summary>
    private const double PhaseStdThreshold = 15.0;

    /// <summary>自动标定周期数</summary>
    private const int CalibrationPeriods = 30;

    /// <summary>连续确认目标时长 (秒)</summary>
    private const double ConsecutiveTargetSeconds = 10.0;

    /// <summary>最小连续确认周期数</summary>
    private const int MinConsecutiveRequired = 3;

    /// <summary>峰值搜索范围 (±bin)</summary>
    private const int PeakSearchRange = 2;

    /// <summary>相位历史缓冲区大小</summary>
    private const int PhaseHistorySize = 10;

    #endregion

    #region 状态字段

    private double _ewma = double.NaN;
    private readonly Queue<double> _phaseHistory = new();

    // Welford 在线算法状态
    private int _calibrationCount;
    private double _baselineMu;
    private double _baselineSigma = 1e6;
    private double _baselineM2;

    // 连续判定
    private int _consecutiveCount;

    #endregion

    #region 公共属性

    /// <summary>是否已完成基线标定</summary>
    public bool IsCalibrated => _calibrationCount >= CalibrationPeriods;

    /// <summary>当前报警状态</summary>
    public BladeAlarmState CurrentState { get; private set; } = BladeAlarmState.Calibrating;

    /// <summary>已收集标定期数</summary>
    public int CalibrationCount => _calibrationCount;

    /// <summary>标定目标周期数</summary>
    public int CalibrationPeriodsRequired => CalibrationPeriods;

    /// <summary>当前连续满足期数</summary>
    public int ConsecutiveCount => _consecutiveCount;

    #endregion

    /// <summary>
    /// 处理一帧振动数据，返回监测结果。
    /// 由 ViewModel 的 UpdateBladeMonitor 每次 ProcessLoop 迭代调用一次。
    /// </summary>
    /// <param name="spectrum">C++ FFT 输出的单边幅值谱 (N/2+1 bins)</param>
    /// <param name="rawData">原始时域振动数据 (用于 Goertzel 相位计算)</param>
    /// <param name="fs">采样率 (Hz)</param>
    public BladeImbalanceResult Process(double[] spectrum, double[] rawData, double fs)
    {
        int n = rawData.Length;
        double resolution = fs / n;

        // 动态计算连续确认要求
        double monitoringPeriod = (n / 2.0) / fs; // ChunkSize/OverlapDivisor / fs
        int consecutiveRequired = Math.Max(MinConsecutiveRequired,
            (int)(ConsecutiveTargetSeconds / monitoringPeriod));

        // ── Step 1: 特征提取 ──

        // 1a. A₁: 1×fr 幅值 (±2 bin 峰值搜索)
        int idxCenter1x = (int)Math.Round(TargetFreqHz / resolution);
        double amp1 = PeakSearch(spectrum, idxCenter1x, PeakSearchRange);

        // 1b. A₂: 2×fr 幅值
        int idxCenter2x = (int)Math.Round(TargetFreqHz * 2 / resolution);
        double amp2 = PeakSearch(spectrum, idxCenter2x, PeakSearchRange);

        // 1c. R₂₁ = A₂ / A₁
        double r21 = amp1 > 1e-12 ? amp2 / amp1 : 0.0;

        // 1d. σφ: Goertzel 算法计算 1×fr 相位
        double phaseDeg = ComputePhaseGoertzel(rawData, fs, TargetFreqHz);
        _phaseHistory.Enqueue(phaseDeg);
        if (_phaseHistory.Count > PhaseHistorySize)
            _phaseHistory.Dequeue();
        double phaseStd = ComputeCircularStdDev(_phaseHistory);

        // ── Step 2: EWMA 平滑 ──
        // Sₙ = (1-λ)·Sₙ₋₁ + λ·A₁
        if (double.IsNaN(_ewma))
            _ewma = amp1;
        else
            _ewma = (1 - EwmaLambda) * _ewma + EwmaLambda * amp1;

        // ── Step 3: 基线标定 (前 30 期, Welford 在线算法) ──
        if (_calibrationCount < CalibrationPeriods)
        {
            UpdateBaseline(amp1);
            return MakeResult(amp1, amp2, r21, phaseStd,
                BladeAlarmState.Calibrating, consecutiveRequired);
        }

        // ── Step 4: 三条件判定 ──
        double threshold = _baselineMu + 3 * _baselineSigma;
        bool condAmp = _ewma > threshold;       // A₁ > μ + 3σ
        bool condR21 = r21 < R21Threshold;      // R₂₁ < 0.3
        bool condPhase = phaseStd < PhaseStdThreshold; // σφ < 15°
        bool allMet = condAmp && condR21 && condPhase;

        // 连续计数
        if (allMet)
            _consecutiveCount++;
        else
            _consecutiveCount = 0;

        // ── Step 5: 状态机 ──
        BladeAlarmState state;
        if (_consecutiveCount >= consecutiveRequired)
            state = BladeAlarmState.Alarm;
        else if (_consecutiveCount > 0)
            state = BladeAlarmState.Warning;
        else if (condAmp && condR21)
            state = BladeAlarmState.Watch;
        else
            state = BladeAlarmState.Normal;

        CurrentState = state;
        return MakeResult(amp1, amp2, r21, phaseStd, state, consecutiveRequired);
    }

    #region 私有辅助方法

    private BladeImbalanceResult MakeResult(double amp1, double amp2, double r21,
        double phaseStd, BladeAlarmState state, int consecutiveRequired)
    {
        double threshold = _baselineMu + 3 * _baselineSigma;
        return new BladeImbalanceResult
        {
            Amplitude1x = amp1,
            Amplitude2x = amp2,
            HarmonicRatioR21 = r21,
            PhaseStdDev = phaseStd,
            EwmaAmplitude = double.IsNaN(_ewma) ? 0 : _ewma,
            BaselineMean = _baselineMu,
            BaselineStdDev = _baselineSigma,
            Threshold = threshold,
            CalibrationCount = _calibrationCount,
            ConsecutiveCount = _consecutiveCount,
            ConsecutiveRequired = consecutiveRequired,
            IsCalibrated = IsCalibrated,
            AlarmState = state
        };
    }

    /// <summary>±range bin 峰值搜索，返回搜索范围内的最大幅值</summary>
    internal static double PeakSearch(double[] spectrum, int centerIdx, int range)
    {
        int start = Math.Max(0, centerIdx - range);
        int end = Math.Min(spectrum.Length - 1, centerIdx + range);
        double maxVal = 0;
        for (int i = start; i <= end; i++)
        {
            if (spectrum[i] > maxVal)
                maxVal = spectrum[i];
        }
        return maxVal;
    }

    /// <summary>
    /// Goertzel 算法：计算单频点的复数 DFT 值，返回相位 (度)。
    /// 复杂度 O(2N)，对 N=32768 约 0.05ms。
    /// </summary>
    internal static double ComputePhaseGoertzel(double[] data, double fs, double targetFreq)
    {
        int n = data.Length;
        double k = targetFreq * n / fs;  // 非整数频率对应的 bin
        double w = 2 * Math.PI * k / n;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;

        for (int i = 0; i < n; i++)
        {
            s0 = data[i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        // 复数结果: X(k) = s1 - s2 * e^(-jw)
        double real = s1 - s2 * Math.Cos(w);
        double imag = s2 * Math.Sin(w);
        return Math.Atan2(imag, real) * 180.0 / Math.PI;
    }

    /// <summary>
    /// 圆周标准差：处理 -180°/+180° 跳变。
    /// 使用 mean resultant length R，circularVar = -2·ln(R)。
    /// </summary>
    internal static double ComputeCircularStdDev(Queue<double> phases)
    {
        if (phases.Count < 2) return 0;

        var rads = phases.Select(d => d * Math.PI / 180.0).ToList();
        int count = rads.Count;

        double sinSum = rads.Sum(Math.Sin);
        double cosSum = rads.Sum(Math.Cos);
        double R = Math.Sqrt(sinSum * sinSum + cosSum * cosSum) / count;

        // 当所有相位完全一致时 R=1，circularVar=0
        // 当相位完全随机时 R≈0，circularVar 很大
        double circularVar = -2 * Math.Log(Math.Max(R, 1e-10));
        return Math.Sqrt(circularVar) * 180.0 / Math.PI;
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

    /// <summary>重置基线标定，清除所有状态</summary>
    public void ResetCalibration()
    {
        _calibrationCount = 0;
        _baselineMu = 0;
        _baselineSigma = 1e6;
        _baselineM2 = 0;
        _ewma = double.NaN;
        _consecutiveCount = 0;
        _phaseHistory.Clear();
        CurrentState = BladeAlarmState.Calibrating;
    }
}
