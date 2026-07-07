using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace C_toolsPlugin;

internal static class CadNativeLayerCommandRepair
{
    private static int s_modulesLoaded;
    private static int s_redefineRequested;

    private static readonly string[] ModuleFileNames =
    [
        "AcLayer.dll",
        "AcLayerTools.dll",
        "AcLayerApps.arx"
    ];

    internal static readonly string[] LayerCommandNames =
    [
        "LAYON",
        "LAYOFF",
        "LAYFRZ",
        "LAYTHW",
        "LAYISO",
        "LAYLCK",
        "LAYULK",
        "LAYMCH",
        "LAYUNISO",
        "LAYMCUR",
        "LAYVPI",
        "LAYDEL",
        "LAYMRG",
        "LAYWALK"
    ];

    internal static void TryLoadModules(string reason)
    {
        if (Interlocked.Exchange(ref s_modulesLoaded, 1) != 0)
            return;

        var acadRoot = TryGetAcadRoot();
        if (string.IsNullOrWhiteSpace(acadRoot))
            return;

        foreach (var fileName in ModuleFileNames)
        {
            var path = Path.Combine(acadRoot, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                SystemObjects.DynamicLinker.LoadModule(path, false, false);
            }
            catch (System.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"{reason}: load native layer module failed: {fileName}", ex);
            }
        }
    }

    internal static void TryRequestRedefine(Document? doc, string reason)
    {
        if (doc == null)
            return;
        if (Interlocked.Exchange(ref s_redefineRequested, 1) != 0)
            return;

        try
        {
            doc.SendStringToExecute(BuildRedefineCommandScript(), true, false, false);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{reason}: request native layer command redefine failed", ex);
        }
    }

    internal static string BuildRedefineCommandScript()
    {
        var lines = new List<string>(LayerCommandNames.Length * 2);
        foreach (var commandName in LayerCommandNames)
        {
            lines.Add("_.REDEFINE");
            lines.Add(commandName);
        }

        return string.Join("\n", lines) + "\n";
    }

    private static string? TryGetAcadRoot()
    {
        try
        {
            var main = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(main))
                return Path.GetDirectoryName(main);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("resolve AutoCAD root from process failed", ex);
        }

        try
        {
            var acMgd = typeof(Application).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(acMgd))
                return Path.GetDirectoryName(acMgd);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("resolve AutoCAD root from AcMgd failed", ex);
        }

        return null;
    }
}
