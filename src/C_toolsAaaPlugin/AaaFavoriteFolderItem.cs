using System.IO;

namespace C_toolsAaaPlugin;

internal sealed class AaaFavoriteFolderItem
{
    public string FullPath { get; set; } = "";

    public string DisplayName
    {
        get
        {
            var trimmed = (FullPath ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0)
                return "未命名目录";

            var folderName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(folderName) ? trimmed : folderName;
        }
    }
}
