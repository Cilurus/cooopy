using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Cooopy.Services
{
    public static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "cooopy";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                // CreateSubKey：Run 键不存在（极少数精简系统）时创建，避免静默失败。
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (key == null) return;
                if (enabled)
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    var command = $"\"{exe}\" --silent";
                    if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                    {
                        var assembly = Assembly.GetEntryAssembly()?.Location;
                        if (!string.IsNullOrEmpty(assembly)) command = $"\"{exe}\" \"{assembly}\" --silent";
                    }

                    key.SetValue(ValueName, command);
                    App.Log("autostart enabled: " + command);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                    App.Log("autostart disabled");
                }
            }
            catch (Exception ex)
            {
                App.Log("autostart update failed: " + ex);
            }
        }
    }
}
