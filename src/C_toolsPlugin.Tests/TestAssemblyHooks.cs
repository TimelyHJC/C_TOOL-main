using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public static class TestAssemblyHooks
{
    [AssemblyInitialize]
    public static void Initialize(TestContext testContext)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("Acad2024InstallPath"),
            @"C:\Program Files\Autodesk\AutoCAD 2024"
        };

        foreach (var candidate in candidates)
        {
            var path = (candidate ?? "").Trim();
            if (path.Length == 0 || !Directory.Exists(path))
                continue;

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Split(';').Any(x => string.Equals(x.Trim(), path, StringComparison.OrdinalIgnoreCase)))
                Environment.SetEnvironmentVariable("PATH", path + ";" + currentPath);

            _ = SetDllDirectory(path);
            break;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
