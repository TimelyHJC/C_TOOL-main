using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsJson;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// F_XG：按设置切换所选线对象的线型、线型比例、颜色和图层，再次执行可恢复到上一次切换前的状态。
/// </summary>
internal static class DashedLineCommandService
{
    private const string CommandName = PluginCommandIds.DashedLine;
    private const string SettingsKeyword = "S";
    private const string ToggleSnapshotDictionaryKey = "C_TOOL_F_XG_TOGGLE";

    internal static void Run()
    {
        var settings = DashedLineSettingsStore.LoadOrDefault();

        CadCommandContext.ExecuteInActiveDocument($"执行 {CommandName}", (doc, ed) =>
        {
            if (!TryGetSelection(doc, ed, ref settings, out var selectionSet, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\nC_TOOL：{CommandName} 已取消。"
                    : $"\nC_TOOL：{CommandName} 未选择任何对象。");
                return;
            }

            ApplyLinetypeSystemVariables(settings, ed);

            var result = ToggleConfiguredStyle(doc, selectionSet!, settings);
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ed.WriteMessage("\nC_TOOL：" + result.ErrorMessage);
                return;
            }

            if (result.ChangedCount == 0)
            {
                var emptyParts = new List<string>();
                if (result.ResetCount > 0)
                    emptyParts.Add($"已重置 {result.ResetCount} 个对象的线型和比例");
                if (result.UnsupportedCount > 0)
                    emptyParts.Add($"跳过 {result.UnsupportedCount} 个非线/多段线对象");
                if (result.LockedLayerCount > 0)
                    emptyParts.Add($"跳过 {result.LockedLayerCount} 个锁定图层对象");
                if (result.FailedCount > 0)
                    emptyParts.Add($"跳过 {result.FailedCount} 个不可写对象");

                if (emptyParts.Count == 0)
                    ed.WriteMessage($"\nC_TOOL：{CommandName} 没有可处理的线/多段线对象。");
                else
                    ed.WriteMessage($"\nC_TOOL：{CommandName} 未切换对象，{string.Join("，", emptyParts)}。");
                return;
            }

            var parts = new List<string>();
            if (result.AppliedCount > 0)
                parts.Add($"已切换 {result.AppliedCount} 个对象");
            if (result.RestoredCount > 0)
                parts.Add($"已恢复 {result.RestoredCount} 个对象");
            if (result.ResetCount > 0)
                parts.Add($"已重置 {result.ResetCount} 个对象的线型和比例");
            if (result.UnsupportedCount > 0)
                parts.Add($"跳过 {result.UnsupportedCount} 个非线/多段线");
            if (result.LockedLayerCount > 0)
                parts.Add($"跳过 {result.LockedLayerCount} 个锁定层");
            if (result.FailedCount > 0)
                parts.Add($"跳过 {result.FailedCount} 个不可写");

            ed.WriteMessage("\nC_TOOL：" + string.Join("，", parts) + "。");
        });
    }

    private static bool TryGetSelection(
        Document doc,
        Editor ed,
        ref DashedLineSettingsDto settings,
        out SelectionSet? selectionSet,
        out bool cancelled)
    {
        selectionSet = null;
        cancelled = false;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            selectionSet = implied.Value;
            return true;
        }

        while (true)
        {
            var currentSettings = settings;
            var options = new PromptSelectionOptions
            {
                MessageForAdding = GetSelectionPromptMessage()
            };
            options.SetKeywords("[设置(S)]", SettingsKeyword);
            options.KeywordInput += (_, e) =>
            {
                var keyword = NormalizeKeyword(e.Input);
                if (!string.Equals(keyword, SettingsKeyword, StringComparison.OrdinalIgnoreCase))
                    return;

                ShowSettingsDialog(doc, ref currentSettings);
                options.MessageForAdding = GetSelectionPromptMessage();
                e.SetErrorMessage(GetSelectionPromptMessage());
            };

            var result = ed.GetSelection(options);
            settings = currentSettings;
            if (result.Status == PromptStatus.OK && result.Value != null)
            {
                selectionSet = result.Value;
                return true;
            }

            if (result.Status == PromptStatus.Keyword)
                continue;

            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }
    }

    private static string GetSelectionPromptMessage() =>
        "\nC_TOOL：选择线/多段线 [设置(S)]：";

    private static string NormalizeKeyword(string? keyword) =>
        (keyword ?? "").Trim();

    private static void ShowSettingsDialog(Document doc, ref DashedLineSettingsDto settings)
    {
        var ed = doc.Editor;

        try
        {
            var linetypeOptions = LoadLinetypeOptions(doc.Database, settings.LinetypeName);
            var layerOptions = LoadLayerOptions(doc.Database, settings.TargetLayerName);
            var window = new DashedLineSettingsWindow(
                settings,
                linetypeOptions,
                layerOptions,
                ownerWindow => PickLineStyleFromDrawing(doc, ownerWindow));
            var accepted = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);

            if (accepted != true || window.SavedSettings == null)
                return;

            DashedLineSettingsStore.Save(window.SavedSettings);
            settings = window.SavedSettings;
            ApplyLinetypeSystemVariables(settings, ed);
            ed.WriteMessage($"\nC_TOOL：线型设置已更新。{FormatSettingsSummary(settings)}。");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_XG 线型设置（路径参数）", ex);
            ed.WriteMessage($"\nC_TOOL：保存线型设置失败：{ex.Message}");
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_XG 线型设置（路径过长）", ex);
            ed.WriteMessage($"\nC_TOOL：保存线型设置失败：{ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_XG 线型设置（路径格式）", ex);
            ed.WriteMessage($"\nC_TOOL：保存线型设置失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_XG 线型设置（权限）", ex);
            ed.WriteMessage($"\nC_TOOL：保存线型设置失败：{ex.Message}");
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("保存 F_XG 线型设置", ex);
            ed.WriteMessage($"\nC_TOOL：保存线型设置失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_XG 线型设置失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：打开线型设置失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_XG 线型设置失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：打开线型设置失败：{ex.Message}");
        }
    }

    private static DashedLineStylePickResult PickLineStyleFromDrawing(Document doc, System.Windows.Window ownerWindow)
    {
        var ed = doc.Editor;

        try
        {
            using (ed.StartUserInteraction(ownerWindow))
            {
                var options = new PromptEntityOptions("\nC_TOOL：选择要读取线型的线/多段线对象：")
                {
                    AllowNone = true
                };

                var result = ed.GetEntity(options);
                if (result.Status != PromptStatus.OK)
                    return DashedLineStylePickResult.CancelledResult();

                return CadDatabaseScope.Read(
                    doc,
                    (database, tr) =>
                    {
                        if (!CadDatabaseScope.TryOpenAs<Entity>(tr, result.ObjectId, OpenMode.ForRead, out var entity) ||
                            entity == null)
                        {
                            return DashedLineStylePickResult.Failure("所选对象不是线/多段线对象。");
                        }

                        if (entity is not Curve)
                            return DashedLineStylePickResult.Failure("只能选择线/多段线对象。");

                        return DashedLineStylePickResult.Success(
                            ResolvePickLinetypeName(database, tr, entity),
                            NormalizeLinetypeScale(entity.LinetypeScale));
                    },
                    requireDocumentLock: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 拾取线型失败（无效操作）", ex);
            return DashedLineStylePickResult.Failure($"读取线型失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 拾取线型失败（CAD）", ex);
            return DashedLineStylePickResult.Failure($"读取线型失败：{ex.Message}");
        }
    }

    private static string ResolvePickLinetypeName(Database db, Transaction tr, Entity entity)
    {
        var directName = NormalizeLinetypeName(entity.Linetype);
        if (!string.Equals(directName, "BYLAYER", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directName, "BYBLOCK", StringComparison.OrdinalIgnoreCase))
        {
            return directName;
        }

        var linetypeId = directName.Equals("BYLAYER", StringComparison.OrdinalIgnoreCase)
            ? ResolveLayerLinetypeId(tr, entity)
            : db.ContinuousLinetype;

        if (!linetypeId.IsNull &&
            CadDatabaseScope.TryOpenAs<LinetypeTableRecord>(tr, linetypeId, OpenMode.ForRead, out var record) &&
            record != null)
        {
            return NormalizeLinetypeName(record.Name);
        }

        return LinetypeNames.Continuous;
    }

    private static ObjectId ResolveLayerLinetypeId(Transaction tr, Entity entity)
    {
        if (entity.LayerId.IsNull)
            return ObjectId.Null;

        return CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, entity.LayerId, OpenMode.ForRead, out var layer) &&
               layer != null
            ? layer.LinetypeObjectId
            : ObjectId.Null;
    }

    private static string FormatSettingsSummary(DashedLineSettingsDto settings)
    {
        var scaleText = settings.LinetypeScale.ToString("0.###", CultureInfo.InvariantCulture);
        var globalScaleText = settings.GlobalLinetypeScale.ToString("0.####", CultureInfo.InvariantCulture);
        var paperSpaceText = settings.UsePaperSpaceUnitsForScaling ? "使用" : "不使用";
        return $"线型 [{settings.LinetypeName}]，比例 [{scaleText}]，全局比例 [{globalScaleText}]，图纸空间单位 [{paperSpaceText}]，颜色 [{FormatColorSummary(settings)}]，图层 [{FormatLayerSummary(settings)}]";
    }

    private static void ApplyLinetypeSystemVariables(DashedLineSettingsDto settings, Editor ed)
    {
        var failures = new List<string>();
        var psLtScaleValue = settings.UsePaperSpaceUnitsForScaling ? (short)1 : (short)0;

        if (!CadSystemVariableService.TrySetValue(SystemVariableNames.PsLtScale, psLtScaleValue, out var psLtScaleError))
            failures.Add($"{SystemVariableNames.PsLtScale}: {psLtScaleError}");

        if (!CadSystemVariableService.TrySetValue(SystemVariableNames.LtScale, settings.GlobalLinetypeScale, out var ltScaleError))
            failures.Add($"{SystemVariableNames.LtScale}: {ltScaleError}");

        if (failures.Count > 0)
            ed.WriteMessage("\nC_TOOL：线型系统变量设置失败：" + string.Join("；", failures));
    }

    private static string FormatColorSummary(DashedLineSettingsDto settings)
    {
        if (string.Equals(settings.ColorMode, DashedLineColorModes.ByLayer, StringComparison.Ordinal))
            return "ByLayer";
        if (string.Equals(settings.ColorMode, DashedLineColorModes.ByBlock, StringComparison.Ordinal))
            return "ByBlock";
        if (string.Equals(settings.ColorMode, DashedLineColorModes.Aci, StringComparison.Ordinal))
            return settings.ColorIndex?.ToString(CultureInfo.InvariantCulture) ?? "7";
        return "保持原颜色";
    }

    private static string FormatLayerSummary(DashedLineSettingsDto settings)
    {
        var layerName = (settings.TargetLayerName ?? "").Trim();
        return layerName.Length == 0 ? "保持原图层" : layerName;
    }

    private static IReadOnlyList<string> LoadLinetypeOptions(Database db, string fallbackLinetypeName)
    {
        var names = new List<string> { LinetypeNames.Dashed, LinetypeNames.Continuous };

        try
        {
            names.AddRange(
                CadDatabaseScope.Read(
                    db,
                    (database, tr) =>
                    {
                        var linetypeNames = new List<string>();
                        var linetypeTable = CadDatabaseScope.OpenAs<LinetypeTable>(tr, database.LinetypeTableId, OpenMode.ForRead);
                        foreach (ObjectId linetypeId in linetypeTable)
                        {
                            if (!CadDatabaseScope.TryOpenAs<LinetypeTableRecord>(tr, linetypeId, OpenMode.ForRead, out var record) ||
                                record == null)
                            {
                                continue;
                            }

                            var name = (record.Name ?? "").Trim();
                            if (name.Length > 0)
                                linetypeNames.Add(name);
                        }

                        return linetypeNames;
                    }));
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 读取线型下拉数据失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 读取线型下拉数据失败（CAD）", ex);
        }

        var fallback = (fallbackLinetypeName ?? "").Trim();
        if (fallback.Length > 0)
            names.Add(fallback);

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> LoadLayerOptions(Database db, string fallbackLayerName)
    {
        var names = new List<string>();

        try
        {
            names.AddRange(
                CadDatabaseScope.Read(
                    db,
                    (database, tr) =>
                    {
                        var layerNames = new List<string>();
                        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, database.LayerTableId, OpenMode.ForRead);
                        foreach (ObjectId layerId in layerTable)
                        {
                            if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead, out var record) ||
                                record == null)
                            {
                                continue;
                            }

                            var name = (record.Name ?? "").Trim();
                            if (name.Length > 0)
                                layerNames.Add(name);
                        }

                        return layerNames;
                    }));
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 读取图层下拉数据失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 读取图层下拉数据失败（CAD）", ex);
        }

        var fallback = (fallbackLayerName ?? "").Trim();
        if (fallback.Length > 0)
            names.Add(fallback);

        if (names.Count == 0)
            names.Add("0");

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ToggleResult ToggleConfiguredStyle(Document doc, SelectionSet selectionSet, DashedLineSettingsDto settings)
    {
        return CadDatabaseScope.Write(doc, (database, tr) =>
        {
            var appliedCount = 0;
            var restoredCount = 0;
            var resetCount = 0;
            var unsupportedCount = 0;
            var lockedLayerCount = 0;
            var failedCount = 0;

            if (!LayerStyleHelper.TryResolveLinetypeId(tr, database, settings.LinetypeName, out var targetLinetypeId) ||
                targetLinetypeId.IsNull)
            {
                return ToggleResult.WithError(
                    $"{CommandName} 未找到线型 {settings.LinetypeName}，请确认 {CadResourceFileNames.AcadLin}。");
            }

            ObjectId targetLayerId = ObjectId.Null;
            var hasTargetLayer = !string.IsNullOrWhiteSpace(settings.TargetLayerName);
            if (hasTargetLayer)
            {
                try
                {
                    targetLayerId = EnsureLayer(tr, database, settings.TargetLayerName);
                }
                catch (ArgumentException ex)
                {
                    C_toolsDiagnostics.LogNonFatal("F_XG 创建目标图层失败（参数错误）", ex);
                    return ToggleResult.WithError($"目标图层无效：{ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    C_toolsDiagnostics.LogNonFatal("F_XG 创建目标图层失败（无效操作）", ex);
                    return ToggleResult.WithError($"创建目标图层失败：{ex.Message}");
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    C_toolsDiagnostics.LogNonFatal("F_XG 创建目标图层失败（CAD）", ex);
                    return ToggleResult.WithError($"创建目标图层失败：{ex.Message}");
                }
            }

            var settingsSignature = BuildSettingsSignature(settings);

            foreach (SelectedObject? item in selectionSet)
            {
                if (item == null || item.ObjectId.IsNull)
                    continue;

                try
                {
                    if (!CadDatabaseScope.TryOpenAs<Entity>(tr, item.ObjectId, OpenMode.ForRead, out var entity) ||
                        entity == null)
                    {
                        unsupportedCount++;
                        continue;
                    }

                    if (entity is not Curve)
                    {
                        unsupportedCount++;
                        continue;
                    }

                    if (CadDatabaseScope.IsOnLockedLayer(tr, entity))
                    {
                        lockedLayerCount++;
                        continue;
                    }

                    var hasSnapshot = TryReadToggleSnapshot(tr, entity, out var snapshot);
                    var matchesTarget = IsMatch(entity, targetLinetypeId, settings, targetLayerId, hasTargetLayer);
                    if (matchesTarget &&
                        hasSnapshot &&
                        snapshot != null &&
                        string.Equals(snapshot.SettingsSignature, settingsSignature, StringComparison.Ordinal))
                    {
                        if (!entity.IsWriteEnabled)
                            entity.UpgradeOpen();

                        RestoreSnapshot(tr, database, entity, snapshot);
                        RemoveToggleSnapshot(tr, entity);
                        restoredCount++;
                        continue;
                    }

                    if (matchesTarget)
                    {
                        if (!entity.IsWriteEnabled)
                            entity.UpgradeOpen();

                        ResetLinetypeOverrides(entity);
                        if (hasSnapshot)
                            RemoveToggleSnapshot(tr, entity);
                        resetCount++;
                        continue;
                    }

                    if (!entity.IsWriteEnabled)
                        entity.UpgradeOpen();

                    var baseSnapshot = CaptureSnapshot(entity);

                    ApplyConfiguredStyle(entity, targetLinetypeId, settings, targetLayerId, hasTargetLayer);
                    baseSnapshot.SettingsSignature = settingsSignature;
                    WriteToggleSnapshot(tr, entity, baseSnapshot);
                    appliedCount++;
                }
                catch (InvalidOperationException ex)
                {
                    failedCount++;
                    C_toolsDiagnostics.LogNonFatal($"{CommandName} 切换对象样式失败（无效操作）", ex);
                }
                catch (ArgumentException ex)
                {
                    failedCount++;
                    C_toolsDiagnostics.LogNonFatal($"{CommandName} 切换对象样式失败（参数错误）", ex);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    failedCount++;
                    C_toolsDiagnostics.LogNonFatal($"{CommandName} 切换对象样式失败（CAD）", ex);
                }
            }

            return new ToggleResult(
                appliedCount,
                restoredCount,
                resetCount,
                unsupportedCount,
                lockedLayerCount,
                failedCount,
                null);
        }, requireDocumentLock: true);
    }

    private static string BuildSettingsSignature(DashedLineSettingsDto settings)
    {
        return string.Join(
            "|",
            (settings.LinetypeName ?? "").Trim().ToUpperInvariant(),
            settings.LinetypeScale.ToString("0.###############", CultureInfo.InvariantCulture),
            (settings.ColorMode ?? "").Trim().ToUpperInvariant(),
            settings.ColorIndex?.ToString(CultureInfo.InvariantCulture) ?? "",
            (settings.TargetLayerName ?? "").Trim().ToUpperInvariant());
    }

    private static ObjectId EnsureLayer(Transaction tr, Database db, string layerName)
    {
        var trimmedLayerName = (layerName ?? "").Trim();
        if (trimmedLayerName.Length == 0)
            return ObjectId.Null;

        var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(trimmedLayerName))
            return layerTable[trimmedLayerName];

        layerTable.UpgradeOpen();
        var layerRecord = new LayerTableRecord
        {
            Name = trimmedLayerName
        };
        layerTable.Add(layerRecord);
        tr.AddNewlyCreatedDBObject(layerRecord, true);
        return layerRecord.ObjectId;
    }

    private static bool IsMatch(
        Entity entity,
        ObjectId targetLinetypeId,
        DashedLineSettingsDto settings,
        ObjectId targetLayerId,
        bool hasTargetLayer)
    {
        if (entity.LinetypeId != targetLinetypeId)
            return false;

        if (!AreClose(entity.LinetypeScale, settings.LinetypeScale))
            return false;

        if (!ColorMatches(entity, settings))
            return false;

        return !hasTargetLayer || entity.LayerId == targetLayerId;
    }

    private static bool AreClose(double left, double right) =>
        Math.Abs(left - right) <= 1e-9;

    private static bool ColorMatches(Entity entity, DashedLineSettingsDto settings)
    {
        if (string.Equals(settings.ColorMode, DashedLineColorModes.ByLayer, StringComparison.Ordinal))
            return entity.ColorIndex == 256;

        if (string.Equals(settings.ColorMode, DashedLineColorModes.ByBlock, StringComparison.Ordinal))
            return entity.ColorIndex == 0;

        if (string.Equals(settings.ColorMode, DashedLineColorModes.Aci, StringComparison.Ordinal))
            return entity.ColorIndex == settings.ColorIndex.GetValueOrDefault();

        return true;
    }

    private static void ApplyConfiguredStyle(
        Entity entity,
        ObjectId targetLinetypeId,
        DashedLineSettingsDto settings,
        ObjectId targetLayerId,
        bool hasTargetLayer)
    {
        entity.LinetypeId = targetLinetypeId;
        entity.LinetypeScale = settings.LinetypeScale;
        ApplyColor(entity, settings);
        if (hasTargetLayer)
            entity.LayerId = targetLayerId;
    }

    private static void ResetLinetypeOverrides(Entity entity)
    {
        entity.Linetype = "ByLayer";
        entity.LinetypeScale = 1.0;
    }

    private static void ApplyColor(Entity entity, DashedLineSettingsDto settings)
    {
        if (string.Equals(settings.ColorMode, DashedLineColorModes.ByLayer, StringComparison.Ordinal))
        {
            entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            return;
        }

        if (string.Equals(settings.ColorMode, DashedLineColorModes.ByBlock, StringComparison.Ordinal))
        {
            entity.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            return;
        }

        if (string.Equals(settings.ColorMode, DashedLineColorModes.Aci, StringComparison.Ordinal) &&
            settings.ColorIndex is >= 1 and <= 255)
        {
            entity.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)settings.ColorIndex.Value);
        }
    }

    private static DashedLineToggleSnapshotDto CaptureSnapshot(Entity entity)
    {
        return new DashedLineToggleSnapshotDto
        {
            LinetypeName = NormalizeLinetypeName(entity.Linetype),
            LinetypeScale = NormalizeLinetypeScale(entity.LinetypeScale),
            LayerName = NormalizeLayerName(entity.Layer),
            ColorMode = ResolveStoredColorMode(entity.Color),
            ColorIndex = ResolveStoredColorIndex(entity.Color),
            Red = entity.Color.Red,
            Green = entity.Color.Green,
            Blue = entity.Color.Blue
        };
    }

    private static void RestoreSnapshot(
        Transaction tr,
        Database db,
        Entity entity,
        DashedLineToggleSnapshotDto snapshot)
    {
        var layerId = EnsureLayer(tr, db, snapshot.LayerName);
        if (!layerId.IsNull)
            entity.LayerId = layerId;

        entity.LinetypeScale = NormalizeLinetypeScale(snapshot.LinetypeScale);
        SetEntityLinetype(tr, db, entity, snapshot.LinetypeName);
        entity.Color = BuildStoredColor(snapshot);
    }

    private static void SetEntityLinetype(Transaction tr, Database db, Entity entity, string linetypeName)
    {
        var normalizedName = NormalizeLinetypeName(linetypeName);
        if (string.Equals(normalizedName, "BYLAYER", StringComparison.OrdinalIgnoreCase))
        {
            entity.Linetype = "ByLayer";
            return;
        }

        if (string.Equals(normalizedName, "BYBLOCK", StringComparison.OrdinalIgnoreCase))
        {
            entity.Linetype = "ByBlock";
            return;
        }

        if (!LayerStyleHelper.TryResolveLinetypeId(tr, db, normalizedName, out var linetypeId) ||
            linetypeId.IsNull)
        {
            entity.Linetype = LinetypeNames.Continuous;
            return;
        }

        entity.LinetypeId = linetypeId;
    }

    private static string NormalizeLinetypeName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? LinetypeNames.Continuous : trimmed;
    }

    private static string NormalizeLayerName(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static double NormalizeLinetypeScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return 1.0;

        return value;
    }

    private static string ResolveStoredColorMode(Color color)
    {
        try
        {
            if (color.ColorMethod == ColorMethod.ByLayer)
                return DashedLineColorModes.ByLayer;
            if (color.ColorMethod == ColorMethod.ByBlock)
                return DashedLineColorModes.ByBlock;
            if (color.ColorMethod == ColorMethod.ByAci)
                return DashedLineColorModes.Aci;
            return "Rgb";
        }
        catch
        {
            return DashedLineColorModes.ByLayer;
        }
    }

    private static int? ResolveStoredColorIndex(Color color)
    {
        try
        {
            return color.ColorMethod == ColorMethod.ByAci
                ? color.ColorIndex
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Color BuildStoredColor(DashedLineToggleSnapshotDto snapshot)
    {
        try
        {
            if (string.Equals(snapshot.ColorMode, DashedLineColorModes.ByLayer, StringComparison.OrdinalIgnoreCase))
                return Color.FromColorIndex(ColorMethod.ByLayer, 256);

            if (string.Equals(snapshot.ColorMode, DashedLineColorModes.ByBlock, StringComparison.OrdinalIgnoreCase))
                return Color.FromColorIndex(ColorMethod.ByBlock, 0);

            if (string.Equals(snapshot.ColorMode, DashedLineColorModes.Aci, StringComparison.OrdinalIgnoreCase) &&
                snapshot.ColorIndex is >= 0 and <= 256)
            {
                return Color.FromColorIndex(ColorMethod.ByAci, (short)snapshot.ColorIndex.Value);
            }

            return Color.FromRgb(snapshot.Red, snapshot.Green, snapshot.Blue);
        }
        catch
        {
            return Color.FromColorIndex(ColorMethod.ByLayer, 256);
        }
    }

    private static bool TryReadToggleSnapshot(
        Transaction tr,
        Entity entity,
        out DashedLineToggleSnapshotDto? snapshot)
    {
        snapshot = null;

        if (entity.ExtensionDictionary.IsNull)
            return false;

        if (!CadDatabaseScope.TryOpenAs<DBDictionary>(tr, entity.ExtensionDictionary, OpenMode.ForRead, out var dictionary) ||
            dictionary == null ||
            !dictionary.Contains(ToggleSnapshotDictionaryKey))
        {
            return false;
        }

        if (!CadDatabaseScope.TryOpenAs<Xrecord>(tr, dictionary.GetAt(ToggleSnapshotDictionaryKey), OpenMode.ForRead, out var xrecord) ||
            xrecord == null ||
            xrecord.Data == null)
        {
            return false;
        }

        try
        {
            foreach (TypedValue value in xrecord.Data)
            {
                if (value.Value is not string json || string.IsNullOrWhiteSpace(json))
                    continue;

                snapshot = JsonSerializer.Deserialize<DashedLineToggleSnapshotDto>(json, JsonOptionsCache.ReadRelaxed);
                if (snapshot != null)
                    return true;
            }
        }
        catch (JsonException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 读取切换快照失败（JSON）", ex);
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_XG 读取切换快照失败（不支持）", ex);
        }

        snapshot = null;
        return false;
    }

    private static void WriteToggleSnapshot(
        Transaction tr,
        Entity entity,
        DashedLineToggleSnapshotDto snapshot)
    {
        if (entity.ExtensionDictionary.IsNull)
            entity.CreateExtensionDictionary();

        var dictionary = CadDatabaseScope.OpenAs<DBDictionary>(tr, entity.ExtensionDictionary, OpenMode.ForWrite);
        var json = JsonSerializer.Serialize(snapshot, JsonOptionsCache.WriteCompact);
        var buffer = new ResultBuffer(new TypedValue((int)DxfCode.Text, json));

        if (dictionary.Contains(ToggleSnapshotDictionaryKey))
        {
            var existingRecord = CadDatabaseScope.OpenAs<Xrecord>(tr, dictionary.GetAt(ToggleSnapshotDictionaryKey), OpenMode.ForWrite);
            existingRecord.Data = buffer;
            return;
        }

        var xrecord = new Xrecord
        {
            Data = buffer
        };
        dictionary.SetAt(ToggleSnapshotDictionaryKey, xrecord);
        tr.AddNewlyCreatedDBObject(xrecord, true);
    }

    private static void RemoveToggleSnapshot(Transaction tr, Entity entity)
    {
        if (entity.ExtensionDictionary.IsNull)
            return;

        if (!CadDatabaseScope.TryOpenAs<DBDictionary>(tr, entity.ExtensionDictionary, OpenMode.ForWrite, out var dictionary) ||
            dictionary == null ||
            !dictionary.Contains(ToggleSnapshotDictionaryKey))
        {
            return;
        }

        var recordId = dictionary.GetAt(ToggleSnapshotDictionaryKey);
        dictionary.Remove(ToggleSnapshotDictionaryKey);

        if (CadDatabaseScope.TryOpenAs<DBObject>(tr, recordId, OpenMode.ForWrite, out var dbObject) &&
            dbObject != null &&
            !dbObject.IsErased)
        {
            dbObject.Erase();
        }
    }

    private readonly struct ToggleResult
    {
        internal ToggleResult(
            int appliedCount,
            int restoredCount,
            int resetCount,
            int unsupportedCount,
            int lockedLayerCount,
            int failedCount,
            string? errorMessage)
        {
            AppliedCount = appliedCount;
            RestoredCount = restoredCount;
            ResetCount = resetCount;
            UnsupportedCount = unsupportedCount;
            LockedLayerCount = lockedLayerCount;
            FailedCount = failedCount;
            ErrorMessage = errorMessage;
        }

        internal int AppliedCount { get; }

        internal int RestoredCount { get; }

        internal int ChangedCount => AppliedCount + RestoredCount + ResetCount;

        internal int ResetCount { get; }

        internal int UnsupportedCount { get; }

        internal int LockedLayerCount { get; }

        internal int FailedCount { get; }

        internal string? ErrorMessage { get; }

        internal static ToggleResult WithError(string message) =>
            new(0, 0, 0, 0, 0, 0, message);
    }

    private sealed class DashedLineToggleSnapshotDto
    {
        public string SettingsSignature { get; set; } = "";

        public string LinetypeName { get; set; } = LinetypeNames.Continuous;

        public double LinetypeScale { get; set; } = 1.0;

        public string LayerName { get; set; } = "0";

        public string ColorMode { get; set; } = DashedLineColorModes.ByLayer;

        public int? ColorIndex { get; set; }

        public byte Red { get; set; }

        public byte Green { get; set; }

        public byte Blue { get; set; }
    }
}
