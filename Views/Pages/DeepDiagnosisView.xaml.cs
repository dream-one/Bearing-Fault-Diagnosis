using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using BearingFaultDiagnosis.ViewModels;
using ScottPlot;

namespace BearingFaultDiagnosis.Views.Pages
{
    /// <summary>
    /// DeepDiagnosisView.xaml 的交互逻辑
    /// </summary>
    public partial class DeepDiagnosisView : UserControl
    {
        private DeepDiagnosisViewModel? _viewModel;

        // 采用 ConcurrentQueue 实现无锁并发与生产消费模型
        private readonly ConcurrentQueue<double[]> _rawQueue = new();
        private readonly ConcurrentQueue<double[]> _pureQueue = new();
        private readonly ConcurrentQueue<SpectrumData> _orderQueue = new();
        private readonly ConcurrentQueue<SpectrumData> _envQueue = new();
        private readonly ConcurrentQueue<CwtHeatmapData> _cwtQueue = new();

        private WriteableBitmap? _cwtBitmap;
        private static readonly byte[] InfernoLut = GenerateInfernoLut();

        private bool _isRendering = false;
        private DeepDiagnosisViewModel _vm;

        public DeepDiagnosisView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            // 订阅生命周期事件，管理硬件渲染循环
            this.Loaded += DeepDiagnosisView_Loaded;
            this.Unloaded += DeepDiagnosisView_Unloaded;

            SetupPlotLabels();
        }
        

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeepDiagnosisViewModel.BarPlot))
            {
                // 必须在 UI 线程刷新
                Dispatcher.Invoke(() => SoftMaxProbabiltyBar.Refresh());
            }
        }

        private void DeepDiagnosisView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= Vm_PropertyChanged;
            }
            if (_isRendering)
            {
                CompositionTarget.Rendering -= OnFrameRender;
                _isRendering = false;
            }
        }
        private void SetupPlotLabels()
        {
            // 自动检测支持中文的字体
            string chineseFont = ScottPlot.Fonts.Detect("测试");
           

            RawPlot.Plot.XLabel("时间 (s)");
            RawPlot.Plot.YLabel("幅值");
            RawPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            RawPlot.Plot.Axes.Left.Label.FontName = chineseFont;

            PurePlot.Plot.XLabel("圈数 (Revolution)");
            PurePlot.Plot.YLabel("幅值");
            PurePlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            PurePlot.Plot.Axes.Left.Label.FontName = chineseFont;

            OrderPlot.Plot.XLabel("阶次 (Order)");
            OrderPlot.Plot.YLabel("幅值");
            OrderPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            OrderPlot.Plot.Axes.Left.Label.FontName = chineseFont;

            EnvPlot.Plot.XLabel("频率 (Hz)");
            EnvPlot.Plot.YLabel("幅值");
            EnvPlot.Plot.Axes.Bottom.Label.FontName = chineseFont;
            EnvPlot.Plot.Axes.Left.Label.FontName = chineseFont;
        }

        private void DeepDiagnosisView_Loaded(object sender, RoutedEventArgs e)
        {
            _vm = DataContext as DeepDiagnosisViewModel;
            if (_vm == null) return;

            // 初始化柱状图
            SoftMaxProbabiltyBar.Reset(_vm.BarPlot);

            // 绑定属性变更事件，当 ViewModel 修改 BarPlot 后自动刷新
            _vm.PropertyChanged += Vm_PropertyChanged;

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
                _viewModel.OnRawDataReady -= OnRawDataReceived;
                _viewModel.OnPureDataReady -= OnPureDataReceived;
                _viewModel.OnOrderSpectrumReady -= OnOrderSpectrumReceived;
                _viewModel.OnEnvelopeSpectrumReady -= OnEnvelopeSpectrumReceived;
                _viewModel.OnCwtHeatmapReady -= OnCwtHeatmapReceived;
            }

            _viewModel = e.NewValue as DeepDiagnosisViewModel;

            if (_viewModel != null)
            {
                _viewModel.OnRawDataReady += OnRawDataReceived;
                _viewModel.OnPureDataReady += OnPureDataReceived;
                _viewModel.OnOrderSpectrumReady += OnOrderSpectrumReceived;
                _viewModel.OnEnvelopeSpectrumReady += OnEnvelopeSpectrumReceived;
                _viewModel.OnCwtHeatmapReady += OnCwtHeatmapReceived;
            }
        }

        // --- 生产者：后台线程推送数据，利用无锁队列彻底告别耗时的 InvokeAsync 阻塞 ---
        private void OnRawDataReceived(double[] data)
        {
            _rawQueue.Enqueue(data);
        }

        private void OnPureDataReceived(double[] data)
        {
            _pureQueue.Enqueue(data);
        }

        private void OnOrderSpectrumReceived(SpectrumData data)
        {
            _orderQueue.Enqueue(data);
        }

        private void OnEnvelopeSpectrumReceived(SpectrumData data)
        {
            _envQueue.Enqueue(data);
        }

        private void OnCwtHeatmapReceived(CwtHeatmapData data)
        {
            _cwtQueue.Enqueue(data);
        }

        
        private void OnFrameRender(object? sender, EventArgs e)
        {
            bool needRefreshRaw = false;
            double[]? lastRaw = null;
            
            while (_rawQueue.TryDequeue(out var data))
            {
                lastRaw = data;
            }

            if (lastRaw != null)
            {
                RawPlot.Plot.Clear();
                double sampleRate = 64000.0; // 默认
                double period = 1.0 / sampleRate;
                var sig = RawPlot.Plot.Add.Signal(lastRaw);
                sig.Data.Period = period;
                RawPlot.Plot.Axes.AutoScale();
                needRefreshRaw = true;
            }

            bool needRefreshPure = false;
            double[]? lastPure = null;
            while (_pureQueue.TryDequeue(out var data))
            {
                lastPure = data;
            }

            if (lastPure != null)
            {
                PurePlot.Plot.Clear();
                int targetSpr = 400; // 默认每圈重采样点数
                double period = 1.0 / targetSpr; // x轴单位转变为圈数(revolution)
                var sig = PurePlot.Plot.Add.Signal(lastPure);
                sig.Data.Period = period;
                PurePlot.Plot.Axes.AutoScale();
                needRefreshPure = true;
            }

            bool needRefreshOrder = false;
            SpectrumData? lastOrder = null;
            while (_orderQueue.TryDequeue(out var data))
            {
                lastOrder = data;
            }
            if (lastOrder != null && lastOrder.Magnitudes.Length > 0)
            {
                OrderPlot.Plot.Clear();
                var sig = OrderPlot.Plot.Add.Signal(lastOrder.Magnitudes);
                sig.Data.Period = lastOrder.Resolution; // 单位：阶 (Order)
                // 常见的阶次谱只关心低阶（如 0~100 阶），这里先让其自适应
                OrderPlot.Plot.Axes.AutoScale();
                needRefreshOrder = true;
            }

            bool needRefreshEnv = false;
            SpectrumData? lastEnv = null;
            while (_envQueue.TryDequeue(out var data))
            {
                lastEnv = data;
            }
            if (lastEnv != null && lastEnv.Magnitudes.Length > 0)
            {
                EnvPlot.Plot.Clear();
                var sig = EnvPlot.Plot.Add.Signal(lastEnv.Magnitudes);
                sig.Data.Period = lastEnv.Resolution; // 单位：赫兹 (Hz)
                EnvPlot.Plot.Axes.AutoScale();
                needRefreshEnv = true;
            }

            // CWT 热力图渲染（WriteableBitmap 像素写入）
            CwtHeatmapData? lastCwt = null;
            while (_cwtQueue.TryDequeue(out var data))
            {
                lastCwt = data;
            }
            if (lastCwt != null && lastCwt.Matrix.Length > 0)
            {
                int width = lastCwt.NumTimeBins;
                int height = lastCwt.NumScales;
                int numPixels = width * height;

                // 确保 WriteableBitmap 尺寸匹配
                if (_cwtBitmap == null || _cwtBitmap.PixelWidth != width || _cwtBitmap.PixelHeight != height)
                {
                    _cwtBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    CwtHeatmapImage.Source = _cwtBitmap;
                }

                var matrix = lastCwt.Matrix;

                // 全局 min-max 归一化
                double minVal = double.MaxValue, maxVal = double.MinValue;
                for (int i = 0; i < numPixels; i++)
                {
                    double v = matrix[i];
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
                double range = maxVal - minVal;

                byte[] pixels = new byte[numPixels * 4];
                if (range > 1e-10)
                {
                    for (int i = 0; i < numPixels; i++)
                    {
                        int idx = (int)((matrix[i] - minVal) / range * 255.0);
                        if (idx > 255) idx = 255;
                        if (idx < 0) idx = 0;
                        pixels[i * 4 + 0] = InfernoLut[idx * 4 + 0]; // B
                        pixels[i * 4 + 1] = InfernoLut[idx * 4 + 1]; // G
                        pixels[i * 4 + 2] = InfernoLut[idx * 4 + 2]; // R
                        pixels[i * 4 + 3] = 255;
                    }
                }

                // 零拷贝写入 WriteableBitmap
                _cwtBitmap.Lock();
                Marshal.Copy(pixels, 0, _cwtBitmap.BackBuffer, pixels.Length);
                _cwtBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _cwtBitmap.Unlock();
            }

            // 图表更新请求 (刷新到屏幕)
            if (needRefreshRaw) RawPlot.Refresh();
            if (needRefreshPure) PurePlot.Refresh();
            if (needRefreshOrder) OrderPlot.Refresh();
            if (needRefreshEnv) EnvPlot.Refresh();
        }

        // 预计算 Inferno 色图 256 色 LUT（BGRA 格式）
        private static byte[] GenerateInfernoLut()
        {
            var lut = new byte[256 * 4];
            (double pos, byte r, byte g, byte b)[] stops = {
                (0.00,   0,   0,   0),
                (0.10,  23,  11,  59),
                (0.20,  66,  10, 104),
                (0.30, 107,  23, 110),
                (0.40, 147,  38, 103),
                (0.50, 187,  55,  84),
                (0.60, 220,  80,  57),
                (0.70, 242, 117,  26),
                (0.80, 252, 165,  10),
                (0.90, 246, 215,  70),
                (1.00, 252, 255, 164),
            };

            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                for (int s = 0; s < stops.Length - 1; s++)
                {
                    if (t >= stops[s].pos && t <= stops[s + 1].pos)
                    {
                        double f = (t - stops[s].pos) / (stops[s + 1].pos - stops[s].pos);
                        byte r = (byte)(stops[s].r + f * (stops[s + 1].r - stops[s].r));
                        byte g = (byte)(stops[s].g + f * (stops[s + 1].g - stops[s].g));
                        byte b = (byte)(stops[s].b + f * (stops[s + 1].b - stops[s].b));
                        lut[i * 4 + 0] = b;     // B
                        lut[i * 4 + 1] = g;     // G
                        lut[i * 4 + 2] = r;     // R
                        lut[i * 4 + 3] = 255;   // A
                        break;
                    }
                }
            }
            return lut;
        }
    }
}
