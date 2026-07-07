using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace C_toolsTemplatePlugin;

/// <summary>
/// 统一封装 AutoCAD 命令的上下文获取、事务边界和异常处理。
/// </summary>
internal static class TemplateCommandExecutor
{
    internal static bool TryGetActiveDocument(
        string operationName,
        out Document? document,
        out Editor? editor)
    {
        return CadCommandContext.TryGetActiveDocument(operationName, out document, out editor);
    }

    internal static void ExecuteRead(
        string operationName,
        Action<Document, Database, Editor, Transaction> action)
    {
        if (!TryGetActiveDocument(operationName, out var document, out var editor) ||
            document == null ||
            editor == null)
        {
            return;
        }

        try
        {
            CadDatabaseScope.Read(
                document,
                (database, transaction) => action(document, database, editor, transaction));
        }
        catch (AcRx.Exception ex) when (IsUserCancelled(ex))
        {
            CadCommandContext.TryWriteMessage(editor, $"\n{operationName}已取消。", $"{operationName}写入取消消息失败");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName, ex);
            CadCommandContext.TryWriteMessage(editor, $"\n{operationName}失败：{ex.Message}", $"{operationName}写入错误消息失败");
        }
    }

    internal static void ExecuteWrite(
        string operationName,
        Action<Document, Database, Editor, Transaction> action,
        bool requireDocumentLock = false)
    {
        if (!TryGetActiveDocument(operationName, out var document, out var editor) ||
            document == null ||
            editor == null)
        {
            return;
        }

        try
        {
            CadDatabaseScope.Write(
                document,
                (database, transaction) => action(document, database, editor, transaction),
                requireDocumentLock);
        }
        catch (AcRx.Exception ex) when (IsUserCancelled(ex))
        {
            CadCommandContext.TryWriteMessage(editor, $"\n{operationName}已取消。", $"{operationName}写入取消消息失败");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName, ex);
            CadCommandContext.TryWriteMessage(editor, $"\n{operationName}失败：{ex.Message}", $"{operationName}写入错误消息失败");
        }
    }

    internal static T OpenAs<T>(Transaction transaction, ObjectId objectId, OpenMode openMode)
        where T : DBObject
    {
        return CadDatabaseScope.OpenAs<T>(transaction, objectId, openMode);
    }

    internal static BlockTableRecord OpenModelSpaceForWrite(Database database, Transaction transaction)
    {
        return CadDatabaseScope.OpenModelSpaceForWrite(database, transaction);
    }

    private static bool IsUserCancelled(AcRx.Exception ex)
    {
        return ex.ErrorStatus == AcRx.ErrorStatus.UserBreak;
    }
}
