using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Services.Implements;
using static BearingFaultDiagnosis.Services.Implements.TCPServerService;

namespace BearingFaultDiagnosis.Services.Interfaces
{
    public interface ITCPServerService
    {
        /// <summary>
        /// 开始监听指定端口的TCP连接，并在接收到消息时触发OnMessageReceived事件
        /// </summary>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task StartListeningAsync(int port, CancellationToken cancellationToken);

        /// <summary>
        /// 停止 TCP 监听并断开当前客户端
        /// </summary>
        void StopListening();

        /// <summary>
        /// ip,消息内容
        /// </summary>
        event Action<string, string> OnMessageReceived;
        event Action<SensorFrameStruct> OnDataReceived;
        ChannelReader<SensorFrameStruct> UiReader { get; }
        ChannelReader<SensorFrameStruct> ChartReader { get; }
        ChannelReader<SensorFrameStruct> DataReader { get; }

        bool isListening { get; }
        bool isSaving { get; }
    }
}
