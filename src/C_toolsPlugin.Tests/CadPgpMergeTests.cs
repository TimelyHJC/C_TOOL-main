using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class CadPgpMergeTests
{
    [TestMethod]
    public void WriteMergedPgpWithManagedBackup_SkipsBackupWhenContentUnchanged()
    {
        using var scope = new TemporaryDirectoryScope();
        var pgpPath = Path.Combine(scope.Path, "acad.pgp");
        File.WriteAllText(pgpPath, "same-content", new UTF8Encoding(true));

        var result = CadPgpMerge.WriteMergedPgpWithManagedBackup(pgpPath, "same-content", new UTF8Encoding(true));

        Assert.IsFalse(result.FileChanged);
        Assert.IsNull(result.BackupPath);
        Assert.IsFalse(Directory.Exists(CadPgpMerge.GetBackupDirectoryPath(pgpPath)));
    }

    [TestMethod]
    public void WriteMergedPgpWithManagedBackup_CreatesBackupInDedicatedDirectory()
    {
        using var scope = new TemporaryDirectoryScope();
        var pgpPath = Path.Combine(scope.Path, "acad.pgp");
        File.WriteAllText(pgpPath, "old-content", new UTF8Encoding(true));

        var result = CadPgpMerge.WriteMergedPgpWithManagedBackup(pgpPath, "new-content", new UTF8Encoding(true));

        Assert.IsTrue(result.FileChanged);
        Assert.IsNotNull(result.BackupPath);
        Assert.AreEqual(CadPgpMerge.GetBackupDirectoryPath(pgpPath), Path.GetDirectoryName(result.BackupPath));
        Assert.AreEqual("new-content", File.ReadAllText(pgpPath));
        Assert.AreEqual("old-content", File.ReadAllText(result.BackupPath));
    }

    [TestMethod]
    public void WriteMergedPgpWithManagedBackup_PrunesBackupsToLatestFive()
    {
        using var scope = new TemporaryDirectoryScope();
        var pgpPath = Path.Combine(scope.Path, "acad.pgp");
        File.WriteAllText(pgpPath, "content-0", new UTF8Encoding(true));

        for (var i = 1; i <= 7; i++)
            _ = CadPgpMerge.WriteMergedPgpWithManagedBackup(pgpPath, $"content-{i}", new UTF8Encoding(true));

        var backups = CadPgpMerge.GetManagedBackupFiles(pgpPath);
        Assert.AreEqual(5, backups.Length);
        Assert.AreEqual("content-7", File.ReadAllText(pgpPath));
    }

    [TestMethod]
    public void BuildSanitizedManagedAliasBlock_RemovesRetiredLegacyLauncherAliases()
    {
        var block = string.Join(Environment.NewLine,
            CadPgpMerge.BeginMarker,
            "KKK,*V_KKK",
            "V_KKK,*C_TOOL",
            "OLD,*V_KKK",
            "CT,*C_TOOL",
            CadPgpMerge.EndMarker);

        var sanitized = CadPgpMerge.BuildSanitizedManagedAliasBlock(block);

        Assert.IsFalse(sanitized.IndexOf("KKK,*V_KKK", StringComparison.Ordinal) >= 0);
        Assert.IsFalse(sanitized.IndexOf("V_KKK,*C_TOOL", StringComparison.Ordinal) >= 0);
        Assert.IsFalse(sanitized.IndexOf("OLD,*V_KKK", StringComparison.Ordinal) >= 0);
        StringAssert.Contains(sanitized, "CT,*C_TOOL");
    }

    [TestMethod]
    public void BuildAliasBlock_SkipsCadNativeAliasKeys()
    {
        var skipped = new List<string>();
        var block = CadPgpMerge.BuildAliasBlock(new[]
        {
            new PgpAliasDto { Alias = "LAYFRZ", Target = "V_AAA" },
            new PgpAliasDto { Alias = "AA", Target = "V_AAA" }
        }, skipped);

        Assert.IsFalse(block.IndexOf("LAYFRZ", StringComparison.Ordinal) >= 0);
        StringAssert.Contains(block, "AA,*V_AAA");
        Assert.AreEqual(1, skipped.Count);
        StringAssert.Contains(skipped[0], "LAYFRZ");
    }

    [TestMethod]
    public void SanitizePgpBaseBeforeC_toolsMerge_RemovesRetiredLegacyLauncherAliasLinesFromBase()
    {
        var baseText = string.Join(Environment.NewLine,
            "KKK,*V_KKK",
            "AA,*V_AAA",
            "V_KKK,*C_TOOL");

        var sanitized = CadPgpMerge.SanitizePgpBaseBeforeC_toolsMerge(baseText);

        Assert.IsFalse(sanitized.IndexOf("KKK,*V_KKK", StringComparison.Ordinal) >= 0);
        Assert.IsFalse(sanitized.IndexOf("V_KKK,*C_TOOL", StringComparison.Ordinal) >= 0);
        StringAssert.Contains(sanitized, "AA,*V_AAA");
    }

    private sealed class TemporaryDirectoryScope : IDisposable
    {
        public TemporaryDirectoryScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "C_toolsPlugin.Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 忽略临时目录清理失败
            }
        }
    }
}
