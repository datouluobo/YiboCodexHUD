using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace YiboCodexHUD.Setup;

internal static class Program
{
    private const string AppName = "YiboCodexHUD";
    private const string AppExeName = "YiboCodexHUD.Desktop.exe";
    private const string AppIconFileName = "YiboCodexHUD.ico";
    private const string UninstallerExeName = "YiboCodexHUD.Uninstall.exe";
    private const string Publisher = "YiboSoft";
    private const string ProductUrl = "";
    private const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YiboCodexHUD";

    [STAThread]
    [SupportedOSPlatform("windows")]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Any(static arg => string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            return RunUninstall();
        }

        try
        {
            var installDir = GetInstallDirectory();
            var startMenuDir = GetStartMenuDirectory();
            var desktopShortcutPath = GetDesktopShortcutPath();
            var exePath = Path.Combine(installDir, AppExeName);

            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(startMenuDir);
            StopRunningInstalledApp(exePath);

            var tempZipPath = Path.Combine(Path.GetTempPath(), $"{AppName}-payload-{Guid.NewGuid():N}.zip");
            using (var resourceStream = GetPayloadStream())
            using (var fileStream = File.Create(tempZipPath))
            {
                resourceStream.CopyTo(fileStream);
            }

            ZipFile.ExtractToDirectory(tempZipPath, installDir, overwriteFiles: true);
            File.Delete(tempZipPath);

            var iconPath = Path.Combine(installDir, AppIconFileName);
            var currentExecutablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to resolve installer executable path.");
            var uninstallerPath = Path.Combine(installDir, UninstallerExeName);

            File.Copy(currentExecutablePath, uninstallerPath, overwrite: true);
            CreateShortcut(desktopShortcutPath, exePath, installDir, iconPath);
            CreateShortcut(Path.Combine(startMenuDir, $"{AppName}.lnk"), exePath, installDir, iconPath);
            WriteUninstallEntry(installDir, exePath, uninstallerPath, iconPath);

            var launchNow = MessageBox.Show(
                $"{AppName} 已安装完成。是否立即启动？",
                $"{AppName} 安装完成",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (launchNow == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"安装失败：{ex.Message}",
                $"{AppName} 安装失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int RunUninstall()
    {
        try
        {
            var installDir = GetInstallDirectory();
            var exePath = Path.Combine(installDir, AppExeName);
            var startMenuDir = GetStartMenuDirectory();
            var desktopShortcutPath = GetDesktopShortcutPath();

            var result = MessageBox.Show(
                $"确定卸载 {AppName} 吗？",
                $"{AppName} 卸载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return 0;
            }

            StopRunningInstalledApp(exePath);

            DeleteIfExists(desktopShortcutPath);
            DeleteIfExists(Path.Combine(startMenuDir, $"{AppName}.lnk"));
            DeleteDirectoryIfExists(startMenuDir);
            DeleteUninstallEntry();

            ScheduleInstallDirectoryRemoval(installDir);

            MessageBox.Show(
                $"{AppName} 已卸载。安装目录将在卸载程序退出后清理。",
                $"{AppName} 已卸载",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"卸载失败：{ex.Message}",
                $"{AppName} 卸载失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static Stream GetPayloadStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("payload.zip");
        if (stream is null)
        {
            throw new InvalidOperationException("Installer payload resource was not found.");
        }

        return stream;
    }

    private static string GetInstallDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppName);

    private static string GetStartMenuDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppName);

    private static string GetDesktopShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk");

    [SupportedOSPlatform("windows")]
    private static void WriteUninstallEntry(string installDir, string exePath, string uninstallerPath, string iconPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Failed to create uninstall registry key.");
        }

        var version = FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "1.0.6";

        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", File.Exists(iconPath) ? iconPath : exePath);
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall");
        if (!string.IsNullOrWhiteSpace(ProductUrl))
        {
            key.SetValue("URLInfoAbout", ProductUrl);
        }
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", GetEstimatedSizeKb(installDir), RegistryValueKind.DWord);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteUninstallEntry()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKeyPath, throwOnMissingSubKey: false);
    }

    private static int GetEstimatedSizeKb(string installDir)
    {
        if (!Directory.Exists(installDir))
        {
            return 0;
        }

        var totalBytes = Directory
            .EnumerateFiles(installDir, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Sum();

        return (int)Math.Clamp((totalBytes + 1023) / 1024, 0, int.MaxValue);
    }

    private static void StopRunningInstalledApp(string exePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void ScheduleInstallDirectoryRemoval(string installDir)
    {
        if (!Directory.Exists(installDir))
        {
            return;
        }

        var currentExecutablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve uninstaller executable path.");
        var cleanupScriptPath = Path.Combine(Path.GetTempPath(), $"cleanup-{AppName}-{Guid.NewGuid():N}.cmd");
        var script = string.Join(
            Environment.NewLine,
            "@echo off",
            "setlocal",
            "ping 127.0.0.1 -n 3 >nul",
            $"del /f /q \"{currentExecutablePath}\" >nul 2>nul",
            $"rmdir /s /q \"{installDir}\" >nul 2>nul",
            $"del /f /q \"%~f0\" >nul 2>nul");

        File.WriteAllText(cleanupScriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{cleanupScriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string iconPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM object is unavailable.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Failed to create WScript.Shell COM object.");
        try
        {
            var shortcutObject = shell.GetType().InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath })
                ?? throw new InvalidOperationException("Failed to create shortcut object.");
            dynamic shortcut = shortcutObject;

            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = File.Exists(iconPath) ? iconPath : targetPath;
            shortcut.Save();
        }
        finally
        {
            if (shell is not null)
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
