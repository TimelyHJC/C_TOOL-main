using System;
using System.Collections.Generic;

namespace C_toolsShared;

/// <summary>
/// 系统变量快照，可按需恢复全部或部分变量。
/// </summary>
public sealed class CadSystemVariableSnapshot
{
    private readonly IReadOnlyDictionary<string, object?> _capturedValues;

    internal CadSystemVariableSnapshot(IDictionary<string, object?> capturedValues)
    {
        if (capturedValues == null)
            throw new ArgumentNullException(nameof(capturedValues));

        _capturedValues = new Dictionary<string, object?>(capturedValues, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 恢复指定系统变量；未传入名称时恢复快照中的全部变量。
    /// </summary>
    public bool TryRestore(params string[] names)
    {
        var targetNames = names is { Length: > 0 } ? names : _capturedValues.Keys;
        var failedNames = new List<string>();

        foreach (var name in targetNames)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                !_capturedValues.TryGetValue(name, out var value) ||
                value == null)
            {
                continue;
            }

            if (!CadSystemVariableService.TrySetValueCore(name, value, logFailure: false, out _))
                failedNames.Add(name);
        }

        if (failedNames.Count > 0)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"恢复系统变量失败：{string.Join(", ", failedNames)}",
                null);
        }

        return failedNames.Count == 0;
    }

    /// <summary>
    /// 恢复快照中的全部系统变量。
    /// </summary>
    public bool TryRestoreAll() => TryRestore(Array.Empty<string>());
}
