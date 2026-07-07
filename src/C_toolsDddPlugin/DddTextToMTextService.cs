using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;
using C_toolsShared;

namespace C_toolsDddPlugin;

internal static class DddTextToMTextService
{
    private const string NativeTextToMTextCommand = "_.TXT2MTXT ";

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;
        var preselectedTextIds = GetPreselectedTextIds(doc, out var hadPreselection);
        if (preselectedTextIds.Length > 0)
            editor.SetImpliedSelection(preselectedTextIds);
        else if (hadPreselection)
            editor.SetImpliedSelection(Array.Empty<ObjectId>());

        if (TryQueueNativeCommand(doc, out var error))
        {
            editor.WriteMessage(preselectedTextIds.Length > 0
                ? $"\nC_TOOL：F_TTM 启动，已带入 {preselectedTextIds.Length} 个预选文字。"
                : "\nC_TOOL：F_TTM 启动，选单行文字。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
            editor.WriteMessage($"\nC_TOOL：{error}");
    }

    private static ObjectId[] GetPreselectedTextIds(Document doc, out bool hadPreselection)
    {
        var implied = doc.Editor.SelectImplied();
        hadPreselection = implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0;
        if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count == 0)
            return Array.Empty<ObjectId>();

        var ids = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();

        CadDatabaseScope.Read(
            doc.Database,
            (_, transaction) =>
            {
                foreach (var objectId in implied.Value.GetObjectIds())
                {
                    if (objectId.IsNull || !seen.Add(objectId))
                        continue;

                    if (!CadDatabaseScope.TryOpenAs<Entity>(transaction, objectId, OpenMode.ForRead, out var entity) ||
                        entity == null ||
                        entity.IsErased)
                    {
                        continue;
                    }

                    if (entity is DBText || entity is MText)
                        ids.Add(objectId);
                }
            });

        return ids.Count == 0 ? Array.Empty<ObjectId>() : ids.ToArray();
    }

    private static bool TryQueueNativeCommand(Document doc, out string error)
    {
        error = string.Empty;

        try
        {
            doc.SendStringToExecute(NativeTextToMTextCommand, true, false, false);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 SendStringToExecute 启动 TXT2MTXT 失败（无效操作），尝试回退 AcadDocument.SendCommand", ex);
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 SendStringToExecute 启动 TXT2MTXT 失败（CAD），尝试回退 AcadDocument.SendCommand", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 SendStringToExecute 启动 TXT2MTXT 失败（参数），尝试回退 AcadDocument.SendCommand", ex);
        }

        try
        {
            var acadApplication = AcAp.AcadApplication;
            if (acadApplication == null)
            {
                error = "执行原生 TXT2MTXT 失败：AcadApplication 不可用。";
                return false;
            }

            var activeDocumentProperty = acadApplication.GetType().GetProperty("ActiveDocument");
            var acadDocument = activeDocumentProperty?.GetValue(acadApplication);
            if (acadDocument == null)
            {
                error = "执行原生 TXT2MTXT 失败：AcadDocument 不可用。";
                return false;
            }

            var sendCommandMethod = acadDocument.GetType().GetMethod("SendCommand", new[] { typeof(string) });
            if (sendCommandMethod == null)
            {
                error = "执行原生 TXT2MTXT 失败：未找到 AcadDocument.SendCommand。";
                return false;
            }

            sendCommandMethod.Invoke(acadDocument, new object[] { NativeTextToMTextCommand });
            C_toolsDiagnostics.LogNonFatal("F_TTM 已回退使用 AcadDocument.SendCommand 启动原生 TXT2MTXT。");
            return true;
        }
        catch (TargetInvocationException ex)
        {
            var actual = ex.InnerException ?? ex;
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 AcadDocument.SendCommand 启动 TXT2MTXT 失败（反射调用）", actual);
            error = $"执行原生 TXT2MTXT 失败：{actual.Message}";
            return false;
        }
        catch (COMException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 AcadDocument.SendCommand 启动 TXT2MTXT 失败（COM）", ex);
            error = $"执行原生 TXT2MTXT 失败：{ex.Message}";
            return false;
        }
        catch (MissingMethodException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 AcadDocument.SendCommand 启动 TXT2MTXT 失败（缺少方法）", ex);
            error = $"执行原生 TXT2MTXT 失败：{ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 AcadDocument.SendCommand 启动 TXT2MTXT 失败（无效操作）", ex);
            error = $"执行原生 TXT2MTXT 失败：{ex.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_TTM 使用 AcadDocument.SendCommand 启动 TXT2MTXT 失败（参数）", ex);
            error = $"执行原生 TXT2MTXT 失败：{ex.Message}";
            return false;
        }
    }
}
