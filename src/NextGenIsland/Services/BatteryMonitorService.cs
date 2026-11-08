using System.Runtime.InteropServices;

namespace NextGenIsland.Services;

public sealed record BatteryMetrics(
    bool HasBattery,
    int Percent,
    bool IsPlugged,
    bool IsCharging,
    string StatusText,
    bool IsLow
)
{
    public string Glyph => !HasBattery ? "⚡" : (IsPlugged ? "⚡" : (Percent > 20 ? "🔋" : "🪫"));
}

public sealed class BatteryMonitorService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public BatteryMetrics Read()
    {
        if (GetSystemPowerStatus(out var s))
        {
            bool hasBattery = s.BatteryFlag != 128 && s.BatteryLifePercent <= 100;
            if (!hasBattery)
            {
                return new BatteryMetrics(false, 100, s.ACLineStatus == 1, false, "Desktop / AC", false);
            }

            int percent = s.BatteryLifePercent;
            bool isPlugged = s.ACLineStatus == 1;
            bool isCharging = (s.BatteryFlag & 8) != 0;
            bool isLow = percent <= 20 && !isPlugged;

            string statusText;
            if (isCharging)
                statusText = $"Charging ({percent}%)";
            else if (isPlugged)
                statusText = $"Plugged In ({percent}%)";
            else
                statusText = $"On Battery ({percent}%)";

            return new BatteryMetrics(true, percent, isPlugged, isCharging, statusText, isLow);
        }

        return new BatteryMetrics(false, 0, true, false, "Power Unavailable", false);
    }
}
