using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>通过 COM 将 REGEDIT4 格式的 <c>.arg</c> 导入并设为当前配置。</summary>
internal static class CadProfileArgImporter
{
    /// <exception cref="ArgumentException">路径或配置名为空。</exception>
    /// <exception cref="FileNotFoundException">找不到文件。</exception>
    /// <exception cref="InvalidOperationException">COM 导入或切换失败。</exception>
    internal static void ImportAndSetCurrent(string argPath, string profileName)
    {
        if (string.IsNullOrWhiteSpace(argPath))
            throw new ArgumentException("argPath 为空。", nameof(argPath));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("profileName 为空。", nameof(profileName));
        if (!File.Exists(argPath))
            throw new FileNotFoundException("找不到 .arg 文件。", argPath);

        string? tempCopy = null;
        try
        {
            tempCopy = TryCopyToTempArg(argPath);
            var pathsToTry = tempCopy != null
                ? new[] { tempCopy, argPath }
                : new[] { argPath };

            COMException? lastCom = null;
            foreach (var path in pathsToTry)
            {
                foreach (var includePathInfo in new[] { true, false })
                {
                    try
                    {
                        RunImport(path, profileName, includePathInfo);
                        return;
                    }
                    catch (COMException ex)
                    {
                        lastCom = ex;
                    }
                }
            }

            var detail = lastCom != null
                ? lastCom.Message
                : "未知 COM 错误";
            throw new InvalidOperationException("导入或切换 AutoCAD 配置失败：" + detail, lastCom);
        }
        finally
        {
            if (tempCopy != null)
            {
                try
                {
                    if (File.Exists(tempCopy))
                        File.Delete(tempCopy);
                }
                catch (IOException)
                {
                    // 临时文件删不掉时忽略
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上
                }
            }
        }
    }

    /// <summary>复制到仅含 ASCII 文件名的临时路径，减轻中文路径或特殊字符下 COM 注册表导入异常。</summary>
    private static string? TryCopyToTempArg(string sourcePath)
    {
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), "C_tools_arg_" + Guid.NewGuid().ToString("N") + ".arg");
            File.Copy(sourcePath, dest, overwrite: true);
            return dest;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RunImport(string registryFile, string profileName, bool includePathInfo)
    {
        dynamic acad = AcAp.AcadApplication;
        dynamic profiles = acad.Preferences.Profiles;
        string currentProfileName = GetActiveProfileName(profiles);
        string? tempProfileName = null;

        if (string.Equals(currentProfileName, profileName, System.StringComparison.OrdinalIgnoreCase))
        {
            tempProfileName = CreateTemporaryActiveProfile(profiles, currentProfileName);
        }

        try
        {
            profiles.ImportProfile(profileName, registryFile, includePathInfo);
            profiles.ActiveProfile = profileName;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(tempProfileName))
            {
                TryRestoreActiveProfile(profiles, currentProfileName);
            }

            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempProfileName))
            {
                TryDeleteTemporaryProfile(profiles, tempProfileName);
            }
        }
    }

    private static string GetActiveProfileName(dynamic profiles)
    {
        try
        {
            return ((string?)profiles.ActiveProfile ?? "").Trim();
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("无法读取 AutoCAD 当前配置。", ex);
        }
    }

    private static string CreateTemporaryActiveProfile(dynamic profiles, string currentProfileName)
    {
        if (string.IsNullOrWhiteSpace(currentProfileName))
            throw new InvalidOperationException("当前 AutoCAD 配置名为空，无法临时切换。");

        var tempProfileName = currentProfileName + "__C_TOOL_TMP__" + System.Guid.NewGuid().ToString("N")[..8];
        try
        {
            profiles.CopyProfile(currentProfileName, tempProfileName);
            profiles.ActiveProfile = tempProfileName;
            return tempProfileName;
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("当前配置正在使用，且无法切换到临时配置后再导入。", ex);
        }
    }

    private static void TryRestoreActiveProfile(dynamic profiles, string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return;

        try
        {
            profiles.ActiveProfile = profileName;
        }
        catch (COMException)
        {
        }
    }

    private static void TryDeleteTemporaryProfile(dynamic profiles, string profileName)
    {
        try
        {
            var active = GetActiveProfileName(profiles);
            if (string.Equals(active, profileName, System.StringComparison.OrdinalIgnoreCase))
                return;

            profiles.DeleteProfile(profileName);
        }
        catch (COMException)
        {
        }
    }
}
