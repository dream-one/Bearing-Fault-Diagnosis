# 工业轴承故障智能诊断与预测性维护系统 (Intelligent Bearing Fault Diagnosis and Predictive Maintenance System)

这是一个基于 .NET 8 构建的高性能跨平台设备健康监测上位机系统。通过采集并分析工业设备的高频振动数据，实现对设备早期故障的准确诊断与预测性维护，降低因设备意外停机带来的经济损失。

## 🛠️ 技术栈 (Tech Stack)

**C#** | **WPF** | **C++** | **.NET 8** | **CommunityToolkit.Mvvm** | **Socket** | **ScottPlot** | **P/Invoke**

## ✨ 核心特性 

- **企业级应用架构：** 基于 **.NET 8** 搭建跨平台 **MVVM** 客户端，并引入了泛型主机架构（Generic Host）实现全局服务依赖注入（Dependency Injection）与对象生命周期管理。
- **双模数据接入机制：** 支持实时与离线两种数据流模式。一方面可利用原生的下位机高速通信接收实时振动数据流进行监控；另一方面支持直接解析加载本地已保存的 `.mat` (MATLAB) 格式历史数据，方便复盘和算法对照分析。
- **高性能数据采集：** 基于原生 **TCP Socket** 封装异步通信模块，引入**生产者-消费者**并发模型与 `ConcurrentQueue` 线程安全队列，可以安全稳定地处理每秒 2000 次的高频振动数据流。
- **跨语言零拷贝计算优化：** 针对密集型计算场景设计了专用的 **C++ 高性能计算模块 (HighPerformanceComputing)**。将 FFT（快速傅里叶变换）、阶次跟踪 (Order Tracking) 等核心算法采用 C++ 封装为动态链接库 (DLL)；通过 **P/Invoke** 实现 C# 与 C++ 数组之间的**零拷贝内存交互**，极大地避免了大量浮点数据在托管与非托管内存切换时引发的垃圾回收（GC）开销，显著提升了实时流数据的处理速度与系统吞吐量。
- **现代化图表渲染：** 结合 **ScottPlot** 提供每秒几十帧的高流畅度波形渲染能力，以图形化手段实时展示波形波动趋势。

## 📂 项目结构说明

本仓库采用多项目解决方案（包含 C# 前端及业务层与 C++ 算法层）：

```text
 ┣ 📁 Behaviors       # 存放附加行为（Attached Behaviors）
 ┣ 📁 Controls        # 存放自定义控件（Custom Controls）或复用的 UserControl
 ┣ 📁 Converters      # 存放各种数据绑定转换器（IValueConverter / IMultiValueConverter）
 ┣ 📁 Core            # 核心基础类（如：中介者、基类、全局配置、常量等）
 ┣ 📁 Extensions      # 存放 C# 扩展方法
 ┣ 📁 HighPerformanceComputing # C++ 核心算法工程（负责 DSP、FFT 信号隔离计算与 DLL 导出）
 ┣ 📁 Models          # 存放数据模型、实体类、DTO 等
 ┣ 📁 Resources       # 存放静态资源(包含文字、图表、全局样式等)
 ┣ 📁 Services        # 存放业务服务、数据访问服务、通信等
 ┃  ┣ 📁 Interfaces   # 服务接口定义（如 ISensorDataService）
 ┃  ┗ 📁 Implements   # 服务接口实现 (依赖注入)
 ┣ 📁 ViewModels      # 存放视图模型类（包含业务逻辑和 UI 状态）
 ┣ 📁 Views           # 存放所有的 UI 视图
 ┃  ┣ 📁 Windows      # 存放独立的 Window
 ┃  ┗ 📁 Pages        # 存放页面（基于 Navigation）
 ┣ 📄 App.xaml        # 应用程序入口及资源引入
 ┗ 📄 MainWindow.xaml # 主窗体
```

## 🚀 快速启动

1. 确保已安装好 **.NET 8 SDK** 以及支持 **C++ 桌面开发工作负载**（MSVC、C++ CMake 等）的 Visual Studio。
2. 使用 `git clone <仓库地址>` 将项目克隆至本地。
3. 双击 `BearingFaultDiagnosis.sln` 打开项目。
4. 编译时，Visual Studio 会自动按工程依赖顺序，先编译 `HighPerformanceComputing` 项目生成 DLL，再编译 C# 主程序。
5. 在需要的情况下还原 NuGet 依赖。
6. 点击上方「启动」编译并运行此上位机系统应用程序。

---

如果遇到使用上的问题，欢迎在 GitHub 提交 Issue。