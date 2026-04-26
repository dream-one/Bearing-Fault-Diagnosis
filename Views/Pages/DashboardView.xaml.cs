using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BearingFaultDiagnosis.ViewModels;
using ScottPlot.Plottables;

namespace BearingFaultDiagnosis.Views.Pages
{
    /// <summary>
    /// DashboardView.xaml 的交互逻辑
    /// </summary>
    public partial class DashboardView : UserControl
    {
     
        private DashboardViewModel _vm;
        public DashboardView()
        {
            InitializeComponent();

            // 订阅界面加载完成事件
            this.Loaded += DashboardView_Loaded;
            this.Unloaded += DashboardView_Unloaded;
        }

        private void DashboardView_Unloaded(object sender, RoutedEventArgs e)
        {
            // 取消后台任务
            if (_renderCts != null)
            {
                _renderCts.Cancel();
                _renderCts.Dispose();
                _renderCts = null;
            }

            System.Windows.Media.CompositionTarget.Rendering -= OnFrameRender;

            // 移除列表变化回调，防止内存泄漏
            if (dataListLog.Items is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged -= MainWindow_CollectionChanged;
            }
        }
        private CancellationTokenSource _renderCts;
        private void DashboardView_Loaded(object sender, RoutedEventArgs e)
        {
            _vm = DataContext as DashboardViewModel;
            if (_vm == null) return;

            // 订阅列表变化以实现滚动
            if (dataListLog.Items is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged += MainWindow_CollectionChanged;
            }

            // 绑定图表 (假设你在 ViewModel 里暴漏了 VibrationPlot)
            WpfPlot1.Reset(_vm.VibrationPlot);

            // 开启 WPF 硬件级帧渲染回调 (与显示器 60Hz 刷新率完美同步)
            CompositionTarget.Rendering += OnFrameRender;
            _renderCts = new CancellationTokenSource();
            Task.Run(() => RenderLoopAsync(_renderCts.Token), _renderCts.Token);
        }
        private async Task RenderLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 尝试将缓冲区数据刷新到图表
                if (_vm != null && _vm.TryConsumeBufferForFrame())
                {
                    // 通知 UI 刷新图表（ScottPlot 的 Refresh 必须在 UI 线程调用）
                    WpfPlot1.Refresh();
                }
                else
                {
                    // 没有数据时短暂休眠，避免空转 CPU
                    await Task.Delay(20, token);
                }
            }
        }
        private void OnFrameRender(object sender, EventArgs e)
        {
            // 每次屏幕刷新时触发，丝般顺滑
            if (_vm != null && _vm.TryConsumeBufferForFrame())
            {
                WpfPlot1.Refresh();
            }
        }
        private void MainWindow_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && dataListLog.Items.Count > 0)
            {
                var lastItem = dataListLog.Items[dataListLog.Items.Count - 1];
                dataListLog.ScrollIntoView(lastItem);
            }

        }

    }
}
