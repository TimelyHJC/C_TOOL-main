using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace C_toolsShared;

/// <summary>
/// AutoCAD 命令执行上下文辅助器，统一处理活动文档获取、基础异常记录与命令行消息输出。
/// </summary>
public static class CadCommandContext
{
    /// <summary>
    /// 尝试获取当前活动文档及其编辑器。
    /// </summary>
    public static bool TryGetActiveDocument(
        string operationName,
        out Document? document,
        out Editor? editor)
    {
        document = AcAp.DocumentManager.MdiActiveDocument;
        editor = document?.Editor;

        if (document != null && editor != null)
            return true;

        C_toolsDiagnostics.LogNonFatal($"{operationName}失败：未找到活动文档。");
        return false;
    }

    /// <summary>
    /// 在活动文档上下文中执行命令，并统一处理取消与异常消息。
    /// </summary>
    public static void ExecuteInActiveDocument(
        string operationName,
        Action<Document, Editor> action)
    {
        if (!TryGetActiveDocument(operationName, out var document, out var editor) ||
            document == null ||
            editor == null)
        {
            return;
        }

        try
        {
            action(document, editor);
        }
        catch (AcRx.Exception ex) when (ex.ErrorStatus == AcRx.ErrorStatus.UserBreak)
        {
            TryWriteMessage(editor, $"\n{operationName}已取消。", $"{operationName}写入取消消息失败");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName, ex);
            TryWriteMessage(editor, $"\n{operationName}失败：{ex.Message}", $"{operationName}写入错误消息失败");
        }
    }

    /// <summary>
    /// 安全写入 AutoCAD 命令行消息，避免 UI 状态异常时再抛出次生错误。
    /// </summary>
    public static void TryWriteMessage(Editor? editor, string message, string failureLogMessage)
    {
        if (editor == null || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            editor.WriteMessage(message);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{failureLogMessage}（无效操作）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(failureLogMessage, ex);
        }
    }

    /// <summary>
    /// 以文档为入口安全写入命令行消息。
    /// </summary>
    public static void TryWriteMessage(Document? document, string message, string failureLogMessage)
    {
        TryWriteMessage(document?.Editor, message, failureLogMessage);
    }
}
