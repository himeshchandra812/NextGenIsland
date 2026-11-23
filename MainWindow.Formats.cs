using System.Globalization;

namespace NextGenIsland;

// Clock and temperature formats chosen in Settings.
public partial class MainWindow
{
    private string ClockText(DateTime t, bool seconds = false)
    {
        string f = _settings.Use24HourClock
            ? (seconds ? "HH:mm:ss" : "HH:mm")
            : (seconds ? "h:mm:ss tt" : "h:mm tt");
        return t.ToString(f, CultureInfo.InvariantCulture);
    }

    // Input is always Celsius.
    private string FormatTemp(double celsius)
    {
        double v = _settings.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "\u00B0";
    }
}
