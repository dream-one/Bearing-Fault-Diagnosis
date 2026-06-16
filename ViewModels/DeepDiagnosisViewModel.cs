using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Models;
using BearingFaultDiagnosis.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using SWM = System.Windows.Media;

namespace BearingFaultDiagnosis.ViewModels
{
    public partial class DeepDiagnosisViewModel : ViewModelBase, IDisposable
    {
        #region 常量与配置

        private const int ChunkSize = 32768;
        private const int OverlapDivisor = 2;
        private const double DefaultSampleRate = 5000.0;
        private const double RatedRpm = 2000.0;
        private const double RatedRpmHz = RatedRpm / 60.0; // ~33.33 Hz

        // 状态颜色常量
        private static readonly SWM.SolidColorBrush ColorGreen = new(SWM.Color.FromRgb(0x16, 0xA3, 0x4A));
        private static readonly SWM.SolidColorBrush ColorGray = new(SWM.Color.FromRgb(0x94, 0xA3, 0xB8));
        private static readonly SWM.SolidColorBrush ColorYellow = new(SWM.Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly SWM.SolidColorBrush ColorOrange = new(SWM.Color.FromRgb(0xF3, 0x9C, 0x12));
        private static readonly SWM.SolidColorBrush ColorRed = new(SWM.Color.FromRgb(0xDC, 0x26, 0x26));

        #endregion

        #region 私有字段

        private readonly IDeepDiagnosisService _deepDiagnosisService;
        private readonly ISensorDataService _sensorDataService;

        private readonly double[] _buffer = new double[ChunkSize];
        private int _currentCount;
        private readonly int _overlapSize;

        private readonly CancellationTokenSource _cts = new();
        private int _cwtFrameCounter;
        private const int CwtThrottleFrames = 2;

        // 风机运行状态
        private bool _isFanRunning = true;

        // 叶片不平衡检测器
        private readonly BladeImbalanceDetector _bladeDetector = new();
        // 风口堵塞检测器
        private readonly BlockageDetector _blockageDetector = new();
        // 螺栓松动检测器 (7×24 运行)
        private readonly BoltLoosenDetector _boltDetector = new();

        // 图表更新队列（后台线程入队，UI 线程出队执行，确保所有 ScottPlot 操作在 UI 线程）
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Action> PlotActions = new();
        internal volatile bool BoltPlotDirty;
        internal volatile bool BladePlotDirty;
        internal volatile bool BlockagePlotDirty;
        internal volatile bool AuxPlotDirty;

        // 中文字体（图例显示用）
        private readonly string _chineseFont = ScottPlot.Fonts.Detect("测试");

        // 复用的绘图对象（避免 Clear+Add 导致集合修改异常）
        private Signal? _boltSig;
        private ScottPlot.Plottables.HorizontalLine? _boltWarnLine;
        private ScottPlot.Plottables.HorizontalLine? _boltAlarmLine;
        private ScottPlot.Plottables.HorizontalLine? _boltBaseLine;

        private Signal? _bladeSig;
        private ScottPlot.Plottables.VerticalLine? _bladeLine1x;
        private ScottPlot.Plottables.VerticalLine? _bladeLine2x;
        private ScottPlot.Plottables.HorizontalLine? _bladeEwmaLine;
        private ScottPlot.Plottables.HorizontalLine? _bladeThreshLine;

        private Signal? _blockageSig;
        private ScottPlot.Plottables.HorizontalLine? _blockageUpperLine;
        private ScottPlot.Plottables.HorizontalLine? _blockageLowerLine;

        private ScottPlot.Plottables.Scatter? _auxScatter;
        private ScottPlot.Plottables.HorizontalLine? _auxRefLine;

        #endregion

        #region 绑定属性 — 风机信息

        /// <summary>风机编号列表</summary>
        [ObservableProperty] private List<DeviceInfo> _fanList = new();
        /// <summary>当前选中的风机</summary>
        [ObservableProperty] private DeviceInfo _selectedFan;

        /// <summary>运行状态颜色 (Green=运行中, Gray=停机)</summary>
        [ObservableProperty] private SWM.SolidColorBrush _runStatusColor = new SWM.SolidColorBrush(SWM.Color.FromRgb(0x16, 0xA3, 0x4A));
        /// <summary>运行状态文本</summary>
        [ObservableProperty] private string _runStatusText = "运行中";

        #endregion

        #region 绑定属性 — 螺栓松动监测

        /// <summary>当前漂移量显示文本</summary>
        [ObservableProperty] private string _currentDrift = "0.000°";
        /// <summary>漂移速率显示文本</summary>
        [ObservableProperty] private string _driftRate = "+0.000°/天";
        /// <summary>报警状态文本</summary>
        [ObservableProperty] private string _boltAlarmText = "标定中";
        /// <summary>报警状态颜色</summary>
        [ObservableProperty] private SWM.SolidColorBrush _boltAlarmColor = ColorGray;
        /// <summary>θ_DC 当前值文本</summary>
        [ObservableProperty] private string _boltTiltText = "0.000°";
        /// <summary>基线 θ₀ 文本</summary>
        [ObservableProperty] private string _boltBaselineText = "标定中 (0/200)";
        /// <summary>温度补偿状态文本</summary>
        [ObservableProperty] private string _boltTempCompText = "待标定";

        #endregion

        #region 绑定属性 — 叶片不平衡监测

        /// <summary>1×fr 幅值显示文本</summary>
        [ObservableProperty] private string _amp1Fr = "0.000";
        /// <summary>谐波比 R21 显示文本</summary>
        [ObservableProperty] private string _harmonicRatio = "0.00";
        /// <summary>相位稳定性 σφ 显示文本</summary>
        [ObservableProperty] private string _phaseStability = "0.0°";
        /// <summary>EWMA 平滑幅值</summary>
        [ObservableProperty] private string _ewmaAmplitude = "0.000";
        /// <summary>报警状态文本</summary>
        [ObservableProperty] private string _bladeAlarmText = "标定中";
        /// <summary>报警状态颜色</summary>
        [ObservableProperty] private SWM.SolidColorBrush _bladeAlarmColor = ColorGray;
        /// <summary>基线状态文本</summary>
        [ObservableProperty] private string _baselineStatus = "标定中 (0/30)";
        /// <summary>连续计数文本</summary>
        [ObservableProperty] private string _consecutiveText = "0/3";

        #endregion

        #region 绑定属性 — 风口堵塞预警

        /// <summary>超限持续时间文本</summary>
        [ObservableProperty] private string _exceedDurationText = "超限持续时间: 0s / 60s";
        /// <summary>超限持续当前值</summary>
        [ObservableProperty] private double _exceedDurationValue = 0;
        /// <summary>超限最大值</summary>
        [ObservableProperty] private double _exceedDurationMax = 60;
        /// <summary>进度条颜色</summary>
        [ObservableProperty] private SWM.SolidColorBrush _exceedDurationColor = new SWM.SolidColorBrush(SWM.Color.FromRgb(0x16, 0xA3, 0x4A));
        /// <summary>报警状态文本</summary>
        [ObservableProperty] private string _blockageAlarmText = "标定中";
        /// <summary>报警状态颜色</summary>
        [ObservableProperty] private SWM.SolidColorBrush _blockageAlarmColor = ColorGray;
        /// <summary>电流 RMS 显示文本</summary>
        [ObservableProperty] private string _blockageRmsText = "0.000";
        /// <summary>ΔI 偏差值文本</summary>
        [ObservableProperty] private string _blockageDeltaIText = "0.00";
        /// <summary>基线状态文本</summary>
        [ObservableProperty] private string _blockageBaselineText = "标定中 (0/720)";
        /// <summary>连续计数文本</summary>
        [ObservableProperty] private string _blockageConsecutiveText = "0/3";

        #endregion

        #region 绑定属性 — 辅助验证

        /// <summary>环境温度显示文本</summary>
        [ObservableProperty] private string _ambientTemp = "25.0 °C";
        /// <summary>温度补偿状态</summary>
        [ObservableProperty] private string _tempCompStatus = "已补偿";

        #endregion

        #region 绑定属性 — 诊断状态

        [ObservableProperty] private string _diagnosisResult = "等待数据...";

        #endregion

        #region 图表脏标记（驱动 UI 刷新）

        [ObservableProperty] private bool _boltMonitorDirty;
        [ObservableProperty] private bool _bladeMonitorDirty;
        [ObservableProperty] private bool _blockageMonitorDirty;
        [ObservableProperty] private bool _auxMonitorDirty;

        #endregion

        #region 事件定义（传感器数据就绪）

        /// <summary>振动加速度数据就绪</summary>
        public event Action<double[]>? OnVibrationDataReady;
        /// <summary>电流数据就绪</summary>
        public event Action<double[]>? OnCurrentDataReady;
        /// <summary>倾角数据就绪</summary>
        public event Action<double[]>? OnTiltDataReady;

        #endregion

        #region 图表实例

        /// <summary>螺栓松动监测图 (由 View 绑定为 WpfPlot 的实际 Plot)</summary>
        public Plot BoltMonitorPlot { get; set; } = new();
        /// <summary>叶片不平衡监测图</summary>
        public Plot BladeMonitorPlot { get; set; } = new();
        /// <summary>风口堵塞预警图</summary>
        public Plot BlockageMonitorPlot { get; set; } = new();
        /// <summary>辅助验证图</summary>
        public Plot AuxMonitorPlot { get; set; } = new();

        /// <summary>
        /// 将 View 中 WpfPlot 控件的内部 Plot 实例绑定到 ViewModel
        /// 必须在 View.Loaded 中调用，否则 ViewModel 绘制的数据无法显示
        /// </summary>
        public void BindPlots(Plot bolt, Plot blade, Plot blockage, Plot aux)
        {
            BoltMonitorPlot = bolt;
            BladeMonitorPlot = blade;
            BlockageMonitorPlot = blockage;
            AuxMonitorPlot = aux;
            InitPlots();
        }

        #endregion

        #region 构造函数与初始化

        public DeepDiagnosisViewModel(IDeepDiagnosisService deepDiagnosisService, ISensorDataService sensorDataService)
        {
            _deepDiagnosisService = deepDiagnosisService;
            _sensorDataService = sensorDataService;
            _sensorDataService.MetadataUpdated += OnSensorMetadataUpdated;

            _overlapSize = ChunkSize / OverlapDivisor;

            _ = Task.Run(() => ProcessLoopAsync(_cts.Token));
            _ = LoadFanListAsync();

            // InitPlots() 由 View.Loaded 中调用 BindPlots() 触发
        }

        private void InitPlots()
        {
            // 仅设置图例，阈值线/参考线由 Update*Monitor 每帧动态绘制
            var boltLeg = BoltMonitorPlot.ShowLegend(Alignment.UpperRight);
            boltLeg.FontName = _chineseFont;
            var bladeLeg = BladeMonitorPlot.ShowLegend(Alignment.UpperRight);
            bladeLeg.FontName = _chineseFont;
        }

        private void OnSensorMetadataUpdated(PuMetadata meta)
        {
            // 根据传感器元数据更新运行状态
            _isFanRunning = meta.rpm > 100; // 简单判定
            RunStatusColor = _isFanRunning
                ? new SWM.SolidColorBrush(SWM.Color.FromRgb(0x16, 0xA3, 0x4A))
                : new SWM.SolidColorBrush(SWM.Color.FromRgb(0x94, 0xA3, 0xB8));
            RunStatusText = _isFanRunning ? "运行中" : "停机";
        }

        private async Task LoadFanListAsync()
        {
            try
            {
                // 复用 DeviceInfo 作为风机列表的实体
                // 实际项目中可创建专门的 FanInfo 实体
                var list = new List<DeviceInfo>
                {
                    new() { Id = 1, DeviceCode = "FAN-001", DeviceName = "隧道A-01号风机", InstallLocation = "隧道A" },
                    new() { Id = 2, DeviceCode = "FAN-002", DeviceName = "隧道A-02号风机", InstallLocation = "隧道A" },
                    new() { Id = 3, DeviceCode = "FAN-003", DeviceName = "隧道B-01号风机", InstallLocation = "隧道B" },
                };
                FanList = list;
                if (list.Count > 0) SelectedFan = list[0];
            }
            catch (Exception ex)
            {
                DiagnosisResult = $"加载风机列表失败：{ex.Message}";
            }
        }

        #endregion

        #region 属性变更

        partial void OnSelectedFanChanged(DeviceInfo value)
        {
            if (value != null)
                DiagnosisResult = $"已选择 {value.DeviceName}";
        }

        #endregion

        #region 核心数据处理循环

        private async Task ProcessLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int samplesNeeded = ChunkSize - _currentCount;
                    int collected = 0;

                    while (collected < samplesNeeded && !ct.IsCancellationRequested)
                    {
                        if (_sensorDataService.BufferV1_X.TryDequeue(out var value))
                        {
                            _buffer[_currentCount + collected] = value;
                            collected++;
                        }
                        else
                        {
                            await Task.Delay(10, ct);
                        }
                    }

                    if (ct.IsCancellationRequested) break;
                    _currentCount += collected;

                    // 复制振动数据
                    double[] rawDataCopy = new double[ChunkSize];
                    Array.Copy(_buffer, rawDataCopy, ChunkSize);
                    OnVibrationDataReady?.Invoke(rawDataCopy);

                    double fs = (_sensorDataService.puMetadata?.sample_rate ?? 0) > 0
                        ? _sensorDataService.puMetadata!.sample_rate
                        : DefaultSampleRate;

                    // 生成模拟电流数据（实际项目从传感器获取）
                    double[] currentData = GenerateSimulatedCurrent(rawDataCopy);
                    OnCurrentDataReady?.Invoke(currentData);

                    // 生成模拟倾角数据
                    double[] tiltData = GenerateSimulatedTilt(rawDataCopy);
                    OnTiltDataReady?.Invoke(tiltData);

                    // 更新算法卡片数据
                    UpdateBoltMonitor(tiltData);
                    UpdateBladeMonitor(rawDataCopy, fs);
                    UpdateBlockageMonitor(currentData);
                    UpdateAuxMonitor(rawDataCopy, fs);

                    // 重叠保留
                    Array.Copy(_buffer, ChunkSize - _overlapSize, _buffer, 0, _overlapSize);
                    _currentCount = _overlapSize;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProcessLoop] 异常: {ex.Message}");
                    await Task.Delay(50, ct);
                }
            }
        }

        #endregion

        #region 模拟传感器数据生成

        private double[] GenerateSimulatedCurrent(double[] vibrationData)
        {
            // 根据振动数据模拟电流信号（实际项目中电流由专用传感器采集）
            double[] current = new double[Math.Min(vibrationData.Length, 1024)];
            double rms = Math.Sqrt(vibrationData.Take(1024).Average(v => v * v));
            double baseCurrent = 15.0; // 额定电流约 15A
            for (int i = 0; i < current.Length; i++)
            {
                current[i] = baseCurrent + rms * 2.0 * Math.Sin(2 * Math.PI * 50 * i / DefaultSampleRate)
                    + (Random.Shared.NextDouble() - 0.5) * 0.5;
            }
            return current;
        }

        private double[] GenerateSimulatedTilt(double[] vibrationData)
        {
            // 模拟倾角数据（含低频漂移）
            double[] tilt = new double[Math.Min(vibrationData.Length / 64, 500)];
            double baseTilt = 0.05;
            for (int i = 0; i < tilt.Length; i++)
            {
                double drift = 0.02 * Math.Sin(2 * Math.PI * i / tilt.Length);
                double noise = (Random.Shared.NextDouble() - 0.5) * 0.02;
                tilt[i] = baseTilt + drift + noise;
            }
            return tilt;
        }

        #endregion

        #region 算法卡片更新

        private void UpdateBoltMonitor(double[] tiltData)
        {
            if (tiltData.Length < 2) return;

            // 注意：螺栓松动监测 7×24 运行，不检查 _isFanRunning

            // 生成模拟温度 (与 UpdateAuxMonitor 一致)
            double temperature = 25.0 + Random.Shared.NextDouble() * 3;

            // 调用检测器
            var result = _boltDetector.Process(tiltData, temperature);

            // 更新绑定属性
            CurrentDrift = $"{result.DriftDelta:F3}°";
            DriftRate = $"{result.DriftRate:+0.000;-0.000}°/天";
            BoltTiltText = $"{result.ThetaDc:F4}°";

            // 基线状态
            if (!result.IsCalibrated)
                BoltBaselineText = $"标定中 ({result.CalibrationCount}/{_boltDetector.CalibrationPeriodsRequired})";
            else
                BoltBaselineText = $"θ₀={result.BaselineTheta0:F4}°";

            // 温度补偿状态
            BoltTempCompText = result.IsTempCompensationActive
                ? $"a={result.TempCoeffA:F4} b={result.TempCoeffB:F4}"
                : "待标定";

            // 报警状态颜色映射
            (BoltAlarmText, BoltAlarmColor) = result.AlarmState switch
            {
                BoltAlarmState.Normal      => ("正常",   ColorGreen),
                BoltAlarmState.Calibrating => ("标定中", ColorGray),
                BoltAlarmState.Warning     => ("预警",   ColorOrange),
                BoltAlarmState.Alarm       => ("报警!",  ColorRed),
                _ => ("未知", ColorGray)
            };

            // 图表更新：入队，由 UI 线程统一执行（确保 ScottPlot 无线程安全问题）
            var capturedTiltData = tiltData;
            var capturedResult = result;
            PlotActions.Enqueue(() =>
            {
                if (_boltSig == null)
                {
                    _boltSig = BoltMonitorPlot.Add.Signal(capturedTiltData);
                    _boltSig.Label = "θ_DC 趋势";
                    _boltSig.Color = ScottPlot.Color.FromHex("#8E44AD");
                }
                else
                {
                    _boltSig.Data = new SignalSourceDouble(capturedTiltData, 1.0);
                }

                double warnLevel = capturedResult.BaselineTheta0 + BoltLoosenDetector.WarnThresholdDeg;
                double alarmLevel = capturedResult.BaselineTheta0 + BoltLoosenDetector.AlarmThresholdDeg;

                if (_boltWarnLine == null)
                {
                    _boltWarnLine = BoltMonitorPlot.Add.HorizontalLine(warnLevel);
                    _boltWarnLine.LineStyle.Color = ScottPlot.Color.FromHex("#F39C12");
                    _boltWarnLine.LineStyle.Width = 1.5f;
                    _boltWarnLine.LineStyle.Pattern = LinePattern.Dashed;
                    _boltWarnLine.LabelStyle.Text = $"预警 {BoltLoosenDetector.WarnThresholdDeg}°";
                }
                else { _boltWarnLine.Y = warnLevel; }

                if (_boltAlarmLine == null)
                {
                    _boltAlarmLine = BoltMonitorPlot.Add.HorizontalLine(alarmLevel);
                    _boltAlarmLine.LineStyle.Color = ScottPlot.Color.FromHex("#E74C3C");
                    _boltAlarmLine.LineStyle.Width = 1.5f;
                    _boltAlarmLine.LineStyle.Pattern = LinePattern.Dashed;
                    _boltAlarmLine.LabelStyle.Text = $"报警 {BoltLoosenDetector.AlarmThresholdDeg}°";
                }
                else { _boltAlarmLine.Y = alarmLevel; }

                if (capturedResult.IsCalibrated)
                {
                    if (_boltBaseLine == null)
                    {
                        _boltBaseLine = BoltMonitorPlot.Add.HorizontalLine(capturedResult.BaselineTheta0);
                        _boltBaseLine.LineStyle.Color = ScottPlot.Color.FromHex("#27AE60");
                        _boltBaseLine.LineStyle.Width = 1f;
                        _boltBaseLine.LineStyle.Pattern = LinePattern.Dotted;
                        _boltBaseLine.LabelStyle.Text = "θ₀";
                    }
                    else { _boltBaseLine.Y = capturedResult.BaselineTheta0; _boltBaseLine.IsVisible = true; }
                }
                else if (_boltBaseLine != null) { _boltBaseLine.IsVisible = false; }

                var boltLegend = BoltMonitorPlot.ShowLegend(Alignment.UpperRight);
                boltLegend.FontName = _chineseFont;
                BoltMonitorPlot.Axes.AutoScale();
            });
            BoltPlotDirty = true;
        }

        private void UpdateBladeMonitor(double[] vibrationData, double fs)
        {
            // 风机停机时跳过叶片不平衡检测
            if (!_isFanRunning) return;

            // 1. 使用 C++ FFT 计算频谱
            double[] spectrum = _deepDiagnosisService.ComputeSpectrum(vibrationData);
            if (spectrum.Length < 20) return;

            // 2. 调用检测器 (所有状态管理在 BladeImbalanceDetector 内部)
            var result = _bladeDetector.Process(spectrum, vibrationData, fs);

            // 3. 更新绑定属性
            Amp1Fr = $"{result.Amplitude1x:F4}";
            HarmonicRatio = $"{result.HarmonicRatioR21:F2}";
            PhaseStability = $"{result.PhaseStdDev:F1}°";
            EwmaAmplitude = $"{result.EwmaAmplitude:F4}";
            ConsecutiveText = $"{result.ConsecutiveCount}/{result.ConsecutiveRequired}";

            // 4. 基线状态
            if (!result.IsCalibrated)
                BaselineStatus = $"标定中 ({result.CalibrationCount}/{_bladeDetector.CalibrationPeriodsRequired})";
            else
                BaselineStatus = $"μ={result.BaselineMean:F4} σ={result.BaselineStdDev:F4}";

            // 5. 报警状态颜色映射
            (BladeAlarmText, BladeAlarmColor) = result.AlarmState switch
            {
                BladeAlarmState.Normal      => ("正常",   ColorGreen),
                BladeAlarmState.Calibrating => ("标定中", ColorGray),
                BladeAlarmState.Watch       => ("观察",   ColorYellow),
                BladeAlarmState.Warning     => ("预警",   ColorOrange),
                BladeAlarmState.Alarm       => ("报警!",  ColorRed),
                _ => ("未知", ColorGray)
            };

            // 6. 图表更新：入队，由 UI 线程统一执行
            var capturedSpectrum = spectrum;
            var capturedFs = fs;
            var capturedBladeResult = result;
            PlotActions.Enqueue(() =>
            {
                if (_bladeSig == null)
                {
                    _bladeSig = BladeMonitorPlot.Add.Signal(capturedSpectrum);
                    _bladeSig.Label = "FFT 频谱";
                    _bladeSig.Data.Period = capturedFs / ChunkSize;
                }
                else
                {
                    _bladeSig.Data = new SignalSourceDouble(capturedSpectrum, capturedFs / ChunkSize);
                }

                double fr = RatedRpmHz;
                double xMax = fr * 4;

                if (_bladeLine1x == null)
                {
                    _bladeLine1x = BladeMonitorPlot.Add.VerticalLine(fr);
                    _bladeLine1x.LineStyle.Color = ScottPlot.Color.FromHex("#3498DB");
                    _bladeLine1x.LineStyle.Width = 1.5f;
                    _bladeLine1x.LineStyle.Pattern = LinePattern.Dashed;
                    _bladeLine1x.LabelStyle.Text = $"1×fr ({fr:F1}Hz)";
                }
                else { _bladeLine1x.X = fr; }

                if (_bladeLine2x == null)
                {
                    _bladeLine2x = BladeMonitorPlot.Add.VerticalLine(fr * 2);
                    _bladeLine2x.LineStyle.Color = ScottPlot.Color.FromHex("#E74C3C");
                    _bladeLine2x.LineStyle.Width = 1.5f;
                    _bladeLine2x.LineStyle.Pattern = LinePattern.Dashed;
                    _bladeLine2x.LabelStyle.Text = $"2×fr ({fr * 2:F1}Hz)";
                }
                else { _bladeLine2x.X = fr * 2; }

                if (capturedBladeResult.EwmaAmplitude > 0)
                {
                    if (_bladeEwmaLine == null)
                    {
                        _bladeEwmaLine = BladeMonitorPlot.Add.HorizontalLine(capturedBladeResult.EwmaAmplitude);
                        _bladeEwmaLine.LineStyle.Color = ScottPlot.Color.FromHex("#27AE60");
                        _bladeEwmaLine.LineStyle.Width = 1f;
                        _bladeEwmaLine.LabelStyle.Text = "EWMA";
                    }
                    else { _bladeEwmaLine.Y = capturedBladeResult.EwmaAmplitude; _bladeEwmaLine.IsVisible = true; }
                }
                else if (_bladeEwmaLine != null) { _bladeEwmaLine.IsVisible = false; }

                if (capturedBladeResult.IsCalibrated)
                {
                    if (_bladeThreshLine == null)
                    {
                        _bladeThreshLine = BladeMonitorPlot.Add.HorizontalLine(capturedBladeResult.Threshold);
                        _bladeThreshLine.LineStyle.Color = ScottPlot.Color.FromHex("#E74C3C");
                        _bladeThreshLine.LineStyle.Width = 1f;
                        _bladeThreshLine.LineStyle.Pattern = LinePattern.Dashed;
                        _bladeThreshLine.LabelStyle.Text = "μ+3σ";
                    }
                    else { _bladeThreshLine.Y = capturedBladeResult.Threshold; _bladeThreshLine.IsVisible = true; }
                }
                else if (_bladeThreshLine != null) { _bladeThreshLine.IsVisible = false; }

                var bladeLegend = BladeMonitorPlot.ShowLegend(Alignment.UpperRight);
                bladeLegend.FontName = _chineseFont;
                BladeMonitorPlot.Axes.SetLimitsX(0, xMax);
                BladeMonitorPlot.Axes.AutoScaleY();
            });
            BladePlotDirty = true;
        }

        private void UpdateBlockageMonitor(double[] currentData)
        {
            if (currentData.Length < 2) return;

            // 1. 计算实际监测周期 (ProcessLoop 迭代周期)
            double monitoringPeriod = (double)_overlapSize / DefaultSampleRate;

            // 2. 调用检测器
            var result = _blockageDetector.Process(currentData, DefaultSampleRate, monitoringPeriod);

            // 3. 更新绑定属性
            BlockageRmsText = $"{result.CurrentRms:F3}";
            BlockageDeltaIText = $"{result.DeviationDeltaI:F2}";
            BlockageConsecutiveText = $"{result.ConsecutiveCount}/{result.ConsecutiveRequired}";

            // 4. 基线状态
            if (!result.IsCalibrated)
                BlockageBaselineText = $"标定中 ({result.CalibrationCount}/{_blockageDetector.CalibrationPeriodsRequired})";
            else
                BlockageBaselineText = $"μ={result.BaselineMean:F3} σ={result.BaselineStdDev:F3}";

            // 5. 报警状态颜色映射
            (BlockageAlarmText, BlockageAlarmColor) = result.AlarmState switch
            {
                BlockageAlarmState.Normal      => ("正常",   ColorGreen),
                BlockageAlarmState.Calibrating => ("标定中", ColorGray),
                BlockageAlarmState.Warning     => ("预警",   ColorOrange),
                BlockageAlarmState.Alarm       => ("报警!",  ColorRed),
                _ => ("未知", ColorGray)
            };

            // 6. 进度条（由检测器驱动）
            ExceedDurationValue = Math.Min(result.ExceedDurationSeconds, 60.0);
            ExceedDurationMax = 60.0;
            ExceedDurationText = $"超限持续时间: {result.ExceedDurationSeconds:F0}s / 60s";
            ExceedDurationColor = result.AlarmState switch
            {
                BlockageAlarmState.Alarm   => ColorRed,
                BlockageAlarmState.Warning => ColorOrange,
                _                          => ColorGreen
            };

            // 7. 图表更新：入队，由 UI 线程统一执行
            var capturedCurrentData = currentData;
            var capturedBlockageResult = result;
            PlotActions.Enqueue(() =>
            {
                double mu = capturedBlockageResult.IsCalibrated ? capturedBlockageResult.BaselineMean : 15.0;
                double sigma = capturedBlockageResult.IsCalibrated ? capturedBlockageResult.BaselineStdDev : 0.5;
                if (sigma < 1e-9) sigma = 0.5;
                var normalizedData = capturedCurrentData.Select(v => (v - mu) / sigma).ToArray();

                if (_blockageSig == null)
                {
                    _blockageSig = BlockageMonitorPlot.Add.Signal(normalizedData);
                    _blockageSig.Label = "ΔI 归一化偏差";
                    _blockageSig.Color = ScottPlot.Color.FromHex("#E67E22");
                }
                else
                {
                    _blockageSig.Data = new SignalSourceDouble(normalizedData, 1.0);
                }

                if (_blockageUpperLine == null)
                {
                    _blockageUpperLine = BlockageMonitorPlot.Add.HorizontalLine(3.0);
                    _blockageUpperLine.LineStyle.Color = ScottPlot.Color.FromHex("#E74C3C");
                    _blockageUpperLine.LineStyle.Width = 1.5f;
                    _blockageUpperLine.LineStyle.Pattern = LinePattern.Dashed;

                    _blockageLowerLine = BlockageMonitorPlot.Add.HorizontalLine(-3.0);
                    _blockageLowerLine!.LineStyle.Color = ScottPlot.Color.FromHex("#E74C3C");
                    _blockageLowerLine.LineStyle.Width = 1.5f;
                    _blockageLowerLine.LineStyle.Pattern = LinePattern.Dashed;
                }

                BlockageMonitorPlot.Axes.AutoScale();
            });
            BlockagePlotDirty = true;
        }

        private void UpdateAuxMonitor(double[] vibrationData, double fs)
        {
            // 计算振动峭度作为辅助特征
            double mean = vibrationData.Average();
            double variance = vibrationData.Average(v => (v - mean) * (v - mean));
            double kurtosis = variance > 0
                ? vibrationData.Average(v => Math.Pow((v - mean) / Math.Sqrt(variance), 4))
                : 3.0;

            AmbientTemp = $"{25.0 + Random.Shared.NextDouble() * 3:F1} °C";
            TempCompStatus = kurtosis > 3.5 ? "补偿激活" : "已补偿";

            // 更新辅助验证图：入队，由 UI 线程统一执行
            PlotActions.Enqueue(() =>
            {
                double[] tempRange = Enumerable.Range(0, 50).Select(i => 22.0 + i * 0.2).ToArray();
                double[] tiltDrift = tempRange.Select(t => 0.01 * (t - 25) + (Random.Shared.NextDouble() - 0.5) * 0.02).ToArray();

                if (_auxScatter == null)
                {
                    _auxScatter = AuxMonitorPlot.Add.Scatter(tempRange, tiltDrift);
                    _auxScatter.Label = "温度 vs 倾角零偏";
                    _auxScatter.Color = ScottPlot.Color.FromHex("#1ABC9C");
                    _auxScatter.LineStyle.Width = 1.5f;

                    _auxRefLine = AuxMonitorPlot.Add.HorizontalLine(0);
                    _auxRefLine.LineStyle.Color = ScottPlot.Color.FromHex("#999");
                    _auxRefLine.LineStyle.Width = 1;

                    var auxLegend = AuxMonitorPlot.ShowLegend();
                    auxLegend.FontName = _chineseFont;
                }
                else
                {
                    AuxMonitorPlot.Remove(_auxScatter);
                    _auxScatter = AuxMonitorPlot.Add.Scatter(tempRange, tiltDrift);
                    _auxScatter.Label = "温度 vs 倾角零偏";
                    _auxScatter.Color = ScottPlot.Color.FromHex("#1ABC9C");
                    _auxScatter.LineStyle.Width = 1.5f;
                }

                AuxMonitorPlot.Axes.AutoScale();
            });
            AuxPlotDirty = true;
        }

        #endregion

        #region 命令

        /// <summary>
        /// 执行基线标定命令
        /// </summary>
        [RelayCommand]
        public async Task ExecuteCalibration()
        {
            try
            {
                _bladeDetector.ResetCalibration();
                BaselineStatus = $"标定中 (0/{_bladeDetector.CalibrationPeriodsRequired})";
                BladeAlarmText = "标定中";
                BladeAlarmColor = ColorGray;

                // 重置堵塞检测器
                _blockageDetector.ResetCalibration();
                BlockageBaselineText = $"标定中 (0/{_blockageDetector.CalibrationPeriodsRequired})";
                BlockageAlarmText = "标定中";
                BlockageAlarmColor = ColorGray;

                // 重置螺栓松动检测器
                _boltDetector.ResetCalibration();
                BoltBaselineText = $"标定中 (0/{_boltDetector.CalibrationPeriodsRequired})";
                BoltAlarmText = "标定中";
                BoltAlarmColor = ColorGray;
                BoltTempCompText = "待标定";

                DiagnosisResult = "基线标定已重置（叶片不平衡 + 风口堵塞 + 螺栓松动），自动采集进行中...";
                await Task.Delay(100); // UI 刷新
                // 标定在 ProcessLoop 中自动进行 (前 30 期使用 Welford 算法)
            }
            catch (Exception ex)
            {
                DiagnosisResult = $"标定重置失败：{ex.Message}";
            }
        }

        #endregion

        #region 资源释放

        public void Dispose()
        {
            _sensorDataService.MetadataUpdated -= OnSensorMetadataUpdated;
            _cts.Cancel();
            _cts.Dispose();
        }

        #endregion
    }
}
