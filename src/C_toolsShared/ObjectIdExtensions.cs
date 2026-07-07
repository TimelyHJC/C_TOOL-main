using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsShared;

/// <summary>
/// ObjectId 扩展方法，提供便捷的验证功能。
/// </summary>
public static class ObjectIdExtensions
{
    /// <summary>
    /// 判断 ObjectId 是否有效（非空且未被删除）。
    /// </summary>
    /// <param name="id">要验证的 ObjectId</param>
    /// <returns>如果 ObjectId 有效则返回 true</returns>
    public static bool IsValid(this ObjectId id) => !id.IsNull && !id.IsErased;

    /// <summary>
    /// 判断 ObjectId 是否无效（为空或已被删除）。
    /// </summary>
    /// <param name="id">要验证的 ObjectId</param>
    /// <returns>如果 ObjectId 无效则返回 true</returns>
    public static bool IsInvalid(this ObjectId id) => id.IsNull || id.IsErased;

    /// <summary>
    /// 判断 ObjectId 是否为空。
    /// </summary>
    /// <param name="id">要验证的 ObjectId</param>
    /// <returns>如果 ObjectId 为空则返回 true</returns>
    public static bool IsEmpty(this ObjectId id) => id.IsNull;

    /// <summary>
    /// 判断 ObjectId 是否已被删除。
    /// </summary>
    /// <param name="id">要验证的 ObjectId</param>
    /// <returns>如果 ObjectId 已被删除则返回 true</returns>
    public static bool IsDeleted(this ObjectId id) => id.IsErased;
}