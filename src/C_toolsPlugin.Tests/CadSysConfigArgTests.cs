using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class CadSysConfigArgTests
{
    [TestMethod]
    public void TryGetFirstProfileName_SkipsFixedProfileAndReturnsFirstUserProfile()
    {
        const string content = """
        REGEDIT4

        [HKEY_CURRENT_USER\Software\Autodesk\AutoCAD\R24.3\ACAD-5101:409\Profiles\FixedProfile\General]
        "Dummy"="0"

        [HKEY_CURRENT_USER\Software\Autodesk\AutoCAD\R24.3\ACAD-5101:409\Profiles\MyProfile\General]
        "Dummy"="1"
        """;

        using var scope = new TemporaryDirectoryScope();
        var path = scope.WriteFile("sample.arg", content);

        var ok = CadSysConfigArg.TryGetFirstProfileName(path, out var profileName);

        Assert.IsTrue(ok);
        Assert.AreEqual("MyProfile", profileName);
    }

    [TestMethod]
    public void LoadRows_ParsesSupportedRowsAndSkipsExcludedRegistryKeys()
    {
        const string content = """
        REGEDIT4

        [HKEY_CURRENT_USER\Software\Autodesk\AutoCAD\R24.3\ACAD-5101:409\Profiles\Demo\General]
        "WhipThreadEnable"=dword:00000002
        "ShowProxyDialog"=dword:00000001
        "TemplatePath"="C:\\Templates"
        "CustomShortText"="A-01"

        [HKEY_CURRENT_USER\Software\Autodesk\AutoCAD\R24.3\ACAD-5101:409\Profiles\Demo\General Configuration]
        "Coords"=dword:00000001
        """;

        using var scope = new TemporaryDirectoryScope();
        var path = scope.WriteFile("rows.arg", content);

        var rows = CadSysConfigArg.LoadRows(path);

        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual("WHIPTHREAD", rows[0].VarName);
        Assert.AreEqual("2", rows[0].Value);
        Assert.AreEqual("PROXYNOTICE", rows[1].VarName);
        Assert.AreEqual("CustomShortText".ToUpperInvariant(), rows[2].VarName);
        Assert.AreEqual("A-01", rows[2].Value);
        Assert.AreEqual("COORDS", rows[3].VarName);
        Assert.IsFalse(rows.Any(static row => string.Equals(row.ArgRegistryKey, "TemplatePath", StringComparison.Ordinal)));
    }

    private sealed class TemporaryDirectoryScope : IDisposable
    {
        public TemporaryDirectoryScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CadSysConfigArgTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }

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
