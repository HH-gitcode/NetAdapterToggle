using System.IO;

namespace NetAdapterToggle.Services;

public static class ShortcutService
{
    public static string CreateDesktopToggleShortcut(string adapterName)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前程序路径。");

        var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safeFileName = string.Concat(adapterName.Split(Path.GetInvalidFileNameChars()));
        var lnkPath = Path.Combine(desktopDir, $"切换 {safeFileName}.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法初始化 WScript.Shell COM 组件。");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = exePath;
        shortcut.Arguments = $"--toggle \"{adapterName}\"";
        shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
        shortcut.Description = $"一键启用或禁用网络适配器: {adapterName}";
        shortcut.IconLocation = @"C:\Windows\System32\ncpa.cpl,0";
        shortcut.Save();

        return lnkPath;
    }
}
