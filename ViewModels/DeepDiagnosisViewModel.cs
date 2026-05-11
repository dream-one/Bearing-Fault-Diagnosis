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
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ScottPlot;

namespace BearingFaultDiagnosis.ViewModels
{
    /// <summary>
    /// 深度学习轴承故障诊断视图模型
    /// 负责实时数据流处理、阶次跟踪、谱分析、ONNX模型集成推理及图表渲染
    /// </summary>
    public partial class DeepDiagnosisViewModel : ViewModelBase, IDisposable
    {
        #region 常量与配置

        /// <summary>数据块大小（与模型输入及缓冲区一致）</summary>
        private const int ChunkSize = 32768;
        /// <summary>重叠保留比例（50% 重叠）</summary>
        private const int OverlapDivisor = 2;
        /// <summary>时域分支模型输入长度</summary>
        private const int TimeDataLength = 2048;
        /// <summary>频谱分支模型输入长度</summary>
        private const int SpecDataLength = 1024;
        /// <summary>默认转速 (RPM)</summary>
        private const double DefaultRpm = 1500.0;
        /// <summary>默认采样率 (Hz)</summary>
        private const double DefaultSampleRate = 64000.0;
        /// <summary>阶次跟踪目标每转采样点数</summary>
        private const int TargetSpr = 512;
        /// <summary>CWT 热力图计算节流帧数（每 N 帧计算一次）</summary>
        private const int CwtThrottleFrames = 2;
        /// <summary>分类标签</summary>
        private static readonly string[] ClassLabels = { "正常状态", "内圈早期损伤", "外圈早期损伤" };

        #endregion

        #region 私有字段

        private readonly IDeepDiagnosisService _deepDiagnosisService;
        private readonly ISensorDataService _sensorDataService;

        /// <summary>环形缓冲区（复用内存，避免频繁 GC）</summary>
        private readonly double[] _buffer = new double[ChunkSize];
        /// <summary>当前缓冲区有效数据点数</summary>
        private int _currentCount;
        /// <summary>重叠区大小</summary>
        private readonly int _overlapSize;

        /// <summary>后台任务取消令牌源</summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>ONNX 推理会话（四模型集成，懒加载）</summary>
        private readonly Lazy<InferenceSession> _inferenceI = new(
            () => new InferenceSession("DLModels/best_I_s2024.onnx"), LazyThreadSafetyMode.ExecutionAndPublication);
        private readonly Lazy<InferenceSession> _inferenceJ = new(
            () => new InferenceSession("DLModels/best_J_s2024.onnx"), LazyThreadSafetyMode.ExecutionAndPublication);
        private readonly Lazy<InferenceSession> _inferenceK = new(
            () => new InferenceSession("DLModels/best_K_s2024.onnx"), LazyThreadSafetyMode.ExecutionAndPublication);
        private readonly Lazy<InferenceSession> _inferenceL = new(
            () => new InferenceSession("DLModels/best_L_s2024.onnx"), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>最新阶次谱缓存（供诊断使用，volatile 保证跨线程可见性）</summary>
        private volatile double[] _latestOrderSpectrum;
        /// <summary>最新时域数据缓存（供诊断使用，volatile 保证跨线程可见性）</summary>
        private volatile double[] _latestTimeData;
        /// <summary>CWT 帧计数器（用于节流）</summary>
        private int _cwtFrameCounter;

        #endregion

        #region 绑定属性

        /// <summary>轴承型号列表</summary>
        [ObservableProperty] private List<BearingInfo> _bearingList = new();
        /// <summary>当前选中的轴承型号</summary>
        [ObservableProperty] private BearingInfo _selectedBearing;
        /// <summary>当前设备转速 (RPM)</summary>
        [ObservableProperty] private double _rpm = DefaultRpm;
        /// <summary>故障特征频率计算结果</summary>
        [ObservableProperty] private BearingFaultResult _faultResult = new();

        /// <summary>是否在图表显示 BPFO 特征线</summary>
        [ObservableProperty] private bool _showBPFO;
        /// <summary>是否在图表显示 BPFI 特征线</summary>
        [ObservableProperty] private bool _showBPFI;
        /// <summary>是否在图表显示 BSF 特征线</summary>
        [ObservableProperty] private bool _showBSF;
        /// <summary>是否在图表显示 FTF 特征线</summary>
        [ObservableProperty] private bool _showFTF;

        /// <summary>诊断状态/结果提示文本</summary>
        [ObservableProperty] private string _diagnosisResult = "等待诊断...";
        /// <summary>是否启用自适应阶次视图</summary>
        [ObservableProperty] private bool _useAdaptiveOrderView;
        /// <summary>是否启用自适应包络视图</summary>
        [ObservableProperty] private bool _useAdaptiveEnvView;

        #endregion

        #region 事件定义

        /// <summary>纯净时域数据（阶次跟踪后）准备就绪时触发</summary>
        public event Action<double[]>? OnPureDataReady;
        /// <summary>原始时域数据准备就绪时触发</summary>
        public event Action<double[]>? OnRawDataReady;
        /// <summary>包络谱数据准备就绪时触发</summary>
        public event Action<SpectrumData>? OnEnvelopeSpectrumReady;
        /// <summary>阶次谱数据准备就绪时触发</summary>
        public event Action<SpectrumData>? OnOrderSpectrumReady;
        /// <summary>连续小波变换(CWT)时频热力图数据准备就绪时触发</summary>
        public event Action<CwtHeatmapData>? OnCwtHeatmapReady;

        #endregion

        #region 图表实例

        /// <summary>诊断置信度柱状图实例（ScottPlot 5）</summary>
        public Plot BarPlot { get; } = new();

        #endregion

        #region 构造函数与初始化

        /// <summary>
        /// 初始化深度学习诊断视图模型
        /// </summary>
        public DeepDiagnosisViewModel(IDeepDiagnosisService deepDiagnosisService, ISensorDataService sensorDataService)
        {
            _deepDiagnosisService = deepDiagnosisService;
            _sensorDataService = sensorDataService;
            _sensorDataService.MetadataUpdated += OnSensorMetadataUpdated;

            _overlapSize = ChunkSize / OverlapDivisor;

            // 启动后台数据处理循环
            _ = Task.Run(() => ProcessLoopAsync(_cts.Token));
            // 异步加载轴承列表
            _ = LoadBearingListAsync();
        }

        /// <summary>传感器元数据更新回调</summary>
        private void OnSensorMetadataUpdated(PuMetadata meta)
        {
            if (meta.rpm > 0) Rpm = meta.rpm;
        }

        /// <summary>异步加载轴承型号列表</summary>
        private async Task LoadBearingListAsync()
        {
            try
            {
                var list = await _deepDiagnosisService.GetBearingListAsync();
                BearingList = list;
                if (list.Count > 0) SelectedBearing = list[0];
            }
            catch (Exception ex)
            {
                DiagnosisResult = $"加载轴承列表失败：{ex.Message}";
            }
        }

        #endregion

        #region 属性变更与频率计算

        partial void OnSelectedBearingChanged(BearingInfo value) => RecalcFaultFrequencies();
        partial void OnRpmChanged(double value) => RecalcFaultFrequencies();
        partial void OnShowBPFOChanged(bool value) => RecalcFaultFrequencies();
        partial void OnShowBPFIChanged(bool value) => RecalcFaultFrequencies();
        partial void OnShowBSFChanged(bool value) => RecalcFaultFrequencies();
        partial void OnShowFTFChanged(bool value) => RecalcFaultFrequencies();

        /// <summary>根据当前轴承参数与转速重新计算故障特征频率</summary>
        private void RecalcFaultFrequencies()
        {
            if (SelectedBearing != null)
                FaultResult = _deepDiagnosisService.CalculateFaultFrequencies(SelectedBearing, Rpm);
        }

        #endregion

        #region 核心数据处理循环 (后台任务)

        /// <summary>
        /// 连续数据流处理主循环（运行于后台线程）
        /// 负责：数据收集 -> 重叠缓冲 -> 阶次跟踪 -> 频谱/CWT计算 -> 事件推送
        /// </summary>
        private async Task ProcessLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1. 收集数据至满足 ChunkSize
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
                            // 队列空时让出时间片，避免空转消耗 CPU
                            await Task.Delay(10, ct);
                        }
                    }

                    if (ct.IsCancellationRequested) break;
                    _currentCount += collected;

                    // 2. 复制当前帧原始数据并触发事件
                    double[] rawDataCopy = new double[ChunkSize];
                    Array.Copy(_buffer, rawDataCopy, ChunkSize);
                    OnRawDataReady?.Invoke(rawDataCopy);
                    _latestTimeData = rawDataCopy; // 缓存供 UI 诊断使用

                    // 3. 获取实时工况参数
                    double rpm = _sensorDataService.puMetadata.rpm > 0 ? _sensorDataService.puMetadata.rpm : DefaultRpm;
                    double fs = _sensorDataService.puMetadata.sample_rate > 0 ? _sensorDataService.puMetadata.sample_rate : DefaultSampleRate;

                    // 4. 阶次跟踪处理（等角度重采样）
                    double[] pureData = _deepDiagnosisService.ProcessChunk(_buffer, fs, rpm, TargetSpr);
                    OnPureDataReady?.Invoke(pureData);

                    // 5. 计算包络谱（基于原始时域）
                    double[] envSpectrum = _deepDiagnosisService.ComputeSpectrum(rawDataCopy);
                    OnEnvelopeSpectrumReady?.Invoke(new SpectrumData
                    {
                        Magnitudes = envSpectrum,
                        Resolution = fs / ChunkSize
                    });

                    // 6. 计算阶次谱（基于阶次跟踪后数据）
                    double[] orderSpectrum = _deepDiagnosisService.ComputeSpectrum(pureData);
                    _latestOrderSpectrum = orderSpectrum; // 缓存供 UI 诊断使用

                    double orderRes = pureData.Length > 0 ? (double)TargetSpr / pureData.Length : 1.0;
                    OnOrderSpectrumReady?.Invoke(new SpectrumData
                    {
                        Magnitudes = orderSpectrum,
                        Resolution = orderRes
                    });

                    // 7. CWT 时频热力图（节流计算，降低性能开销）
                    _cwtFrameCounter++;
                    if (_cwtFrameCounter % CwtThrottleFrames == 0)
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

                    // 8. 重叠保留法：将尾部数据移至头部，作为下一帧的起始重叠区
                    Array.Copy(_buffer, ChunkSize - _overlapSize, _buffer, 0, _overlapSize);
                    _currentCount = _overlapSize;
                }
                catch (OperationCanceledException)
                {
                    break; // 正常退出
                }
                catch (Exception ex)
                {
                    // 记录日志（建议接入 ILogger），短暂休眠后继续，防止单帧异常中断数据流
                    System.Diagnostics.Debug.WriteLine($"[ProcessLoop] 数据处理异常: {ex.Message}");
                    await Task.Delay(50, ct);
                }
            }
        }

        #endregion

        #region 深度学习诊断逻辑

        /// <summary>
        /// 执行双分支（时域+阶次谱）集成诊断命令
        /// </summary>
        [RelayCommand]
        public async Task ExecuteDualBranchDiagnosis()
        {
            try
            {
                DiagnosisResult = "正在诊断...";

                // 1. 准备时域分支输入 (2048点)
                float[] timeData = new float[TimeDataLength];
                if (_latestTimeData != null && _latestTimeData.Length >= TimeDataLength)
                {
                    for (int i = 0; i < TimeDataLength; i++)
                        timeData[i] = (float)_latestTimeData[i];
                }
                else
                {
                    DiagnosisResult = "时域数据尚未准备好，请稍后重试";
                    return;
                }

                // 2. 准备频谱分支输入 (1024点)
                float[] specData = new float[SpecDataLength];
                if (_latestOrderSpectrum != null)
                {
                    for (int i = 0; i < SpecDataLength && i < _latestOrderSpectrum.Length; i++)
                        specData[i] = (float)_latestOrderSpectrum[i];
                }
                else
                {
                    DiagnosisResult = "谱数据尚未准备好，请稍后重试";
                    return;
                }

                // 3. 并行执行四个子模型推理
                var tasks = new[]
                {
                    Task.Run(() => RunSingleModel(_inferenceI.Value, timeData, specData)),
                    Task.Run(() => RunSingleModel(_inferenceJ.Value, timeData, specData)),
                    Task.Run(() => RunSingleModel(_inferenceK.Value, timeData, specData)),
                    Task.Run(() => RunSingleModel(_inferenceL.Value, timeData, specData))
                };

                float[][] allResults = await Task.WhenAll(tasks);

                // 4. 软投票融合（概率平均）
                int numClasses = allResults[0].Length;
                float[] finalProbs = new float[numClasses];
                for (int i = 0; i < numClasses; i++)
                {
                    finalProbs[i] = (allResults[0][i] + allResults[1][i] + allResults[2][i] + allResults[3][i]) / 4.0f;
                }

                // 5. 更新 UI 柱状图
                SetBars(ClassLabels, finalProbs, new double[] { 1, 2, 3 });

                // 6. 根据最高置信度自动勾选对应故障参考线（先确保频率基于当前转速计算）
                RecalcFaultFrequencies();
                int maxIdx = Array.IndexOf(finalProbs, finalProbs.Max());
                switch (maxIdx)
                {
                    case 0: // 正常状态 — 全部取消
                        ShowBPFO = false;
                        ShowBPFI = false;
                        ShowBSF = false;
                        ShowFTF = false;
                        break;
                    case 1: // 内圈早期损伤 → 勾选 BPFI
                        ShowBPFI = true;
                        ShowBPFO = false;
                        ShowBSF = false;
                        ShowFTF = false;
                        break;
                    case 2: // 外圈早期损伤 → 勾选 BPFO
                        ShowBPFO = true;
                        ShowBPFI = false;
                        ShowBSF = false;
                        ShowFTF = false;
                        break;
                }

                DiagnosisResult = "诊断完成";
            }
            catch (Exception ex)
            {
                DiagnosisResult = $"诊断失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 运行单个 ONNX 模型进行推理
        /// </summary>
        /// <param name="session">ONNX 推理会话</param>
        /// <param name="timeData">时域输入张量数据</param>
        /// <param name="specData">频谱输入张量数据</param>
        /// <returns>各类别预测概率数组</returns>
        private float[] RunSingleModel(InferenceSession session, float[] timeData, float[] specData)
        {
            var timeTensor = new DenseTensor<float>(timeData, new[] { 1, 1, TimeDataLength });
            var specTensor = new DenseTensor<float>(specData, new[] { 1, 1, SpecDataLength });

            // 动态匹配 ONNX 模型输入节点名称（增强兼容性）
            var inputNames = session.InputMetadata.Keys.ToList();
            string timeInputName = inputNames.FirstOrDefault(k => k.Contains("time") || k.Contains("x_time")) ?? "x_time";
            string specInputName = inputNames.FirstOrDefault(k => k.Contains("spec") || k.Contains("x_spec")) ?? "x_spec";

            // 兜底逻辑：若未匹配到预期名称，则按定义顺序赋值
            if (inputNames.Count >= 2 && !inputNames.Contains(timeInputName))
            {
                timeInputName = inputNames[0];
                specInputName = inputNames[1];
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(timeInputName, timeTensor),
                NamedOnnxValue.CreateFromTensor(specInputName, specTensor)
            };

            using var results = session.Run(inputs);
            return results.First().AsEnumerable<float>().ToArray();
        }

        #endregion

        #region 图表渲染逻辑

        /// <summary>
        /// 绘制诊断置信度柱状图
        /// </summary>
        /// <param name="labels">X轴分类标签</param>
        /// <param name="values">原始概率值</param>
        /// <param name="positions">柱状图 X 轴位置</param>
        private void SetBars(string[] labels, float[] values, double[] positions)
        {
            if (labels == null || values == null || positions == null)
                throw new ArgumentNullException();
            if (labels.Length != values.Length || labels.Length != positions.Length)
                throw new ArgumentException("标签、值与位置数组长度必须一致");

            BarPlot.Clear();

            // 1. 数据归一化至 [0, 1] 区间
            float min = values.Min();
            float max = values.Max();
            float range = max - min;
            double[] normalizedValues = Math.Abs(range) < 0.0001f
                ? values.Select(_ => 1.0).ToArray()
                : values.Select(v => (double)((v - min) / range)).ToArray();

            // 2. 计算等间距 X 轴坐标
            double[] spacedPositions = new double[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                spacedPositions[i] = 1.0 + i * 1.8;

            var barPlotObj = BarPlot.Add.Bars(spacedPositions, normalizedValues);

            // 3. 设置柱体颜色
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

            // 4. 添加柱顶数值标签
            for (int i = 0; i < spacedPositions.Length; i++)
            {
                double x = spacedPositions[i];
                double y = normalizedValues[i];
                var txt = BarPlot.Add.Text(y.ToString("F2"), x, y + 0.03);
                txt.LabelStyle.FontName = "微软雅黑";
                txt.LabelStyle.FontSize = 18;
                txt.LabelStyle.ForeColor = ScottPlot.Color.FromHex("#333333");
                txt.LabelStyle.Alignment = Alignment.LowerCenter;
            }

            // 5. 配置 X 轴分类标签
            ScottPlot.Tick[] ticks = spacedPositions
                .Zip(labels, (pos, lbl) => new ScottPlot.Tick(pos, lbl))
                .ToArray();
            BarPlot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
            BarPlot.Axes.Bottom.MajorTickStyle.Length = 0;
            BarPlot.Axes.Bottom.TickLabelStyle.FontName = "微软雅黑";
            BarPlot.Axes.Bottom.TickLabelStyle.FontSize = 14;

            // 6. 配置 Y 轴与边距
            BarPlot.YLabel("置信度");
            BarPlot.Axes.Left.Label.FontName = "微软雅黑";
            BarPlot.Axes.Left.Label.FontSize = 18;
            BarPlot.Axes.Left.TickLabelStyle.FontName = "微软雅黑";
            BarPlot.Axes.Left.TickLabelStyle.FontSize = 18;
            BarPlot.Axes.SetLimitsY(0, 1.1);
            BarPlot.Axes.Margins(bottom: 0);

            // 注：ScottPlot 5 控件绑定 Plot 实例后会自动监听内部变更并渲染，
            // 此处无需调用 OnPropertyChanged(nameof(BarPlot))。
            // 若 UI 未刷新，可在 XAML 绑定的控件上调用 .Render() 或触发 Plot 的 RenderRequest 事件。
        }

        #endregion

        #region 资源释放

        /// <summary>
        /// 释放非托管资源与后台任务
        /// </summary>
        public void Dispose()
        {
            _sensorDataService.MetadataUpdated -= OnSensorMetadataUpdated;

            _cts.Cancel();
            _cts.Dispose();

            // 释放 ONNX 推理会话（仅当已创建时）
            if (_inferenceI.IsValueCreated) _inferenceI.Value?.Dispose();
            if (_inferenceJ.IsValueCreated) _inferenceJ.Value?.Dispose();
            if (_inferenceK.IsValueCreated) _inferenceK.Value?.Dispose();
            if (_inferenceL.IsValueCreated) _inferenceL.Value?.Dispose();
        }

        #endregion
    }
}