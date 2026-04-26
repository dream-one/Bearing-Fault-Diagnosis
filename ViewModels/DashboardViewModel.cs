using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Models;
using BearingFaultDiagnosis.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using ScottPlot.Plottables;

namespace BearingFaultDiagnosis.ViewModels
{
    public partial class DashboardViewModel : ViewModelBase
    {
        private readonly ITCPServerService _tcpService;
        private readonly ServerSettings _settings;
        private readonly ISensorDataService _sensorDataService;
        private CancellationTokenSource _cts;
        public ObservableCollection<object> DisplayList { get; } = new ObservableCollection<object>();

        private System.Timers.Timer _timer;
        [ObservableProperty]
        private SensorDisplayItem _currentSensorData;
        private DataStreamer _streamerV1_X;
        private DataStreamer _streamerV1_Y;
        private DataStreamer _streamerV1_Z;
        // 暴露出 Plot 对象供 View 使用
        public ScottPlot.Plot VibrationPlot { get; } = new();
        private bool _isReadingFile = false;
        public DashboardViewModel(ITCPServerService tcpService, IOptions<ServerSettings> options, ISensorDataService sensorDataService)
        {
            _tcpService = tcpService;
            _settings = options.Value;
            _sensorDataService = sensorDataService;
            _tcpService.OnMessageReceived += OnMessageReceivedHandler;
            InitChartForTcp();
            //每一百毫秒更新日志
            _timer = new System.Timers.Timer(500);
            _timer.Elapsed += (s, e) => _ = FlushDisplayListAsync();
        }

        /// <summary>
        /// 读取文件夹
        /// </summary>
        [RelayCommand]
        public async Task ReadData()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择包含 .bin 文件的文件夹"
            };

            if (dialog.ShowDialog() == true)
            {
                string folderPath = dialog.FolderName;
                await _sensorDataService.ProcessFolderAsync(folderPath,
                    message => App.Current.Dispatcher.Invoke(() => DisplayList.Add(message)),
                    meta => App.Current.Dispatcher.Invoke(() => InitChartForFile(meta)));
            }
        }
        /// <summary>
        /// 把数据从 SensorDataService 的 BufferV1_X/Y/Z 里取出来，批量推送到 ScottPlot 的 DataStreamer 里
        /// </summary>
        /// <returns></returns>
        public bool TryConsumeBufferForFrame()
        {
            int currentCount = _sensorDataService.BufferV1_X.Count;
            if (currentCount == 0) return false;
            if (_isReadingFile == false)
            {
                double[] batchX = new double[currentCount];
                double[] batchY = new double[currentCount];
                double[] batchZ = new double[currentCount];

                for (int i = 0; i < currentCount; i++)
                {
                    _sensorDataService.BufferV1_X.TryDequeue(out batchX[i]);
                    _sensorDataService.BufferV1_Y.TryDequeue(out batchY[i]);
                    _sensorDataService.BufferV1_Z.TryDequeue(out batchZ[i]);
                }

                _streamerV1_X.AddRange(batchX);
                _streamerV1_Y.AddRange(batchY);
                _streamerV1_Z.AddRange(batchZ);
            }
            else
            {
                double[] batchX = new double[currentCount];
                for (int i = 0; i < currentCount; i++)
                {
                    _sensorDataService.BufferV1_X.TryDequeue(out batchX[i]);
                }
                _streamerV1_X.AddRange(batchX);
            }
            return true;
        }

        public void InitChartForTcp()
        {
            VibrationPlot.Clear();
            _isReadingFile = false;

            // 1. 初始化扫描器
            _streamerV1_X = VibrationPlot.Add.DataStreamer(12800);
            _streamerV1_Y = VibrationPlot.Add.DataStreamer(12800);
            _streamerV1_Z = VibrationPlot.Add.DataStreamer(12800);

            // ==========================================
            // 🎨 1. 高级感配色设置 (支持十六进制颜色)
            // ==========================================
            // X轴：科技青色 (Cyan)
            _streamerV1_X.Color = ScottPlot.Color.FromHex("#00B4D8");
            // Y轴：珊瑚橙色 (Coral)
            _streamerV1_Y.Color = ScottPlot.Color.FromHex("#F77F00");
            // Z轴：紫罗兰色 (Violet)
            _streamerV1_Z.Color = ScottPlot.Color.FromHex("#7B2CBF");

            // 可选：稍微把线调粗一点点，质感更好 (默认是 1)
            _streamerV1_X.LineStyle.Width = 1.5f;
            _streamerV1_Y.LineStyle.Width = 1.5f;
            _streamerV1_Z.LineStyle.Width = 1.5f;

            // ==========================================
            // 🏷️ 2. 添加图例 (Legend)
            // ==========================================
            // 告诉图例这三根线叫什么名字
            string zhFont = ScottPlot.Fonts.Detect("中文");
            _streamerV1_X.LegendText = "X 轴振动";
            _streamerV1_Y.LegendText = "Y 轴振动";
            _streamerV1_Z.LegendText = "Z 轴振动";

            // 开启图例显示，并放在右上角
            VibrationPlot.ShowLegend(ScottPlot.Alignment.UpperRight);

            // 让图例的背景变成半透明白色，不至于完全遮住后面扫过的波形
            VibrationPlot.Legend.BackgroundColor = ScottPlot.Colors.White.WithAlpha(0.8);
            VibrationPlot.Legend.FontSize = 14;
            VibrationPlot.Legend.FontName = zhFont;

            // ==========================================
            // ⏱️ 3. 换算 X 轴为真实时间 (2000Hz 采样率)
            // ==========================================
            double samplePeriod = 1.0 / 2000.0;
            _streamerV1_X.Period = samplePeriod;
            _streamerV1_Y.Period = samplePeriod;
            _streamerV1_Z.Period = samplePeriod;

            // ==========================================
            // 💎 4. 整体美化与网格弱化
            // ==========================================
            // 主标题与坐标轴标签
            VibrationPlot.Axes.Title.Label.Text = "主轴高频振动实时监测";
            VibrationPlot.Axes.Title.Label.FontSize = 18;
            VibrationPlot.Axes.Bottom.Label.Text = "时间 (秒)";
            VibrationPlot.Axes.Bottom.Label.FontSize = 14;
            VibrationPlot.Axes.Left.Label.Text = "振幅 (g)"; // 这里根据你传感器的实际单位修改，比如 g 或 mm/s
            VibrationPlot.Axes.Left.Label.FontSize = 14;
            VibrationPlot.Axes.Title.Label.FontName = zhFont; // 【关键】设置主标题字体
            // 让网格线变成极简的浅灰色虚线
            VibrationPlot.Grid.MajorLineColor = ScottPlot.Colors.LightGray.WithAlpha(0.5);
            VibrationPlot.Grid.MajorLinePattern = ScottPlot.LinePattern.Dotted;

            // 背景颜色微调 (外框纯白，数据区极浅灰，拉开视觉层次)
            VibrationPlot.FigureBackground.Color = ScottPlot.Colors.White;
            VibrationPlot.DataBackground.Color = ScottPlot.Color.FromHex("#FAFAFA");
            VibrationPlot.Axes.Bottom.Label.FontName = zhFont; // 【关键】设置 X 轴标签字体
            VibrationPlot.Axes.Bottom.TickLabelStyle.FontName = zhFont; // 让底部的刻度数字字体统一
            VibrationPlot.Axes.Left.Label.FontName = zhFont; // 【关键】设置 Y 轴标签字体
            VibrationPlot.Axes.Left.TickLabelStyle.FontName = zhFont; // 让左侧的刻度数字字体统一

            _streamerV1_X.ViewScrollLeft();
            _streamerV1_Y.ViewScrollLeft();
            _streamerV1_Z.ViewScrollLeft();
        }

        public void InitChartForFile(PuMetadata meta)
        {
            VibrationPlot.Clear();
            _isReadingFile = true;

            // 1. 初始化扫描器 (单轴, 适用离线数据)
            _streamerV1_X = VibrationPlot.Add.DataStreamer(12800 * 2); // 适当增大以适应高采样率观察

            _streamerV1_X.Color = ScottPlot.Color.FromHex("#00B4D8");
            _streamerV1_X.LineStyle.Width = 1.5f;

            string zhFont = ScottPlot.Fonts.Detect("中文");
            _streamerV1_X.LegendText = meta != null && !string.IsNullOrEmpty(meta.channel) ? $"{meta.channel} 轴振动" : "X 轴振动";

            VibrationPlot.ShowLegend(ScottPlot.Alignment.UpperRight);
            VibrationPlot.Legend.BackgroundColor = ScottPlot.Colors.White.WithAlpha(0.8);
            VibrationPlot.Legend.FontSize = 14;
            VibrationPlot.Legend.FontName = zhFont;

            // 根据从 meta 读取的 sample_rate 配置时间轴
            double sampleRate = (meta != null && meta.sample_rate > 0) ? meta.sample_rate : 64000.0;
            double samplePeriod = 1.0 / sampleRate;
            _streamerV1_X.Period = samplePeriod;

            // 将元数据信息放入主标题中
            string titleMeta = meta != null ? $"转速: {meta.rpm} RPM | 采样率: {meta.sample_rate} Hz" : "64kHz";
            VibrationPlot.Axes.Title.Label.Text = $"回放文件 - {(meta != null ? meta.source_file : "未知")} ({titleMeta})";
            VibrationPlot.Axes.Title.Label.FontSize = 16;
            VibrationPlot.Axes.Bottom.Label.Text = "时间 (秒)";
            VibrationPlot.Axes.Bottom.Label.FontSize = 14;
            VibrationPlot.Axes.Left.Label.Text = "振幅";
            VibrationPlot.Axes.Left.Label.FontSize = 14;
            VibrationPlot.Axes.Title.Label.FontName = zhFont;

            VibrationPlot.Grid.MajorLineColor = ScottPlot.Colors.LightGray.WithAlpha(0.5);
            VibrationPlot.Grid.MajorLinePattern = ScottPlot.LinePattern.Dotted;

            VibrationPlot.FigureBackground.Color = ScottPlot.Colors.White;
            VibrationPlot.DataBackground.Color = ScottPlot.Color.FromHex("#FAFAFA");
            VibrationPlot.Axes.Bottom.Label.FontName = zhFont;
            VibrationPlot.Axes.Bottom.TickLabelStyle.FontName = zhFont;
            VibrationPlot.Axes.Left.Label.FontName = zhFont;
            VibrationPlot.Axes.Left.TickLabelStyle.FontName = zhFont;

            _streamerV1_X.ViewScrollLeft();
        }
        /// <summary>
        /// 更新日志模块
        /// </summary>
        /// <returns></returns>
        private async Task FlushDisplayListAsync()
        {
            if (_cts.Token.IsCancellationRequested || _tcpService.isListening == false) return;
            var batch = new ConcurrentBag<object>();

            // TryRead 非阻塞，把当前 Channel 里有的全取出来
            while (_tcpService.UiReader.TryRead(out var frame))
            {
                batch.Add(SensorDisplayItem.FromStruct(frame));
            }
            CurrentSensorData = (SensorDisplayItem)batch.Last();
            var displayBatch = batch.TakeLast(1).ToList();
            if (batch.Count > 0)
            {
                // 切回 UI 线程更新列
                await App.Current.Dispatcher.InvokeAsync(() =>
                {

                    foreach (var item in displayBatch)
                    {
                        DisplayList.Add(item);
                    }
                    // 防爆内存机制：如果日志超过 2000 条，把最老的数据删掉
                    while (DisplayList.Count > 100000)
                    {
                        DisplayList.RemoveAt(0);
                    }
                });
            }

        }

        private void OnMessageReceivedHandler(string ip, string message)
        {
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                DisplayList.Add($"来自 {ip} 的消息: {message}");
            });
        }



        [RelayCommand]
        public void StartServer()
        {
            InitChartForTcp();
            _cts = new CancellationTokenSource();
            _ = _tcpService.StartListeningAsync(_settings.Port, _cts.Token); _timer.Start();
            _sensorDataService.HandleVibrationDataForChart();
        }

        [RelayCommand]
        public void StopServer()
        {
            _cts?.Cancel();
            _timer.Stop();
        }

        public void Dispose()
        {
            _tcpService.OnMessageReceived -= OnMessageReceivedHandler;
            _cts?.Cancel();
            _timer.Dispose();
        }
    }
}
