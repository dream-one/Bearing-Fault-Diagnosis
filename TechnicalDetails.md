# 技术原理解析

本文档将详细展开介绍本项目中应用到的一些核心技术点与架构设计。

## 1. P/Invoke 与 C#/C++ 数组“零拷贝”交互

在进行工业设备高频振动数据的状态监测时，例如快速傅里叶变换（FFT）、包络谱计算以及阶次跟踪等。由于流转的数据量庞大，且内部含有极其密集的浮点乘加运算，如果系统全局均使用 C# 托管代码处理，不仅计算性能往往无法达到最高利用率，而且海量中间结算结果数组的频繁创建与销毁，会给底层垃圾回收器（GC）造成极大的扫描与回收压力，引发明显的 GC 抖动，最终导致上位机 UI 出现肉眼可见的掉帧卡顿。

为了避开这一瓶颈，本项目引入了 C++ 构建的 `HighPerformanceComputing.dll` 原生动态链接库，专职负责对性能高度敏感的数字信号处理。

但在 C# 与 C++ 层面的函数调用（P/Invoke）这道跨边界的墙上，如果采取常规的封送处理（Marshaling），系统执行时会隐式地将托管堆（Managed Heap）里的数组拷贝一份放入非托管内存（Unmanaged Memory），C++ 运算结束后将结果又拷贝一层返回到托管代码中。这种在两个堆区域之间频繁的 `Copy-In / Copy-Out` ，实际上把 C++ 争取来的运行时间全部在数据传输阶段消耗殆尽了。

为了实现**零拷贝传输优化**，本项目利用了 C# 的 `unsafe` 机制及 `fixed` 关键字直接绕开了拷贝封装层：

### 核心实现原理与流程

```csharp
// 1. 通过 DllImport 声明非托管 C++ 方法接口
[DllImport("HighPerformanceComputing.dll", CallingConvention = CallingConvention.Cdecl)]
private static extern void ComputeOrderTracking(
    IntPtr in_data,
    int in_len,
    double current_fs,
    double rpm,
    int target_spr,
    IntPtr out_data,
    out int out_len
);

public double[] ProcessChunk(double[] rawData, double fs, double rpm, int targetSpr)
{
    int inLen = rawData.Length;
    // 提前估算并分配一个足量的数组作为输出缓冲区
    double[] outDataBuffer = new double[maxPossiblePoints];
    int actualOutLen = 0;

    // 2. 结合 unsafe 代码块与 fixed 关键字锁定数组内存区域
    unsafe
    {
        // 取得首地址指针，并将这段数组内存“钉住”
        fixed (double* pIn = rawData)
        fixed (double* pOut = outDataBuffer)
        {
            // 3. 直接将指针（强转 IntPtr）跨界传递给 C++ 方法
            ComputeOrderTracking(
                (IntPtr)pIn,   // C++ 侧接收为: double* in_data
                inLen,
                fs,
                rpm,
                targetSpr,
                (IntPtr)pOut,  // C++ 侧接收为: double* out_data
                out actualOutLen
            );
        }
    }
    
    // ... 运算结束，根据返回的有效长度实际截取结果
}
```

#### 工作流剖析：
1. **防止 GC 乱序移动对象：** `fixed` 语句的主要职能是将 C# 托管堆上的对象“钉住 (Pinning)”。由于 C# 的内存管理机制是自动缩并的，后台 GC 线程在运行清理时，可能会像碎片整理一样擅自挪动 `rawData` 和 `outDataBuffer` 在内存条上的物理地址，以保证堆空间的紧密性。如果在 C++ 计算期间其底层地址发生挪动，则 C++ 手中握持着的会变成**旧的非法野指针**，进而访问冲突引发程序崩溃。`fixed` 保证了在这段代码域结束前该数据块的物理位置绝不会被 GC 修改打扰。
2. **底层直连指针（零拷贝）：** 在数组对象由于被 `fixed` 锁定后，我们拿到了指向连续内存在首元素的真正硬件内存地址指针。我们将它转换成通用的句柄对象 `IntPtr` 抛给 C++ DLL 消费。因为 C# 主程序与被挂载的 C++ 动态链接库始终是运行在**完全一致的同一个应用程序进程下的同一块虚拟地址空间**中，这就使得处于底层的 C++ 可以无障碍地跨过 Runtime 的保护层，直接探入这块地址对应的区域，拿回它所需要的每一个数组元素。
3. **输出直写复用：** 同样地，对于想要接收的结果，在 C# 端先垫付并向操作系统申请好够用的托盘空间 `outDataBuffer`，随后依然将句柄移交给 C++。当 C++ 完成繁重的时频域计算循环后，它能够直接粗暴地将产生的最新分析值，原位复写注入到 `pOut` 对应的这块地址区间。而该空间正是挂载于 C# 托管堆上的数组。完美地避免了传统跨界调用中必须经过中间人（封送子）代为搬运所造成的数据冗余传输。

## 2. 生产者-消费者模式与 ConcurrentQueue 无锁并发提取

在工业设备的数据采集场景中，振动传感器通常会以非常高的采样率（例如本项目中的 2000Hz 甚至更高）将数据推向系统。如果我们在接收数据的同一线程里去同时执行“接收 -> UI渲染 -> 数据分析”的一条龙操作，那么不可避免地会造成系统严重的阻塞，UI 卡死且极易丢失关键数据帧。

为此，本项目使用 `ConcurrentQueue<double>` 搭配了经典的**生产者-消费者并发模型**来解决这一难题，彻底解耦了数据接入层与 UI 渲染层的频率差。

### 生产者：高速数据接收（后台计算线程）

在 `SensorDataService` 内部，系统利用 `Task.Run` 长期驻留了一个后台死循环读取任务。

```csharp
Task.Run(async () =>
{
    // 利用 Channel 的 WaitToReadAsync 异步等待，不占用 CPU 轮询资源
    while (await _tcpService.ChartReader.WaitToReadAsync())
    {
        while (_tcpService.ChartReader.TryRead(out var frame))
        {
            UpdatePoint(frame.V1_X, frame.V1_Y, frame.V1_Z);
            // ... 
        }
    }
});

private void UpdatePoint(double x, double y, double z)
{
    // 生产者将接收到的细碎数据丢入线程安全的并发队列
    BufferV1_X.Enqueue(x);
    BufferV1_Y.Enqueue(y);
    BufferV1_Z.Enqueue(z);
}
```

*   **完全无锁阻塞：** 这里的 `BufferV1_X` 是 .NET 提供的 `ConcurrentQueue<double>` 并发队列。它在底层使用了**无锁（Lock-Free）与原子操作（Interlocked）**的设计算法进行入队操作，这意味着即使生产者线程以每秒极大的数量级疯狂调用 `Enqueue`，它也不会像传统的 `lock (obj)` 一样产生严重的线程安全开销和上下文切换。
*   **平滑吞吐：** 只要底层网络传来数据，它就只管收然后立即推入这个内存缓冲区。

### 消费者：受控批量提取与渲染（UI 刷新线程）

在界面端 `DashboardView.xaml.cs`，渲染层面对的难题是如何不把图表组件“撑爆”。

```csharp
// 在 UI 挂载时，订阅 CompositionTarget.Rendering 事件
CompositionTarget.Rendering += OnFrameRender;

private void OnFrameRender(object sender, EventArgs e)
{
    // 这个事件的触发频率与显示器的刷新率天然同步（通常为 60Hz 左右）
    if (_vm != null && _vm.TryConsumeBufferForFrame())
    {
        WpfPlot1.Refresh();  // 最终推入 UI 一次性渲染
    }
}
```

紧接着在 `DashboardViewModel` 内定义的 `TryConsumeBufferForFrame` 就是真正的**消费者**逻辑：

```csharp
public bool TryConsumeBufferForFrame()
{
    // 1. 获取当前缓冲区内“积攒”的任务总量
    int currentCount = _sensorDataService.BufferV1_X.Count;
    if (currentCount == 0) return false;
    
    // 2. 准备同等尺寸的批量转运数组
    double[] batchX = new double[currentCount];

    // 3. 循环批量出队
    for (int i = 0; i < currentCount; i++)
    {
        _sensorDataService.BufferV1_X.TryDequeue(out batchX[i]);
    }

    // 4. 将批处理后的连续数据，一次性推给 ScottPlot 组件图表流水线
    _streamerV1_X.AddRange(batchX);
    
    return true;
}
```

#### 工作流剖析与优势：
1. **天然的帧同步批处理（Batching）：** 以 60Hz 的屏幕为例，每两次 `OnFrameRender` 刷新事件中间相隔了大约 16.6ms。在这段无感的时间间隔里，传感器可能已经以 2000Hz 的频率给系统推送了 30~40 个数据点！因为有了并发队列充当水库缓冲，消费者不需要被动地接1点画1点，而是**一次性直接带走这 40 个积压点作为一帧**。这就是高频流数据之所以能在这套上位机上维持丝滑渲染的核心原因——批量重绘（Redraw）。
2. **读写分离与高解耦：** 整个数据流的生命周期非常清晰完整：网络 I/O  -> `ConcurrentQueue` -> 60Hz UI Loop，两边的线程各司其职。就算网络突然剧增抖动产生洪峰，UI 层依然维持着自己淡定的 60Hz 节奏去消费重绘，绝不卡顿主界面。这种架构在处理工控监控应用时具备极佳的稳定性优势。

## 3. 原生 TCP Socket 异步通信与零拷贝解析模型

对于上位机而言，如何长期稳定、低延迟地保持与底层下位机（如 STM32 等单片机）的通讯，是整个系统的基石。本项目抛弃了封装过重的高层通信组件，直接基于 `System.Net.Sockets.TcpListener` 打造了纯异步的高性能 TCP 服务端。

我们在 `TCPServerService.cs` 中实现了以下几个核心的架构设计：

### 3.1 全异步非阻塞的监听与接收

传统的 TCP 接收往往容易犯一个错误：在主线程里 `AcceptTcpClient` 甚至 `Read`，这直接导致界面僵死。本项目全面拥抱了 `.NET` 提供的 `async/await` 异步状态机模型：

```csharp
public async Task StartListeningAsync(int port, CancellationToken cancellationToken)
{
    // ...
    while (!cancellationToken.IsCancellationRequested)
    {
        // 1. 【异步等待连接】：线程在这里是被让出的，不会卡死 UI
        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);

        // ... 管理旧连接并清理 ...
        
        // 2. 【无头任务分发】：新客户端连入后，甩给后台线程池去独立处理，主循环马上回头继续去等下一个客户端
        _ = Task.Run(() => HandleClientAsync(client, _clientCts.Token), CancellationToken.None);
    }
}
```

而在最为耗时的 `HandleClientAsync` 数据长连接读取循环中，同样使用了 `await stream.ReadAsync(...)`。这意味着当网络上没有收到数据包时，承载它的线程是会被“释放”回线程池供其他任务使用的，从而将系统的闲置资源消耗降到最低。

### 3.2 利用 CancellationTokenSource 实现智能“看门狗”

工控环境下的网络有时是不稳定的，如果网线突然被拔掉或者下位机死机，TCP 很有可能变成“半死不活”的状态（即服务端还在傻等着读数据，但其实物理链路已经断了）。
本项目非常巧妙地利用了 `CancellationTokenSource` 的级联与倒计时功能，实现了一个自带资源回收的自动连接超时看门狗：

```csharp
while (!token.IsCancellationRequested)
{
    // 创建一个级联的 Token，它既受全局停止控制，又带有 15 秒倒计时爆炸属性
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
    
    try
    {
        // 传入带有倒计时的 Token
        bytesRead = await stream.ReadAsync(buffer, validBytes, buffer.Length - validBytes, timeoutCts.Token);
    }
    catch (OperationCanceledException)
    {
        // 捕捉取消异常：如果是超时引发的取消，说明设备掉线了！
        if (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // 记录日志，并打破循环，下方的 using 和 finally 会自动把废弃连接斩断清理
            break; 
        }
    }
    // ...
}
```

### 3.3 Span&lt;T&gt; 与 MemoryMarshal 的内存零分配解包

当网络流（流通常是没有完整帧边界概念的，存在粘包和半包）累积拼凑成一个完整的帧长度（例如 `PACKET_SIZE = 93` 字节）后，需要将其解析为 C# 可以操作的结构体对象。如果按照老旧的 `BinaryReader` 方式挨个读取属性，或者分配一个新的 byte 数组去给它独立切片，会引发海量的内存碎片。

项目中大量使用了现代 C# 的 `ReadOnlySpan<byte>` 与 `MemoryMarshal.Read<T>`，在原始的 `byte[] buffer` 上实现了就地结构体强转（In-place Cast）：

```csharp
// 【零拷贝截取与强转】：直接把底层大 buffer 中的某一小段切片视为一整个物理结构体！
ReadOnlySpan<byte> packetSpan = new ReadOnlySpan<byte>(buffer, processedOffset, PACKET_SIZE);
SensorFrameStruct frame = MemoryMarshal.Read<SensorFrameStruct>(packetSpan);

// 将强转出来的值对象通过 Channel 广播给其他消费者通道
BroadcastFrame(frame);
```

这里的 `SensorFrameStruct` 结构体使用了 `[StructLayout(LayoutKind.Sequential, Pack = 1)]` 进行标记，使其在内存中的排列方式和 C/C++ 单片机发送的裸流完全保持 1 字节对齐。通过这层底层的内存映射，直接将一串 93 字节的数据包顺滑地还原为了含有数十个属性的高级结构体，**全程没有 new 任何新的临时内存块**，极度压榨了反序列化的开销！

## 4. CWT 连续小波时频热力图

为在深度诊断页面直观展示轴承振动信号的能量随时间和频率的分布情况，本项目实现了完整的 CWT（Continuous Wavelet Transform）计算与热力图渲染管线。

### 4.1 算法设计

采用 Morlet 小波作为母小波，通过频域卷积实现快速 CWT 计算：

- **Morlet 小波**：中心频率 ω₀ = 6（标准值），兼顾时频分辨率平衡
- **频域卷积法**：对信号做一次 FFT（O(N log N)），每个尺度在频域构造小波谱并相乘，IFFT 后取模
- **对数频率尺度**：50 Hz ~ 16 kHz 间以对数均匀分布 128 个频率点，低频分辨率高（捕捉轴承转频谐波），高频减少冗余计算
- **时间轴降采样**：原始 32768 时间点通过区间最大值池化（max pooling）降至 512，确保不丢失瞬态冲击特征

### 4.2 独立 FFTW 资源隔离

CWT 模块维护独立的 `fftw_complex` 缓存与 FFTW Plan，与 `OrderTracking.cpp` 中的全局变量完全隔离，避免多模块并发时的资源竞争。

```cpp
static fftw_complex* cwt_fft_in = nullptr;
static fftw_complex* cwt_fft_out = nullptr;
static fftw_plan cwt_plan_fwd = nullptr;
static fftw_plan cwt_plan_inv = nullptr;
static int cwt_signal_len = 0;
static std::mutex cwt_mutex;
```

### 4.3 C++ 导出接口

```cpp
// 初始化 CWT 专用 FFTW 资源（启动时调用一次）
EXPORT_API void InitCWT(int signal_len, int num_scales, int num_time_bins);

// 核心计算：输入原始信号，输出 CWT 系数矩阵的幅值（已降采样）
EXPORT_API void ComputeCWT(
    const double* signal,     // 输入信号 [signal_len]
    int signal_len,           // 信号长度 (32768)
    double fs,                // 采样率 (64000)
    double freq_low,          // 最低分析频率 (50 Hz)
    double freq_high,         // 最高分析频率 (16000 Hz)
    double* out_matrix,       // 输出矩阵 [num_scales × num_time_bins]，行优先
    int num_scales,           // 频率轴点数 (128)
    int num_time_bins         // 时间轴降采样点数 (512)
);
```

### 4.4 热力图渲染管线

采用 WPF 原生 `WriteableBitmap` 直接像素写入，避免使用 ScottPlot Heatmap 控件在大矩阵高频刷新场景下的性能瓶颈：

1. **C# P/Invoke 封装**：通过 `fixed` 指针零拷贝传递数组到 C++ DLL
2. **ViewModel 节流**：每 2 帧触发一次 CWT 计算（约 15ms/帧），不拖慢主数据流
3. **WriteableBitmap 渲染**：
   - 图像尺寸 512×128（Bgra32 格式）
   - 全局 min-max 归一化
   - 预计算 256 色 Inferno 色图 LUT，按值映射为 BGRA 像素
   - `Lock()` → `Marshal.Copy` → `AddDirtyRect` → `Unlock()`，零 GC 分配
4. **CompositionTarget.Rendering**：与显示器刷新率同步消费 `ConcurrentQueue` 中的 CWT 数据帧

### 数据流架构

```
传感器数据 (64kHz) → ViewModel ProcessLoop → C++ ComputeCWT (频域卷积)
  → CwtHeatmapData (128×512 矩阵) → ConcurrentQueue (无锁队列)
  → OnFrameRender (CompositionTarget) → WriteableBitmap (像素写入)
  → WPF Image 控件 (GPU 硬件加速)
```
