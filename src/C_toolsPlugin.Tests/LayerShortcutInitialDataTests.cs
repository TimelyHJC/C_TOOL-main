using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class LayerShortcutInitialDataTests
{
    [TestMethod]
    public void ParseMarkdown_ReadsShortcutRowsFromTable()
    {
        var markdown =
            "| 快捷键 | 命令 | 说明 |\n" +
            "| --- | --- | --- |\n" +
            "| CC | COPY | 复制 |\n\n" +
            "| 快捷键 | 图层名称 | 颜色 | 线型 | 线宽 | 说明 | 尺寸标注 | 填充样式 |\n" +
            "| --- | --- | --- | --- | --- | --- | --- | --- |\n" +
            "| A1, AW | A-WALL | 1 | Continuous | LineWeight025 | 墙体 | 是 | |\n" +
            "| H1 | A-HATCH | 8 | | | 填充 | 是 | 默认 |\n";
        var warnings = new List<string>();

        var entries = LayerShortcutInitialData.ParseMarkdown(markdown, warnings);

        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual(0, warnings.Count);

        Assert.AreEqual("A1", entries[0].Alias);
        Assert.AreEqual("A-WALL", entries[0].LayerName);
        Assert.AreEqual(1, entries[0].ColorIndex);
        Assert.AreEqual("Continuous", entries[0].LinetypeName);
        Assert.AreEqual("LineWeight025", entries[0].LineWeight);
        Assert.AreEqual("墙体", entries[0].Description);
        Assert.IsTrue(entries[0].RunDimensionWhenNoSelection);
        Assert.IsFalse(entries[0].RunHatchWhenNoSelection);

        Assert.AreEqual("AW", entries[1].Alias);
        Assert.AreEqual("H1", entries[2].Alias);
        Assert.IsFalse(entries[2].RunDimensionWhenNoSelection);
        Assert.IsTrue(entries[2].RunHatchWhenNoSelection);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entries[2].HatchStyle));
    }

    [TestMethod]
    public void ParseCommandAliases_ReadsCommandAliasTableOnly()
    {
        var markdown =
            "| 快捷键 | 命令 | 说明 |\n" +
            "| --- | --- | --- |\n" +
            "| CC, CP | COPY | 复制 |\n\n" +
            "| 快捷键 | 图层名称 |\n" +
            "| --- | --- |\n" +
            "| A1 | A-WALL |\n";
        var warnings = new List<string>();

        var aliases = LayerShortcutInitialData.ParseCommandAliases(markdown, warnings);

        Assert.AreEqual(2, aliases.Count);
        Assert.AreEqual(0, warnings.Count);
        Assert.AreEqual("CC", aliases[0].Alias);
        Assert.AreEqual("COPY", aliases[0].Target);
        Assert.AreEqual("CP", aliases[1].Alias);
        Assert.AreEqual("COPY", aliases[1].Target);
    }

    [TestMethod]
    public void ParseMarkdown_SkipsInvalidAliasAndReportsWarning()
    {
        var markdown =
            "| alias | layerName |\n" +
            "| --- | --- |\n" +
            "| LAYFRZ | A-WALL |\n" +
            "| B1 | A-DOOR |\n";
        var warnings = new List<string>();

        var entries = LayerShortcutInitialData.ParseMarkdown(markdown, warnings);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("B1", entries[0].Alias);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "LAYFRZ");
    }
}
