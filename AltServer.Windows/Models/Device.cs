using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AltServer.Windows.Models;

public enum ConnectionType
{
    Usb,
    WiFi
}

public class Device : INotifyPropertyChanged
{
    private string _udid = string.Empty;
    private string _name = string.Empty;
    private string _model = string.Empty;
    private string _productType = string.Empty;
    private string _osVersion = string.Empty;
    private string _serial = string.Empty;
    private bool _isPaired;
    private ConnectionType _connection = ConnectionType.Usb;

    public string Udid
    {
        get => _udid;
        set { if (_udid != value) { _udid = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    }

    public string Model
    {
        get => _model;
        set { if (_model != value) { _model = value; OnPropertyChanged(); } }
    }

    public string ProductType
    {
        get => _productType;
        set { if (_productType != value) { _productType = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
    }

    public string OsVersion
    {
        get => _osVersion;
        set { if (_osVersion != value) { _osVersion = value; OnPropertyChanged(); } }
    }

    public string Serial
    {
        get => _serial;
        set { if (_serial != value) { _serial = value; OnPropertyChanged(); } }
    }

    public bool IsPaired
    {
        get => _isPaired;
        set { if (_isPaired != value) { _isPaired = value; OnPropertyChanged(); } }
    }

    public ConnectionType Connection
    {
        get => _connection;
        set { if (_connection != value) { _connection = value; OnPropertyChanged(); } }
    }

    /// <summary>用户友好显示名称：若设备名为空，自动使用型号或类型+UDID前8位</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            var shortUdid = Udid.Length >= 8 ? Udid[..8] : Udid;
            if (!string.IsNullOrWhiteSpace(ProductType)) return $"{ProductType} ({shortUdid})";
            return $"iOS设备 ({shortUdid})";
        }
        set { } // WPF Run.Text 默认按 TwoWay 绑定，需提供 setter 避免 XamlParseException
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class InstalledApp
{
    public string BundleIdentifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Build { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public DateTime? ExpirationDate { get; set; }
}

public class ServerCertificate
{
    public string CommonName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string TeamIdentifier { get; set; } = string.Empty;
    public DateTime NotAfter { get; set; }
    public bool IsValid => DateTime.Now < NotAfter;
    public int DaysRemaining => IsValid ? (NotAfter - DateTime.Now).Days : 0;
}

public class Source
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastUpdated { get; set; }
    public List<SourceApp> Apps { get; set; } = new();
}

public class SourceApp
{
    public string Name { get; set; } = string.Empty;
    public string BundleIdentifier { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}