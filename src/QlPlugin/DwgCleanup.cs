using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace QlPlugin;

/// <summary>
/// DWG 文件清理服务：AUDIT、PURGE、RegApps、OVERKILL
/// </summary>
public static class DwgCleanup
{
    /// <summary>
    /// 执行完整清理流程
    /// </summary>
    /// <param name="hasSelection">是否先对选中对象执行 OVERKILL</param>
    /// <returns>清理结果摘要</returns>
    public static string RunFullCleanup(bool hasSelection = false)
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return "未打开文档";

        var ed = doc.Editor;
        var results = new List<string>();

        try
        {
            // 选中时：先执行 OVERKILL 对选中对象删除重复/重叠
            if (hasSelection)
            {
                try
                {
                    ed.Command("_.OVERKILL", "", "");
                    results.Add("OVERKILL: 已对选中对象删除重复和重叠");
                }
                catch (System.Exception)
                {
                    results.Add("OVERKILL(选中): 未执行（可能未安装或命令不可用）");
                }
            }

            // 1. AUDIT - 检查并修复错误
            try
            {
                ed.Command("_.AUDIT", "Y");
                results.Add("AUDIT: 已执行图形完整性检查");
            }
            catch (System.Exception ex)
            {
                results.Add($"AUDIT: {ex.Message}");
            }

            // 2. PURGE - 多次执行直到无法再清理（块、图层、线型等有依赖关系）
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    ed.Command("_.-PURGE", "A", "*", "N");
                }
                catch
                {
                    break;
                }
            }
            results.Add("PURGE: 已清理未使用的块、图层、线型、样式等");

            // 3. -PURGE RegApps - 清理注册应用程序（常能显著减小文件）
            try
            {
                ed.Command("_.-PURGE", "R", "*", "N");
                results.Add("RegApps: 已清理未使用的注册应用程序");
            }
            catch (System.Exception ex)
            {
                results.Add($"RegApps: {ex.Message}");
            }

            // 4. 再次 PURGE（RegApps 清理后可能产生新的可清理项）
            try
            {
                ed.Command("_.-PURGE", "A", "*", "N");
            }
            catch
            {
                // 忽略
            }

            // 5. OVERKILL - 全图删除重复/重叠对象（未选中时执行；选中时上面已对选中执行，此处对全图再执行一次）
            try
            {
                ed.Command("_.OVERKILL", "ALL", "");
                results.Add("OVERKILL: 已对全图删除重复和重叠对象");
            }
            catch (System.Exception)
            {
                results.Add("OVERKILL(全图): 未执行（可能未安装或命令不可用）");
            }

            return string.Join("\n", results);
        }
        catch (System.Exception ex)
        {
            return $"清理过程出错: {ex.Message}\n\n已执行: {string.Join("\n", results)}";
        }
    }
}
