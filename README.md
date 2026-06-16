# 工业轴承故障智能诊断与预测性维护系统

基于 .NET 8 构建的高性能工业设备健康监测上位机系统。通过采集并分析振动、电流、倾角等多源传感器数据，实现对隧道射流风机三大典型故障的实时监测与早期预警。

## 技术栈

`C#` | `WPF` | `C++` | `.NET 8` | `CommunityToolkit.Mvvm` | `ScottPlot 5` | `P/Invoke` | `FFTW` | `ONNX Runtime` | `Python`

## 核心特性

- **企业级应用架构：** 基于 .NET 8 泛型主机（Generic Host）与 MVVM 模式，依赖注入 + 组件生命周期管理。
- **双模数据接入：** 实时 TCP 数据流（5 kHz 采样率）+ 离线 `.mat` 文件加载。
- **高性能数据采集：** 原生 TCP Socket + 生产者-消费者并发模型（ConcurrentQueue + Channel）。
- **跨语言零拷贝计算：** C++ FFTW/FFT 封装为 DLL，P/Invoke 零拷贝内存交互。
- **CWT 连续小波时频热力图：** Morlet 小波 + FFTW 加速 + WriteableBitmap 像素渲染。
- **AI 智能诊断：** ONNX Runtime 四模型并行推理，故障分类置信度评估。
- **三大故障监测算法：** 叶片不平衡、风口堵塞、螺栓松动的实时监测与自动报警。
- **传感器数据模拟器：** Python TCP 模拟器，支持四种故障模式实时切换。

## 界面概览

### 1. 主界面
![主界面](img/dashboard.png)

### 2. 深度诊断
![深度诊断](img/deep_diag.png)

---

## 三大故障监测算法

### 1. 叶片不平衡监测

| 维度 | 实现 |
|---|---|
| 输入信号 | 振动加速度（主通道），5 kHz 采样率 |
| 特征提取 | 1×fr 幅值 A₁（±2 bin 峰值搜索）、谐波比 R₂₁ = A₂/A₁、Goertzel 相位 σφ |
| 平滑策略 | EWMA 指数加权移动平均（λ=0.15） |
| 基线标定 | Welford 在线算法，前 30 期自动建立 μ/σ 基线 |
| 判定逻辑 | A₁ > μ+3σ **且** R₂₁ < 0.3 **且** σφ < 15°，连续 ≥3 期 → 报警 |
| 状态机 | Calibrating → Normal → Watch → Warning → Alarm |
| 算法类 | [`Models/BladeImbalanceDetector.cs`](Models/BladeImbalanceDetector.cs) |

### 2. 进/出风口堵塞监测

| 维度 | 实现 |
|---|---|
| 输入信号 | 电流 RMS（主通道） |
| 特征提取 | 归一化偏差 ΔI = (E_n − μ_I) / σ_I |
| 平滑策略 | EWMA（λ=0.15） |
| 基线标定 | Welford 在线算法，720 期（~39 分钟）自动建立 |
| 判定逻辑 | \|ΔI\| > 3（3σ 准则），连续 ≥60s（~19 期） → 报警 |
| 状态机 | Calibrating → Normal → Warning → Alarm |
| 算法类 | [`Models/BlockageDetector.cs`](Models/BlockageDetector.cs) |

### 3. 安装螺栓松动监测

| 维度 | 实现 |
|---|---|
| 输入信号 | 倾角传感器（主通道），**7×24 全时运行** |
| 特征提取 | 静态分量 θ_DC、10 分钟滑动窗口、7 天线性回归漂移速率 dθ/dt |
| 温度补偿 | 线性回归模型 θ = a·T + b，标定期间自动学习系数 |
| 基线标定 | Welford 在线算法，200 期（~11 分钟，生产环境建议 ≥24h） |
| 判定逻辑 | 两级阈值：Δθ > 0.1° 且 dθ/dt > 0 → 预警；Δθ > 0.3° → 报警 |
| 状态机 | Calibrating → Normal → Warning → Alarm |
| 算法类 | [`Models/BoltLoosenDetector.cs`](Models/BoltLoosenDetector.cs) |

### 算法架构

三个检测器遵循统一的设计模式：

```
独立算法类 (Models/*Detector.cs)
├── *AlarmState 枚举        — 状态机定义
├── *Result record          — 单次监测结果快照
├── Process() 方法          — 每帧调用，输入原始数据，输出结果快照
├── Welford 在线标定        — μ/σ 增量计算，无需存储历史数据
├── ResetCalibration()      — 重置基线，支持手动重新标定
└── 内部状态管理            — EWMA、连续计数、相位历史等
```

### 标定时间参考

| 算法 | 标定期数 | 实际耗时 | 说明 |
|---|---|---|---|
| 叶片不平衡 | 30 期 | ~98 秒 | 自动标定 |
| 风口堵塞 | 720 期 | ~39 分钟 | 自动标定 |
| 螺栓松动 | 200 期 | ~11 分钟 | 测试值，生产建议 ≥24h |

---

## 传感器数据模拟器

[`Tools/sensor_simulator.py`](Tools/sensor_simulator.py) — 纯 Python 标准库实现的 TCP 传感器帧模拟器：

| 特性 | 说明 |
|---|---|
| 协议 | 93 字节 TCP 帧，与 `SensorFrameStruct` 完全一致 |
| 速率 | 5000 Hz 实时发送，100 帧/批 |
| 故障模式 | 正常运行、叶片不平衡、风口堵塞、螺栓松动（快捷键 1~4 切换） |
| 严重程度 | `+/-` 键实时调节 0%~100% |

```bash
# WSL 中运行
cd /mnt/d/上位机/BearingFaultDiagnosis/Tools
python3 sensor_simulator.py --host <Windows主机IP> --port 5000
```

---

## 单元测试

共 **59 个测试用例**，覆盖三大算法的核心逻辑：

| 测试文件 | 用例数 | 覆盖范围 |
|---|---|---|
| `BladeImbalanceDetectorTests.cs` | 27 | Goertzel 相位、峰值搜索、EWMA、标定、状态机 |
| `BlockageDetectorTests.cs` | 16 | RMS 计算、Welford 基线、偏差判定、60s 持续性 |
| `BoltLoosenDetectorTests.cs` | 16 | 滑动窗口、线性回归、温度补偿、两级阈值 |

```powershell
dotnet test Tests\BearingFaultDiagnosis.Tests.csproj
```

---

## 项目结构

```text
 ┣ 📁 Models
 ┃  ┣ 📄 BladeImbalanceDetector.cs   # 叶片不平衡检测引擎
 ┃  ┣ 📄 BlockageDetector.cs         # 风口堵塞检测引擎
 ┃  ┗ 📄 BoltLoosenDetector.cs       # 螺栓松动检测引擎
 ┣ 📁 ViewModels
 ┃  ┗ 📄 DeepDiagnosisViewModel.cs   # 三大检测器集成 + 数据管线
 ┣ 📁 Views/Pages
 ┃  ┣ 📄 DeepDiagnosisView.xaml      # 四卡片双行布局
 ┃  ┗ 📄 DeepDiagnosisView.xaml.cs   # BindPlots + 线程安全渲染
 ┣ 📁 Services
 ┃  ┣ 📁 Interfaces                  # 服务接口定义
 ┃  ┗ 📁 Implements                  # 服务实现（TCP、传感器数据、FFT 等）
 ┣ 📁 HighPerformanceComputing       # C++ 算法层（FFT、阶次跟踪、CWT）
 ┣ 📁 DLModels                       # ONNX 深度学习模型
 ┣ 📁 Tests/Services
 ┃  ┣ 📄 BladeImbalanceDetectorTests.cs
 ┃  ┣ 📄 BlockageDetectorTests.cs
 ┃  ┗ 📄 BoltLoosenDetectorTests.cs
 ┣ 📁 Tools
 ┃  ┗ 📄 sensor_simulator.py         # Python TCP 传感器模拟器
 ┣ 📄 BearingFaultDiagnosis.sln      # 解决方案文件
 ┗ 📄 appsettings.json               # 运行时配置（TCP 端口等）
```

---

## 快速启动（开发环境）

### 环境要求

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 8.0+ | [下载地址](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Visual Studio | 2022 | 需安装 **C++ 桌面开发** 工作负载（MSVC v145） |
| Python（可选） | 3.10+ | 仅运行模拟器时需要 |

### 步骤

1. `git clone` 并打开 `BearingFaultDiagnosis.sln`。
2. 将解决方案平台切换为 **x64**（C++ 项目仅支持 x64）。
3. Visual Studio 自动按依赖顺序编译：先 C++ DLL → 再 C# 主程序。
4. 按 F5 启动调试。

---

## 打包发布

### 方式一：框架依赖发布（目标机器已安装 .NET 8）

```powershell
# 在项目根目录执行
dotnet publish BearingFaultDiagnosis.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o publish\framework-dependent
```

发布后手动复制原生 DLL：

```powershell
# 复制 C++ 计算库和 FFTW3 依赖到发布目录
copy bin\Release\net8.0-windows\HighPerformanceComputing.dll publish\framework-dependent\
copy libs\fftw3\libfftw3-3.dll publish\framework-dependent\
copy libs\fftw3\libfftw3f-3.dll publish\framework-dependent\
copy libs\fftw3\libfftw3l-3.dll publish\framework-dependent\
```

### 方式二：自包含发布（目标机器无需安装 .NET）

```powershell
dotnet publish BearingFaultDiagnosis.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o publish\self-contained
```

> **注意**：自包含模式不支持 SingleFile，因为 WPF 和本机 DLL 加载不兼容单文件打包。

同样需要复制原生 DLL：

```powershell
copy bin\Release\net8.0-windows\HighPerformanceComputing.dll publish\self-contained\
copy libs\fftw3\libfftw3-3.dll publish\self-contained\
copy libs\fftw3\libfftw3f-3.dll publish\self-contained\
copy libs\fftw3\libfftw3l-3.dll publish\self-contained\
```

### 发布目录结构

发布完成后，发布目录应包含以下关键文件：

```text
publish/
 ┣ 📄 BearingFaultDiagnosis.exe       # 主程序
 ┣ 📄 HighPerformanceComputing.dll    # C++ FFT/阶次跟踪计算库
 ┣ 📄 libfftw3-3.dll                  # FFTW3 双精度库
 ┣ 📄 libfftw3f-3.dll                 # FFTW3 单精度库
 ┣ 📄 libfftw3l-3.dll                 # FFTW3 长双精度库
 ┣ 📄 appsettings.json                # 运行时配置
 ┣ 📁 DLModels/                       # ONNX 模型文件
 ┃  ┣ 📄 best_I_s2024.onnx
 ┃  ┣ 📄 best_J_s2024.onnx
 ┃  ┣ 📄 best_K_s2024.onnx
 ┃  ┗ 📄 best_L_s2024.onnx
 ┗ 📁 ...                             # .NET 运行时及其他依赖
```

---

## 部署与使用

### 部署到目标机器

1. 将整个 `publish/` 目录复制到目标 Windows 机器（如 `C:\Program Files\BearingFaultDiagnosis\`）。
2. 确保以下文件齐全：
   - `BearingFaultDiagnosis.exe`
   - `HighPerformanceComputing.dll`
   - `libfftw3-3.dll` / `libfftw3f-3.dll` / `libfftw3l-3.dll`
   - `appsettings.json`
   - `DLModels/` 目录（含 4 个 .onnx 文件）
3. 如果是框架依赖发布，目标机器需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 配置

编辑 `appsettings.json` 调整运行参数：

```json
{
  "ServerSettings": {
    "Port": 5000,          // TCP 监听端口（与下位机/模拟器端口一致）
    "SampleRate": 5000     // 采样率 (Hz)
  }
}
```

### 运行

1. 双击 `BearingFaultDiagnosis.exe` 启动上位机。
2. 在主界面点击 **启动服务** 开始 TCP 监听。
3. 下位机（STM32）或模拟器连接后，数据自动流入。
4. 切换到 **深度诊断** 页面，观察四张算法监测卡片：
   - **螺栓松动监测**（左上）— 7×24 全时运行，无需风机运转
   - **风口堵塞预警**（左下）— 基于电流 RMS 偏差
   - **叶片不平衡监测**（右上）— 基于振动频谱特征
   - **辅助验证与环境补偿**（右下）— 温度 vs 倾角散点
5. 各算法自动进入标定阶段，标定完成后进入正常监测模式。
6. 点击 **执行基线标定** 按钮可手动重置所有算法的基线。

### 使用模拟器测试（无下位机时）

```bash
# 在 WSL 或 Linux 环境中运行
python3 Tools/sensor_simulator.py --host <上位机IP> --port 5000

# 运行后按 1~4 切换故障模式，按 +/- 调节严重程度
```

---

## 开源协议

本项目基于 MIT 协议开源，详见 [LICENSE](LICENSE) 文件。
