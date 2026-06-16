# AGENTS.md

This file provides guidance to Qoder (qoder.com) when working with code in this repository.

## Build & Run

**前提条件**: .NET 8 SDK + Visual Studio（需安装 C++ 桌面开发工作负载，MSVC v145 工具集）

**构建解决方案**（C++ DLL 必须先编译，C# 主项目引用了它）:
```powershell
dotnet build BearingFaultDiagnosis.sln
```

C++ 项目 (`HighPerformanceComputing`) 仅支持 **x64** 平台配置。构建后 PostBuildEvent 会自动将 DLL 和 FFTW3 依赖拷贝到 C# 项目的输出目录 `bin\Debug\net8.0-windows\`。

**运行测试** (xUnit v3):
```powershell
dotnet test Tests\BearingFaultDiagnosis.Tests.csproj
```

**运行单个测试类**:
```powershell
dotnet test Tests\BearingFaultDiagnosis.Tests.csproj --filter "FullyQualifiedName~DeepDiagnosisServiceTests"
```

**注意**: 测试项目直接引用主项目（而非独立抽离业务层），测试中涉及 P/Invoke 调用 C++ DLL 的服务需要确保 DLL 在测试输出目录中可用。

## Architecture Overview

本项目是一个 **.NET 8 WPF + C++ DLL** 的混合架构工业轴承故障诊断上位机系统，采用三层设计：

### 1. 服务层（.NET Generic Host + DI）

`App.xaml.cs` 通过 `Host.CreateDefaultBuilder()` 构建泛型主机，所有服务和 ViewModel 在 `ConfigureServices` 中注册：

- **单例服务**: `TCPServerService`（TCP 通信）、`SensorDataService`（数据缓冲）、`AppSession`（用户会话）
- **瞬态服务**: `DeepDiagnosisService`（DSP 计算 + P/Invoke）、`UserService`、`BearingInfoService`、所有 ViewModel
- **DbContext**: 使用 `AddDbContextFactory<AppDbContext>`（而非 `AddDbContext`），因为 WPF 长生命周期没有 Web 请求边界，直接用 AddDbContext 会导致全局单例 DbContext

配置绑定：`services.Configure<ServerSettings>(configuration.GetSection("ServerSettings"))`

### 2. 数据流管线（Producer-Consumer）

核心数据流路径：
```
传感器/下位机 → TCP Socket (异步) → TCPServerService (Channel广播)
  → SensorDataService (ConcurrentQueue 缓冲) → ViewModel ProcessLoop (后台Task)
  → C++ DLL (P/Invoke 零拷贝) → ScottPlot/WriteableBitmap → UI
```

**TCPServerService** 使用三个 `Channel<SensorFrameStruct>` 分发数据：
- `_uiChannel`: Bounded(1024, DropOldest) → UI 显示
- `_chartChannel`: Bounded(5000, DropOldest) → 图表渲染
- `_dataChannel`: Unbounded → 数据存储

**SensorDataService** 用 `ConcurrentQueue<double>` (BufferV1_X/Y/Z) 作为生产者-消费者缓冲区，后台 Task 从 ChartReader 消费入队，UI 通过 `CompositionTarget.Rendering` 事件批量出队渲染。

**SensorFrameStruct** 使用 `[StructLayout(LayoutKind.Sequential, Pack=1)]` 与 C 结构体 1 字节对齐匹配，93 字节包格式 (0xAA55 帧头 + 0x0D0A 帧尾)。TCP 流解包通过 `MemoryMarshal.Read<SensorFrameStruct>(packetSpan)` 实现零分配结构体强转。

### 3. 跨语言计算层（C++ DLL + P/Invoke）

`HighPerformanceComputing.dll` 包含三个独立的计算模块，各自维护独立的 FFTW 资源：

- **OrderTracking.cpp**: 计算阶次跟踪 (COT) + 包络谱 — 使用全局 `g_fft_in/out` + `g_plan_fwd/inv`
- **CWT.cpp**: 连续小波变换 (Morlet, 频域卷积法) — 使用独立的 `cwt_fft_in/out` + `cwt_plan_fwd/inv`
- **fft.cpp**: 辅助接口

C# 侧 `DeepDiagnosisService` 通过 `DllImport` + `unsafe fixed` 指针实现**零拷贝**数据传递：
- C# 数组通过 `fixed (double* pIn = rawData)` 锁定（Pinning），防止 GC 移动
- 指针强转 `(IntPtr)pIn` 直接传给 C++，同一进程虚拟地址空间无需拷贝
- 输出缓冲区在 C# 端预分配，C++ 直写复用

**初始化顺序**: C# 静态构造函数中调用 `InitFFTW()` + `InitCWT(32768, 128, 512)` 缓存 FFTW Plan，运行时复用避免重复 `fftw_plan` 创建开销。

### 4. ViewModel 层（CommunityToolkit.Mvvm）

所有 ViewModel 继承 `ViewModelBase : ObservableObject`，使用源生成器：
- `[ObservableProperty]` 自动生成属性 + PropertyChanged 通知
- `[RelayCommand]` 自动生成 ICommand
- `partial void OnXxxChanged(value)` 响应属性变更

**导航**: `MainViewModel.NavigateByRoute` 通过 DI 容器 `GetRequiredService<TViewModel>` 切换 CurrentViewModel，菜单权限由 `UserService.GetMenusByRoleId` 从数据库查询。

**DeepDiagnosisViewModel** 核心循环：
- `ProcessLoopAsync` 后台 Task 持续从 `ConcurrentQueue` 消费数据填入 `_buffer[32768]`
- 满 ChunkSize 后执行 DSP 计算（FFT、CWT 等），更新四个 ScottPlot Plot 实例
- 通过 `XxxDirty` 标记驱动 View 端 `CompositionTarget.Rendering` 刷新图表

### 5. 数据持久层（EF Core + SQLite）

- `AppDbContext` 使用 SQLite，种子数据包含：管理员/普通用户角色、轴承参数（8 种型号含 BPFO/BPFI/BSF/FTF 系数）、设备信息、菜单权限
- `BearingInfoService.CalculateFaultFrequencies` 优先使用数据库已存系数，无系数时用几何参数公式推算
- 默认管理员账号: `admin` / `123456`

## Key Conventions

- **C++ 导出接口**: 使用 `extern "C" __declspec(dllexport)` 宏 (`EXPORT_API`)，防止 C++ 名称重整
- **C++ 标准**: C++20，预编译头 `pch.h`，`/utf-8` 编译选项
- **FFTW3 依赖**: DLL 和 `.lib` 文件位于 `libs\fftw3\`，C++ 项目通过 PostBuildEvent 拷贝到输出目录
- **ONNX 模型**: 4 个模型文件 (`DLModels/best_I/J/K/L_s2024.onnx`) 配置为 `CopyToOutputDirectory: PreserveNewest`
- **unsafe 代码**: 主项目启用 `AllowUnsafeBlocks`，用于 P/Invoke 零拷贝指针操作
- **MVVM 源生成器**: 属性命名用 `_xxx` 私有字段 + `[ObservableProperty]`，命令用 `[RelayCommand]` 私有方法
- **WPF 生命周期**: 启动时显示 LoginWindow → 登录后切换 MainWindow，主机在 `OnExit` 中优雅关闭