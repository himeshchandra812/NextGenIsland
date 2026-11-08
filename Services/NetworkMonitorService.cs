using System.Net.NetworkInformation;

namespace NextGenIsland.Services;

public sealed record NetworkMetrics(
    bool IsConnected,
    string AdapterName,
    double DownloadSpeedBytesPerSec,
    double UploadSpeedBytesPerSec,
    string DownloadSpeedText,
    string UploadSpeedText,
    string StatusText
);

public sealed class NetworkMonitorService
{
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSampleTime = DateTime.UtcNow;
    private string? _selectedInterfaceName;

    public NetworkMonitorService(string? selectedInterfaceName = null)
    {
        _selectedInterfaceName = selectedInterfaceName;
    }

    public void SetSelectedInterface(string? name)
    {
        _selectedInterfaceName = name;
        _lastBytesReceived = 0;
        _lastBytesSent = 0;
    }

    public static List<string> GetAvailableInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.Name)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public NetworkMetrics Read()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.OperationalStatus == OperationalStatus.Up)
                .ToList();

            if (interfaces.Count == 0)
            {
                return new NetworkMetrics(false, "No Connection", 0, 0, "↓ 0 B/s", "↑ 0 B/s", "Disconnected");
            }

            NetworkInterface? target = null;
            if (!string.IsNullOrWhiteSpace(_selectedInterfaceName))
            {
                target = interfaces.FirstOrDefault(n => string.Equals(n.Name, _selectedInterfaceName, StringComparison.OrdinalIgnoreCase));
            }

            // Fallback: auto-select preferred active interface (Wi-Fi, Ethernet, or first up)
            target ??= interfaces.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                                     n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                       ?? interfaces.First();

            var stats = target.GetIPStatistics();
            long currentRecv = stats.BytesReceived;
            long currentSent = stats.BytesSent;
            var now = DateTime.UtcNow;
            double elapsed = (now - _lastSampleTime).TotalSeconds;

            double downSpeed = 0;
            double upSpeed = 0;

            if (_lastBytesReceived > 0 && elapsed > 0.1)
            {
                downSpeed = Math.Max(0, (currentRecv - _lastBytesReceived) / elapsed);
                upSpeed = Math.Max(0, (currentSent - _lastBytesSent) / elapsed);
            }

            _lastBytesReceived = currentRecv;
            _lastBytesSent = currentSent;
            _lastSampleTime = now;

            string downText = "↓ " + FormatSpeed(downSpeed);
            string upText = "↑ " + FormatSpeed(upSpeed);
            string status = $"{target.Name} • Connected";

            return new NetworkMetrics(true, target.Name, downSpeed, upSpeed, downText, upText, status);
        }
        catch
        {
            return new NetworkMetrics(false, "Network Offline", 0, 0, "↓ 0 B/s", "↑ 0 B/s", "Error");
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024 * 1024):0.1} GB/s";
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):0.1} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }
}
