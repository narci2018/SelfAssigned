namespace AltServer.Windows.Models;

/// <summary>
/// 设备详细信息（用于“设备信息”主面板，100% 对标爱思助手“我的设备”全套真实参数）
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
    public string SalesRegion { get; set; } = "中国 (国行)";
    public string RegulatoryModelNumber { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string BasebandVersion { get; set; } = string.Empty;
    public string BluetoothAddress { get; set; } = string.Empty;
    public string WiFiAddress { get; set; } = string.Empty;
    public string EthernetAddress { get; set; } = string.Empty;
    public string InternationalMobileEquipmentIdentity { get; set; } = string.Empty; // IMEI1
    public string Imei2 { get; set; } = string.Empty;                               // IMEI2
    public string ProductionDate { get; set; } = string.Empty;                     // 生产日期
    public string FullModelName => !string.IsNullOrEmpty(ModelNumber) && !string.IsNullOrEmpty(RegionInfo)
        ? $"{ModelNumber} {RegionInfo}" : ModelNumber;

    // 电池真实指标 (来自 IORegistry AppleSmartBattery)
    public int BatteryCurrentCapacity { get; set; } = -1;
    public bool BatteryIsCharging { get; set; }
    public int BatteryCycleCount { get; set; } = -1;
    public int BatteryDesignCapacity { get; set; } = -1;
    public int BatteryFullAvailableCapacity { get; set; } = -1; // NominalChargeCapacity
    public int BatteryHealthPercent { get; set; } = -1;
    public string BatteryHealthText => BatteryHealthPercent > 0 ? $"{BatteryHealthPercent}%"
        : (BatteryDesignCapacity > 0 && BatteryFullAvailableCapacity > 0
            ? $"{Math.Min(100, (int)Math.Round((double)BatteryFullAvailableCapacity / BatteryDesignCapacity * 100))}%"
            : (BatteryCurrentCapacity >= 0 ? $"{BatteryCurrentCapacity}%" : "未知"));

    public string ChargingStatusText => BatteryIsCharging 
        ? $"正在充电 ⚡{BatteryCurrentCapacity}%" 
        : $"未充电 ({BatteryCurrentCapacity}%)";

    // 存储信息 (com.apple.disk_usage)
    public long TotalDiskCapacity { get; set; }
    public long TotalDataCapacity { get; set; }
    public long TotalDataAvailable { get; set; }
    public long AmountDataAvailable { get; set; }
    public long AmountDataReserved { get; set; }

    public string TotalDiskCapacityText => TotalDiskCapacity > 0 
        ? $"{Math.Round(TotalDiskCapacity / 1_000_000_000.0)} GB" 
        : FormatBytes(TotalDataCapacity);
    public string AvailableDiskText => AmountDataAvailable > 0
        ? $"{AmountDataAvailable / 1_000_000_000.0:F2} GB"
        : FormatBytes(TotalDataAvailable);
    public string UsedDiskText => TotalDiskCapacity > 0 && AmountDataAvailable > 0
        ? $"{(TotalDiskCapacity - AmountDataAvailable) / 1_000_000_000.0:F2} GB"
        : FormatBytes(Math.Max(0, (TotalDiskCapacity > 0 ? TotalDiskCapacity : TotalDataCapacity) - TotalDataAvailable));

    public double DiskUsagePercentage
    {
        get
        {
            if (TotalDiskCapacity > 0 && AmountDataAvailable > 0)
            {
                return Math.Clamp((double)(TotalDiskCapacity - AmountDataAvailable) / TotalDiskCapacity * 100, 0, 100);
            }
            var total = TotalDiskCapacity > 0 ? TotalDiskCapacity : TotalDataCapacity;
            if (total <= 0) return 0;
            var used = total - TotalDataAvailable;
            return Math.Clamp((double)used / total * 100, 0, 100);
        }
        set { }
    }

    // 激活与安全状态 (对标爱思验机)
    public string ActivationState { get; set; } = "已激活";
    public string JailbreakStatus { get; set; } = "未越狱";
    public string ActivationLockStatus { get; set; } = "已开启";
    public string ICloudStatus { get; set; } = "已开启";
    public string SimStatus { get; set; } = string.Empty;
    public string CarrierName { get; set; } = string.Empty;
    public string CrashLogCount { get; set; } = "0次";
    public string WarrantyStatus { get; set; } = "保修期内 / 在线查询";

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        double giga = bytes / (1024.0 * 1024 * 1024);
        return $"{giga:F2} GB";
    }
}
