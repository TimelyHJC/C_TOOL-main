using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class LayerLispShortcutsTests
{
    [TestMethod]
    public void BuildScript_AppendsSilentPrincAfterAliasRegistration()
    {
        var entries = new[]
        {
            new LayerShortcutEntry
            {
                Alias = "A1",
                LayerName = "A-WALL"
            }
        };

        var script = LayerLispShortcuts.BuildScript(entries, out var count, out var skipped);

        Assert.AreEqual(1, count);
        Assert.AreEqual(0, skipped.Count);
        StringAssert.Contains(
            script,
            $"(setq *ctools_layer_alias_symbols* '(c:A1)){System.Environment.NewLine}(princ)");
        StringAssert.EndsWith(script.TrimEnd(), "(princ)");
    }

    [TestMethod]
    public void BuildScript_RejectsCadNativeLayerAlias()
    {
        var entries = new[]
        {
            new LayerShortcutEntry
            {
                Alias = "LAYFRZ",
                LayerName = "A-WALL"
            }
        };

        var script = LayerLispShortcuts.BuildScript(entries, out var count, out var skipped);

        Assert.AreEqual(0, count);
        Assert.AreEqual(1, skipped.Count);
        StringAssert.Contains(skipped[0], "LAYFRZ");
        StringAssert.Contains(script, "(defun c:LAYFRZ ()");
        StringAssert.Contains(script, "._F_LAYFRZ");
        Assert.IsFalse(script.IndexOf("(set _ctools_cmd nil)", StringComparison.Ordinal) >= 0);
        StringAssert.Contains(script, "(if (not (member _ctools_cmd _ctools_layer_protected_cmds))");
    }

    [TestMethod]
    public void LayerAliasRules_RejectsCadNativeCommandName()
    {
        var ok = LayerAliasRules.IsValidGeneratedCommandAlias("LAYFRZ", out var reason);

        Assert.IsFalse(ok);
        StringAssert.Contains(reason, "CAD");
    }

    [TestMethod]
    public void BuildLoadCommand_WrapsLoadWithSilentPrinc()
    {
        var command = LayerLispShortcuts.BuildLoadCommand("D:/C_tool插件/User/c_tools_layer_shortcuts.lsp");

        Assert.AreEqual(
            "(progn (load \"D:/C_tool插件/User/c_tools_layer_shortcuts.lsp\") (princ))\n",
            command);
    }
}
