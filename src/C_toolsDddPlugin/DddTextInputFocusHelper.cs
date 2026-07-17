using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using C_toolsShared;

namespace C_toolsDddPlugin;

internal static class DddTextInputFocusHelper
{
    internal static void Attach(Window window)
    {
        window.AddHandler(
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDown),
            true);
        window.AddHandler(
            Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnPreviewGotKeyboardFocus),
            true);
    }

    internal static void FocusTextBox(Window window, TextBox textBox, bool selectAll, bool moveCaretToEnd = false)
    {
        TryActivate(window);

        textBox.Focus();
        Keyboard.Focus(textBox);

        if (selectAll)
        {
            textBox.SelectAll();
        }
        else if (moveCaretToEnd)
        {
            textBox.CaretIndex = (textBox.Text ?? "").Length;
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window)
            return;
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (!IsTextInputElementOrChild(source))
            return;

        TryActivate(window);
    }

    private static void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Window window)
            return;
        if (e.NewFocus is not DependencyObject source)
            return;
        if (!IsTextInputElementOrChild(source))
            return;

        TryActivate(window);
    }

    private static void TryActivate(Window window)
    {
        try
        {
            if (!window.IsActive)
                window.Activate();
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("DDD text input window activation failed (invalid operation)", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("DDD text input window activation failed", ex);
        }
    }

    private static bool IsTextInputElementOrChild(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is TextBoxBase || current is PasswordBox || current is ComboBox)
                return true;

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        try
        {
            var parent = VisualTreeHelper.GetParent(element);
            if (parent != null)
                return parent;
        }
        catch (InvalidOperationException)
        {
        }

        if (element is FrameworkElement frameworkElement)
            return frameworkElement.Parent;
        if (element is FrameworkContentElement frameworkContentElement)
            return frameworkContentElement.Parent;

        return LogicalTreeHelper.GetParent(element);
    }
}
