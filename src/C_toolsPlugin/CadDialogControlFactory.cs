using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace C_toolsPlugin;

internal static class CadDialogControlFactory
{
    internal static Border CreateField(string label, UIElement input)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brush("#A7B1BC"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(input);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    internal static TextBox CreateTextBox(string text)
    {
        return new TextBox
        {
            Text = text,
            Height = 32,
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brush("#1B2027"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#4E5A67"),
            BorderThickness = new Thickness(1)
        };
    }

    internal static Button CreateActionButton(string text, bool isPrimary)
    {
        return new Button
        {
            Content = text,
            Width = 92,
            Height = 34,
            Margin = new Thickness(8, 12, 0, 0),
            Background = isPrimary ? Brush("#2D89EF") : Brush("#2E3640"),
            Foreground = Brushes.White,
            BorderBrush = isPrimary ? Brush("#2D89EF") : Brush("#56616C"),
            BorderThickness = new Thickness(1)
        };
    }

    internal static SolidColorBrush Brush(string colorText) =>
        new((Color)ColorConverter.ConvertFromString(colorText));
}
