using System.IO;
using Microsoft.Win32;

namespace ChestCounter.Gui;

// "Start with Windows": a per-user entry under HKCU\...\Run, so no admin rights are needed. The
// entry passes --minimized, which makes the window start hidden in the tray.
public static class StartupRegistration
{
    public const string MinimizedArg = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ChestTracker";

    public static bool IsEnabled() => ReadValue() != null;

    // Turns the Run entry on or off. Returns false if Windows refused or the exe could not be found.
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return false;
            if (enabled)
            {
                var exe = CurrentExe();
                if (exe == null) return false;
                key.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }

    // If the entry points at an exe that has since been moved or deleted, point it at this copy.
    public static void RepairIfStale()
    {
        var value = ReadValue()?.Trim();
        if (string.IsNullOrEmpty(value) || !value.StartsWith('"')) return;   // not an entry we wrote
        int end = value.IndexOf('"', 1);
        if (end < 2) return;
        if (!File.Exists(value.Substring(1, end - 1))) Set(true);
    }

    private static string? ReadValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    // The exe to launch. Under "dotnet run" the process is dotnet.exe, so use the app's own exe.
    private static string? CurrentExe()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)
            && !string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            return path;

        var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(dll)) return null;
        var exe = Path.ChangeExtension(dll, ".exe");
        return File.Exists(exe) ? exe : null;
    }
}
