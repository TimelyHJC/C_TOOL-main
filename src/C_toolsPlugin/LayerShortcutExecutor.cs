using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// 按别名读 JSON → <see cref="LayerApplyService">：
/// <c>Clayer</c> + 有预选时改实体图层。
/// <c>c_tools_layer_shortcuts.lsp</c> 中 <c>c:A1</c> 等将别名写入 <c>USERS1</c> 后调用 <c>F_Layer</c> → <see cref="RunByAlias"/>。
/// </summary>
internal static class LayerShortcutExecutor
{
    private sealed class PendingHatchState
    {
        private readonly ConcurrentQueue<ObjectId> _appendedHatchIds = new();

        internal PendingHatchState(string targetLayer)
        {
            TargetLayer = targetLayer;
        }

        internal string TargetLayer { get; }
        internal int? ColorIndex { get; init; }

        internal void TrackHatch(ObjectId hatchId)
        {
            if (!hatchId.IsNull && !hatchId.IsErased)
                _appendedHatchIds.Enqueue(hatchId);
        }

        internal IReadOnlyList<ObjectId> DrainTrackedHatchIds()
        {
            var ids = new List<ObjectId>();
            var seen = new HashSet<ObjectId>();

            while (_appendedHatchIds.TryDequeue(out var id))
            {
                if (id.IsNull || id.IsErased)
                    continue;
                if (seen.Add(id))
                    ids.Add(id);
            }

            return ids;
        }
    }

    // 使用线程安全的字典，支持多文档并发访问
    private static readonly ConcurrentDictionary<string, PendingHatchState> _pendingHatchStates = new();

    // 记录已订阅事件的文档，防止重复订阅
    private static readonly ConcurrentDictionary<string, Document> _subscribedDocuments = new();

    // 存储清理任务的取消令牌，用于文档关闭时取消
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cleanupTokens = new();

    // 安全网：定时清理过期的待定状态（5分钟超时）
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromMinutes(5);

    // AutoCAD 命令与系统变量常量
    private static class AcadCommands
    {
        public const string Hatch = "HATCH";
        public const string HatchUnderscore = CommandNames.Hatch;
        public const string DimAligned = CommandNames.DimAligned;
    }

    /// <summary>
    /// 获取并移除指定文档的待处理 Hatch 图层（供 HatchLayerCommand 使用）
    /// </summary>
    internal static string? GetAndRemovePendingHatchLayer(Document doc)
    {
        var docKey = GetDocumentKey(doc);
        if (!_pendingHatchStates.TryRemove(docKey, out var state))
            return null;
        return state.TargetLayer;
    }

    /// <summary>
    /// 生成稳定的文档标识键（使用数据库句柄，跨会话稳定）
    /// </summary>
    private static string GetDocumentKey(Document doc)
    {
        return $"{doc.Name ?? "unknown"}_{doc.Database.GetHashCode()}";
    }

    internal static void RunByAlias(string aliasKey)
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var na = CadPgpMerge.NormalizeAlias(aliasKey);
        if (na.Length == 0)
        {
            ed.WriteMessage("\nC_TOOL：图层别名无效。");
            return;
        }

        var list = LoadValidShortcuts();
        var chosen = list.FirstOrDefault(x =>
            string.Equals(CadPgpMerge.NormalizeAlias(x.Alias), na, StringComparison.OrdinalIgnoreCase));

        if (chosen == null)
        {
            var available = string.Join(", ", list.Select(x => CadPgpMerge.NormalizeAlias(x.Alias)));
            ed.WriteMessage($"\nC_TOOL：未找到别名「{na}」。可用别名：{available}");
            return;
        }

        ApplyEntry(doc, chosen);
    }

    private static List<LayerShortcutEntry> LoadValidShortcuts()
    {
        return LayerShortcutStore.Load()
            .Where(x => CadPgpMerge.NormalizeAlias(x.Alias).Length > 0 && !string.IsNullOrWhiteSpace(x.LayerName))
            .ToList();
    }

    internal static void ApplyEntry(Document doc, LayerShortcutEntry chosen)
    {
        var ed = doc.Editor;
        var layerName = chosen.LayerName.Trim();
        if (layerName.Length == 0)
        {
            ed.WriteMessage("\nC_TOOL：图层名为空。");
            return;
        }

        var psr = ed.SelectImplied();
        var hasSelection = psr.Status == PromptStatus.OK && psr.Value is { Count: > 0 };

        try
        {
            if (hasSelection)
            {
                ApplyToSelection(doc, psr.Value!, chosen, layerName);
            }
            else
            {
                ApplyWithoutSelection(doc, chosen, layerName);
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ApplyEntry 改层失败", ex);
            ed.WriteMessage($"\nC_TOOL：改层失败: {ex.Message}");
        }
    }

    private static void ApplyToSelection(Document doc, SelectionSet ss, LayerShortcutEntry chosen, string layerName)
    {
        var (changed, skipped) = LayerApplyService.ApplyToSelection(doc, ss, chosen);
        var msg = $"\n当前层已切换为: {layerName}；已将 {changed} 个对象改到该图层。";
        if (skipped > 0)
            msg += $"（{skipped} 个未改：可能位于锁定层或无法作为图元写打开）";
        doc.Editor.WriteMessage(msg);
    }

    private static void ApplyWithoutSelection(Document doc, LayerShortcutEntry chosen, string layerName)
    {
        var ed = doc.Editor;

        if (!string.IsNullOrWhiteSpace(chosen.HatchStyle))
        {
            // 有填充样式时：跟踪本次 HATCH 新建的填充对象，避免结束后全图扫描
            var snap = HatchStyleSnapshot.TryParseJson(chosen.HatchStyle) ?? HatchStyleSnapshot.Defaults();
            StartHatchWithFixLayer(doc, layerName, snap.PatternName, snap.Scale, snap.AngleDegrees, chosen.ColorIndex);
        }
        else
        {
            // 无填充时：正常切层
            LayerApplyService.SetCurrentLayerOnly(doc, chosen);
            ed.WriteMessage($"\nC_TOOL：当前层已切换为: {layerName}（未选择对象，仅切当前层）。");

            if (chosen.RunDimensionWhenNoSelection)
            {
                ed.WriteMessage("\nC_TOOL：已启动对齐标注（DIMALIGNED），请按命令行提示选择。");
                doc.SendStringToExecute(AcadCommands.DimAligned + "\n", true, false, false);
            }
        }
    }

    /// <summary>
    /// 启动 HATCH 命令，并通过事件只跟踪本次新建的 Hatch 对象
    /// 逻辑：设置HP*变量 -> 切层 -> HATCH -> 事件记录新建 Hatch -> 命令结束后只修正这些对象
    /// </summary>
    private static void StartHatchWithFixLayer(
        Document doc,
        string layerName,
        string pattern,
        double scale,
        double angleDeg,
        int? colorIndex)
    {
        var ed = doc.Editor;
        var docKey = GetDocumentKey(doc);

        // 防御性清理：先移除可能存在的残留订阅和状态
        CleanupDocumentState(doc, docKey);

        try
        {
            // 步骤1：设置 HP* 系统变量
            HatchCommandSystemVariableService.Apply(pattern, scale, angleDeg);

            // 步骤2：确保图层存在并设置为当前层（数据库级别）
            LayerApplyService.SetCurrentLayerOnly(doc, new LayerShortcutEntry { LayerName = layerName });

            // 步骤3：注册数据库/命令事件并存储状态（支持多文档）
            _pendingHatchStates[docKey] = new PendingHatchState(layerName)
            {
                ColorIndex = colorIndex is >= 1 and <= 255 ? colorIndex : null
            };
            _subscribedDocuments[docKey] = doc;
            doc.Database.ObjectAppended += OnDatabaseObjectAppended;
            doc.CommandEnded += OnHatchCommandEnded;
            doc.CommandCancelled += OnHatchCommandCancelledOrFailed;
            doc.CommandFailed += OnHatchCommandCancelledOrFailed;

            // 步骤4：启动安全网定时器（5分钟后自动清理）
            ScheduleCleanup(docKey, doc);

            // 步骤5：启动 HATCH（用户交互完成后会触发事件）
            doc.SendStringToExecute(AcadCommands.HatchUnderscore + "\n", true, false, false);
        }
        catch (System.Exception ex)
        {
            // 发生异常时立即清理，防止事件泄漏
            CleanupDocumentState(doc, docKey);
            C_toolsDiagnostics.LogNonFatal("启动 Hatch 失败", ex);
            ed.WriteMessage($"\nC_TOOL：启动填充失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 清理指定文档的所有状态（防御性清理，确保无残留）
    /// </summary>
    private static void CleanupDocumentState(Document doc, string docKey)
    {
        // 移除待定状态
        _pendingHatchStates.TryRemove(docKey, out _);

        // 如果之前订阅了事件，取消订阅
        if (_subscribedDocuments.TryRemove(docKey, out _))
        {
            try
            {
                doc.Database.ObjectAppended -= OnDatabaseObjectAppended;
                doc.CommandEnded -= OnHatchCommandEnded;
                doc.CommandCancelled -= OnHatchCommandCancelledOrFailed;
                doc.CommandFailed -= OnHatchCommandCancelledOrFailed;
            }
            catch
            {
                // 文档可能已关闭，忽略异常
            }
        }

        // 取消并移除清理任务的取消令牌
        if (_cleanupTokens.TryRemove(docKey, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // 忽略取消/释放时的异常
            }
        }
    }

    /// <summary>
    /// 安排安全网清理（防止事件因异常未被触发而泄漏）
    /// </summary>
    private static void ScheduleCleanup(string docKey, Document doc)
    {
        // 创建新的取消令牌源，并存储到字典中
        var cts = new CancellationTokenSource();
        _cleanupTokens[docKey] = cts;

        Task.Delay(_cleanupTimeout, cts.Token).ContinueWith(t =>
        {
            // 如果任务被取消，则不执行清理
            if (t.IsCanceled)
                return;

            try
            {
                // 检查是否还有残留状态
                if (_pendingHatchStates.ContainsKey(docKey))
                {
                    C_toolsDiagnostics.LogNonFatal($"[安全网] 清理残留状态: {docKey}", null);
                    CleanupDocumentState(doc, docKey);
                }
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("安全网清理失败", ex);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// HATCH 运行期间记录新建的 Hatch 对象，避免命令完成后遍历整个空间。
    /// </summary>
    private static void OnDatabaseObjectAppended(object? sender, ObjectEventArgs e)
    {
        if (sender is not Database db || e.DBObject is not Hatch hatch)
            return;

        foreach (var pair in _subscribedDocuments)
        {
            if (!ReferenceEquals(pair.Value.Database, db))
                continue;

            if (_pendingHatchStates.TryGetValue(pair.Key, out var state))
                state.TrackHatch(hatch.ObjectId);
            return;
        }
    }

    /// <summary>
    /// HATCH 命令结束时触发，只修复本次新建的填充图层
    /// </summary>
    private static void OnHatchCommandEnded(object? sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, AcadCommands.Hatch, StringComparison.OrdinalIgnoreCase))
            return;

        // 获取当前文档（从 sender 获取更可靠）
        var doc = sender as Document ?? AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var docKey = GetDocumentKey(doc);

        // 原子性地获取并移除状态
        if (!_pendingHatchStates.TryRemove(docKey, out var pendingState))
            return;

        // 从订阅记录中移除
        _subscribedDocuments.TryRemove(docKey, out _);

        // 取消并释放清理任务的令牌（命令已正常完成，不需要安全网）
        if (_cleanupTokens.TryRemove(docKey, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        // 取消订阅（状态已移除）
        try
        {
            doc.Database.ObjectAppended -= OnDatabaseObjectAppended;
            doc.CommandEnded -= OnHatchCommandEnded;
            doc.CommandCancelled -= OnHatchCommandCancelledOrFailed;
            doc.CommandFailed -= OnHatchCommandCancelledOrFailed;
        }
        catch
        {
            // 文档可能已关闭，忽略
        }

        if (string.IsNullOrEmpty(pendingState.TargetLayer))
            return;

        try
        {
            // 只修复本次 HATCH 新建的填充对象，避免扫整张图
            var fixedCount = FixTrackedHatchLayers(doc, pendingState);
            if (fixedCount > 0)
            {
                doc.Editor.WriteMessage($"\nC_TOOL：已将 {fixedCount} 个填充对象改到图层 [{pendingState.TargetLayer}]。");
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("修复填充图层失败", ex);
        }
    }

    /// <summary>
    /// HATCH 被取消或失败时，只做清理，不再继续修层。
    /// </summary>
    private static void OnHatchCommandCancelledOrFailed(object? sender, CommandEventArgs e)
    {
        if (!string.Equals(e.GlobalCommandName, AcadCommands.Hatch, StringComparison.OrdinalIgnoreCase))
            return;

        var doc = sender as Document ?? AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        CleanupDocumentState(doc, GetDocumentKey(doc));
    }

    /// <summary>
    /// 仅修改本次 HATCH 新建的填充对象图层。
    /// </summary>
    private static int FixTrackedHatchLayers(Document doc, PendingHatchState pendingState)
    {
        var trackedIds = pendingState.DrainTrackedHatchIds();
        if (trackedIds.Count == 0)
            return 0;

        return CadDatabaseScope.Write(
            doc,
            (_, tr) =>
            {
                var fixedCount = 0;
                foreach (var id in trackedIds)
                {
                    if (id.IsNull || id.IsErased)
                        continue;

                    try
                    {
                        if (!CadDatabaseScope.TryOpenAs<Hatch>(tr, id, OpenMode.ForRead, out var hatch) ||
                            hatch == null)
                        {
                            continue;
                        }

                        var layerMatches = string.Equals(
                            hatch.Layer,
                            pendingState.TargetLayer,
                            StringComparison.OrdinalIgnoreCase);
                        var colorMatches = HasExpectedColor(hatch, pendingState.ColorIndex);
                        if (layerMatches && colorMatches)
                            continue;

                        hatch.UpgradeOpen();
                        if (!layerMatches)
                            hatch.Layer = pendingState.TargetLayer;

                        ApplyColor(hatch, pendingState.ColorIndex);
                        fixedCount++;
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("修复新建填充图层失败", ex);
                    }
                }

                return fixedCount;
            },
            requireDocumentLock: true);
    }

    private static void ApplyColor(Entity entity, int? colorIndex)
    {
        if (colorIndex is >= 1 and <= 255)
        {
            entity.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex.Value);
            return;
        }

        entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
    }

    private static bool HasExpectedColor(Entity entity, int? colorIndex)
    {
        var color = entity.Color;
        if (colorIndex is >= 1 and <= 255)
            return color.ColorMethod == ColorMethod.ByAci && color.ColorIndex == colorIndex.Value;

        return color.ColorMethod == ColorMethod.ByLayer;
    }
}
