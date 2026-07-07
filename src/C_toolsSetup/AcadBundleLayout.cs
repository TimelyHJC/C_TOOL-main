using System.Xml.Linq;

namespace C_toolsSetup;

/// <summary>与 ApplicationPlugins / 安装目录下的 *.bundle 目录布局相关的解析。</summary>
internal static class AcadBundleLayout
{
    internal sealed record StartupModule(string RegistrationName, string ModulePath);

    /// <summary>与主插件同目录的常见依赖，不得作为 LOADER 目标（枚举顺序不确定时易误选）。</summary>
    private static readonly string[] s_thirdPartyDllPrefixes =
    {
        "ClosedXML", "DocumentFormat", "Microsoft.", "System.", "SixLabors", "RBush",
        "ExcelNumberFormat", "netstandard"
    };

    /// <summary>按 <c>PackageContents.xml</c> 的 <c>ComponentEntry</c> 读取启动 DLL；若缺失则回退到旧启发式查找。</summary>
    internal static IReadOnlyList<StartupModule> GetStartupModules(string bundleRoot)
    {
        var fromManifest = TryReadStartupModulesFromPackageContents(bundleRoot);
        if (fromManifest.Count > 0)
            return fromManifest;

        var primaryDll = TryFindPrimaryDllByHeuristic(bundleRoot);
        if (string.IsNullOrWhiteSpace(primaryDll))
            return Array.Empty<StartupModule>();

        var registrationName = Path.GetFileNameWithoutExtension(primaryDll);
        if (string.IsNullOrWhiteSpace(registrationName))
            registrationName = "Plugin";
        return new[] { new StartupModule(registrationName, primaryDll) };
    }

    /// <summary>在 <c>Contents\Win64</c> 下查找主插件 DLL：优先取 manifest 中第一个可加载组件，否则回退到旧启发式。</summary>
    internal static string? TryFindPrimaryDll(string bundleRoot)
    {
        var startupModules = GetStartupModules(bundleRoot);
        return startupModules.Count == 0 ? null : startupModules[0].ModulePath;
    }

    private static IReadOnlyList<StartupModule> TryReadStartupModulesFromPackageContents(string bundleRoot)
    {
        var packageContentsPath = Path.Combine(bundleRoot, "PackageContents.xml");
        if (!File.Exists(packageContentsPath))
            return Array.Empty<StartupModule>();

        try
        {
            var fullBundleRoot = Path.GetFullPath(bundleRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var document = XDocument.Load(packageContentsPath);
            var modules = new List<StartupModule>();
            var seenModulePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var componentEntry in document.Descendants().Where(x =>
                         string.Equals(x.Name.LocalName, "ComponentEntry", StringComparison.OrdinalIgnoreCase)))
            {
                if (!ShouldLoadOnAcadStartup(componentEntry))
                    continue;

                var moduleName = componentEntry.Attribute("ModuleName")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(moduleName) ||
                    !moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolvedModulePath = TryResolveBundleRelativePath(fullBundleRoot, moduleName);
                if (resolvedModulePath == null || !File.Exists(resolvedModulePath))
                    continue;

                if (!seenModulePaths.Add(resolvedModulePath))
                    continue;

                var registrationName = componentEntry.Attribute("AppName")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(registrationName))
                    registrationName = Path.GetFileNameWithoutExtension(resolvedModulePath);
                if (string.IsNullOrWhiteSpace(registrationName))
                    registrationName = "Plugin";

                modules.Add(new StartupModule(registrationName, resolvedModulePath));
            }

            return modules;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanStartupModules: {ex.Message}");
            return Array.Empty<StartupModule>();
        }
    }

    private static bool ShouldLoadOnAcadStartup(XElement componentEntry)
    {
        var raw = componentEntry.Attribute("LoadOnAutoCADStartup")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string? TryResolveBundleRelativePath(string fullBundleRoot, string moduleName)
    {
        var relativePath = moduleName
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        while (relativePath.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            relativePath = relativePath[2..];

        if (Path.IsPathRooted(relativePath))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(fullBundleRoot, relativePath));
            if (!IsResolvedPathWithinBundle(fullPath, fullBundleRoot))
                return null;
            return fullPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryResolveBundleRelativePath: {ex.Message}");
            return null;
        }
    }

    private static bool IsResolvedPathWithinBundle(string candidateFullPath, string bundleRootFullPath)
    {
        if (string.Equals(candidateFullPath, bundleRootFullPath, StringComparison.OrdinalIgnoreCase))
            return true;
        return candidateFullPath.StartsWith(bundleRootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || candidateFullPath.StartsWith(bundleRootFullPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFindPrimaryDllByHeuristic(string bundleRoot)
    {
        var win64 = Path.Combine(bundleRoot, "Contents", "Win64");
        if (!Directory.Exists(win64))
            return null;

        try
        {
            var dirName = Path.GetFileName(bundleRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var stem = dirName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                ? dirName[..^".bundle".Length]
                : dirName;
            var expectedDll = stem + ".dll";

            string? pick = null;
            foreach (var f in Directory.EnumerateFiles(win64, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(f), expectedDll, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(f);
            }

            foreach (var f in Directory.EnumerateFiles(win64, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(f).Contains("NetFx", StringComparison.OrdinalIgnoreCase))
                {
                    pick = Path.GetFullPath(f);
                    break;
                }
            }

            if (pick != null)
                return pick;

            var candidates = new List<string>();
            foreach (var f in Directory.EnumerateFiles(win64, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var fn = Path.GetFileName(f);
                if (IsLikelyShippedDependencyDll(fn))
                    continue;
                candidates.Add(f);
            }

            candidates.Sort(StringComparer.OrdinalIgnoreCase);
            return candidates.Count == 0 ? null : Path.GetFullPath(candidates[0]);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLikelyShippedDependencyDll(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName))
            return true;
        foreach (var p in s_thirdPartyDllPrefixes)
        {
            if (baseName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
