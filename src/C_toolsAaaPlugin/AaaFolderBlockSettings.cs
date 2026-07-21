namespace C_toolsAaaPlugin;

internal sealed class AaaFolderBlockSettings
{
    public int FileVersion { get; set; } = 1;
    public string FolderPath { get; set; } = "";
    public bool IncludeSubfolders { get; set; }
    public bool ThumbnailDarkBackground { get; set; }
    public string LastCategoryKey { get; set; } = AaaCategoryTagItem.SingleLibraryKey;
    public List<string> FavoriteFolders { get; set; } = new();
}
