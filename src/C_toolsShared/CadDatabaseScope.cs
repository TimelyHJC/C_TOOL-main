using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsShared;

/// <summary>
/// 统一封装 AutoCAD 数据库事务与文档锁生命周期。
/// </summary>
public static class CadDatabaseScope
{
    /// <summary>
    /// 在只读事务中执行数据库操作。
    /// </summary>
    public static void Read(
        Database database,
        Action<Database, Transaction> action)
    {
        _ = Read(database, (currentDatabase, transaction) =>
        {
            action(currentDatabase, transaction);
            return true;
        });
    }

    /// <summary>
    /// 在只读事务中执行数据库操作并返回结果。
    /// </summary>
    public static T Read<T>(
        Database database,
        Func<Database, Transaction, T> action)
    {
        if (database == null)
            throw new ArgumentNullException(nameof(database));
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return Execute(database, action, commit: false);
    }

    /// <summary>
    /// 在只读事务中执行数据库操作。
    /// </summary>
    public static void Read(
        Document document,
        Action<Database, Transaction> action,
        bool requireDocumentLock = false)
    {
        _ = Read(document, (database, transaction) =>
        {
            action(database, transaction);
            return true;
        }, requireDocumentLock);
    }

    /// <summary>
    /// 在只读事务中执行数据库操作并返回结果。
    /// </summary>
    public static T Read<T>(
        Document document,
        Func<Database, Transaction, T> action,
        bool requireDocumentLock = false)
    {
        if (document == null)
            throw new ArgumentNullException(nameof(document));
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return Execute(document, action, requireDocumentLock, commit: false);
    }

    /// <summary>
    /// 在写事务中执行数据库操作。
    /// </summary>
    public static void Write(
        Document document,
        Action<Database, Transaction> action,
        bool requireDocumentLock = false)
    {
        _ = Write(document, (database, transaction) =>
        {
            action(database, transaction);
            return true;
        }, requireDocumentLock);
    }

    /// <summary>
    /// 在写事务中执行数据库操作并返回结果。
    /// </summary>
    public static T Write<T>(
        Document document,
        Func<Database, Transaction, T> action,
        bool requireDocumentLock = false)
    {
        if (document == null)
            throw new ArgumentNullException(nameof(document));
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return Execute(document, action, requireDocumentLock, commit: true);
    }

    /// <summary>
    /// 在写事务中执行数据库操作。
    /// </summary>
    public static void Write(
        Database database,
        Action<Database, Transaction> action)
    {
        _ = Write(database, (currentDatabase, transaction) =>
        {
            action(currentDatabase, transaction);
            return true;
        });
    }

    /// <summary>
    /// 在写事务中执行数据库操作并返回结果。
    /// </summary>
    public static T Write<T>(
        Database database,
        Func<Database, Transaction, T> action)
    {
        if (database == null)
            throw new ArgumentNullException(nameof(database));
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return Execute(database, action, commit: true);
    }

    /// <summary>
    /// 以类型安全方式打开 DBObject；类型不匹配时返回 false。
    /// </summary>
    public static bool TryOpenAs<T>(
        Transaction transaction,
        ObjectId objectId,
        OpenMode openMode,
        out T? dbObject,
        bool openErased = false)
        where T : DBObject
    {
        if (transaction == null)
            throw new ArgumentNullException(nameof(transaction));

        dbObject = null;
        if (objectId.IsNull)
            return false;

        dbObject = transaction.GetObject(objectId, openMode, openErased) as T;
        return dbObject != null;
    }

    /// <summary>
    /// 以类型安全方式打开 DBObject；类型不匹配时抛出异常。
    /// </summary>
    public static T OpenAs<T>(
        Transaction transaction,
        ObjectId objectId,
        OpenMode openMode,
        bool openErased = false)
        where T : DBObject
    {
        if (transaction == null)
            throw new ArgumentNullException(nameof(transaction));

        var dbObject = transaction.GetObject(objectId, openMode, openErased);
        if (dbObject is T typedObject)
            return typedObject;

        throw new InvalidOperationException(
            $"对象 {objectId} 的实际类型为 {dbObject.GetType().Name}，不是期望的 {typeof(T).Name}。");
    }

    /// <summary>
    /// 打开模型空间以便写入新实体。
    /// </summary>
    public static BlockTableRecord OpenModelSpaceForWrite(Database database, Transaction transaction)
    {
        if (database == null)
            throw new ArgumentNullException(nameof(database));
        if (transaction == null)
            throw new ArgumentNullException(nameof(transaction));

        var blockTable = OpenAs<BlockTable>(transaction, database.BlockTableId, OpenMode.ForRead);
        return OpenAs<BlockTableRecord>(transaction, blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
    }

    /// <summary>
    /// 打开当前空间以便写入新实体。
    /// </summary>
    public static BlockTableRecord OpenCurrentSpaceForWrite(Database database, Transaction transaction)
    {
        if (database == null)
            throw new ArgumentNullException(nameof(database));
        if (transaction == null)
            throw new ArgumentNullException(nameof(transaction));

        return OpenAs<BlockTableRecord>(transaction, database.CurrentSpaceId, OpenMode.ForWrite);
    }

    /// <summary>
    /// 判断实体所在图层是否已锁定。
    /// </summary>
    public static bool IsOnLockedLayer(Transaction transaction, Entity entity)
    {
        if (transaction == null)
            throw new ArgumentNullException(nameof(transaction));
        if (entity == null)
            throw new ArgumentNullException(nameof(entity));
        if (entity.LayerId.IsNull)
            return false;

        return TryOpenAs<LayerTableRecord>(transaction, entity.LayerId, OpenMode.ForRead, out var layer) &&
               layer != null &&
               layer.IsLocked;
    }

    private static T Execute<T>(
        Document document,
        Func<Database, Transaction, T> action,
        bool requireDocumentLock,
        bool commit)
    {
        DocumentLock? documentLock = null;

        try
        {
            if (requireDocumentLock)
                documentLock = document.LockDocument();

            var database = document.Database;
            using var transaction = database.TransactionManager.StartTransaction();
            try
            {
                var result = action(database, transaction);
                if (commit)
                    transaction.Commit();

                return result;
            }
            catch
            {
                TryAbort(transaction);
                throw;
            }
        }
        finally
        {
            documentLock?.Dispose();
        }
    }

    private static T Execute<T>(
        Database database,
        Func<Database, Transaction, T> action,
        bool commit)
    {
        using var transaction = database.TransactionManager.StartTransaction();
        try
        {
            var result = action(database, transaction);
            if (commit)
                transaction.Commit();

            return result;
        }
        catch
        {
            TryAbort(transaction);
            throw;
        }
    }

    private static void TryAbort(Transaction transaction)
    {
        try
        {
            transaction.Abort();
        }
        catch
        {
            // 保留原始失败原因；释放阶段仍由 using 负责兜底。
        }
    }
}
