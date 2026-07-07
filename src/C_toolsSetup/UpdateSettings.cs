using System.Reflection;
using Microsoft.Win32;

namespace C_toolsSetup;

internal static class UpdateSettings
{
    internal const string EnvironmentVariableName = "C_TOOL_UPDATE_MANIFEST_URL";

    // 填入你的服务器地址后，安装器即可直接检查更新。
    internal const string DefaultManifestUrl = "";

    internal static string? GetManifestUrl()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(BundleInstall.RegistryKeyPath, writable: false);
            var fromRegistry = key?.GetValue(BundleInstall.RegistryValueUpdateManifestUrl) as string;
            if (!string.IsNullOrWhiteSpace(fromRegistry))
                return fromRegistry.Trim();
        }
        catch
        {
            // Registry access failures should not prevent normal installation.
        }

        return string.IsNullOrWhiteSpace(DefaultManifestUrl) ? null : DefaultManifestUrl.Trim();
    }

    internal static string GetCurrentProductVersion()
    {
        var installedVersion = BundleInstall.TryReadInstalledVersion();
        if (!string.IsNullOrWhiteSpace(installedVersion))
            return ProductVersionComparer.NormalizeForDisplay(installedVersion);

        var informational = typeof(UpdateSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return ProductVersionComparer.NormalizeForDisplay(informational.Split('+', 2)[0]);

        return typeof(UpdateSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
