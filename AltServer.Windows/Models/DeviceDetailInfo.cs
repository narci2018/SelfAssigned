namespace AltServer.Windows.Models;

/// <summary>
/// 设备详细信息（用于“设备信息”主面板，对标爱思助手验机与软硬件状态）
/// </summary>
public class DeviceDetailInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Udid { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    public string CpuArchitecture { get; set; } = string.Empty;
    public string HardwareModel { get; set; } = string.Empty;
    public string RegionInfo { get; set; } = string.Empty;
    public string RegulatoryModelNumber { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string BasebandVersion { get; set; } = string.Empty;
    public string BluetoothAddress { get; set; } = string.Empty;
    public string WiFiAddress { get; set; } = string.Empty;
    public string EthernetAddress { get; set; } = string.Empty;
    public string InternationalMobileEquipmentIdentity { get; set; } = string.Empty; // IMEI

    // 电池信息 (com.apple.mobile.battery)
    public int BatteryCurrentCapacity { get; set; } = -1;
    public bool BatteryIsCharging { get; set; }
    public int BatteryCycleCount { get; set; } = -1;
    public int BatteryDesignCapacity { get; set; } = -1;
    public int BatteryFullAvailableCapacity { get; set; } = -1;
    public string BatteryHealthText => BatteryDesignCapacity > 0 && BatteryFullAvailableCapacity > 0
        ? $"{Math.Min(100, (int)Math.Round((double)BatteryFullAvailableCapacity / BatteryDesignCapacity * 100))}%"
        : (BatteryCurrentCapacity >= 0 ? $"{BatteryCurrentCapacity}%" : "未知");

    // 存储信息 (com.apple.disk_usage)
    public long TotalDiskCapacity { get; set; }
    public long TotalDataCapacity { get; set; }
    public long TotalDataAvailable { get; set; }
    public long AmountDataReserved { get; set; }

    public string TotalDiskCapacityText => FormatBytes(TotalDiskCapacity > 0 ? TotalDiskCapacity : TotalDataCapacity);
    public string AvailableDiskText => FormatBytes(TotalDataAvailable);
    public string UsedDiskText => FormatBytes(Math.Max(0, (TotalDiskCapacity > 0 ? TotalDiskCapacity : TotalDataCapacity) - TotalDataAvailable));
    public double DiskUsagePercentage
    {
        get
        {
            var total = TotalDiskCapacity > 0 ? TotalDiskCapacity : TotalDataCapacity;
            if (total <= 0) return 0;
            var used = total - TotalDataAvailable;
            return Math.Clamp((double)used / total * 100, 0, 100);
        }
    }

    // 激活与网络
    public string ActivationState { get; set; } = "已激活";
    public string SimStatus { get; set; } = string.Empty;
    public string CarrierName { get; set; } = string.Empty;

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        double giga = bytes / (1024.0 * 1024 * 1024);
        return $"{giga:F1} GB";
    }
}
