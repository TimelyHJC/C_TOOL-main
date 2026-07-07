using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class WallFinishSettingsManagerTests
{
    [TestMethod]
    public void FormatLayerSummary_ReturnsFallbackForEmptyInput()
    {
        var summary = WallFinishCommandService.SettingsManager.FormatLayerSummary(Array.Empty<string>());

        Assert.AreEqual("[0]", summary);
    }

    [TestMethod]
    public void FormatLayerSummary_SortsAndDeduplicatesLayerNames()
    {
        var summary = WallFinishCommandService.SettingsManager.FormatLayerSummary(new[] { "B-WALL", "A-FINISH", "a-finish", "C-ANNO" });

        Assert.AreEqual("[A-FINISH]、[B-WALL]、[C-ANNO]", summary);
    }

    [TestMethod]
    public void ResolveHatchStyle_UsesSavedSnapshotWhenPresent()
    {
        var entry = new LayerShortcutEntry
        {
            HatchStyle = new HatchStyleSnapshot
            {
                PatternName = "ANSI31",
                Scale = 2.5,
                AngleDegrees = 30.0
            }.ToJson()
        };

        var style = WallFinishCommandService.SettingsManager.ResolveHatchStyle(entry);

        Assert.AreEqual("ANSI31", style.PatternName);
        Assert.AreEqual(2.5, style.Scale, 1e-9);
        Assert.AreEqual(30.0, style.AngleDegrees, 1e-9);
    }

    [TestMethod]
    public void ResolveHatchStyle_FallsBackToSolidWhenMissing()
    {
        var style = WallFinishCommandService.SettingsManager.ResolveHatchStyle(null);

        Assert.AreEqual("SOLID", style.PatternName);
        Assert.AreEqual(1.0, style.Scale, 1e-9);
        Assert.AreEqual(0.0, style.AngleDegrees, 1e-9);
    }
}
