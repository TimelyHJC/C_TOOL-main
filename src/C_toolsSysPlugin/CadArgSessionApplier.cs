using System.IO;

namespace C_toolsPlugin;

/// <summary>将 .arg 中可安全映射的系统变量应用到当前 CAD 会话，避免整套 Profile 导入带来的菜单/工作区副作用。</summary>
internal static class CadArgSessionApplier
{
    internal static ApplyResult ApplyToCurrentSession(string argPath)
    {
        if (string.IsNullOrWhiteSpace(argPath))
            return ApplyResult.Fail(UIMessages.Errors.ArgFileInvalid);
        if (!File.Exists(argPath))
            return ApplyResult.Fail("找不到选定的 .arg 配置文件。");

        List<SysConfigRow> rows;
        try
        {
            rows = CadSysConfigArg.LoadRows(argPath);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 .arg 配置文件", ex);
            return ApplyResult.Fail("读取 .arg 配置文件失败：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 .arg 配置文件（权限）", ex);
            return ApplyResult.Fail("读取 .arg 配置文件失败：" + ex.Message);
        }

        if (rows.Count == 0)
            return ApplyResult.Fail("该 .arg 中未找到可安全应用的系统变量。");

        var latestByVar = new Dictionary<string, SysConfigRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            latestByVar[row.VarName] = row;

        var applied = 0;
        var failed = new List<string>();
        foreach (var row in latestByVar.Values)
        {
            if (CadSysVarTextConverter.TryWriteVar(row.VarName, row.Value, out var error))
            {
                applied++;
                continue;
            }

            var detail = error?.Trim();
            if (string.IsNullOrWhiteSpace(detail))
                detail = "写入失败";
            failed.Add(row.VarName + "：" + detail);
        }

        if (applied == 0)
            return ApplyResult.Fail("未能将 .arg 中的系统变量写入当前 CAD。");

        return ApplyResult.Success(applied, latestByVar.Count - applied, failed);
    }

    internal sealed class ApplyResult
    {
        private ApplyResult(bool ok, string message, int appliedCount, int failedCount, IReadOnlyList<string> failedVars)
        {
            Ok = ok;
            Message = message;
            AppliedCount = appliedCount;
            FailedCount = failedCount;
            FailedVars = failedVars;
        }

        internal bool Ok { get; }
        internal string Message { get; }
        internal int AppliedCount { get; }
        internal int FailedCount { get; }
        internal IReadOnlyList<string> FailedVars { get; }

        internal static ApplyResult Success(int appliedCount, int failedCount, IReadOnlyList<string> failedVars)
        {
            return new ApplyResult(true, "", appliedCount, failedCount, failedVars);
        }

        internal static ApplyResult Fail(string message)
        {
            return new ApplyResult(false, message, 0, 0, Array.Empty<string>());
        }
    }
}
