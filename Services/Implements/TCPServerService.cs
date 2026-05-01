using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using BearingFaultDiagnosis.Services.Interfaces;
using Microsoft.Extensions.Logging;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BearingFaultDiagnosis.Services.Implements
{
    public class TCPServerService : ITCPServerService
    {
        public event Action<string, string> OnMessageReceived;
        public event Action<SensorFrameStruct> OnDataReceived;
        private readonly ILogger<TCPServerService> _logger;
        private TcpListener _listener;
        private const int PACKET_SIZE = 93;
        private bool _isSaving = false;
        private bool _isListening = false;

        private readonly Channel<SensorFrameStruct> _uiChannel =
         Channel.CreateBounded<SensorFrameStruct>(new BoundedChannelOptions(1024)
         {
             FullMode = BoundedChannelFullMode.DropOldest
         });
        private readonly Channel<SensorFrameStruct> _chartChannel =
        Channel.CreateBounded<SensorFrameStruct>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly Channel<SensorFrameStruct> _dataChannel =
     Channel.CreateUnbounded<SensorFrameStruct>();

        public ChannelReader<SensorFrameStruct> UiReader => _uiChannel.Reader;
        public ChannelReader<SensorFrameStruct> ChartReader => _chartChannel.Reader;
        public ChannelReader<SensorFrameStruct> DataReader => _dataChannel.Reader;

        bool ITCPServerService.isListening => _isListening;
        bool ITCPServerService.isSaving => _isSaving;

        private TcpClient _activateClient;
        private CancellationTokenSource _clientCts; // 专门管理当前客户端的令牌

        public void StartSaving() => _isSaving = true;
        public void StopSaving()
        {
            _isSaving = false;
            // 清空队列，避免停止后还在消费旧数据
            while (_dataChannel.Reader.TryRead(out _)) { }
        }

        public TCPServerService(ILogger<TCPServerService> logger)
        {
            _logger = logger;
          
        }

        public void BroadcastFrame(SensorFrameStruct frame)
        {
            if (_isSaving)
            {
                _dataChannel.Writer.TryWrite(frame);
            }
            _uiChannel.Writer.TryWrite(frame);
            _chartChannel.Writer.TryWrite(frame);
        }

        public async Task StartListeningAsync(int port, CancellationToken cancellationToken)
        {
            ResetChannels();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isListening = true;

            OnMessageReceived.Invoke(port.ToString(), $"TCP 服务器已启动，正在监听端口: {port}");
            try
            {
                // 无限循环接收客户端连接，直到触发 cancellationToken
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);

                    // --- 核心改动：先关闭旧连接任务，并等待它清理完成 ---
                    if (_activateClient != null)
                    {
                        _clientCts?.Cancel(); // 通知旧任务停止
                        try { _activateClient.Close(); } catch { } // 强制关流
                    }
                    _clientCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _activateClient = client;

                    // 这里的 await 释放了线程，绝对不会卡死 WPF 的 UI 界面
                    var clientIp = client.Client.RemoteEndPoint?.ToString();
                    OnMessageReceived.Invoke(clientIp, $" 新客户端已连接: {clientIp}");

                    // 启动一个独立的后台任务去处理这个客户端的数据，不阻塞下一个客户端连入
                    _ = Task.Run(() => HandleClientAsync(client, _clientCts.Token), CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("TCP 监听已被手动取消。");
            }
            finally
            {
                _listener.Stop();
                _isListening = false;
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client) // 确保处理完后断开
            using (NetworkStream stream = client.GetStream())
            {
                byte[] buffer = new byte[65536];
                int validBytes = 0;//  关键：记录有效数据长度

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 🌟 新增：读超时机制 (例如 5 秒没收到数据，认为物理断线)
                        // 这相当于服务端的“看门狗”
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                        int bytesRead;
                        try
                        {
                            // 传入 timeoutCts.Token
                            bytesRead = await stream.ReadAsync(buffer, validBytes, buffer.Length - validBytes, timeoutCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // 🌟 判断是手动停止的，还是超时触发的
                            if (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
                            {
                                OnMessageReceived.Invoke("", "读取超时，下位机疑似掉线，强行断开连接等待重连...");
                                break; // 退出 while 循环，由 using 自动清理资源
                            }
                            throw; // 手动停止的话，继续向上抛出
                        }
                        if (bytesRead == 0) break; // STM32 断开了

                        validBytes += bytesRead;

                        // 调用之前的解包函数
                        int processed = ProcessBuffer(buffer, validBytes);

                        // 内存搬运
                        if (processed > 0)
                        {
                            //判断剩下的数据还有多少
                            int remaining = validBytes - processed;
                            //把剩下的数据移到前面
                            if (remaining > 0) buffer.AsSpan(processed, remaining).CopyTo(buffer.AsSpan(0, remaining));
                            //记录有效数据位置
                            validBytes = remaining;
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    // 这是正常的，说明是我们手动点了停止
                    OnMessageReceived.Invoke("", "设备数据传输中断: " + ex.Message);

                }
                catch (Exception ex)
                {
                    OnMessageReceived.Invoke("", "设备数据传输中断: " + ex.Message);
                }
                finally
                {

                }
            }
        }
        private int ProcessBuffer(byte[] buffer, int length)
        {
            int processedOffset = 0;

            // 只要剩余数据够一个包长，就尝试解析
            while (length - processedOffset >= PACKET_SIZE)
            {
                // 1. 快速检查帧头 (0x55AA -> 小端 AA 55)
                if (buffer[processedOffset] != 0xAA || buffer[processedOffset + 1] != 0x55)
                {
                    processedOffset++; // 滑动窗口，找下一个字节
                    continue;
                }

                // 2. 快速检查帧尾 (0x0D0A -> 小端 0A 0D) at Offset 91, 92
                // 注意：相对 processedOffset 的位置
                if (buffer[processedOffset + 91] != 0x0A || buffer[processedOffset + 92] != 0x0D)
                {
                    processedOffset++; // 也是假头，跳过
                    continue;
                }

                // 4. 零拷贝解析结构体
                // 使用 Span 切片，零内存分配
                ReadOnlySpan<byte> packetSpan = new ReadOnlySpan<byte>(buffer, processedOffset, PACKET_SIZE);
                SensorFrameStruct frame = MemoryMarshal.Read<SensorFrameStruct>(packetSpan);

                // 5. 写入队列 (非阻塞)
                BroadcastFrame(frame);
                //_dataChannel.Writer.TryWrite(frame);

                // 6. 成功处理一包，指针后移
                processedOffset += PACKET_SIZE;
            }

            return processedOffset; // 返回一共处理掉了多少字节
        }
        public void ResetChannels()
        {
            // 清空 UI 通道
            while (_uiChannel.Reader.TryRead(out _)) { }
            // 清空图表通道
            while (_chartChannel.Reader.TryRead(out _)) { }
            // 清空数据通道
            while (_dataChannel.Reader.TryRead(out _)) { }

            _logger.LogInformation("所有通道已重置，残留数据已丢弃。");
        }
    }
    // 1. 定义与 C 语言完全一致的结构体
    // Pack=1 对应 C 语言的 #pragma pack(1)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SensorFrameStruct
    {
        public ushort Head;      // 0x55AA
        public uint Time_T;
        public ushort Cnt_Q;
        public ushort Cnt_Pn;
        public float Roll;
        public float Pitch;
        public float Temp;
        public float Humi;

        // 振动数据：C语言是 VibData_t[5]，C# 这里要展开
        // MarshalFixedArray 需要 unsafe，为了安全我们直接罗列或用定长Buffer
        // 简单起见，这里展开写，或者用 Marshal 技巧。
        // 为了极致性能和兼容性，推荐直接定义 5x3 = 15个 float
        // 或者使用 fixed buffer (需要 unsafe)

        public float V1_X; public float V1_Y; public float V1_Z;
        public float V2_X; public float V2_Y; public float V2_Z;
        public float V3_X; public float V3_Y; public float V3_Z;
        public float V4_X; public float V4_Y; public float V4_Z;
        public float V5_X; public float V5_Y; public float V5_Z;

        public float Current;
        public byte CheckSum;
        public ushort Tail;      // 0x0D0A
    }

}
