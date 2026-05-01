using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BCrypt.Net;
using BearingFaultDiagnosis.Services.Interfaces;
using BearingFaultDiagnosis.Views.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BearingFaultDiagnosis.ViewModels
{
    public partial class LoginViewModel : ViewModelBase
    {
        private IUserService _userService;
        private readonly IServiceProvider _serviceProvider; // 用于解析主窗口
        private AppSession _appSession;
        [ObservableProperty]
        public string _userName = "admin";

        public LoginViewModel(IUserService userService, IServiceProvider serviceProvider, AppSession appSession)
        {
            _userService = userService;
            _serviceProvider = serviceProvider;
            _appSession = appSession;
        }
        [RelayCommand]
        private void Login(PasswordBox pwdBox)
        {
            try
            {
                string password = pwdBox.Password;
                var user = _userService.Login(UserName, password);
                if (user == null)
                {
                    // 登录失败，显示错误消息
                    System.Windows.MessageBox.Show("用户名或密码错误！");
                }
                else
                {
                    //设置全局用户信息
                    _appSession.CurrentUser = user;

                    // 打开主窗口
                    var mainWindow = _serviceProvider.GetService(typeof(MainWindow)) as MainWindow;
                    mainWindow.Show();
                    // 关闭登录窗口
                    System.Windows.Application.Current.Windows.OfType<LoginWindow>().FirstOrDefault()?.Close();
                }
            }
            catch (SaltParseException saltParseException)
            {
                MessageBox.Show("密码解析错误，请检查密码格式！");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"登录过程中发生错误: {ex.Message}");
            }
        }
    }
}