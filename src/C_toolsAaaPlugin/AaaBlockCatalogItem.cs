using System.IO;
using System.Windows.Media.Imaging;

namespace C_toolsAaaPlugin;

internal enum AaaBlockCatalogItemKind
{
    Dwg = 0,
    Combo = 1
}

internal sealed class AaaBlockCatalogItem
{
    public AaaBlockCatalogItemKind Kind { get; set; } = AaaBlockCatalogItemKind.Dwg;
    public string LibraryRootPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RelativeFolder { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string PreviewSourcePath { get; set; } = "";
    public DateTime PreviewLastWriteTimeUtc { get; set; }
    public string FileSizeText { get; set; } = "";
    public string ModifiedText { get; set; } = "";
    public DateTime LastWriteTimeUtc { get; set; }
    public int MemberCount { get; set; }
    public IReadOnlyList<string> CategoryTags { get; set; } = Array.Empty<string>();

    public string RelativeFolderDisplay =>
        string.IsNullOrWhiteSpace(RelativeFolder) ? "根目录" : RelativeFolder;

    public string CategoryTagsText =>
        CategoryTags.Count == 0 ? "根目录" : string.Join(" · ", CategoryTags);

    public bool IsCombo => Kind == AaaBlockCatalogItemKind.Combo;

    public string TypeText => IsCombo ? "组合" : "图块";

    public string LibraryCategoryText => IsCombo ? "组合图库" : "独立图库";

    public string PreviewAssetPath =>
        string.IsNullOrWhiteSpace(PreviewSourcePath) ? FullPath : PreviewSourcePath;

    public bool ExistsOnDisk =>
        IsCombo
            ? !string.IsNullOrWhiteSpace(ManifestPath) && File.Exists(ManifestPath)
            : File.Exists(FullPath);

    public string PreviewCacheKey => $"{PreviewAssetPath}|{PreviewLastWriteTimeUtc.Ticks}|{LastWriteTimeUtc.Ticks}";

    public BitmapSource? ThumbnailImage => AaaBlockThumbnailService.Load(this);

    public string SearchText =>
        $"{DisplayName} {TypeText} {LibraryCategoryText} {RelativeFolderDisplay} {CategoryTagsText} {FullPath} {MemberCount}";
}
