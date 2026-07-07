using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace C_toolsShared;

internal static class DialogWindowPlacementStore
{
    private const string FileExtension = ".dialog.state";
    private const string KeyLeft = "left";
    private const string KeyTop = "top";
    private const string KeyDpiScaleX = "dpiScaleX";
    private const string KeyDpiScaleY = "dpiScaleY";

    internal static bool TryRestore(Window window, string placementKey)
    {
        if (!TryReadSnapshot(placementKey, out var snapshot))
            return false;

        var dpi = WindowDpiHelper.GetDpiScale(window);
        var scaledBounds = WindowDpiHelper.ScaleBoundsForCurrentDpi(
            new Rect(snapshot.Left, snapshot.Top, ResolveWindowWidth(window), ResolveWindowHeight(window)),
            snapshot.DpiScaleX,
            snapshot.DpiScaleY,
            dpi);

        if (!TryClampToVisibleBounds(scaledBounds, out var normalizedBounds))
            return false;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = normalizedBounds.Left;
        window.Top = normalizedBounds.Top;
        return true;
    }

    internal static void TrySave(Window window, string placementKey)
    {
        var bounds = GetPersistBounds(window);
        if (!IsUsableBounds(bounds))
            return;

        var dpi = WindowDpiHelper.GetDpiScale(window);
        var sb = new StringBuilder();
        AppendLine(sb, KeyLeft, bounds.Left);
        AppendLine(sb, KeyTop, bounds.Top);
        AppendLine(sb, KeyDpiScaleX, dpi.DpiScaleX);
        AppendLine(sb, KeyDpiScaleY, dpi.DpiScaleY);

        C_toolsTextFileStore.TryWriteAllText(
            GetFilePath(placementKey),
            sb.ToString(),
            Encoding.UTF8,
            $"写入对话框布局 {placementKey}");
    }

    private static Rect GetPersistBounds(Window window)
    {
        Rect bounds;
        try
        {
            bounds = window.RestoreBounds;
        }
        catch
        {
            bounds = Rect.Empty;
        }

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new Rect(window.Left, window.Top, ResolveWindowWidth(window), ResolveWindowHeight(window));

        return bounds;
    }

    private static bool TryReadSnapshot(string placementKey, out PlacementSnapshot snapshot)
    {
        snapshot = default;
        var text = C_toolsTextFileStore.TryReadAllText(GetFilePath(placementKey), $"读取对话框布局 {placementKey}");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        double? left = null;
        double? top = null;
        double? dpiScaleX = null;
        double? dpiScaleY = null;
        var lineStart = 0;
        while (TryReadNextTrimmedLine(text!, ref lineStart, out var line))
        {
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
                continue;

            var key = line.Substring(0, separatorIndex).Trim();
            var valueText = line.Substring(separatorIndex + 1).Trim();
            if (!double.TryParse(valueText, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
                continue;

            if (key.Equals(KeyLeft, StringComparison.OrdinalIgnoreCase))
                left = value;
            else if (key.Equals(KeyTop, StringComparison.OrdinalIgnoreCase))
                top = value;
            else if (key.Equals(KeyDpiScaleX, StringComparison.OrdinalIgnoreCase))
                dpiScaleX = value;
            else if (key.Equals(KeyDpiScaleY, StringComparison.OrdinalIgnoreCase))
                dpiScaleY = value;
        }

        if (!left.HasValue || !top.HasValue)
            return false;

        snapshot = new PlacementSnapshot(left.Value, top.Value, dpiScaleX, dpiScaleY);
        return true;
    }

    private static bool TryReadNextTrimmedLine(string text, ref int lineStart, out string line)
    {
        var length = text.Length;
        while (lineStart < length)
        {
            var lineEnd = lineStart;
            while (lineEnd < length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
                lineEnd++;

            var contentStart = lineStart;
            while (contentStart < lineEnd && char.IsWhiteSpace(text[contentStart]))
                contentStart++;

            var contentEnd = lineEnd - 1;
            while (contentEnd >= contentStart && char.IsWhiteSpace(text[contentEnd]))
                contentEnd--;

            if (lineEnd < length && text[lineEnd] == '\r' && lineEnd + 1 < length && text[lineEnd + 1] == '\n')
                lineEnd++;

            lineStart = lineEnd + 1;
            if (contentStart > contentEnd)
                continue;

            line = text.Substring(contentStart, contentEnd - contentStart + 1);
            return true;
        }

        line = "";
        return false;
    }

    private static double ResolveWindowWidth(Window window)
    {
        if (IsFinitePositive(window.ActualWidth))
            return window.ActualWidth;
        if (IsFinitePositive(window.Width))
            return window.Width;
        if (IsFinitePositive(window.MinWidth))
            return window.MinWidth;
        return 0d;
    }

    private static double ResolveWindowHeight(Window window)
    {
        if (IsFinitePositive(window.ActualHeight))
            return window.ActualHeight;
        if (IsFinitePositive(window.Height))
            return window.Height;
        if (IsFinitePositive(window.MinHeight))
            return window.MinHeight;
        return 0d;
    }

    private static bool IsUsableBounds(Rect bounds)
    {
        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || !IsFinite(bounds.Width) || !IsFinite(bounds.Height))
            return false;

        if (bounds.Width < 120d || bounds.Height < 80d)
            return false;

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (virtualScreen.IsEmpty || virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
            return true;

        var intersection = Rect.Intersect(bounds, virtualScreen);
        return !intersection.IsEmpty && intersection.Width >= 40d && intersection.Height >= 40d;
    }

    private static bool TryClampToVisibleBounds(Rect bounds, out Rect normalizedBounds)
    {
        normalizedBounds = Rect.Empty;
        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || !IsFinite(bounds.Width) || !IsFinite(bounds.Height))
            return false;

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (virtualScreen.IsEmpty || virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            normalizedBounds = bounds;
            return IsUsableBounds(normalizedBounds);
        }

        var maxLeft = virtualScreen.Right - Math.Min(bounds.Width, virtualScreen.Width);
        var maxTop = virtualScreen.Bottom - Math.Min(bounds.Height, virtualScreen.Height);
        var left = Clamp(bounds.Left, virtualScreen.Left, maxLeft);
        var top = Clamp(bounds.Top, virtualScreen.Top, maxTop);

        normalizedBounds = new Rect(left, top, bounds.Width, bounds.Height);
        return IsUsableBounds(normalizedBounds);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsFinitePositive(double value) => IsFinite(value) && value > 0d;

    private static double Clamp(double value, double min, double max)
    {
        if (!IsFinite(value))
            return min;

        if (!IsFinite(min))
            min = value;
        if (!IsFinite(max))
            max = value;
        if (max < min)
            max = min;

        return Math.Min(Math.Max(value, min), max);
    }

    private static void AppendLine(StringBuilder sb, string key, double value)
    {
        sb.Append(key)
            .Append('=')
            .Append(value.ToString("R", CultureInfo.InvariantCulture))
            .Append("\r\n");
    }

    private static string GetFilePath(string placementKey) =>
        Path.Combine(C_toolsPaths.WindowStateFolder, SanitizeFileName(placementKey) + FileExtension);

    private static string SanitizeFileName(string placementKey)
    {
        var value = string.IsNullOrWhiteSpace(placementKey) ? "dialog" : placementKey.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value;
    }

    private readonly struct PlacementSnapshot
    {
        internal PlacementSnapshot(
            double left,
            double top,
            double? dpiScaleX,
            double? dpiScaleY)
        {
            Left = left;
            Top = top;
            DpiScaleX = dpiScaleX;
            DpiScaleY = dpiScaleY;
        }

        internal double Left { get; }

        internal double Top { get; }

        internal double? DpiScaleX { get; }

        internal double? DpiScaleY { get; }
    }
}
