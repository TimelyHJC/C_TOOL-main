using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class CommandAliasSaveTests
{
    [TestMethod]
    public void SuggestedAlias_IsUsedForCommandSaveButNotSnapshotPersistence()
    {
        var row = new CommandCatalogRow("V_AAA", "", "", CadCommandCatalogBuilder.TagVCommand);

        row.SetSuggestedDefaultAlias("AA");

        Assert.AreEqual("AA", row.Alias);
        Assert.AreEqual("AA", row.AliasForCommandSave);
        Assert.AreEqual("", row.AliasForPersistence);
        Assert.IsTrue(row.AliasIsSuggestedDefault);
    }

    [DataTestMethod]
    [DataRow("COPY", "CC")]
    [DataRow("ROTATE", "RR")]
    [DataRow("MATCHPROP", "V")]
    [DataRow("RECTANG", "R")]
    public void ApplyMissingAliases_AddsCadNativeDefaultAlias(string commandName, string expectedAlias)
    {
        var row = new CommandCatalogRow(commandName, "", "", CadCommandCatalogBuilder.TagCadNative);

        CommandAliasDefaults.ApplyMissingAliases(new[] { row });

        Assert.AreEqual(expectedAlias, row.Alias);
        Assert.AreEqual(expectedAlias, row.AliasForCommandSave);
        Assert.AreEqual("", row.AliasForPersistence);
        Assert.IsTrue(row.AliasIsSuggestedDefault);
    }

    [TestMethod]
    public void DuplicateChecker_IncludesSuggestedCommandAlias()
    {
        var layerRow = new CommandCatalogRow(
            PluginCommandIds.LayerShortcutCatalogCommandLabel,
            "",
            "",
            CadCommandCatalogBuilder.TagLayerShortcut)
        {
            Alias = "AA",
            LayerName = "A-WALL"
        };
        var commandRow = new CommandCatalogRow("V_AAA", "", "", CadCommandCatalogBuilder.TagVCommand);
        commandRow.SetSuggestedDefaultAlias("AA");

        var result = SaveDuplicateChecker.Analyze(new[] { layerRow, commandRow });

        Assert.IsTrue(result.HasIssues);
        StringAssert.Contains(result.DialogText, "AA");
        StringAssert.Contains(result.DialogText, "V_AAA");
    }

    [TestMethod]
    public void CommandSaveTokens_SkipDirectCommandSelfAlias()
    {
        var row = new CommandCatalogRow("KDR", "", "", CadCommandCatalogBuilder.TagPluginCommand);
        row.SetSuggestedDefaultAlias("KDR");

        var tokens = row.EnumerateAliasTokensForCommandSave().ToList();

        Assert.AreEqual(0, tokens.Count);
    }

    [TestMethod]
    public void SaveSummary_IncludesModifiedSuggestedAlias()
    {
        var row = new CommandCatalogRow("V_AAA", "", "", CadCommandCatalogBuilder.TagVCommand)
        {
            Description = "Block library",
            IsUserModified = true
        };
        row.SetSuggestedDefaultAlias("AA");

        var summary = SaveSummaryBuilder.Build(new[] { row });

        StringAssert.Contains(summary, "AA");
        StringAssert.Contains(summary, "Block library");
    }
}
