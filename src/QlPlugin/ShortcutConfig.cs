using System.Text.Json;

namespace QlPlugin;

/// <summary>
/// 快捷命令配置存储
/// </summary>
public static class ShortcutConfig
{
    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QLPlugin", "shortcuts.json");

    public static List<ShortcutEntry> Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                return [];

            if (!File.Exists(ConfigPath)) return [];

            var json = File.ReadAllText(ConfigPath);
            var items = JsonSerializer.Deserialize<List<ShortcutItem>>(json);
            return items?.Select(x => new ShortcutEntry(x.Alias, x.Command)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(List<ShortcutEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var items = entries.Select(e => new ShortcutItem { Alias = e.Alias, Command = e.Command }).ToList();
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 忽略保存失败
        }
    }

    private class ShortcutItem
    {
        public string Alias { get; set; } = "";
        public string Command { get; set; } = "";
    }
}

public record ShortcutEntry(string Alias, string Command);
