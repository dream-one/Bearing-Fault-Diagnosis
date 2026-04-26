using CommunityToolkit.Mvvm.ComponentModel;
using BearingFaultDiagnosis.Entities; // 换成你 User 实体所在的命名空间

public partial class AppSession : ObservableObject
{
    // 使用 ObservableProperty，这样后续如果你做“注销切换账号”功能，界面绑定的名字会自动变
    [ObservableProperty]
    private User? _currentUser;

    // 方便其他地方判断是否已登录
    public bool IsLoggedIn => CurrentUser != null;

    // 方便直接获取当前角色 ID
    public int? CurrentRoleId => (int?)(CurrentUser?.RoleId);
}