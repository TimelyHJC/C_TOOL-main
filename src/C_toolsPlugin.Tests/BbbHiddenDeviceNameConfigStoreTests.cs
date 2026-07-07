using Microsoft.VisualStudio.TestTools.UnitTesting;
using C_toolsShared;

namespace C_toolsPlugin.Tests;

[TestClass]
public class BbbHiddenDeviceNameConfigStoreTests
{
    [TestMethod]
    public void NormalizeWorkbookPath_ReturnsDefaultForBlankText()
    {
        Assert.AreEqual(
            BbbHiddenDeviceNameConfigStore.DefaultWorkbookPath,
            BbbHiddenDeviceNameConfigStore.NormalizeWorkbookPath("  "));
    }

    [TestMethod]
    public void NormalizeWorkbookPath_TrimsConfiguredPath()
    {
        const string workbookPath = @"D:\Data\设备清单.xlsx";

        var normalized = BbbHiddenDeviceNameConfigStore.NormalizeWorkbookPath("  " + workbookPath + "  ");

        Assert.AreEqual(workbookPath, normalized);
    }
}
