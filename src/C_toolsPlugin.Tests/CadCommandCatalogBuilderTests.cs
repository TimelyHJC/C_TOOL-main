using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class CadCommandCatalogBuilderTests
{
    [TestMethod]
    public void MergeCatalog_IncludesBuiltInNativeCommandsWithoutPgpAlias()
    {
        var rows = CadCommandCatalogBuilder.MergeCatalog(
            Array.Empty<PgpAliasDto>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var row = rows.SingleOrDefault(x =>
            string.Equals(x.CommandName, "LAYVPI", StringComparison.OrdinalIgnoreCase));

        Assert.IsNotNull(row);
        Assert.AreEqual(CadCommandCatalogBuilder.TagCadNative, row!.CategoryTag);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.Description));
    }

    [TestMethod]
    public void MergeCatalog_KeepsPgpAliasForBuiltInNativeCommand()
    {
        var rows = CadCommandCatalogBuilder.MergeCatalog(
            new[] { new PgpAliasDto { Alias = "LV", Target = "LAYVPI" } },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var row = rows.Single(x =>
            string.Equals(x.CommandName, "LAYVPI", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual("LV", row.AliasesSummary);
        Assert.AreEqual(CadCommandCatalogBuilder.TagCadNative, row.CategoryTag);
    }
}
