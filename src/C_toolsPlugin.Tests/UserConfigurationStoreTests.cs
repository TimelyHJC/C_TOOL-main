using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class UserConfigurationStoreTests
{
    [TestMethod]
    public void BuildDefaultFileContent_KeepsOnlyRuntimeKeysAndOverrides()
    {
        var content = UserConfigurationStore.BuildDefaultFileContent();

        StringAssert.Contains(content, "dimStyleName=Standard");
        StringAssert.Contains(content, "dimStyleGroupPrefix=");
        StringAssert.Contains(content, "mLeaderStyleName=Standard");
        StringAssert.Contains(content, "command.F_DddLeader.mLeaderStyleName=");
        StringAssert.Contains(content, "command.F_DDD_INSERT_LEADER.mLeaderStyleName=");
        StringAssert.Contains(content, "command.F_DDD_INSERT_TEXT.mLeaderStyleName=");
        StringAssert.Contains(content, "command.F_JT.mLeaderStyleName=");
        Assert.IsFalse(content.IndexOf("reference.dimStyle.", StringComparison.Ordinal) >= 0);
        Assert.IsFalse(content.IndexOf("reference.mLeader.", StringComparison.Ordinal) >= 0);
    }

    [TestMethod]
    public void BuildDefaultFileContent_ExplainsWhereAdvancedSettingsMoved()
    {
        var content = UserConfigurationStore.BuildDefaultFileContent();

        StringAssert.Contains(content, "系统配置面板或“设置引线”界面");
        StringAssert.Contains(content, "本文件不再展开 reference.* 只读清单");
    }

    [TestMethod]
    public void TryBuildCompactedFileContent_CompactsLegacyKeyValueTemplateAndPreservesValues()
    {
        var legacyText = string.Join("\r\n", new[]
        {
            "# C_TOOL Configuration",
            "dimStyleName=A-ANNO",
            "dimStyleGroupPrefix=A",
            "mLeaderStyleName=LEAD-MAIN",
            "command.F_DddLeader.mLeaderStyleName=LEAD-DDD",
            "command.F_DA.dimStyleName=LEGACY-DA",
            "reference.dimStyle.arrow=_DOT"
        });

        var compacted = UserConfigurationStore.TryBuildCompactedFileContent("Configuration", legacyText);

        Assert.IsNotNull(compacted);
        StringAssert.Contains(compacted, "dimStyleName=A-ANNO");
        StringAssert.Contains(compacted, "dimStyleGroupPrefix=A");
        StringAssert.Contains(compacted, "mLeaderStyleName=LEAD-MAIN");
        StringAssert.Contains(compacted, "command.F_DddLeader.mLeaderStyleName=LEAD-DDD");
        StringAssert.Contains(compacted, "command.F_DA.dimStyleName=LEGACY-DA");
        Assert.IsFalse(compacted.IndexOf("reference.dimStyle.", StringComparison.Ordinal) >= 0);
    }

    [TestMethod]
    public void TryBuildCompactedFileContent_CompactsLegacyJson()
    {
        var legacyJson = """
        {
          "dimStyleName": "A-JSON",
          "mLeaderStyleName": "LEAD-JSON",
          "commandMLeaderStyleNames": {
            "F_JT": "LEAD-QA"
          }
        }
        """;

        var compacted = UserConfigurationStore.TryBuildCompactedFileContent("Configuration.json", legacyJson);

        Assert.IsNotNull(compacted);
        StringAssert.Contains(compacted, "dimStyleName=A-JSON");
        StringAssert.Contains(compacted, "mLeaderStyleName=LEAD-JSON");
        StringAssert.Contains(compacted, "command.F_JT.mLeaderStyleName=LEAD-QA");
        Assert.IsFalse(compacted.TrimStart().StartsWith("{", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryBuildCompactedFileContent_IgnoresCurrentCompactTemplate()
    {
        var current = UserConfigurationStore.BuildDefaultFileContent();

        var compacted = UserConfigurationStore.TryBuildCompactedFileContent("Configuration", current);

        Assert.IsNull(compacted);
    }
}
