using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class CadNativeLayerCommandRepairTests
{
    [TestMethod]
    public void BuildRedefineCommandScript_IncludesLayfrz()
    {
        var script = CadNativeLayerCommandRepair.BuildRedefineCommandScript();

        StringAssert.Contains(script, "_.REDEFINE\nLAYFRZ\n");
    }

    [TestMethod]
    public void BuildRedefineCommandScript_IncludesLayvpi()
    {
        var script = CadNativeLayerCommandRepair.BuildRedefineCommandScript();

        StringAssert.Contains(script, "_.REDEFINE\nLAYVPI\n");
    }
}
