using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace C_toolsShared;

internal static class ModelessWindowPlacementStore
{
    private const string FileExtension = ".state";
    private const string KeyLeft = "left";
    private const string KeyTop = "top";
    private const string KeyWidth = "width";
    private const string KeyHeight = "height";
    private const string KeyDpiScaleX = "dpiScaleX";
    private const string KeyDpiScaleY = "dpiScaleY";

    internal static void TryRestore(Window window, string placementKey)
    {
        if (!TryReadSnapshot(placementKey, out var snapshot))
            return;

        if (PresentationSource.FromVisual(window) != null)
        {
            ApplySnapshot(window, snapshot);
            return;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler != null)
                window.SourceInitialized -= handler;
            ApplySnapshot(window, snapshot);
        };

        window.SourceInitialized += handler;
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
        AppendLine(sb, KeyWidth, bounds.Width);
        AppendLine(sb, KeyHeight, bounds.Height);
        AppendLine(sb, KeyDpiScaleX, dpi.DpiScaleX);
        AppendLine(sb, KeyDpiScaleY, dpi.DpiScaleY);

        C_toolsTextFileStore.TryWriteAllText(
            GetFilePath(placementKey),
            sb.ToString(),
            Encoding.UTF8,
            $"写入窗口布局 {placementKey}");
    }

    private static void ApplySnapshot(Window window, PlacementSnapshot snapshot)
    {
        var dpi = WindowDpiHelper.GetDpiScale(window);
        var scaledBounds = WindowDpiHelper.ScaleBoundsForCurrentDpi(
            new Rect(snapshot.Left, snapshot.Top, snapshot.Width, snapshot.Height),
            snapshot.DpiScaleX,
            snapshot.DpiScaleY,
            dpi);

        var width = Math.Max(scaledBounds.Width, window.MinWidth);
        var height = Math.Max(scaledBounds.Height, window.MinHeight);
        var bounds = new Rect(scaledBounds.Left, scaledBounds.Top, width, height);
        if (!IsUsableBounds(bounds))
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
    }

    private static bool TryReadSnapshot(string placementKey, out PlacementSnapshot snapshot)
    {
        snapshot = default;
        var text = C_toolsTextFileStore.TryReadAllText(GetFilePath(placementKey), $"读取窗口布局 {placementKey}");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        double? left = null;
        double? top = null;
        double? width = null;
        double? height = null;
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
            else if (key.Equals(KeyWidth, StringComparison.OrdinalIgnoreCase))
                width = value;
            else if (key.Equals(KeyHeight, StringComparison.OrdinalIgnoreCase))
                height = value;
            else if (key.Equals(KeyDpiScaleX, StringComparison.OrdinalIgnoreCase))
                dpiScaleX = value;
            else if (key.Equals(KeyDpiScaleY, StringComparison.OrdinalIgnoreCase))
                dpiScaleY = value;
        }

        if (!left.HasValue || !top.HasValue || !width.HasValue || !height.HasValue)
            return false;

        snapshot = new PlacementSnapshot(left.Value, top.Value, width.Value, height.Value, dpiScaleX, dpiScaleY);
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
            bounds = new Rect(window.Left, window.Top, window.Width, window.Height);

        return bounds;
    }

    private static bool IsUsableBounds(Rect bounds)
    {
        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || !IsFinite(bounds.Width) || !IsFinite(bounds.Height))
            return false;

        if (bounds.Width < 200d || bounds.Height < 120d)
            return false;

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (virtualScreen.IsEmpty || virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
            return true;

        var intersection = Rect.Intersect(bounds, virtualScreen);
        return !intersection.IsEmpty && intersection.Width >= 80d && intersection.Height >= 80d;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

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
        var value = string.IsNullOrWhiteSpace(placementKey) ? "window" : placementKey.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');

        return value;
    }

    private readonly struct PlacementSnapshot
    {
        internal PlacementSnapshot(
            double left,
            double top,
            double width,
            double height,
            double? dpiScaleX,
            double? dpiScaleY)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            DpiScaleX = dpiScaleX;
            DpiScaleY = dpiScaleY;
        }

        internal double Left { get; }

        internal double Top { get; }

        internal double Width { get; }

        internal double Height { get; }

        internal double? DpiScaleX { get; }

        internal double? DpiScaleY { get; }
    }
}
