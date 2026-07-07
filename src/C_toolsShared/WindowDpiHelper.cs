using System.Windows;
using System.Windows.Media;

namespace C_toolsShared;

public static class WindowDpiHelper
{
    public static void InstallWindowSizeFromCurrentPixels(Window window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        InstallWindowSizeFromPixels(
            window,
            NormalizePixelValue(window.Width),
            NormalizePixelValue(window.Height),
            NormalizePixelValue(window.MinWidth),
            NormalizePixelValue(window.MinHeight),
            NormalizePixelValue(window.MaxWidth),
            NormalizePixelValue(window.MaxHeight));
    }

    public static void InstallWindowSizeFromPixels(
        Window window,
        double? widthPixels = null,
        double? heightPixels = null,
        double? minWidthPixels = null,
        double? minHeightPixels = null,
        double? maxWidthPixels = null,
        double? maxHeightPixels = null)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        void ApplySize()
        {
            ApplyWindowSizeFromPixels(
                window,
                widthPixels,
                heightPixels,
                minWidthPixels,
                minHeightPixels,
                maxWidthPixels,
                maxHeightPixels);
        }

        if (PresentationSource.FromVisual(window) != null)
        {
            ApplySize();
            return;
        }

        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler != null)
                window.SourceInitialized -= handler;
            ApplySize();
        };

        window.SourceInitialized += handler;
    }

    public static void ApplyWindowSizeFromPixels(
        Window window,
        double? widthPixels = null,
        double? heightPixels = null,
        double? minWidthPixels = null,
        double? minHeightPixels = null,
        double? maxWidthPixels = null,
        double? maxHeightPixels = null)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        var dpi = GetDpiScale(window);

        if (widthPixels.HasValue)
            window.Width = PixelsToDip(widthPixels.Value, dpi.DpiScaleX);
        if (heightPixels.HasValue)
            window.Height = PixelsToDip(heightPixels.Value, dpi.DpiScaleY);
        if (minWidthPixels.HasValue)
            window.MinWidth = PixelsToDip(minWidthPixels.Value, dpi.DpiScaleX);
        if (minHeightPixels.HasValue)
            window.MinHeight = PixelsToDip(minHeightPixels.Value, dpi.DpiScaleY);
        if (maxWidthPixels.HasValue)
            window.MaxWidth = PixelsToDip(maxWidthPixels.Value, dpi.DpiScaleX);
        if (maxHeightPixels.HasValue)
            window.MaxHeight = PixelsToDip(maxHeightPixels.Value, dpi.DpiScaleY);
    }

    internal static DpiScale GetDpiScale(Window window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = dpi.DpiScaleX > 0d ? dpi.DpiScaleX : 1d;
        var scaleY = dpi.DpiScaleY > 0d ? dpi.DpiScaleY : 1d;
        return new DpiScale(scaleX, scaleY);
    }

    internal static Rect ScaleBoundsForCurrentDpi(
        Rect dipBounds,
        double? savedScaleX,
        double? savedScaleY,
        DpiScale currentDpi)
    {
        if (dipBounds.IsEmpty)
            return dipBounds;

        return new Rect(
            ScaleDipValue(dipBounds.Left, savedScaleX, currentDpi.DpiScaleX),
            ScaleDipValue(dipBounds.Top, savedScaleY, currentDpi.DpiScaleY),
            ScaleDipValue(dipBounds.Width, savedScaleX, currentDpi.DpiScaleX),
            ScaleDipValue(dipBounds.Height, savedScaleY, currentDpi.DpiScaleY));
    }

    private static double PixelsToDip(double pixels, double scale)
    {
        var normalizedScale = scale > 0d ? scale : 1d;
        return pixels / normalizedScale;
    }

    private static double ScaleDipValue(double value, double? savedScale, double currentScale)
    {
        var normalizedCurrentScale = currentScale > 0d ? currentScale : 1d;
        if (!savedScale.HasValue || savedScale.Value <= 0d)
            return value;

        return value * savedScale.Value / normalizedCurrentScale;
    }

    private static double? NormalizePixelValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            return null;

        return value;
    }
}
