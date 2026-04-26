using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Models;
using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Services.Implements;
using BearingFaultDiagnosis.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Views.Pages;

namespace BearingFaultDiagnosis.ViewModels
{
    public partial class MainViewModel : ViewModelBase
    {

        [ObservableProperty]
        private ViewModelBase _currentViewModel;
        [ObservableProperty]
        private Menu _selectedNavItem;

       

        public ObservableCollection<Menu> NavigationItems { get; set; } = new();
        public MainViewModel(DashboardViewModel viewModel, AppSession appSession, IUserService userService)
        {
            _currentViewModel = viewModel;
            User user = appSession.CurrentUser;
            if (user != null)
            {
                var roleId = user.RoleId;
                List<Menu> menus = userService.GetMenusByRoleId(roleId);
                foreach (var item in menus)
                {
                    NavigationItems.Add(item);
                }
                SelectedNavItem = NavigationItems.First();
            }
        }
        /// <summary>
        /// CommunityToolkit.Mvvm 会在后台自动生成一个公共属性，自动触发这个方法
        /// </summary>
        /// <param name="value"></param>
        partial void OnSelectedNavItemChanged(Menu value)
        {
            if (value != null)
            {
                NavigateByRoute(value.Route);
            }
        }
        [RelayCommand]
        private void NavigateByRoute(string route)
        {
            // 这里可以根据传入的参数来决定导航到哪个 ViewModel
            // 例如，如果有一个字符串参数 "Dashboard"，就导航到 DashboardViewModel

            if (route == "Dashboard")
            {
                CurrentViewModel = App.AppHost!.Services.GetRequiredService<DashboardViewModel>();
            }
            else if (route == "DeepDiagnosis")
            {
                CurrentViewModel = App.AppHost!.Services.GetRequiredService<DeepDiagnosisViewModel>();
            }
        }
    }
}
