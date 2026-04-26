# 轴承故障诊断上位机系统 (Bearing Fault Diagnosis System)

这是一个基于 WPF (Windows Presentation Foundation) 和 C# 开发的轴承故障诊断上位机系统。项目采用了 MVVM (Model-View-ViewModel) 架构模式，以实现界面表现与业务逻辑的解耦，方便后期维护和扩展。

## 主要特性

- **现代化的 UI 界面**：使用 WPF 构建，支持自定义样式和丰富的数据绑定。
- **MVVM 架构设计**：项目有着清晰的层次结构，分为 Models，ViewModels 和 Views。
- **可扩展性强**：具有松耦合的设计理念以及专门的 Services 和 Core 核心基础类层。
- **丰富的 UI 控件和资源**：封装了自定义控件和常用的转换器以提升开发效率。

## 项目结构说明

```
 ┣ 📁 Behaviors       # 存放附加行为（Attached Behaviors）
 ┣ 📁 Controls        # 存放自定义控件（Custom Controls）或复用的 UserControl
 ┣ 📁 Converters      # 存放各种数据绑定转换器（IValueConverter / IMultiValueConverter）
 ┣ 📁 Core            # 核心基础类（如：中介者、基类、全局配置、常量等）
 ┣ 📁 Extensions      # 存放 C# 扩展方法
 ┣ 📁 Models          # 存放数据模型、实体类、DTO 等
 ┣ 📁 Resources       # 存放静态资源
 ┃  ┣ 📁 Fonts        # 字体文件
 ┃  ┣ 📁 Images       # 图片、图标
 ┃  ┣ 📁 Locales      # 多语言资源文件（Resx或JSON）
 ┃  ┗ 📁 Styles       # 存放 XAML 样式字典（Colors.xaml, Buttons.xaml, Themes 等）
 ┣ 📁 Services        # 存放业务服务、数据访问服务、对话框服务等
 ┃  ┣ 📁 Interfaces   # 服务接口定义（如 IUserService, IDialogService）
 ┃  ┗ 📁 Implements   # 服务接口实现
 ┣ 📁 ViewModels      # 存放视图模型类（包含业务逻辑和 UI 状态）
 ┣ 📁 Views           # 存放所有的 UI 视图
 ┃  ┣ 📁 Windows      # 存放独立的 Window（如 MainWindow, LoginWindow）
 ┃  ┣ 📁 Pages        # 存放页面（基于 Navigation 的应用）
 ┃  ┗ 📁 UserControls # 存放各个模块的 UserControl
 ┣ 📄 App.xaml        # 应用程序入口及全局资源引入
 ┗ 📄 MainWindow.xaml # 主窗体（通常会移到 Views/Windows 目录下）
```

## 运行环境

- Windows 操作系统
- .NET 运行环境 (请确保安装了合适的 .NET SDK，如 .NET 6 / .NET 8)
- Visual Studio (推荐) 或者其他支持 C# WPF 的 IDE

## 快速启动

1. 克隆本项目到本地。
2. 使用 Visual Studio 打开 `BearingFaultDiagnosis.sln`。
3. 还原 NuGet 包。
4. 编译并运行项目。

## 贡献指南

欢迎各位开发者提交 Issue 和 Pull Request，共同完善此项目！