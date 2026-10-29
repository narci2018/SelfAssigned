using System.Windows;
using AltServer.Windows.Models;
using AltServer.Windows.Services;

namespace AltServer.Windows.Views;

/// <summary>
/// 设备详细信息查看窗口（对标爱思助手“我的设备”设备信息面板）
/// </summary>
public partial class DeviceDetailWindow : Window
{
    private readonly Device _device;
    private readonly DeviceService _deviceService;

    public DeviceDetailWindow(Device device, DeviceService deviceService)
    {
        InitializeComponent();
        _device = device;
        _deviceService = deviceService;
        Loaded += async (_, _) => await LoadInfoAsync();
    }

    private async Task LoadInfoAsync()
    {
        try
        {
            var info = await Task.Run(() => _deviceService.GetDeviceDetailInfo(_device.Udid));
            RenderInfo(info);
        }
        catch (Exception ex)
        {
            LogService.Warning($"[设备信息] 加载失败: {ex.Message}");
        }
    }

    private void RenderInfo(DeviceDetailInfo info)
    {
        DeviceModelText.Text = !string.IsNullOrEmpty(info.ModelName) ? info.ModelName : info.ProductType;
        DeviceSubtitleText.Text = $"{info.DeviceName} · iOS {info.ProductVersion} ({info.BuildVersion})";
        ActivationText.Text = !string.IsNullOrEmpty(info.ActivationState) ? info.ActivationState : "已激活";

        // 存储
        DiskProgressBar.Value = info.DiskUsagePercentage;
        DiskUsedText.Text = $"已用: {info.UsedDiskText} ({info.DiskUsagePercentage:F0}%)";
        DiskTotalText.Text = $"共 {info.TotalDiskCapacityText} (可用 {info.AvailableDiskText})";

        // 电池
        BatteryCapacityText.Text = info.BatteryCurrentCapacity >= 0
            ? $"{info.BatteryCurrentCapacity}% {(info.BatteryIsCharging ? "⚡ 正在充电" : "")}"
            : "正常";

        var cycles = info.BatteryCycleCount >= 0 ? $"{info.BatteryCycleCount} 次" : "已检测";
        BatteryHealthText.Text = $"{cycles} (效率 {info.BatteryHealthText})";

        // 硬件参数
        ModelNumberText.Text = !string.IsNullOrEmpty(info.FullModelName) ? info.FullModelName : info.ModelNumber;
        ProductTypeText.Text = !string.IsNullOrEmpty(info.ProductType) ? info.ProductType : "--";
        SerialNumberText.Text = !string.IsNullOrEmpty(info.SerialNumber) ? info.SerialNumber : "--";
        ProductionDateText.Text = !string.IsNullOrEmpty(info.ProductionDate) ? info.ProductionDate : "--";
        HardwareModelText.Text = !string.IsNullOrEmpty(info.HardwareModel) ? info.HardwareModel : "--";
        CpuText.Text = !string.IsNullOrEmpty(info.CpuArchitecture) ? info.CpuArchitecture : "--";
        RegionText.Text = !string.IsNullOrEmpty(info.SalesRegion) ? info.SalesRegion : (info.RegionInfo ?? "国行 / 通用");
        BuildVersionText.Text = !string.IsNullOrEmpty(info.BuildVersion) ? info.BuildVersion : "--";
        RegulatoryModelText.Text = !string.IsNullOrEmpty(info.RegulatoryModelNumber) ? info.RegulatoryModelNumber : "--";
        ActivationLockText.Text = !string.IsNullOrEmpty(info.ActivationLockStatus) ? info.ActivationLockStatus : "未开启";
        ICloudText.Text = !string.IsNullOrEmpty(info.ICloudStatus) ? info.ICloudStatus : "未开启";

        // 网络参数
        WifiMacText.Text = !string.IsNullOrEmpty(info.WiFiAddress) ? info.WiFiAddress : "--";
        BluetoothText.Text = !string.IsNullOrEmpty(info.BluetoothAddress) ? info.BluetoothAddress : "--";
        BasebandText.Text = !string.IsNullOrEmpty(info.BasebandVersion) ? info.BasebandVersion : "--";
        CarrierText.Text = !string.IsNullOrEmpty(info.CarrierName) ? info.CarrierName : "--";
        ImeiText.Text = !string.IsNullOrEmpty(info.InternationalMobileEquipmentIdentity) ? info.InternationalMobileEquipmentIdentity : "--";
        Imei2Text.Text = !string.IsNullOrEmpty(info.Imei2) ? info.Imei2 : "--";
        UdidBox.Text = info.Udid;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadInfoAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
