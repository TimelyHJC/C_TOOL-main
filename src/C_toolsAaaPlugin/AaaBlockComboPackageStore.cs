using System.IO;
using System.Text.Json;

namespace C_toolsAaaPlugin;

internal static class AaaBlockComboPackageStore
{
    internal const string PackageSuffix = ".aaa.combo";
    internal const string ManifestFileName = "manifest.json";
    internal const string MembersFolderName = "members";
    internal const string PreviewFileName = "preview.dwg";
    private const int CurrentSchemaVersion = 2;

    internal static bool IsComboPackageDirectory(string? path)
    {
        return path != null &&
               !string.IsNullOrWhiteSpace(path) &&
               path.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(path);
    }

    internal static bool IsComboDefinitionFile(string? path)
    {
        return path != null &&
               !string.IsNullOrWhiteSpace(path) &&
               path.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path);
    }

    internal static bool ComboArtifactExists(string? path)
    {
        return path != null &&
               !string.IsNullOrWhiteSpace(path) &&
               path.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase) &&
               (File.Exists(path) || Directory.Exists(path));
    }

    internal static string GetDisplayName(string comboPath)
    {
        var name = Path.GetFileName(comboPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^PackageSuffix.Length];
        return name;
    }

    internal static string GetManifestPath(string comboPath) =>
        IsComboPackageDirectory(comboPath)
            ? Path.Combine(comboPath, ManifestFileName)
            : comboPath;

    internal static string GetMembersDirectoryPath(string packageDirectoryPath) =>
        Path.Combine(packageDirectoryPath, MembersFolderName);

    internal static string GetPreviewPath(string packageDirectoryPath) =>
        Path.Combine(packageDirectoryPath, PreviewFileName);

    internal static string BuildUniqueDefinitionPath(string targetFolder, string displayName)
    {
        return BuildUniqueArtifactPath(targetFolder, displayName, "组合");
    }

    internal static AaaBlockComboManifest? Load(string comboPath)
    {
        try
        {
            var manifestPath = GetManifestPath(comboPath);
            if (!File.Exists(manifestPath))
                return null;

            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<AaaBlockComboManifest>(json, JsonOptionsCache.ReadRelaxed);
            if (manifest == null)
                return null;

            manifest.SchemaVersion = manifest.SchemaVersion <= 0 ? 1 : manifest.SchemaVersion;
            manifest.DisplayName = (manifest.DisplayName ?? "").Trim();
            manifest.PreviewRelativePath = NormalizeRelativePath(
                manifest.PreviewRelativePath,
                IsComboPackageDirectory(comboPath) ? PreviewFileName : "");
            manifest.Members ??= new List<AaaBlockComboMember>();

            foreach (var member in manifest.Members)
            {
                member.DisplayName = (member.DisplayName ?? "").Trim();
                member.DeviceName = (member.DeviceName ?? "").Trim();
                if (member.DeviceName.Length == 0)
                    member.DeviceName = AaaBlockLibraryNameHelper.GetDeviceName(member.DisplayName);

                member.RelativePath = NormalizeRelativePath(member.RelativePath, "");
                member.SourceRelativePath = NormalizeRelativePath(member.SourceRelativePath, "");
                member.SourceRelativeFolder = NormalizeRelativeFolder(member.SourceRelativeFolder);
                member.SourceHandle = (member.SourceHandle ?? "").Trim();
                member.LayerName = (member.LayerName ?? "").Trim();

                if (Math.Abs(member.ScaleX) < 1e-9)
                    member.ScaleX = 1d;
                if (Math.Abs(member.ScaleY) < 1e-9)
                    member.ScaleY = 1d;
                if (Math.Abs(member.ScaleZ) < 1e-9)
                    member.ScaleZ = 1d;
            }

            return manifest;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 V_AAA 组合定义", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 V_AAA 组合定义（权限）", ex);
        }
        catch (JsonException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析 V_AAA 组合定义", ex);
        }

        return null;
    }

    internal static void Save(string comboPath, AaaBlockComboManifest manifest)
    {
        var manifestPath = GetManifestPath(comboPath);
        var parentFolder = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(parentFolder))
            Directory.CreateDirectory(parentFolder);

        manifest.SchemaVersion = CurrentSchemaVersion;
        manifest.DisplayName = (manifest.DisplayName ?? "").Trim();
        manifest.PreviewRelativePath = NormalizeRelativePath(manifest.PreviewRelativePath, "");
        manifest.Members ??= new List<AaaBlockComboMember>();

        foreach (var member in manifest.Members)
        {
            member.DisplayName = (member.DisplayName ?? "").Trim();
            member.DeviceName = (member.DeviceName ?? "").Trim();
            if (member.DeviceName.Length == 0)
                member.DeviceName = AaaBlockLibraryNameHelper.GetDeviceName(member.DisplayName);

            member.RelativePath = NormalizeRelativePath(member.RelativePath, "");
            member.SourceRelativePath = NormalizeRelativePath(member.SourceRelativePath, "");
            member.SourceRelativeFolder = NormalizeRelativeFolder(member.SourceRelativeFolder);
            member.SourceHandle = (member.SourceHandle ?? "").Trim();
            member.LayerName = (member.LayerName ?? "").Trim();

            if (Math.Abs(member.ScaleX) < 1e-9)
                member.ScaleX = 1d;
            if (Math.Abs(member.ScaleY) < 1e-9)
                member.ScaleY = 1d;
            if (Math.Abs(member.ScaleZ) < 1e-9)
                member.ScaleZ = 1d;
        }

        var json = JsonSerializer.Serialize(manifest, JsonOptionsCache.WriteIndented);
        File.WriteAllText(manifestPath, json);
    }

    internal static string? ResolvePreviewSourcePath(string comboPath, AaaBlockComboManifest? manifest)
    {
        if (!IsComboPackageDirectory(comboPath))
            return null;

        var previewPath = GetPreviewPath(comboPath);
        if (File.Exists(previewPath))
            return previewPath;

        if (manifest == null)
            return null;

        var declaredPreview = ResolveRelativePath(comboPath, manifest.PreviewRelativePath);
        if (File.Exists(declaredPreview))
            return declaredPreview;

        foreach (var member in manifest.Members ?? Enumerable.Empty<AaaBlockComboMember>())
        {
            var memberPath = ResolveMemberPath(comboPath, member);
            if (File.Exists(memberPath))
                return memberPath;
        }

        return null;
    }

    internal static string ResolveMemberPath(string comboPath, AaaBlockComboMember member)
    {
        if (!IsComboPackageDirectory(comboPath))
            return "";

        return ResolveRelativePath(comboPath, member.RelativePath);
    }

    internal static void TryDeleteComboArtifact(string comboPath)
    {
        if (string.IsNullOrWhiteSpace(comboPath))
            return;

        try
        {
            if (File.Exists(comboPath))
            {
                File.Delete(comboPath);
                return;
            }

            if (Directory.Exists(comboPath))
                Directory.Delete(comboPath, true);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 清理组合定义失败：{comboPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 清理组合定义失败（权限）：{comboPath}", ex);
        }
    }

    private static string BuildUniqueArtifactPath(string targetFolder, string displayName, string fallbackStem)
    {
        var safeStem = AaaPathNamingHelper.SanitizeStem(displayName, fallbackStem);
        var preferred = Path.Combine(targetFolder, safeStem + PackageSuffix);
        if (!ComboArtifactExists(preferred))
            return preferred;

        var safeFallback = AaaPathNamingHelper.SanitizeStem(fallbackStem, "combo");
        var withFallback = Path.Combine(targetFolder, $"{safeStem}_{safeFallback}{PackageSuffix}");
        if (!ComboArtifactExists(withFallback))
            return withFallback;

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(targetFolder, $"{safeStem}_{index}{PackageSuffix}");
            if (!ComboArtifactExists(candidate))
                return candidate;
        }
    }

    private static string ResolveRelativePath(string comboPath, string? relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath, "");
        if (normalized.Length == 0)
            return comboPath;

        var baseFolder = IsComboPackageDirectory(comboPath)
            ? comboPath
            : Path.GetDirectoryName(Path.GetFullPath(comboPath)) ?? "";
        if (baseFolder.Length == 0)
            return comboPath;

        return BuildSafePath(baseFolder, normalized, comboPath);
    }

    private static string NormalizeRelativePath(string? relativePath, string fallback)
    {
        var trimmed = (relativePath ?? "").Trim();
        if (trimmed.Length == 0)
            trimmed = fallback;

        if (Path.IsPathRooted(trimmed))
            trimmed = fallback;

        return trimmed
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string NormalizeRelativeFolder(string? relativeFolder)
    {
        var normalized = NormalizeRelativePath(relativeFolder, "");
        return normalized.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string BuildSafePath(string baseFolder, string relativePath, string fallbackPath)
    {
        var root = AppendDirectorySeparator(Path.GetFullPath(baseFolder));
        var resolvedPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return IsUnderDirectory(root, resolvedPath)
            ? resolvedPath
            : fallbackPath;
    }

    private static bool IsUnderDirectory(string directoryPath, string candidatePath)
    {
        var normalizedDirectory = AppendDirectorySeparator(Path.GetFullPath(directoryPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   normalizedCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   normalizedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            return path;

        return path + Path.DirectorySeparatorChar;
    }
}

internal sealed class AaaBlockComboManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string DisplayName { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string PreviewRelativePath { get; set; } = "";
    public List<AaaBlockComboMember> Members { get; set; } = new();
}

internal sealed class AaaBlockComboMember
{
    public string DisplayName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SourceRelativePath { get; set; } = "";
    public string SourceRelativeFolder { get; set; } = "";
    public string SourceHandle { get; set; } = "";
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double OffsetZ { get; set; }
    public double Rotation { get; set; }
    public double ScaleX { get; set; } = 1d;
    public double ScaleY { get; set; } = 1d;
    public double ScaleZ { get; set; } = 1d;
    public string LayerName { get; set; } = "";
}
