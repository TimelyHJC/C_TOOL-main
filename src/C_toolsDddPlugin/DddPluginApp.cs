using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

[assembly: ExtensionApplication(typeof(C_toolsDddPlugin.DddPluginApp))]
[assembly: CommandClass(typeof(C_toolsDddPlugin.DddCommands))]

namespace C_toolsDddPlugin;

public class DddPluginApp : CadPluginAppBase
{
    private sealed class InitFlag { public bool Value; }
    private static readonly ConditionalWeakTable<Database, InitFlag> s_initializedDbs = new();

    protected override string PluginLogPrefix => C_toolsCommandIds.Ddd.Main;

    protected override void OnInitializeCore()
    {
        C_toolsPaths.EnsureFolders();
        UserConfigurationStore.EnsureInitialFileIfMissing();
        AcAp.DocumentManager.DocumentCreated += OnDocumentCreated;
        AcAp.DocumentManager.DocumentActivated += OnDocumentActivated;
    }

    protected override void OnFirstIdleCore()
    {
        TryApplyConfiguredStylesOnce(AcAp.DocumentManager.MdiActiveDocument, "首次 Idle");
    }

    protected override void OnTerminateCore()
    {
        AcAp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        AcAp.DocumentManager.DocumentActivated -= OnDocumentActivated;
        DddCommands.CloseDddPanelIfAny();
    }

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
    {
        TryApplyConfiguredStylesOnce(e.Document, "DocumentCreated");
    }

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
    {
        TryApplyConfiguredStylesOnce(e.Document, "DocumentActivated");
    }

    private static void TryApplyConfiguredStylesOnce(Document? doc, string reason)
    {
        if (doc == null)
            return;

        InitFlag flag;
        try
        {
            flag = s_initializedDbs.GetOrCreateValue(doc.Database);
        }
        catch
        {
            return;
        }

        if (flag.Value)
            return;
        flag.Value = true;

        try
        {
            ApplyConfiguredMLeaderStyle(doc);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{reason}：应用 Configuration 默认多重引线样式", ex);
        }
    }

    private static void ApplyConfiguredMLeaderStyle(Document doc)
    {
        var configuredStyleName = UserConfigurationStore.TryGetMLeaderStyleName();
        if (configuredStyleName == null || string.IsNullOrWhiteSpace(configuredStyleName))
            return;
        var styleName = configuredStyleName.Trim();

        using (doc.LockDocument())
        {
            if (!MLeaderStyleHelper.TryGetMLeaderStyleObjectId(doc.Database, styleName, out var styleId) || styleId.IsNull)
                return;

            var currentStyleName = MLeaderStyleHelper.TryGetCurrentStyleName(doc);
            if (string.Equals(currentStyleName, styleName, System.StringComparison.OrdinalIgnoreCase))
                return;

            _ = MLeaderStyleHelper.TrySetCurrentStyleName(doc, styleName);
        }
    }
}
