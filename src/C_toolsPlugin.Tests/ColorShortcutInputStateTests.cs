using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace C_toolsPlugin.Tests;

[TestClass]
public class ColorShortcutInputStateTests
{
    private const int KeyA = 0x41;
    private const int KeyB = 0x42;
    private const int Key1 = 0x31;
    private const int Key2 = 0x32;

    [TestMethod]
    public void ProcessVirtualKey_DigitsThenReturn_AppliesColor()
    {
        var state = new ColorShortcutInputState();

        var first = state.ProcessVirtualKey(Key1);
        var second = state.ProcessVirtualKey(Key2);
        var enter = state.ProcessVirtualKey(ColorShortcutService.VirtualKeys.Return);

        Assert.IsTrue(first.Handled);
        Assert.IsTrue(second.Handled);
        Assert.IsTrue(enter.Handled);
        Assert.AreEqual("12", enter.ColorTextToApply);
        Assert.AreEqual(0, state.BufferLength);
    }

    [TestMethod]
    public void ProcessVirtualKey_LetterThenDigit_PassesThroughCommandText()
    {
        var state = new ColorShortcutInputState();

        var letter = state.ProcessVirtualKey(KeyA);
        var digit = state.ProcessVirtualKey(Key1);

        Assert.IsFalse(letter.Handled);
        Assert.IsFalse(digit.Handled);
        Assert.IsNull(digit.ColorTextToApply);
        Assert.IsTrue(state.IsCommandTextPassthrough);
        Assert.AreEqual(0, state.BufferLength);
    }

    [TestMethod]
    public void ProcessVirtualKey_CommandTextTerminator_ReenablesColorShortcut()
    {
        var state = new ColorShortcutInputState();

        state.ProcessVirtualKey(KeyA);
        var commandDigit = state.ProcessVirtualKey(Key1);
        var enterCommand = state.ProcessVirtualKey(ColorShortcutService.VirtualKeys.Return);
        var colorDigit = state.ProcessVirtualKey(Key2);
        var enterColor = state.ProcessVirtualKey(ColorShortcutService.VirtualKeys.Return);

        Assert.IsFalse(commandDigit.Handled);
        Assert.IsFalse(enterCommand.Handled);
        Assert.IsTrue(colorDigit.Handled);
        Assert.IsTrue(enterColor.Handled);
        Assert.AreEqual("2", enterColor.ColorTextToApply);
    }

    [TestMethod]
    public void ProcessVirtualKey_MultipleLettersAndDigits_PassesThroughAllUntilTerminator()
    {
        var state = new ColorShortcutInputState();

        Assert.IsFalse(state.ProcessVirtualKey(KeyA).Handled);
        Assert.IsFalse(state.ProcessVirtualKey(KeyB).Handled);
        Assert.IsFalse(state.ProcessVirtualKey(Key1).Handled);
        Assert.IsFalse(state.ProcessVirtualKey(Key2).Handled);
        Assert.IsTrue(state.IsCommandTextPassthrough);
        Assert.AreEqual(0, state.BufferLength);
    }

    [TestMethod]
    public void ProcessVirtualKey_CommandModifierDigit_PassesThroughShortcut()
    {
        var state = new ColorShortcutInputState();

        var digit = state.ProcessVirtualKey(Key1, hasCommandModifier: true);

        Assert.IsFalse(digit.Handled);
        Assert.IsNull(digit.ColorTextToApply);
        Assert.AreEqual(0, state.BufferLength);
    }

    [TestMethod]
    public void ProcessVirtualKey_CommandModifier_ClearsPendingColorDigits()
    {
        var state = new ColorShortcutInputState();

        Assert.IsTrue(state.ProcessVirtualKey(Key1).Handled);
        Assert.AreEqual(1, state.BufferLength);

        var shortcutDigit = state.ProcessVirtualKey(Key2, hasCommandModifier: true);
        var enter = state.ProcessVirtualKey(ColorShortcutService.VirtualKeys.Return);

        Assert.IsFalse(shortcutDigit.Handled);
        Assert.IsFalse(enter.Handled);
        Assert.IsNull(enter.ColorTextToApply);
        Assert.AreEqual(0, state.BufferLength);
    }
}
