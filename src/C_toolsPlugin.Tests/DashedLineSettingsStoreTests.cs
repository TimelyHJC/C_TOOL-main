using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class DashedLineSettingsStoreTests
{
    [TestMethod]
    public void Normalize_DefaultsLinetypeSystemSettings()
    {
        var normalized = DashedLineSettingsStore.Normalize(new DashedLineSettingsDto());

        Assert.AreEqual(2, normalized.Version);
        Assert.IsFalse(normalized.UsePaperSpaceUnitsForScaling);
        Assert.AreEqual(1.0, normalized.GlobalLinetypeScale, 1e-9);
    }

    [TestMethod]
    public void Normalize_PreservesValidLinetypeSystemSettings()
    {
        var normalized = DashedLineSettingsStore.Normalize(new DashedLineSettingsDto
        {
            UsePaperSpaceUnitsForScaling = true,
            GlobalLinetypeScale = 10.0
        });

        Assert.IsTrue(normalized.UsePaperSpaceUnitsForScaling);
        Assert.AreEqual(10.0, normalized.GlobalLinetypeScale, 1e-9);
    }

    [TestMethod]
    public void Normalize_RejectsInvalidGlobalLinetypeScale()
    {
        var normalized = DashedLineSettingsStore.Normalize(new DashedLineSettingsDto
        {
            GlobalLinetypeScale = double.NaN
        });

        Assert.AreEqual(1.0, normalized.GlobalLinetypeScale, 1e-9);
    }

    [TestMethod]
    public void Normalize_ClampsGlobalLinetypeScale()
    {
        var low = DashedLineSettingsStore.Normalize(new DashedLineSettingsDto
        {
            GlobalLinetypeScale = 0.000001
        });
        var high = DashedLineSettingsStore.Normalize(new DashedLineSettingsDto
        {
            GlobalLinetypeScale = 10000000.0
        });

        Assert.AreEqual(0.0001, low.GlobalLinetypeScale, 1e-9);
        Assert.AreEqual(1000000.0, high.GlobalLinetypeScale, 1e-9);
    }
}
