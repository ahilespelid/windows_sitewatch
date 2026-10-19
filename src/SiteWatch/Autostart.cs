using Microsoft.Win32;

namespace SiteWatch;

/// <summary>Запуск вместе с Windows (для текущего пользователя, без прав администратора).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SiteWatch";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value) &&
                       value.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {Program.TrayArg}");
        else if (key.GetValue(ValueName) != null)
            key.DeleteValue(ValueName, false);
    }
}
