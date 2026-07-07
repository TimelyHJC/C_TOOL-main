using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class QuickWallCommandServiceTests
{
    [TestMethod]
    public void ResolveEffectiveWidths_UsesPrimaryWidthWhenSecondaryIsMissing()
    {
        var settings = new QuickWallSettingsDto
        {
            PrimaryWidth = 110,
            SecondaryWidth = null,
            UseSecondaryWidth = false
        };

        var result = QuickWallCommandService.ResolveEffectiveWidths(settings);

        Assert.AreEqual(110.0, result.PrimaryWidth, 1e-9);
        Assert.AreEqual(0.0, result.SecondaryWidth, 1e-9);
    }

    [TestMethod]
    public void ResolveEffectiveWidths_UsesLargestConfiguredWidthInSingleLayerMode()
    {
        var settings = new QuickWallSettingsDto
        {
            PrimaryWidth = 40,
            SecondaryWidth = 110,
            UseSecondaryWidth = false
        };

        var result = QuickWallCommandService.ResolveEffectiveWidths(settings);

        Assert.AreEqual(110.0, result.PrimaryWidth, 1e-9);
        Assert.AreEqual(0.0, result.SecondaryWidth, 1e-9);
    }

    [TestMethod]
    public void ResolveEffectiveWidths_PreservesPrimaryAndSecondaryWidthsInDoubleLayerMode()
    {
        var settings = new QuickWallSettingsDto
        {
            PrimaryWidth = 40,
            SecondaryWidth = 110,
            UseSecondaryWidth = true
        };

        var result = QuickWallCommandService.ResolveEffectiveWidths(settings);

        Assert.AreEqual(40.0, result.PrimaryWidth, 1e-9);
        Assert.AreEqual(110.0, result.SecondaryWidth, 1e-9);
    }

    [TestMethod]
    public void SwapConfiguredWidths_SwapsPrimaryAndSecondaryWidths()
    {
        var settings = new QuickWallSettingsDto
        {
            PrimaryWidth = 40,
            SecondaryWidth = 110,
            UseSecondaryWidth = true
        };

        var message = QuickWallCommandService.SwapConfiguredWidths(settings);

        Assert.AreEqual(110.0, settings.PrimaryWidth, 1e-9);
        Assert.AreEqual(40.0, settings.SecondaryWidth!.Value, 1e-9);
        StringAssert.Contains(message, "110");
        StringAssert.Contains(message, "40");
    }

    [TestMethod]
    public void SwapConfiguredWidths_ReturnsHintWhenSecondaryWidthIsMissing()
    {
        var settings = new QuickWallSettingsDto
        {
            PrimaryWidth = 110,
            SecondaryWidth = null,
            UseSecondaryWidth = false
        };

        var message = QuickWallCommandService.SwapConfiguredWidths(settings);

        Assert.AreEqual(110.0, settings.PrimaryWidth, 1e-9);
        Assert.IsNull(settings.SecondaryWidth);
        StringAssert.Contains(message, "未设置第二宽度");
    }
}
