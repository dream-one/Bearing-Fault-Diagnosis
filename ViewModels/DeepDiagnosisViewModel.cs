using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Services.Interfaces;
using BearingFaultDiagnosis.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NWaves.Utils;
using ScottPlot;

namespace BearingFaultDiagnosis.ViewModels
{
    public class SpectrumData
    {
        public double[] Magnitudes { get; set; } = Array.Empty<double>();
        public double Resolution { get; set; } = 1.0;
    }

    public class CwtHeatmapData
    {
        public double[] Matrix { get; set; } = Array.Empty<double>();
        public int NumScales { get; set; }
        public int NumTimeBins { get; set; }
        public double FreqLow { get; set; }
        public double FreqHigh { get; set; }
    }

    /// <summary>
    /// DeepDiagnosisViewModel.xaml 的交互逻辑
    /// </summary>
    public partial class DeepDiagnosisViewModel : ViewModelBase, IDisposable
    {
        private readonly IDeepDiagnosisService _deepDiagnosisService;
        private readonly ISensorDataService _sensorDataService;
        private readonly double[] _buffer = new double[32768];
        private int _currentCount = 0;
        private readonly int _chunkSize = 32768;
        private readonly int _overlapSize;
        private readonly CancellationTokenSource _cts = new();

        public event Action<double[]>? OnPureDataReady;
        public event Action<double[]>? OnRawDataReady;
        public event Action<SpectrumData>? OnEnvelopeSpectrumReady;
        public event Action<SpectrumData>? OnOrderSpectrumReady;
        public event Action<CwtHeatmapData>? OnCwtHeatmapReady;

        private InferenceSession _inferenceI;
        private InferenceSession _inferenceJ;
        private InferenceSession _inferenceK;
        private InferenceSession _inferenceL;

        public ScottPlot.Plot BarPlot { get; } = new();
        private static readonly string[] ClassLabels =
            [
                "正常状态",
                "内圈早期损伤",
                "外圈早期损伤"
            ];
        public DeepDiagnosisViewModel(IDeepDiagnosisService deepDiagnosisService, ISensorDataService sensorDataService)
        {
            _deepDiagnosisService = deepDiagnosisService;
            _sensorDataService = sensorDataService;
            // 提高重叠率 (例如 90% 重叠)，这样每次只需筹集少量新数据，大大提高计算更新频率和图表帧率
            _overlapSize = (int)(_chunkSize * 0.5);
            // 加载 ONNX 模型 (路径需要根据你实际放置的位置调整)
            _inferenceI = new InferenceSession("DLModels/best_I_s2024.onnx");
            _inferenceJ = new InferenceSession("DLModels/best_J_s2024.onnx");
            _inferenceK = new InferenceSession("DLModels/best_K_s2024.onnx");
            _inferenceL = new InferenceSession("DLModels/best_L_s2024.onnx");

            // 使用 Task.Run 启动后台循环，避免 async void
            _ = Task.Run(() => ProcessLoopAsync(_cts.Token));
        }
        private string _diagnosisResult = "等待诊断...";
        public string DiagnosisResult
        {
            get => _diagnosisResult;
            set => SetProperty(ref _diagnosisResult, value);
        }

        private double[] _latestOrderSpectrum;
        private double[] _latestTimeData;
        private int _cwtFrameCounter = 0;

        [RelayCommand]
        public async Task ExecuteDualBranchDiagnosis()
        {
            try
            {
                DiagnosisResult = "正在诊断...";

                // 1. 获取 x_time: 时域信号 (截取 2048 个点)
                float[] timeData = new float[2048];
                if (_latestTimeData != null && _latestTimeData.Length >= 2048)
                {
                    for (int i = 0; i < 2048; i++)
                    {
                        timeData[i] = (float)_latestTimeData[i];
                    }
                }
                else
                {
                    DiagnosisResult = "时域数据尚未准备好，请稍后重试";
                    return;
                }

                // 2. 获取 x_spec: 阶次谱特征 (需要 1024 个点)
                // 复用后台循环已经算好的 orderSpectrum
                float[] specData = new float[1024];
                if (_latestOrderSpectrum != null)
                {
                    for (int i = 0; i < 1024 && i < _latestOrderSpectrum.Length; i++)
                    {
                        specData[i] = (float)_latestOrderSpectrum[i];
                    }
                }
                else
                {
                    DiagnosisResult = "谱数据尚未准备好，请稍后重试";
                    return;
                }

                // 3. 并行执行四个模型的推理 (集成投票)
                var tasks = new[]
                {
                    Task.Run(() => RunSingleModel(_inferenceI, timeData, specData)),
                    Task.Run(() => RunSingleModel(_inferenceJ, timeData, specData)),
                    Task.Run(() => RunSingleModel(_inferenceK, timeData, specData)),
                    Task.Run(() => RunSingleModel(_inferenceL, timeData, specData))
                };

                float[][] allResults = await Task.WhenAll(tasks);

                // 4. 软投票融合 (四个模型输出概率相加求平均)
                int numClasses = allResults[0].Length;
                float[] finalProbs = new float[numClasses];

                for (int i = 0; i < numClasses; i++)
                {
                    finalProbs[i] = (allResults[0][i] + allResults[1][i] + allResults[2][i] + allResults[3][i]) / 4.0f;
                }

                SetBars(ClassLabels, finalProbs, new double[] { 1, 2, 3 });
            }
            catch (Exception ex)
            {
                DiagnosisResult = "诊断失败：" + ex.Message;
            }
        }

        private float[] RunSingleModel(InferenceSession session, float[] timeData, float[] specData)
        {
            // 通过 float 数组构建正确的张量形状
            var timeTensor = new DenseTensor<float>(timeData, new[] { 1, 1, 2048 });
            var specTensor = new DenseTensor<float>(specData, new[] { 1, 1, 1024 });

            // 动态对齐 ONNX 定义时的输入节点名称
            var inputNames = session.InputMetadata.Keys.ToList();
            string timeInputName = inputNames.FirstOrDefault(k => k.Contains("time") || k.Contains("x_time")) ?? "x_time";
            string specInputName = inputNames.FirstOrDefault(k => k.Contains("spec") || k.Contains("x_spec")) ?? "x_spec";

            if (inputNames.Count >= 2 && timeInputName == "x_time" && specInputName == "x_spec" && !inputNames.Contains("x_time"))
            {
                timeInputName = inputNames[0];
                specInputName = inputNames[1];
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(timeInputName, timeTensor),
                NamedOnnxValue.CreateFromTensor(specInputName, specTensor)
            };

            // 推理
            using var results = session.Run(inputs);
            return results.First().AsEnumerable<float>().ToArray();
        }

        private async Task ProcessLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int samplesNeeded = _chunkSize - _currentCount;
                    int collected = 0;

                    // 安全凑数据：不依赖 Count，直接 TryDequeue
                    while (collected < samplesNeeded && !ct.IsCancellationRequested)
                    {
                        if (_sensorDataService.BufferV1_X.TryDequeue(out var value))
                        {
                            _buffer[_currentCount + collected] = value;
                            collected++;
                        }
                        else
                        {
                            // 数据不足时让出时间片，降低 CPU 占用
                            await Task.Delay(10, ct);
                        }
                    }

                    if (ct.IsCancellationRequested) break;

                    _currentCount += collected;

                    double[] rawDataCopy = new double[_chunkSize];
                    Array.Copy(_buffer, rawDataCopy, _chunkSize);
                    OnRawDataReady?.Invoke(rawDataCopy);

                    // 缓存当帧采样的时域数据，保证与下方计算出的谱严格对应
                    _latestTimeData = rawDataCopy;

                    double rpm = _sensorDataService.puMetadata.rpm > 0 ? _sensorDataService.puMetadata.rpm : 1500.0;
                    double fs = _sensorDataService.puMetadata.sample_rate > 0 ? _sensorDataService.puMetadata.sample_rate : 64000.0;
                    int targetSpr = 512; // ⚠️ 建议替换为具名常量或配置

                    // 调用 C++ 处理（建议确认 ProcessChunk 是否同步阻塞，若耗时较长可改为异步）
                    double[] pureData = _deepDiagnosisService.ProcessChunk(
                        _buffer,
                        fs,
                        rpm,
                        targetSpr);

                    OnPureDataReady?.Invoke(pureData);

                    // 计算并触发包络谱
                    double[] envSpectrum = _deepDiagnosisService.ComputeSpectrum(rawDataCopy); // 也可以是对其他数据的频谱
                    OnEnvelopeSpectrumReady?.Invoke(new SpectrumData { Magnitudes = envSpectrum, Resolution = fs / _chunkSize });

                    // 计算并触发阶次谱
                    double[] orderSpectrum = _deepDiagnosisService.ComputeSpectrum(pureData);
                    _latestOrderSpectrum = orderSpectrum;

                    // 频率分辨率 = 角采样率 / 总点数 = targetSpr / pureData.Length (单位: 阶次 order)
                    double orderRes = pureData.Length > 0 ? (double)targetSpr / pureData.Length : 1.0;
                    OnOrderSpectrumReady?.Invoke(new SpectrumData { Magnitudes = orderSpectrum, Resolution = orderRes });

                    // CWT 连续小波时频热力图（每 2 帧计算一次，节流）
                    _cwtFrameCounter++;
                    if (_cwtFrameCounter % 2 == 0)
                    {
                        double[] cwtMatrix = _deepDiagnosisService.ComputeCWT(
                            rawDataCopy, fs, 50.0, 16000.0, 128, 512);
                        OnCwtHeatmapReady?.Invoke(new CwtHeatmapData
                        {
                            Matrix = cwtMatrix,
                            NumScales = 128,
                            NumTimeBins = 512,
                            FreqLow = 50.0,
                            FreqHigh = 16000.0
                        });
                    }

                    // 重叠保留：尾部搬到头部
                    Array.Copy(_buffer, _chunkSize - _overlapSize, _buffer, 0, _overlapSize);
                    _currentCount = _overlapSize;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // 记录日志，防止单次异常中断整个数据流
                    // _logger.LogError(ex, "Chunk processing failed");
                    await Task.Delay(50, ct);
                }
            }
        }
        private void SetBars(string[] labels, float[] values, double[] positions)
        {
            if (labels == null || values == null || positions == null)
                throw new ArgumentNullException();
            if (labels.Length != values.Length || labels.Length != positions.Length)
                throw new ArgumentException("数组长度必须一致");

            BarPlot.Clear();

            // ========== 1. 归一化到 [0,1] ==========
            float min = values.Min();
            float max = values.Max();
            float range = max - min;
            double[] valuesD;
            if (Math.Abs(range) < 0.0001f)
                valuesD = values.Select(v => 1.0).ToArray();
            else
                valuesD = values.Select(v => (double)((v - min) / range)).ToArray();

            // ========== 2. 绘制柱状图（柱子变窄的两种方案） ==========
            // 方案A：如果你的 ScottPlot 支持 width 参数（绝大多数版本支持，先试这个）
            //var barPlotObj = BarPlot.Add.Bars(positions, valuesD, width: 0.5);

            // 如果 width 参数报错，请换成方案B（增大positions间距），并同时替换底部刻度坐标：
            double[] spacedPositions = new double[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                spacedPositions[i] = 1.0 + i * 1.8;   // 间距1.8，柱子间空隙明显
            positions = spacedPositions;               // 后续都用这个新 positions
            var barPlotObj = BarPlot.Add.Bars(positions, valuesD); // 无 width 参数

            // ========== 3. 柱子颜色 ==========
            ScottPlot.Color[] colors =
            {
        ScottPlot.Color.FromHex("#4E79A7"),
        ScottPlot.Color.FromHex("#F28E2B"),
        ScottPlot.Color.FromHex("#E15759"),
        ScottPlot.Color.FromHex("#76B7B2"),
        ScottPlot.Color.FromHex("#59A14F"),
        ScottPlot.Color.FromHex("#EDC948"),
    };
            for (int i = 0; i < barPlotObj.Bars.Count; i++)
                barPlotObj.Bars[i].FillColor = colors[i % colors.Length];

            // ========== 4. 在柱子上方显示归一化值（LabelStyle 代替已弃用的 FontName 等） ==========
            for (int i = 0; i < positions.Length; i++)
            {
                double x = positions[i];
                double y = valuesD[i];
                string text = valuesD[i].ToString("F2");   // 归一化值，两位小数

                var txt = BarPlot.Add.Text(text, x, y + 0.03);

                // 正确设置文本样式：通过 LabelStyle
                txt.LabelStyle.FontName = "微软雅黑";
                txt.LabelStyle.FontSize = 18;
                txt.LabelStyle.ForeColor = ScottPlot.Color.FromHex("#333333");
                txt.LabelStyle.Alignment = Alignment.LowerCenter;   // 保证在点上方居中
            }

            // ========== 5. 底部分类标签 ==========
            ScottPlot.Tick[] ticks = positions
                .Zip(labels, (pos, lbl) => new ScottPlot.Tick(pos, lbl))
                .ToArray();
            BarPlot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
            BarPlot.Axes.Bottom.MajorTickStyle.Length = 0;
            BarPlot.Axes.Bottom.TickLabelStyle.FontName = "微软雅黑";
            BarPlot.Axes.Bottom.TickLabelStyle.FontSize = 14;

            // ========== 6. Y 轴设置 ==========
            BarPlot.YLabel("置信度（归一化）");
            BarPlot.Axes.Left.Label.FontName = "微软雅黑";
            BarPlot.Axes.Left.Label.FontSize = 18;
            BarPlot.Axes.Left.TickLabelStyle.FontName = "微软雅黑";
            BarPlot.Axes.Left.TickLabelStyle.FontSize = 18;

            // 固定 Y 轴范围 [0, 1.1]，并确保不会再被自动缩放改变
            BarPlot.Axes.SetLimitsY(0, 1.1);
            // 你环境里 ContinuouslyAutoscale 可能也不存在，直接不调用 AutoScale 即可。
            // BarPlot.Axes.ContinuouslyAutoscale = false;   // 如果有此属性则开启，没有就忽略

            BarPlot.Axes.Margins(bottom: 0);

            // ========== 7. 通知视图刷新 ==========
            OnPropertyChanged(nameof(BarPlot));
        }
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
