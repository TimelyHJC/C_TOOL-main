using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static partial class ColorShortcutService
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private static readonly ColorShortcutInputState s_inputState = new();
    private static bool s_isEnabled;
    private static bool s_isRegistered;
    private static bool s_isApplying;

    internal static void Enable() => SetEnabled(true);

    private static void SetEnabled(bool enabled)
    {
        if (s_isEnabled == enabled)
            return;

        s_isEnabled = enabled;
        s_inputState.Clear();

        if (enabled)
            Register();
        else
            Unregister();
    }

    internal static void Terminate()
    {
        s_isEnabled = false;
        s_inputState.Clear();
        Unregister();
    }

    private static void Register()
    {
        if (s_isRegistered)
            return;

        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        s_isRegistered = true;
    }

    private static void Unregister()
    {
        if (!s_isRegistered)
            return;

        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
        s_isRegistered = false;
    }

    private static void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled || !s_isEnabled || s_isApplying)
            return;

        if (msg.message != WmKeyDown && msg.message != WmSysKeyDown)
            return;

        if (IsCadCommandActive() || IsTextEditingFocused())
            return;

        var vk = msg.wParam.ToInt32() & 0xFFFF;
        var outcome = s_inputState.ProcessVirtualKey(vk, HasCommandModifier(Keyboard.Modifiers));
        if (outcome.Handled)
            handled = true;

        if (outcome.ColorTextToApply != null)
            ApplyColorShortcut(outcome.ColorTextToApply);
    }

    internal static bool HasCommandModifier(ModifierKeys modifiers) =>
        (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != ModifierKeys.None;
}

internal readonly struct ColorShortcutKeyOutcome
{
    internal ColorShortcutKeyOutcome(bool handled, string? colorTextToApply)
    {
        Handled = handled;
        ColorTextToApply = colorTextToApply;
    }

    internal bool Handled { get; }

    internal string? ColorTextToApply { get; }
}

internal sealed class ColorShortcutInputState
{
    private readonly StringBuilder _buffer = new();
    private bool _isCommandTextPassthrough;

    internal int BufferLength => _buffer.Length;

    internal bool IsCommandTextPassthrough => _isCommandTextPassthrough;

    internal void Clear()
    {
        _buffer.Clear();
        _isCommandTextPassthrough = false;
    }

    internal ColorShortcutKeyOutcome ProcessVirtualKey(int virtualKey, bool hasCommandModifier = false)
    {
        if (hasCommandModifier)
        {
            Clear();
            return new ColorShortcutKeyOutcome(handled: false, colorTextToApply: null);
        }

        if (_isCommandTextPassthrough)
        {
            if (IsTextInputTerminator(virtualKey))
                _isCommandTextPassthrough = false;
            return new ColorShortcutKeyOutcome(handled: false, colorTextToApply: null);
        }

        if (TryGetDigit(virtualKey, out var digit))
        {
            if (_buffer.Length < 3)
                _buffer.Append(digit);
            return new ColorShortcutKeyOutcome(handled: true, colorTextToApply: null);
        }

        if (virtualKey == ColorShortcutService.VirtualKeys.Back && _buffer.Length > 0)
        {
            _buffer.Remove(_buffer.Length - 1, 1);
            return new ColorShortcutKeyOutcome(handled: true, colorTextToApply: null);
        }

        if ((virtualKey == ColorShortcutService.VirtualKeys.Return ||
             virtualKey == ColorShortcutService.VirtualKeys.Space) &&
            _buffer.Length > 0)
        {
            var colorText = _buffer.ToString();
            _buffer.Clear();
            return new ColorShortcutKeyOutcome(handled: true, colorTextToApply: colorText);
        }

        if (virtualKey == ColorShortcutService.VirtualKeys.Escape)
        {
            Clear();
            return new ColorShortcutKeyOutcome(handled: false, colorTextToApply: null);
        }

        if (IsCommandTextKey(virtualKey))
        {
            _buffer.Clear();
            _isCommandTextPassthrough = true;
        }

        return new ColorShortcutKeyOutcome(handled: false, colorTextToApply: null);
    }

    private static bool TryGetDigit(int virtualKey, out char digit)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            digit = (char)('0' + virtualKey - 0x30);
            return true;
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            digit = (char)('0' + virtualKey - 0x60);
            return true;
        }

        digit = '\0';
        return false;
    }

    private static bool IsTextInputTerminator(int virtualKey) =>
        virtualKey == ColorShortcutService.VirtualKeys.Return ||
        virtualKey == ColorShortcutService.VirtualKeys.Space ||
        virtualKey == ColorShortcutService.VirtualKeys.Escape;

    private static bool IsCommandTextKey(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
            return true;
        if (virtualKey is >= 0xBA and <= 0xE2)
            return true;
        return virtualKey == 0x6D || virtualKey == 0x6E || virtualKey == 0x6F;
    }
}

internal static partial class ColorShortcutService
{
    internal static class VirtualKeys
    {
        internal const int Back = 0x08;
        internal const int Return = 0x0D;
        internal const int Escape = 0x1B;
        internal const int Space = 0x20;
    }

    private static void ApplyColorShortcut(string colorText)
    {
        if (!int.TryParse(colorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var colorIndex) ||
            colorIndex < 1 ||
            colorIndex > 255)
        {
            TryWriteMessage($"颜色快捷只支持 1-255 的 ACI 颜色编号：{colorText}。");
            return;
        }

        s_isApplying = true;
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    try
                    {
                        ApplyColorToImpliedSelection(colorIndex);
                    }
                    finally
                    {
                        s_isApplying = false;
                    }
                },
                null);
        }
        catch (Exception ex)
        {
            s_isApplying = false;
            C_toolsDiagnostics.LogNonFatal("颜色快捷应用失败", ex);
            TryWriteMessage("颜色快捷失败：" + ex.Message);
        }
    }

    private static void ApplyColorToImpliedSelection(int colorIndex)
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            TryWriteMessage("当前没有活动图纸。");
            return;
        }

        var ed = doc.Editor;
        var implied = ed.SelectImplied();
        if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count == 0)
        {
            ed.WriteMessage("\nC_TOOL：颜色快捷：请先选中对象，再输入 1-255 并回车。");
            return;
        }

        ed.SetImpliedSelection(Array.Empty<ObjectId>());

        var changedCount = 0;
        var lockedLayerCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in implied.Value.GetObjectIds())
                {
                    if (id.IsNull)
                        continue;

                    try
                    {
                        if (!CadDatabaseScope.TryOpenAs<Entity>(tr, id, OpenMode.ForRead, out var entity) ||
                            entity == null)
                        {
                            skippedCount++;
                            continue;
                        }

                        if (CadDatabaseScope.IsOnLockedLayer(tr, entity))
                        {
                            lockedLayerCount++;
                            continue;
                        }

                        if (!entity.IsWriteEnabled)
                            entity.UpgradeOpen();

                        entity.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex);
                        changedCount++;
                    }
                    catch (InvalidOperationException ex)
                    {
                        failedCount++;
                        C_toolsDiagnostics.LogNonFatal("颜色快捷修改对象失败（无效操作）", ex);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        failedCount++;
                        C_toolsDiagnostics.LogNonFatal("颜色快捷修改对象失败（CAD）", ex);
                    }
                    catch (ArgumentException ex)
                    {
                        failedCount++;
                        C_toolsDiagnostics.LogNonFatal("颜色快捷修改对象失败（参数）", ex);
                    }
                }

                tr.Commit();
            }

            var parts = new List<string>();
            if (changedCount > 0)
                parts.Add($"已将 {changedCount} 个对象颜色改为{colorIndex}");
            if (lockedLayerCount > 0)
                parts.Add($"跳过 {lockedLayerCount} 个锁定图层对象");
            if (skippedCount > 0)
                parts.Add($"跳过 {skippedCount} 个非实体对象");
            if (failedCount > 0)
                parts.Add($"跳过 {failedCount} 个不可写对象");

            ed.WriteMessage(parts.Count == 0
                ? "\nC_TOOL：颜色快捷：没有可修改的对象。"
                : "\nC_TOOL：颜色快捷：" + string.Join("，", parts) + "。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("颜色快捷执行失败（无效操作）", ex);
            ed.WriteMessage("\nC_TOOL：颜色快捷失败：" + ex.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("颜色快捷执行失败（CAD）", ex);
            ed.WriteMessage("\nC_TOOL：颜色快捷失败：" + ex.Message);
        }
    }

    private static bool IsCadCommandActive()
    {
        try
        {
            return Convert.ToInt32(AcAp.GetSystemVariable("CMDACTIVE"), CultureInfo.InvariantCulture) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTextEditingFocused()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
            return false;

        while (focused != null)
        {
            if (focused is TextBoxBase || focused is PasswordBox || focused is ComboBox)
                return true;

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private static void TryWriteMessage(string message)
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            doc.Editor.WriteMessage("\nC_TOOL：" + message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("颜色快捷写入提示失败", ex);
        }
    }
}
