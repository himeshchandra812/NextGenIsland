using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NextGenIsland.Services;

// Switches Windows between dark and light mode (apps and system), the same setting as Settings > Personalization > Colors.
public static class SystemTheme
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    public static bool IsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
        }
        catch { return false; }
    }

    public static bool SetDark(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return false;
            int v = dark ? 0 : 1;
            k.SetValue("AppsUseLightTheme", v, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", v, RegistryValueKind.DWord);
            // Tell running apps and the taskbar that the colour setting changed.
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 0x0002, 200, out _);
            return true;
        }
        catch { return false; }
    }
}
