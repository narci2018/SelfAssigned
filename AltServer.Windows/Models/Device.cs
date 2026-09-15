namespace AltServer.Windows.Models;

public enum ConnectionType
{
    Usb,
    WiFi
}

public class Device
{
    public string Udid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ProductType { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public bool IsPaired { get; set; }
    public ConnectionType Connection { get; set; } = ConnectionType.Usb;
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