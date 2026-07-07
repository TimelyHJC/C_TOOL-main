using System.Globalization;
using System.Windows.Controls;

namespace C_toolsPlugin;

internal static class CadDialogValueHelper
{
    internal static void SelectComboValue(ComboBox combo, string preferredValue, string fallbackValue)
    {
        var preferred = (preferredValue ?? "").Trim();
        if (TrySelectComboItem(combo, preferred))
            return;

        var fallback = (fallbackValue ?? "").Trim();
        if (TrySelectComboItem(combo, fallback))
            return;

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    internal static bool TrySelectComboItem(ComboBox combo, string value)
    {
        if (value.Length == 0)
            return false;

        foreach (var item in combo.Items)
        {
            if (item is string text && string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return true;
            }
        }

        return false;
    }

    internal static bool TryParsePositiveDouble(string? text, out double value)
    {
        value = 0;
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
            return false;

        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value) &&
            !double.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
        {
            return false;
        }

        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
    }
}
