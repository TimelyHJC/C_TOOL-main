using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsShared;

/// <summary>
/// 临时切换 <see cref="HostApplicationServices.WorkingDatabase"/>，并在结束后自动恢复。
/// </summary>
public static class CadWorkingDatabaseScope
{
    /// <summary>
    /// 在指定工作库上下文中执行操作。
    /// </summary>
    public static void Run(Database database, Action action)
    {
        _ = Run(database, () =>
        {
            action();
            return true;
        });
    }

    /// <summary>
    /// 在指定工作库上下文中执行操作并返回结果。
    /// </summary>
    public static T Run<T>(Database database, Func<T> action)
    {
        if (database == null)
            throw new ArgumentNullException(nameof(database));
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        var originalDatabase = HostApplicationServices.WorkingDatabase;
        if (ReferenceEquals(originalDatabase, database))
            return action();

        try
        {
            HostApplicationServices.WorkingDatabase = database;
            return action();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = originalDatabase;
        }
    }
}
