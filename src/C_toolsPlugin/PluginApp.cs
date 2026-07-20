using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using System.IO;
using System.Linq;
using System.Threading;

[assembly: ExtensionApplication(typeof(C_toolsPlugin.PluginApp))]
[assembly: CommandClass(typeof(C_toolsPlugin.Commands))]
[assembly: CommandClass(typeof(C_toolsPlugin.HatchLayerCommand))]

namespace C_toolsPlugin;

public class PluginApp : CadPluginAppBase
{
    private static int s_startupAliasFilesPrepared;
    private static int s_startupPgpReloadPending;
    private static string? s_cachedStartupPgpBlock;

    protected override string PluginLogPrefix => "C_TOOL";

    protected override void OnInitializeCore()
    {
        CtoolRuntimeAssemblyResolver.EnsureInitialized();
        Volatile.Write(ref s_startupAliasFilesPrepared, 0);
        Volatile.Write(ref s_startupPgpReloadPending, 1);
        ColorShortcutService.Enable();
        AcAp.DocumentManager.DocumentCreated += OnDocumentCreated;
        AcAp.DocumentManager.DocumentActivated += OnDocumentActivated;
    }

    private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e) =>
        HandleDocumentEvent(e, "DocumentCreated");

    private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e) =>
        HandleDocumentEvent(e, "DocumentActivated");

    private static void HandleDocumentEvent(DocumentCollectionEventArgs e, string reason)
    {
        TryActivateAliasesInDocument(e.Document, reason);
    }

    private static void TryActivateAliasesInDocument(Document? doc, string reason)
    {
        if (doc == null)
            return;

        CadNativeLayerCommandRepair.TryLoadModules(reason);
        CadNativeLayerCommandRepair.TryRequestRedefine(doc, reason);
        TryReloadLayerLispInDocument(doc, reason);

        if (Volatile.Read(ref s_startupAliasFilesPrepared) == 0)
        {
            return;
        }

        if (TryEnsureStartupPgpAliasBlock(reason))
            Volatile.Write(ref s_startupPgpReloadPending, 1);

        if (Volatile.Read(ref s_startupPgpReloadPending) == 0)
            return;

        TryReloadPgpInDocument(doc, reason);
    }

    private static void TryReloadLayerLispInDocument(Document doc, string reason)
    {
        try
        {
            LayerLispShortcuts.TryLoadInDocument(doc);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{reason}：加载 {LayerLispShortcuts.FileName} 失败", ex);
        }
    }

    private static void TryReloadPgpInDocument(Document doc, string reason)
    {
        try
        {
            var result = CadPgpReload.TryReloadPgp(doc);
            if (result.Ok)
            {
                Interlocked.Exchange(ref s_startupPgpReloadPending, 0);
                return;
            }

            C_toolsDiagnostics.LogNonFatal($"{reason}：启动阶段重载 PGP 失败：{result.Message}", null);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{reason}：启动阶段重载 PGP 失败", ex);
        }
    }

    private static bool TryEnsureStartupPgpAliasBlock(string reason)
    {
        try
        {
            TrySeedStartupCommandAliasesFromInitialFileIfMissing(reason);

            var blockPath = C_toolsPaths.UserSiblingC_toolsAliasesPgpPath;
            var mergedPath = C_toolsPaths.UserAcadPgpPath;

            string? block = null;
            if (File.Exists(blockPath))
            {
                var originalBlock = File.ReadAllText(blockPath, CadPgpMerge.DetectEncoding(blockPath));
                block = CadPgpMerge.BuildSanitizedManagedAliasBlock(originalBlock).Trim();
                if (!string.Equals(originalBlock.Trim(), block, StringComparison.Ordinal))
                    File.WriteAllText(blockPath, block + Environment.NewLine, new System.Text.UTF8Encoding(true));
            }
            else if (File.Exists(mergedPath))
            {
                var mergedContent = File.ReadAllText(mergedPath, CadPgpMerge.DetectEncoding(mergedPath));
                block = CadPgpMerge.BuildAliasBlock(CadPgpMerge.ParseC_toolsBlockAliases(mergedContent)).Trim();
                if (block.Length > 0)
                    File.WriteAllText(blockPath, block + Environment.NewLine, new System.Text.UTF8Encoding(true));
            }

            if (string.IsNullOrWhiteSpace(block))
                return false;

            s_cachedStartupPgpBlock = block;

            if (!ShouldRepairStartupMergedPgp(block!))
                return false;

            var result = CadPgpMerge.ApplyToDiscoveredPgp(null, block!);
            if (!result.Ok)
            {
                C_toolsDiagnostics.LogNonFatal($"{reason}：补写 C_TOOL PGP 管理块失败：{result.Message}", null);
                return false;
            }

            return true;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{reason}：补写 C_TOOL PGP 管理块失败", ex);
            return false;
        }
    }

    private static bool TrySeedStartupCommandAliasesFromInitialFileIfMissing(string reason)
    {
        try
        {
            var blockPath = C_toolsPaths.UserSiblingC_toolsAliasesPgpPath;
            if (File.Exists(blockPath))
                return false;

            var mergedPath = C_toolsPaths.UserAcadPgpPath;
            if (File.Exists(mergedPath))
            {
                var mergedContent = File.ReadAllText(mergedPath, CadPgpMerge.DetectEncoding(mergedPath));
                if (mergedContent.IndexOf(CadPgpMerge.BeginMarker, StringComparison.Ordinal) >= 0 &&
                    mergedContent.IndexOf(CadPgpMerge.EndMarker, StringComparison.Ordinal) >= 0)
                {
                    return false;
                }
            }

            if (!LayerShortcutInitialData.TryLoadCommandAliases(out var aliases, out var sourcePath, out var warnings))
                return false;
            if (aliases.Count == 0)
                return false;

            var skipped = new List<string>();
            var block = CadPgpMerge.BuildAliasBlock(aliases, skipped).Trim();
            if (string.IsNullOrWhiteSpace(block))
                return false;

            var dir = Path.GetDirectoryName(blockPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(blockPath, block + Environment.NewLine, new System.Text.UTF8Encoding(true));

            C_toolsDiagnostics.LogNonFatal(
                $"{reason}：已从 {LayerShortcutInitialData.FileName} 生成默认命令快捷键（{aliases.Count} 条）：{sourcePath}",
                null);
            foreach (var warning in warnings.Concat(skipped))
                C_toolsDiagnostics.LogNonFatal($"{LayerShortcutInitialData.FileName}：{warning}", null);
            return true;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{reason}：从 {LayerShortcutInitialData.FileName} 生成默认命令快捷键失败", ex);
            return false;
        }
    }

    private static bool ShouldRepairStartupMergedPgp(string desiredBlock)
    {
        try
        {
            var mergedPath = C_toolsPaths.UserAcadPgpPath;
            if (!File.Exists(mergedPath))
                return true;

            var content = File.ReadAllText(mergedPath, CadPgpMerge.DetectEncoding(mergedPath));
            if (content.IndexOf(CadPgpMerge.BeginMarker, StringComparison.Ordinal) < 0 ||
                content.IndexOf(CadPgpMerge.EndMarker, StringComparison.Ordinal) < 0)
            {
                return true;
            }

            if (CadPgpMerge.ContainsRetiredLauncherAliasEntries(content))
                return true;

            var currentBlock = s_cachedStartupPgpBlock ?? CadPgpMerge.BuildAliasBlock(CadPgpMerge.ParseC_toolsBlockAliases(content)).Trim();
            return !string.Equals(currentBlock, desiredBlock.Trim(), StringComparison.Ordinal);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("检查合并后的 acad.pgp 是否缺少 C_TOOL 管理块失败", ex);
            return false;
        }
    }

    protected override void OnFirstIdleCore()
    {
        C_toolsPaths.EnsureFolders();
        CadPgpSupportPath.EnsureC_toolsSupportFirst();
        CadNativeLayerCommandRepair.TryLoadModules("首次 Idle");
        UserConfigurationStore.EnsureInitialFileIfMissing();
        CommandDescriptionStore.TryMigrateLegacyFileToUserFolder();

        var layerShortcuts = LayerShortcutStore.Load();
        var validEntries = new List<LayerShortcutEntry>(layerShortcuts.Count);
        foreach (var x in layerShortcuts)
        {
            if (CadPgpMerge.NormalizeAlias(x.Alias).Length > 0 && !string.IsNullOrWhiteSpace(x.LayerName))
                validEntries.Add(x);
        }

        LayerLispShortcuts.EmitFromEntries(validEntries);
        Volatile.Write(ref s_startupAliasFilesPrepared, 1);

        if (TryEnsureStartupPgpAliasBlock("首次 Idle"))
            Volatile.Write(ref s_startupPgpReloadPending, 1);

        var d = AcAp.DocumentManager.MdiActiveDocument;
        if (d != null)
            TryActivateAliasesInDocument(d, "首次 Idle");
    }

    protected override void OnTerminateCore()
    {
        AcAp.DocumentManager.DocumentCreated -= OnDocumentCreated;
        AcAp.DocumentManager.DocumentActivated -= OnDocumentActivated;
        Commands.CloseFloatingPanelIfAny();
        ViewportCommandService.CloseAnnotationScaleWindowIfAny();
        LayerVisibilityToggleService.Terminate();
        ColorShortcutService.Terminate();
    }
}
