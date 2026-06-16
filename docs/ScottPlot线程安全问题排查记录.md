# ScottPlot 5 WpfPlot 多线程渲染竞态问题排查与修复记录

## 问题背景

本项目在深度诊断页面（`DeepDiagnosisView`）中同时展示 6 张 ScottPlot 图表：

- 振动加速度时域波形
- 电流 + 倾角混合趋势图
- 螺栓松动监测（倾角漂移）
- 叶片不平衡监测（振动频谱）
- 风口堵塞预警（电流 RMS 偏差）
- 辅助验证与环境补偿（温度 vs 倾角散点）

数据流架构为**生产者-消费者**模型：后台线程（`ProcessLoopAsync`）以 ~3.3s 的周期采集传感器数据、运行检测算法，然后通过 `ConcurrentQueue` 和事件将数据推送到 UI 线程。

## 异常现象

应用运行数分钟至十几分钟后，随机抛出以下异常并崩溃：

```
System.InvalidOperationException
  Message=Collection was modified; enumeration operation may not execute.
  Source=System.Private.CoreLib
  StackTrace:
    在 System.ThrowHelper.ThrowInvalidOperationException_EnumFailedVersion()
    在 System.Collections.Generic.List`1.Enumerator.MoveNext()
    在 System.Linq.Enumerable.DistinctIterator`1.MoveNext()
    在 ScottPlot.Rendering.RenderActions.RegenerateTicks.Render(RenderPack rp)
    在 ScottPlot.Rendering.RenderManager.RenderOnce(SKCanvas canvas, PixelRect rect)
    在 ScottPlot.Plot.Render(SKCanvas canvas, PixelRect rect)
    在 SkiaSharp.Views.WPF.SKElement.OnRender(DrawingContext drawingContext)
    在 System.Windows.UIElement.Arrange(Rect finalRect)
    ...
```

**关键线索**：
1. 异常发生在 `RegenerateTicks.Render` 中遍历 tick 集合时
2. 触发来源是 WPF 的 `Arrange` 布局回调，**不是**我们显式调用的 `Refresh()`
3. 崩溃位置不固定——有时在振动图，有时在算法卡片图

## 根因分析

ScottPlot 5 的 WPF 控件（`WpfPlot`）**不支持多线程并发访问**。问题的本质是：

```
后台线程:  Plot.Clear() / Plot.Add.Signal() / Plot.Axes.AutoScale()
                                                    ↕ 同一 Plot 对象
UI 线程:   WpfPlot.OnRender() → Plot.Render() → RegenerateTicks (遍历集合)
```

WPF 的布局系统（`Arrange` → `OnRender`）可以在**任何时刻**触发渲染回调，不受应用代码控制。当后台线程正在修改 Plot 的内部集合（plottables、axes、ticks）时，WPF 恰好触发了渲染，导致 `foreach` 遍历过程中集合被修改。

## 修复方案演进

### 方案一：lock 互斥锁（无效）

```csharp
// 后台线程
lock (plotLock) { Plot.Clear(); Plot.Add.Signal(data); }

// UI 线程
lock (plotLock) { WpfPlot.Refresh(); }
```

**失败原因**：`lock` 只能保护我们自己的代码。WPF 内部的 `Arrange` → `OnRender` 渲染管线**不获取我们的锁**，它直接调用 `Plot.Render()`，此时后台线程可能正在修改 Plot 状态。

### 方案二：Monitor.TryEnter 非阻塞锁（无效）

```csharp
// 后台线程：获取不到锁就跳过本帧
if (Monitor.TryEnter(plotLock)) { try { ... } finally { Monitor.Exit(); } }

// UI 线程：阻塞等待后台修改完毕
lock (plotLock) { WpfPlot.Refresh(); }
```

**失败原因**：同上。WPF 内部渲染不经过 `Refresh()` 方法，而是在布局回调中直接调用 `Plot.Render()`。即使我们的 `Refresh()` 加了锁，WPF 的自动渲染仍然可以绕过。

### 方案三：复用 Signal 对象，避免 Clear()（部分有效）

```csharp
// 不再 Clear() + Add()，而是替换数据源
sig.Data = new SignalSourceDouble(data, period);
```

**改善**：运行时间从几十秒延长到十几分钟。`Clear()` 是导致集合突变的最大元凶，移除后竞态窗口大幅缩小。

**仍然失败原因**：`Axes.AutoScale()` 等方法仍会修改 axis/tick 相关集合，WPF 渲染管线遍历时仍可能撞上。

### 方案四：ConcurrentQueue&lt;Action&gt; 队列模式（当前方案）

**核心思想**：**所有 ScottPlot 操作统一在 UI 线程执行**，后台线程只负责计算数据和入队。

```
后台线程 (ProcessLoopAsync)          UI 线程 (OnFrameRender)
┌─────────────────────┐              ┌─────────────────────┐
│ 1. 采集传感器数据     │              │ 1. 消费振动/电流队列  │
│ 2. 运行检测算法       │  ──队列──▶   │ 2. 更新振动/电流图     │
│ 3. 更新绑定属性       │  PlotActions │ 3. 出队 PlotActions   │
│ 4. PlotActions.Enqueue│              │ 4. 执行所有 Action    │
│    (() => 修改图表)   │              │ 5. Refresh() 所有脏图  │
└─────────────────────┘              └─────────────────────┘
     不接触 ScottPlot                   独占 ScottPlot 访问
```

#### ViewModel 侧

```csharp
// 字段
internal readonly ConcurrentQueue<Action> PlotActions = new();
internal volatile bool BoltPlotDirty;
// ...

// Update*Monitor 方法（后台线程调用）
var capturedData = tiltData;  // 捕获闭包变量
PlotActions.Enqueue(() =>
{
    // 这些代码将在 UI 线程执行
    if (_boltSig == null)
        _boltSig = BoltMonitorPlot.Add.Signal(capturedData);
    else
        _boltSig.Data = new SignalSourceDouble(capturedData, 1.0);
    // ... 更新阈值线、AutoScale 等
});
BoltPlotDirty = true;
```

#### View 侧

```csharp
private void OnFrameRender(object? sender, EventArgs e)
{
    // 1. 消费振动/电流/倾角队列（也在 UI 线程）
    // 2. 更新振动/电流图
    // 3. 出队并执行所有 PlotActions
    while (_vm?.PlotActions.TryDequeue(out var action) == true)
        action();

    // 4. 统一刷新所有脏标记的图表
    if (_vm?.BoltPlotDirty == true)
    {
        BoltMonitorPlot.Refresh();
        _vm.BoltPlotDirty = false;
    }
    // ...
}
```

## 关键设计决策

| 决策 | 说明 |
|---|---|
| `ConcurrentQueue<Action>` 而非锁 | 彻底消除后台线程对 ScottPlot 的访问，而非试图同步它 |
| `volatile bool *Dirty` 标记 | 标记哪些图表需要 `Refresh()`，避免无谓刷新 |
| 闭包捕获局部变量 | `capturedData` 确保 Action 在 UI 线程执行时使用正确的数据副本 |
| `try-catch` 兜底 | `OnFrameRender` 外层捕获异常，ScottPlot 内部异常不导致崩溃 |
| `SignalSourceDouble(data, period)` | 一次性设置 period 参数，避免后续 `Data.Period` 赋值触发额外状态变更 |

## 涉及文件

| 文件 | 变更内容 |
|---|---|
| `ViewModels/DeepDiagnosisViewModel.cs` | 移除锁对象，添加 `PlotActions` 队列和 `*Dirty` 标记，四个 `Update*Monitor` 改为入队 |
| `Views/Pages/DeepDiagnosisView.xaml.cs` | `OnFrameRender` 出队执行 Action + 统一 Refresh，移除所有 lock |

## 待观察事项

> **⚠️ 注意**：截至记录时，此方案已通过编译和 59 个单元测试。实际长时间运行的稳定性尚需进一步观察。

需要关注的点：
1. **长时间运行稳定性** — 是否仍会出现 `Collection was modified`（预期不会，因为 ScottPlot 仅在 UI 线程被访问）
2. **内存泄漏** — `ConcurrentQueue<Action>` 中的闭包是否会导致数据对象无法被 GC（预期不会，因为队列被及时消费）
3. **UI 帧率影响** — 所有 Plot Action 在 `CompositionTarget.Rendering` 回调中执行，如果 Action 数量多（4 个算法 × 每 3.3s 一次），是否影响 60fps 渲染（预期影响极小，Action 执行时间在微秒级）

## 参考信息

- ScottPlot 版本：5.1.58
- .NET 版本：8.0
- WPF 框架：.NET 8 Desktop
- 相关 Issue：ScottPlot GitHub 上有关于 WpfPlot 线程安全的讨论，官方建议"所有 Plot 操作在 UI 线程执行"
