using System.Configuration;
using System.Data;
using System.Windows;
using BearingFaultDiagnosis.Core;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.ViewModels;
using BearingFaultDiagnosis.Views.Pages;
using BearingFaultDiagnosis.Views.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BearingFaultDiagnosis
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // 声明一个全局主机
        public static IHost AppHost { get; private set; }
        public App()
        {
            // 配置和构建主机
            AppHost = Host.CreateDefaultBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    // 1. 注册基础服务
                    //services.AddSingleton<IUserService, UserService>();
                    //services.AddSingleton<IDialogService, DialogService>();

                    // 注册数据库上下文
                    var connectionString = hostContext.Configuration["ConnectionStrings:DefaultConnection"]
                        ?? "Data Source=bearing_fault.db";

                    //在wpf这种长周期形态，没有web那种请求模式，如果使用AddDbContext，就变成全局单例了
                    //所以要改成AddDbContextFactory，需要的时候再去new一个dbcontext
                    services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString));

                    // 2. 注册 ViewModel
                    services.AddTransient<MainViewModel>();
                    services.AddTransient<DashboardViewModel>();
                    services.AddTransient<LoginViewModel>();
                    services.AddTransient<DeepDiagnosisViewModel>();

                    // 3. 注册 UI 窗体
                    services.AddTransient<MainWindow>();
                    services.AddTransient<LoginWindow>();
                    services.AddTransient<DashboardView>();
                    services.AddTransient<DeepDiagnosisView>();

                    //注册单例服务
                    services.AddSingleton<Services.Interfaces.ITCPServerService, Services.Implements.TCPServerService>();
                    services.AddTransient<Services.Interfaces.IDeepDiagnosisService, Services.Implements.DeepDiagnosisService>();
                    services.AddSingleton<AppSession>();
                    services.AddTransient<Services.Interfaces.IUserService, Services.Implements.UserService>();
                    services.AddSingleton<Services.Interfaces.ISensorDataService, Services.Implements.SensorDataService>();

                    // 将 JSON 中的 "ServerSettings" 部分绑定到 ServerSettings 类
                    services.Configure<ServerSettings>(hostContext.Configuration.GetSection("ServerSettings"));
                })
                .Build();
        }
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 启动主机（此时会自动加载配置、启动后台任务等）
            await AppHost.StartAsync();

            // 初始化数据库并写入默认数据
            using (var scope = AppHost.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await dbContext.Database.EnsureCreatedAsync();

                // 如果你用了 DbContextFactory，这里就获取 Factory 然后 CreateDbContext
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                string defaultPasswordHash = BCrypt.Net.BCrypt.HashPassword("123456");
                // 检查是不是连管理员账号都没了
                if (!context.Users.Any(u => u.UserName == "admin"))
                {
                    context.Users.Add(new User
                    {
                        UserName = "admin",
                        // 记得存之前写好的密码 Hash
                        PasswordHash = defaultPasswordHash,
                        DisplayName = "系统管理员",
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        RoleId = 1
                    });
                    context.SaveChanges();
                }

                // 从 DI 容器中获取 MainWindow！
                // 容器会自动实例化 MainWindow，并自动为它注入 MainViewModel 等依赖
                //var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
                //mainWindow.Show();
                var loginWindow = AppHost.Services.GetRequiredService<LoginWindow>();
                loginWindow.Show();
            }
        }
        protected override async void OnExit(ExitEventArgs e)
        {
            // 优雅关闭主机，释放所有注册的单例和服务
            await AppHost.StopAsync();
            AppHost.Dispose();
            base.OnExit(e);
        }
    }

}
