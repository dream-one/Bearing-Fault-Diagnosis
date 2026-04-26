using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
using BearingFaultDiagnosis.ViewModels;

namespace BearingFaultDiagnosis.Views.Pages
{
    /// <summary>
    /// DeepDiagnosisView.xaml 的交互逻辑
    /// </summary>
    public partial class DeepDiagnosisView : UserControl
    {
        private DeepDiagnosisViewModel? _viewModel;

        public DeepDiagnosisView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.OnRawDataReady -= UpdateRawPlot;
                _viewModel.OnPureDataReady -= UpdatePurePlot;
            }

            _viewModel = e.NewValue as DeepDiagnosisViewModel;

            if (_viewModel != null)
            {
                _viewModel.OnRawDataReady += UpdateRawPlot;
                _viewModel.OnPureDataReady += UpdatePurePlot;
            }
        }

        private void UpdateRawPlot(double[] data)
        {
            Dispatcher.InvokeAsync(() =>
            {
                RawPlot.Plot.Clear();
                RawPlot.Plot.Add.Signal(data);
                RawPlot.Plot.Axes.AutoScale();
                RawPlot.Refresh();
            });
        }

        private void UpdatePurePlot(double[] data)
        {
            Dispatcher.InvokeAsync(() =>
            {
                PurePlot.Plot.Clear();
                PurePlot.Plot.Add.Signal(data);
                PurePlot.Plot.Axes.AutoScale();
                PurePlot.Refresh();
            });
        }
    }
}
