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
        // ==================== 私有字段 ====================
        private readonly ITCPServerService _tcpService;
        private readonly ServerSettings _settings;
        private readonly ISensorDataService _sensorDataService;
        private CancellationTokenSource _cts;
        private System.Timers.Timer _timer;
        private DataStreamer _streamerV1_X;
        private DataStreamer _streamerV1_Y;
        private DataStreamer _streamerV1_Z;
        private bool _isReadingFile = false;

        // ==================== 属性 ====================
        public ObservableCollection<object> DisplayList { get; } = new ObservableCollection<object>();

        [ObservableProperty]
        private SensorDisplayItem _currentSensorData;

        public ScottPlot.Plot VibrationPlot { get; } = new();

        // ==================== 构造函数 ====================
        public DashboardViewModel(ITCPServerService tcpService, IOptions<ServerSettings> options, ISensorDataService sensorDataService)
        {
            _tcpService = tcpService;
            _settings = options.Value;
            _sensorDataService = sensorDataService;
            _tcpService.OnMessageReceived += OnMessageReceivedHandler;
            InitChartForTcp();
            _timer = new System.Timers.Timer(500);
            _timer.Elapsed += (s, e) => _ = FlushDisplayListAsync();
        }

        // ==================== 命令方法 ====================
        /// <summary>
        /// 读取文件夹中的 .bin 数据文件
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
            //同时停止读取文件数据
            _sensorDataService.StopFileReading();
        }

        // ==================== 公共方法 ====================
        /// <summary>
        /// 把数据从 SensorDataService 取出来，批量推送到 ScottPlot 的 DataStreamer
        /// </summary>
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

        /// <summary>
        /// 初始化 TCP 实时振动图表（三轴配色 + 图例 + 时间轴）
        /// </summary>
        public void InitChartForTcp()
        {
            VibrationPlot.Clear();
            _isReadingFile = false;

            // 1. 初始化 DataStreamer
            _streamerV1_X = VibrationPlot.Add.DataStreamer(12800);
            _streamerV1_Y = VibrationPlot.Add.DataStreamer(12800);
            _streamerV1_Z = VibrationPlot.Add.DataStreamer(12800);

            // ========== 配色设置 ==========
            // X 轴：科技青色
            _streamerV1_X.Color = ScottPlot.Color.FromHex("#00B4D8");
            // Y 轴：珊瑚橙色
            _streamerV1_Y.Color = ScottPlot.Color.FromHex("#F77F00");
            // Z 轴：紫罗兰色
            _streamerV1_Z.Color = ScottPlot.Color.FromHex("#7B2CBF");

            _streamerV1_X.LineStyle.Width = 1.5f;
            _streamerV1_Y.LineStyle.Width = 1.5f;
            _streamerV1_Z.LineStyle.Width = 1.5f;

            string zhFont = ScottPlot.Fonts.Detect("中文");
            // ========== 图例设置 ==========
            _streamerV1_X.LegendText = "X 轴振动";
            _streamerV1_Y.LegendText = "Y 轴振动";
            _streamerV1_Z.LegendText = "Z 轴振动";

            VibrationPlot.ShowLegend(ScottPlot.Alignment.UpperRight);
            VibrationPlot.Legend.BackgroundColor = ScottPlot.Colors.White.WithAlpha(0.8);
            VibrationPlot.Legend.FontSize = 14;
            VibrationPlot.Legend.FontName = zhFont;

            // ========== 时间轴换算 (2000Hz 采样率) ==========
            double samplePeriod = 1.0 / 2000.0;
            _streamerV1_X.Period = samplePeriod;
            _streamerV1_Y.Period = samplePeriod;
            _streamerV1_Z.Period = samplePeriod;

            // ========== 整体美化与网格弱化 ==========
            VibrationPlot.Axes.Title.Label.Text = "主轴高频振动实时监测";
            VibrationPlot.Axes.Title.Label.FontSize = 18;
            VibrationPlot.Axes.Bottom.Label.Text = "时间 (秒)";
            VibrationPlot.Axes.Bottom.Label.FontSize = 14;
            VibrationPlot.Axes.Left.Label.Text = "振幅 (g)";
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
            _streamerV1_Y.ViewScrollLeft();
            _streamerV1_Z.ViewScrollLeft();
        }

        /// <summary>
        /// 初始化文件回放图表（根据元数据配置采样率与标题）
        /// </summary>
        public void InitChartForFile(PuMetadata meta)
        {
            VibrationPlot.Clear();
            _isReadingFile = true;

            _streamerV1_X = VibrationPlot.Add.DataStreamer(12800 * 2);

            _streamerV1_X.Color = ScottPlot.Color.FromHex("#00B4D8");
            _streamerV1_X.LineStyle.Width = 1.5f;

            string zhFont = ScottPlot.Fonts.Detect("中文");
            _streamerV1_X.LegendText = meta != null && !string.IsNullOrEmpty(meta.channel) ? $"{meta.channel} 轴振动" : "X 轴振动";

            VibrationPlot.ShowLegend(ScottPlot.Alignment.UpperRight);
            VibrationPlot.Legend.BackgroundColor = ScottPlot.Colors.White.WithAlpha(0.8);
            VibrationPlot.Legend.FontSize = 14;
            VibrationPlot.Legend.FontName = zhFont;

            double sampleRate = (meta != null && meta.sample_rate > 0) ? meta.sample_rate : 64000.0;
            double samplePeriod = 1.0 / sampleRate;
            _streamerV1_X.Period = samplePeriod;

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

        // ==================== 私有方法 ====================
        /// <summary>
        /// 定期刷新日志列表（从 Channel 消费数据显示到 UI）
        /// </summary>
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
                // 切回 UI 线程更新列表
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in displayBatch)
                    {
                        DisplayList.Add(item);
                    }
                    // 防爆内存：日志超过阈值时移除最旧数据
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

        // ==================== 资源释放 ====================
        public void Dispose()
        {
            _tcpService.OnMessageReceived -= OnMessageReceivedHandler;
            _cts?.Cancel();
            _timer.Dispose();
        }
    }
}
