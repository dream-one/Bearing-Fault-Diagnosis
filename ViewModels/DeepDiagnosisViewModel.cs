using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Services.Interfaces;
using BearingFaultDiagnosis.ViewModels;

namespace BearingFaultDiagnosis.ViewModels
{
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

        public DeepDiagnosisViewModel(IDeepDiagnosisService deepDiagnosisService, ISensorDataService sensorDataService)
        {
            _deepDiagnosisService = deepDiagnosisService;
            _sensorDataService = sensorDataService;
            _overlapSize = (int)(_chunkSize * 0.1);

            // 使用 Task.Run 启动后台循环，避免 async void
            _ = Task.Run(() => ProcessLoopAsync(_cts.Token));
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

                    // 调用 C++ 处理（建议确认 ProcessChunk 是否同步阻塞，若耗时较长可改为异步）
                    double[] pureData = _deepDiagnosisService.ProcessChunk(
                        _buffer,
                        _sensorDataService.puMetadata.sample_rate,
                        _sensorDataService.puMetadata.rpm,
                        400); // ⚠️ 建议替换为具名常量或配置

                    OnPureDataReady?.Invoke(pureData);

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

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
