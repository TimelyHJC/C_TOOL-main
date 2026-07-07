using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsDddPlugin;

internal static class DddTextContentHelper
{
    internal static bool HasVisibleText(string? text)
    {
        return !string.IsNullOrWhiteSpace(NormalizeLineEndings(text));
    }

    internal static string ToEditableText(string? rawText, bool isMText)
    {
        var text = NormalizeLineEndings(rawText);
        if (!isMText || text.Length == 0)
            return text;

        return text.Replace("\\P", "\n");
    }

    internal static string ToEditableText(MText mText)
    {
        var text = NormalizeLineEndings(mText?.Text);
        if (text.Length > 0)
            return text;

        return ToEditableText(mText?.Contents, isMText: true);
    }

    internal static string ToMTextContents(string? editableText)
    {
        var text = NormalizeLineEndings(editableText);
        if (text.Length == 0)
            return "";

        return text
            .Replace("\\", "\\\\")
            .Replace("{", "\\{")
            .Replace("}", "\\}")
            .Replace("\n", "\\P");
    }

    internal static string NormalizeLineEndings(string? text)
    {
        var value = text ?? "";
        if (value.Length == 0)
            return "";

        var firstCrIndex = value.IndexOf('\r');
        if (firstCrIndex < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, firstCrIndex);

        for (var i = firstCrIndex; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\r')
            {
                builder.Append('\n');
                if (i + 1 < value.Length && value[i + 1] == '\n')
                    i++;
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
