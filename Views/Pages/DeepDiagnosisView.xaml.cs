using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BearingFaultDiagnosis.Models;
using BearingFaultDiagnosis.ViewModels;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;

namespace BearingFaultDiagnosis.Views.Pages
{
    public partial class DeepDiagnosisView : UserControl
    {
        private DeepDiagnosisViewModel? _viewModel;

        // 无锁队列：振动加速度 / 电流 / 倾角数据
        private readonly ConcurrentQueue<double[]> _vibrationQueue = new();
        private readonly ConcurrentQueue<double[]> _currentQueue = new();
        private readonly ConcurrentQueue<double[]> _tiltQueue = new();

        private bool _isRendering = false;
        private DeepDiagnosisViewModel _vm;

        // 复用的 Signal 对象（避免 Clear+Add 导致集合修改异常）
        private Signal? _vibSig;
        private Signal? _currentSig;
        private Signal? _tiltSig;

        // 中文字体（用于图例显示）
        private readonly string _chineseFont = ScottPlot.Fonts.Detect("测试");

        public DeepDiagnosisView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            this.Loaded += DeepDiagnosisView_Loaded;
            this.Unloaded += DeepDiagnosisView_Unloaded;

            SetupPlotLabels();
        }

        private void DeepDiagnosisView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isRendering)
            {
                CompositionTarget.Rendering -= OnFrameRender;
                _isRendering = false;
            }
        }

        private void SetupPlotLabels()
        {
            string chineseFont = _chineseFont;

            VibrationPlot.Plot.XLabel("时间 (s)");
            VibrationPlot.Plot.YLabel("幅值 (m/s²)");
            VibrationPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            VibrationPlot.Plot.Axes.Left.Label.FontName = chineseFont;

            CurrentTiltPlot.Plot.XLabel("时间 (s)");
            CurrentTiltPlot.Plot.YLabel("电流 (A)");
            CurrentTiltPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            CurrentTiltPlot.Plot.Axes.Left.Label.FontName = chineseFont;
            CurrentTiltPlot.Plot.Axes.Right.Label.Text = "倾角 (°)";
            CurrentTiltPlot.Plot.Axes.Right.Label.FontName = chineseFont;
            CurrentTiltPlot.Plot.Axes.Right.IsVisible = true;

            BoltMonitorPlot.Plot.XLabel("时间");
            BoltMonitorPlot.Plot.YLabel("Δθ (°)");
            BoltMonitorPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            BoltMonitorPlot.Plot.Axes.Left.Label.FontName = chineseFont;

            BladeMonitorPlot.Plot.XLabel("频率 (Hz)");
            BladeMonitorPlot.Plot.YLabel("幅值");
            BladeMonitorPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            BladeMonitorPlot.Plot.Axes.Left.Label.FontName = chineseFont;

            BlockageMonitorPlot.Plot.XLabel("时间");
            BlockageMonitorPlot.Plot.YLabel("归一化偏差 ΔI");
            BlockageMonitorPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            BlockageMonitorPlot.Plot.Axes.Left.Label.FontName = chineseFont;

            AuxMonitorPlot.Plot.XLabel("温度 (°C)");
            AuxMonitorPlot.Plot.YLabel("倾角零偏 (°)");
            AuxMonitorPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            AuxMonitorPlot.Plot.Axes.Left.Label.FontName = chineseFont;
        }

        private void DeepDiagnosisView_Loaded(object sender, RoutedEventArgs e)
        {
            _vm = DataContext as DeepDiagnosisViewModel;
            if (_vm == null) return;

            _vm.BindPlots(
                BoltMonitorPlot.Plot,
                BladeMonitorPlot.Plot,
                BlockageMonitorPlot.Plot,
                AuxMonitorPlot.Plot);

            if (!_isRendering)
            {
                CompositionTarget.Rendering += OnFrameRender;
                _isRendering = true;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.OnVibrationDataReady -= OnVibrationDataReceived;
                _viewModel.OnCurrentDataReady -= OnCurrentDataReceived;
                _viewModel.OnTiltDataReady -= OnTiltDataReceived;
            }

            _viewModel = e.NewValue as DeepDiagnosisViewModel;

            if (_viewModel != null)
            {
                _viewModel.OnVibrationDataReady += OnVibrationDataReceived;
                _viewModel.OnCurrentDataReady += OnCurrentDataReceived;
                _viewModel.OnTiltDataReady += OnTiltDataReceived;
            }
        }

        // --- 生产者：后台线程推送数据 ---
        private void OnVibrationDataReceived(double[] data) => _vibrationQueue.Enqueue(data);
        private void OnCurrentDataReceived(double[] data) => _currentQueue.Enqueue(data);
        private void OnTiltDataReceived(double[] data) => _tiltQueue.Enqueue(data);

        private void OnFrameRender(object? sender, EventArgs e)
        {
            try
            {
                // ─── 1. 消费振动/电流/倾角队列数据 ───
                double[]? lastVibration = null;
                while (_vibrationQueue.TryDequeue(out var vib))
                    lastVibration = vib;

                double[]? lastCurrent = null;
                while (_currentQueue.TryDequeue(out var cur))
                    lastCurrent = cur;

                double[]? lastTilt = null;
                while (_tiltQueue.TryDequeue(out var tilt))
                    lastTilt = tilt;

                // ─── 2. 振动图：更新数据源 + 刷新 ───
                if (lastVibration != null)
                {
                    if (_vibSig == null)
                    {
                        _vibSig = VibrationPlot.Plot.Add.Signal(lastVibration);
                    }
                    else
                    {
                        _vibSig.Data = new SignalSourceDouble(lastVibration, 1.0 / 5000.0);
                    }
                    double totalTime = lastVibration.Length / 5000.0;
                    VibrationPlot.Plot.Axes.SetLimitsX(0, totalTime);
                    VibrationPlot.Plot.Axes.AutoScaleY();
                    VibrationPlot.Refresh();
                }

                // ─── 3. 电流/倾角图：更新数据源 + 刷新 ───
                if (lastCurrent != null)
                {
                    if (_currentSig == null)
                    {
                        _currentSig = CurrentTiltPlot.Plot.Add.Signal(lastCurrent);
                        _currentSig.Label = "电流 (A)";
                        _currentSig.Color = ScottPlot.Color.FromHex("#3498DB");
                    }
                    else
                    {
                        _currentSig.Data = new SignalSourceDouble(lastCurrent, 1.0 / 5000.0);
                    }
                }
                if (lastTilt != null)
                {
                    if (_tiltSig == null)
                    {
                        _tiltSig = CurrentTiltPlot.Plot.Add.Signal(lastTilt);
                        _tiltSig.Label = "倾角 (°)";
                        _tiltSig.Color = ScottPlot.Color.FromHex("#E74C3C");
                        _tiltSig.Axes.YAxis = CurrentTiltPlot.Plot.Axes.Right;
                    }
                    else
                    {
                        double tiltPeriod = (lastCurrent?.Length ?? 32768.0) / (5000.0 * lastTilt.Length);
                        _tiltSig.Data = new SignalSourceDouble(lastTilt, tiltPeriod);
                    }
                }
                if (lastCurrent != null || lastTilt != null)
                {
                    CurrentTiltPlot.Plot.Axes.AutoScale();
                    var legend = CurrentTiltPlot.Plot.ShowLegend();
                    legend.FontName = _chineseFont;
                    CurrentTiltPlot.Refresh();
                }

                // ─── 4. 算法卡片：出队执行所有 Plot Action（统一在 UI 线程） ───
                bool anyDirty = false;
                while (_vm != null && _vm.PlotActions.TryDequeue(out var action))
                {
                    action();
                    anyDirty = true;
                }

                // ─── 5. 统一刷新所有脏标记的算法卡片 ───
                if (anyDirty || _vm?.BoltPlotDirty == true || _vm?.BladePlotDirty == true ||
                    _vm?.BlockagePlotDirty == true || _vm?.AuxPlotDirty == true)
                {
                    if (_vm != null)
                    {
                        if (_vm.BoltPlotDirty) { BoltMonitorPlot.Refresh(); _vm.BoltPlotDirty = false; }
                        if (_vm.BladePlotDirty) { BladeMonitorPlot.Refresh(); _vm.BladePlotDirty = false; }
                        if (_vm.BlockagePlotDirty) { BlockageMonitorPlot.Refresh(); _vm.BlockagePlotDirty = false; }
                        if (_vm.AuxPlotDirty) { AuxMonitorPlot.Refresh(); _vm.AuxPlotDirty = false; }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnFrameRender] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
