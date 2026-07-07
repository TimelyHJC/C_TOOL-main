using System;
using System.Collections.Generic;
using System.IO;
using C_toolsShared;

namespace C_toolsPlugin;

internal static class LayerShortcutInitialData
{
    internal const string FileName = "初始化文件.md";
    private const string LegacyFileName = "初始化快捷键文件.md";

    internal static string PrimaryPath => Path.Combine(C_toolsPaths.UserEditableFolder, FileName);

    internal static string? ResolveFilePath()
    {
        foreach (var candidate in EnumerateCandidatePaths())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore invalid candidate paths.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { FileName, LegacyFileName })
        {
            foreach (var path in EnumerateCandidatePathsForName(name))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidatePathsForName(string fileName)
    {
        yield return Path.Combine(C_toolsPaths.UserEditableFolder, fileName);
        yield return Path.Combine(C_toolsPaths.UserSiblingFolder, fileName);
        yield return Path.Combine(C_toolsPaths.AppDataRoot, fileName);

        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
            yield break;

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "model", fileName);
            yield return Path.Combine(dir.FullName, fileName);
        }
    }
}
