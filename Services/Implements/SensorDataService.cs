using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using BearingFaultDiagnosis.Models;
using BearingFaultDiagnosis.Services.Interfaces;

namespace BearingFaultDiagnosis.Services.Implements
{
    public class SensorDataService : ISensorDataService
    {
        private ITCPServerService _tcpService;
        public ConcurrentQueue<double> BufferV1_X { get; } = new();
        public ConcurrentQueue<double> BufferV1_Y { get; } = new();
        public ConcurrentQueue<double> BufferV1_Z { get; } = new();
        public bool IsProcessing { get; private set; } = false;
        public PuMetadata puMetadata { get; set; }
        public SensorDataService(ITCPServerService tcpService)
        {
            _tcpService = tcpService;
        }
        /// <summary>
        /// 处理振动数据用于波形图显示
        /// </summary>
        public void HandleVibrationDataForChart()
        {
            Task.Run(async () =>
            {
                // WaitToReadAsync() 是灵魂：有数据它就瞬间唤醒，没数据它就安静等待，彻底干掉 Timer！
                while (await _tcpService.ChartReader.WaitToReadAsync())
                {
                    while (_tcpService.ChartReader.TryRead(out var frame))
                    {
                        UpdatePoint(frame.V1_X, frame.V1_Y, frame.V1_Z);
                        UpdatePoint(frame.V2_X, frame.V2_Y, frame.V2_Z);
                        UpdatePoint(frame.V3_X, frame.V3_Y, frame.V3_Z);
                        UpdatePoint(frame.V4_X, frame.V4_Y, frame.V4_Z);
                        UpdatePoint(frame.V5_X, frame.V5_Y, frame.V5_Z);
                    }
                }
            }
            );
        }


        public async Task ProcessFolderAsync(string folderPath, Action<string> onMessage, Action<PuMetadata> onFileRead)
        {
            string[] binFiles = await Task.Run(() => System.IO.Directory.GetFiles(folderPath, "*.bin", System.IO.SearchOption.AllDirectories));
            onMessage?.Invoke($"已选择文件夹: {folderPath}");
            onMessage?.Invoke($"共找到 {binFiles.Length} 个 .bin 文件 (包含子文件夹)。");

            foreach (var file in binFiles)
            {
                string baseName = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file), System.IO.Path.GetFileNameWithoutExtension(file));
                onMessage?.Invoke($"找到文件: {System.IO.Path.GetFileNameWithoutExtension(file)}");
                var (signData, meta) = await ReadWithMetaAsync(baseName);
                this.puMetadata = meta;
                onFileRead?.Invoke(meta);
                await TestFlushAsync(signData);
            }
        }

        public async Task TestFlushAsync(double[] signData)
        {
            int chunkSize = 1024;       // 每批推送 200 个点
            int delayMs = 16;          // 每批之间等待 50 ms
            for (int i = 0; i < signData.Length; i += chunkSize)
            {
                int end = Math.Min(i + chunkSize, signData.Length);
                for (int j = i; j < end; j++)
                {
                    UpdatePoint(signData[j]);
                }

                await Task.Delay(delayMs);
            }
        }

        private async Task<double[]> ReadBinAsync(string binPath)
        {
            return await Task.Run(async () =>
            {
                var bytes = await File.ReadAllBytesAsync(binPath).ConfigureAwait(false);
                var doubles = new double[bytes.Length / 8];
                Buffer.BlockCopy(bytes, 0, doubles, 0, bytes.Length);
                return doubles;
            });
        }

        private async Task<(double[] signal, PuMetadata meta)> ReadWithMetaAsync(string basePathWithoutExt)
        {
            return await Task.Run(async () =>
            {
                var binPath = basePathWithoutExt + ".bin";
                var jsonPath = basePathWithoutExt + ".json";
                var signal = await ReadBinAsync(binPath).ConfigureAwait(false);
                var json = await File.ReadAllTextAsync(jsonPath).ConfigureAwait(false);
                var meta = JsonSerializer.Deserialize<PuMetadata>(json);
                return (signal, meta);
            });
        }
        private void UpdatePoint(double x, double y, double z)
        {
            BufferV1_X.Enqueue(x);
            BufferV1_Y.Enqueue(y);
            BufferV1_Z.Enqueue(z);
        }
        private void UpdatePoint(double data)
        {
            BufferV1_X.Enqueue(data);
        }
    }
}