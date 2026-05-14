using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BearingFaultDiagnosis.Entities;
using BearingFaultDiagnosis.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BearingFaultDiagnosis.ViewModels;

public partial class BearingInfoViewModel : ViewModelBase
{
    private readonly IBearingInfoService _bearingInfoService;

    // ==================== 列表属性 ====================

    /// <summary>轴承列表</summary>
    [ObservableProperty]
    private ObservableCollection<BearingInfo> _bearings = new();

    /// <summary>当前选中的轴承</summary>
    [ObservableProperty]
    private BearingInfo? _selectedBearing;

    // ==================== 表单绑定属性 ====================

    [ObservableProperty]
    private string _formManufacturer = string.Empty;

    [ObservableProperty]
    private string _formModel = string.Empty;

    [ObservableProperty]
    private int _formRollerCount;

    [ObservableProperty]
    private double _formRollerDiameter;

    [ObservableProperty]
    private double _formPitchDiameter;

    [ObservableProperty]
    private double _formContactAngle;

    [ObservableProperty]
    private double _formBpfoMultiplier;

    [ObservableProperty]
    private double _formBpfiMultiplier;

    [ObservableProperty]
    private double _formBsfMultiplier;

    [ObservableProperty]
    private double _formFtfMultiplier;

    // ==================== UI 状态 ====================

    /// <summary>是否为编辑模式（否则为新增模式）</summary>
    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>当前正在编辑的轴承 ID（编辑模式下有效）</summary>
    private long _editingId;

    /// <summary>加载中标志</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>状态消息</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>数据是否已加载</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    // ==================== 构造函数 ====================

    public BearingInfoViewModel(IBearingInfoService bearingInfoService)
    {
        _bearingInfoService = bearingInfoService;
        _ = LoadBearingsAsync();
    }

    // ==================== 命令 ====================

    /// <summary>加载轴承列表</summary>
    [RelayCommand]
    private async Task LoadBearingsAsync()
    {
        IsLoading = true;
        StatusMessage = "正在加载轴承数据...";
        HasLoaded = false;

        try
        {
            var list = await _bearingInfoService.GetAllAsync();
            Bearings = new ObservableCollection<BearingInfo>(list);
            StatusMessage = $"共加载 {list.Count} 条轴承记录";
            HasLoaded = true;
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>选中轴承后填充到表单</summary>
    partial void OnSelectedBearingChanged(BearingInfo? value)
    {
        if (value != null)
        {
            FillForm(value);
        }
    }

    /// <summary>新建轴承（清空表单，进入新增模式）</summary>
    [RelayCommand]
    private void NewBearing()
    {
        ClearForm();
        IsEditMode = false;
        _editingId = 0;
    }

    /// <summary>保存（新增或更新）</summary>
    [RelayCommand]
    private async Task SaveBearingAsync()
    {
        if (string.IsNullOrWhiteSpace(FormModel))
        {
            StatusMessage = "轴承型号不能为空";
            return;
        }
        if (FormRollerCount <= 0)
        {
            StatusMessage = "滚子数必须大于 0";
            return;
        }

        IsLoading = true;

        try
        {
            if (IsEditMode)
            {
                // 更新
                var bearing = new BearingInfo
                {
                    Id = _editingId,
                    Manufacturer = FormManufacturer,
                    Model = FormModel,
                    RollerCount_n = FormRollerCount,
                    RollerDiameter_d = FormRollerDiameter > 0 ? FormRollerDiameter : null,
                    PitchDiameter_D = FormPitchDiameter > 0 ? FormPitchDiameter : null,
                    ContactAngle_alpha = FormContactAngle,
                    BPFO_Multiplier = FormBpfoMultiplier > 0 ? FormBpfoMultiplier : null,
                    BPFI_Multiplier = FormBpfiMultiplier > 0 ? FormBpfiMultiplier : null,
                    BSF_Multiplier = FormBsfMultiplier > 0 ? FormBsfMultiplier : null,
                    FTF_Multiplier = FormFtfMultiplier > 0 ? FormFtfMultiplier : null,
                };

                var result = await _bearingInfoService.UpdateAsync(bearing);
                if (result != null)
                {
                    StatusMessage = $"轴承「{bearing.DisplayName}」更新成功";
                    await LoadBearingsAsync();
                    SelectedBearing = Bearings.FirstOrDefault(b => b.Id == _editingId);
                }
                else
                {
                    StatusMessage = "更新失败：未找到对应记录";
                }
            }
            else
            {
                // 新增
                var bearing = new BearingInfo
                {
                    Manufacturer = FormManufacturer,
                    Model = FormModel,
                    RollerCount_n = FormRollerCount,
                    RollerDiameter_d = FormRollerDiameter > 0 ? FormRollerDiameter : null,
                    PitchDiameter_D = FormPitchDiameter > 0 ? FormPitchDiameter : null,
                    ContactAngle_alpha = FormContactAngle,
                    BPFO_Multiplier = FormBpfoMultiplier > 0 ? FormBpfoMultiplier : null,
                    BPFI_Multiplier = FormBpfiMultiplier > 0 ? FormBpfiMultiplier : null,
                    BSF_Multiplier = FormBsfMultiplier > 0 ? FormBsfMultiplier : null,
                    FTF_Multiplier = FormFtfMultiplier > 0 ? FormFtfMultiplier : null,
                };

                var created = await _bearingInfoService.AddAsync(bearing);
                StatusMessage = $"轴承「{created.DisplayName}」添加成功";
                ClearForm();
                await LoadBearingsAsync();
                SelectedBearing = Bearings.FirstOrDefault(b => b.Id == created.Id);
            }
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除轴承</summary>
    [RelayCommand]
    private async Task DeleteBearingAsync()
    {
        if (SelectedBearing == null)
        {
            StatusMessage = "请先选择要删除的轴承";
            return;
        }

        IsLoading = true;

        try
        {
            var success = await _bearingInfoService.DeleteAsync(SelectedBearing.Id);
            if (success)
            {
                StatusMessage = $"轴承「{SelectedBearing.DisplayName}」已删除";
                ClearForm();
                await LoadBearingsAsync();
            }
            else
            {
                StatusMessage = "删除失败：未找到对应记录";
            }
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>取消编辑（清空表单）</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        ClearForm();
        IsEditMode = false;
        _editingId = 0;
        SelectedBearing = null;
    }

    /// <summary>编辑选中的轴承（将数据加载到表单）</summary>
    [RelayCommand]
    private void EditBearing()
    {
        if (SelectedBearing == null)
        {
            StatusMessage = "请先选择要编辑的轴承";
            return;
        }

        FillForm(SelectedBearing);
        IsEditMode = true;
        _editingId = SelectedBearing.Id;
    }

    // ==================== 私有辅助方法 ====================

    private void FillForm(BearingInfo bearing)
    {
        FormManufacturer = bearing.Manufacturer ?? string.Empty;
        FormModel = bearing.Model;
        FormRollerCount = bearing.RollerCount_n;
        FormRollerDiameter = bearing.RollerDiameter_d ?? 0;
        FormPitchDiameter = bearing.PitchDiameter_D ?? 0;
        FormContactAngle = bearing.ContactAngle_alpha;
        FormBpfoMultiplier = bearing.BPFO_Multiplier ?? 0;
        FormBpfiMultiplier = bearing.BPFI_Multiplier ?? 0;
        FormBsfMultiplier = bearing.BSF_Multiplier ?? 0;
        FormFtfMultiplier = bearing.FTF_Multiplier ?? 0;
    }

    private void ClearForm()
    {
        FormManufacturer = string.Empty;
        FormModel = string.Empty;
        FormRollerCount = 0;
        FormRollerDiameter = 0;
        FormPitchDiameter = 0;
        FormContactAngle = 0;
        FormBpfoMultiplier = 0;
        FormBpfiMultiplier = 0;
        FormBsfMultiplier = 0;
        FormFtfMultiplier = 0;
    }
}
