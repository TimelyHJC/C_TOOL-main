using System.Diagnostics;
using Microsoft.Win32;

namespace C_toolsSetup;

/// <summary>安装完成后按所选 R*/ACAD-* 启动 AutoCAD：从注册表/App Paths 解析 acad.exe。</summary>
internal static class AcadLauncher
{
    internal static bool TryLaunch(string? acadVersionKey, string? acadProductKey, out string? errorMessage)
    {
        var exe = FindAcadExe(acadVersionKey, acadProductKey);
        if (string.IsNullOrEmpty(exe))
        {
            errorMessage = "未找到 AutoCAD（acad.exe）。请确认已安装，或从开始菜单手动启动。";
            return false;
        }

        return TryLaunchPath(exe, out errorMessage);
    }

    internal static bool TryGetSpecificInstallDirectory(
        string? acadVersionKey,
        string? acadProductKey,
        out string? installDirectory)
    {
        installDirectory = null;
        if (string.IsNullOrWhiteSpace(acadVersionKey) || string.IsNullOrWhiteSpace(acadProductKey))
            return false;

        var exe = FindSpecificAcadExe(acadVersionKey, acadProductKey);
        if (string.IsNullOrWhiteSpace(exe))
            return false;

        installDirectory = Path.GetDirectoryName(exe);
        return !string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory);
    }

    private static bool TryLaunchPath(string exePath, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            errorMessage = "无效的 acad.exe 路径。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string? FindAcadExe(string? version, string? product)
    {
        if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(product))
        {
            var specific = FindSpecificAcadExe(version, product);
            if (!string.IsNullOrEmpty(specific))
                return specific;
        }

        var appPaths = ReadAppPathsDefault(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\acad.exe");
        if (!string.IsNullOrEmpty(appPaths) && File.Exists(appPaths))
            return appPaths;

        return ScanFirstAcad(RegistryHive.LocalMachine, RegistryView.Registry64)
            ?? ScanFirstAcad(RegistryHive.LocalMachine, RegistryView.Registry32)
            ?? ScanFirstAcad(RegistryHive.CurrentUser, RegistryView.Registry64)
            ?? ScanFirstAcad(RegistryHive.CurrentUser, RegistryView.Registry32);
    }

    private static string? FindSpecificAcadExe(string version, string product) =>
        FindUnderVersionProduct(RegistryHive.LocalMachine, RegistryView.Registry64, version, product)
        ?? FindUnderVersionProduct(RegistryHive.LocalMachine, RegistryView.Registry32, version, product)
        ?? FindUnderVersionProduct(RegistryHive.CurrentUser, RegistryView.Registry64, version, product)
        ?? FindUnderVersionProduct(RegistryHive.CurrentUser, RegistryView.Registry32, version, product);

    private static string? ReadAppPathsDefault(string relativePath)
    {
        try
        {
            using var k64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(relativePath);
            var p = k64?.GetValue("") as string;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p.Trim().Trim('"')))
                return Path.GetFullPath(p.Trim().Trim('"'));
        }
        catch
        {
            // 忽略
        }

        try
        {
            using var k32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(relativePath);
            var p = k32?.GetValue("") as string;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p.Trim().Trim('"')))
                return Path.GetFullPath(p.Trim().Trim('"'));
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static string? FindUnderVersionProduct(RegistryHive hive, RegistryView view, string version, string product)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var prod = baseKey.OpenSubKey($@"SOFTWARE\Autodesk\AutoCAD\{version}\{product}");
            return TryAcadExeFromProductKey(prod);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryAcadExeFromProductKey(RegistryKey? prod)
    {
        if (prod == null)
            return null;

        foreach (var valueName in new[] { "AcadLocation", "InstallationPath", "InstallLocation", "AcadInstallDir" })
        {
            var dir = prod.GetValue(valueName) as string;
            var found = TryDirOrExe(dir);
            if (!string.IsNullOrEmpty(found))
                return found;
        }

        try
        {
            using var inst = prod.OpenSubKey("Installer");
            if (inst != null)
            {
                foreach (var valueName in new[] { "AcadInstallDir", "InstallLocation", "InstallationPath" })
                {
                    var dir = inst.GetValue(valueName) as string;
                    var found = TryDirOrExe(dir);
                    if (!string.IsNullOrEmpty(found))
                        return found;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static string? TryDirOrExe(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        var d = dir.Trim().Trim('"');
        if (d.EndsWith("acad.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(d))
            return Path.GetFullPath(d);
        var acad = Path.Combine(d, "acad.exe");
        return File.Exists(acad) ? Path.GetFullPath(acad) : null;
    }

    private static string? ScanFirstAcad(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var cad = baseKey.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD");
            if (cad == null)
                return null;

            foreach (var versionName in cad.GetSubKeyNames())
            {
                if (versionName.Length < 2 || versionName[0] != 'R')
                    continue;
                using var ver = cad.OpenSubKey(versionName);
                if (ver == null)
                    continue;
                foreach (var productName in ver.GetSubKeyNames())
                {
                    if (!productName.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var exe = TryAcadExeFromProductKey(ver.OpenSubKey(productName));
                    if (!string.IsNullOrEmpty(exe))
                        return exe;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }
}
