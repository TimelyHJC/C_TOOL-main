using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace C_toolsShared;

public static class WindowTitleBarHelper
{
    private const int DwAttributeUseImmersiveDarkMode = 20;
    private const int DwAttributeUseImmersiveDarkModeLegacy = 19;
    private const int DwAttributeBorderColor = 34;
    private const int DwAttributeCaptionColor = 35;
    private const int DwAttributeTextColor = 36;
    private const double TitleBarIconViewBoxWidth = 378.58d;
    private const double TitleBarIconViewBoxHeight = 376.4d;
    private static ImageSource? _cachedWindowIcon;

    public static void TryApplyDarkTitleBar(
        Window window,
        byte backgroundR = 0x23,
        byte backgroundG = 0x28,
        byte backgroundB = 0x30,
        byte textR = 0xE6,
        byte textG = 0xE8,
        byte textB = 0xEA,
        bool applyCaptionColorToBorder = false)
    {
        try
        {
            TryApplySharedWindowIcon(window);

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            SetDwmIntAttribute(handle, DwAttributeUseImmersiveDarkMode, 1);
            SetDwmIntAttribute(handle, DwAttributeUseImmersiveDarkModeLegacy, 1);
            if (applyCaptionColorToBorder)
                SetDwmIntAttribute(handle, DwAttributeBorderColor, ToColorRef(backgroundR, backgroundG, backgroundB));
            SetDwmIntAttribute(handle, DwAttributeCaptionColor, ToColorRef(backgroundR, backgroundG, backgroundB));
            SetDwmIntAttribute(handle, DwAttributeTextColor, ToColorRef(textR, textG, textB));
        }
        catch
        {
            // 标题栏着色是增强项；当前系统不支持时直接忽略即可。
        }
    }

    private static void TryApplySharedWindowIcon(Window window)
    {
        try
        {
            if (window.Icon != null)
                return;

            var icon = GetSharedWindowIcon();
            if (icon != null)
                window.Icon = icon;
        }
        catch
        {
            // 标题栏图标是增强项；生成失败时保持系统默认图标。
        }
    }

    private static ImageSource? GetSharedWindowIcon()
    {
        if (_cachedWindowIcon != null)
            return _cachedWindowIcon;

        const int pixelSize = 256;
        const double padding = 24d;

        var scale = Math.Min(
            (pixelSize - padding * 2d) / TitleBarIconViewBoxWidth,
            (pixelSize - padding * 2d) / TitleBarIconViewBoxHeight);
        var offsetX = (pixelSize - TitleBarIconViewBoxWidth * scale) / 2d;
        var offsetY = (pixelSize - TitleBarIconViewBoxHeight * scale) / 2d;

        var iconDrawing = new DrawingGroup
        {
            Transform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new ScaleTransform(scale, scale),
                    new TranslateTransform(offsetX, offsetY)
                }
            }
        };

        iconDrawing.Children.Add(CreateStrokeDrawing(
            "M 318.13,152.17 l 0.74,0.74 a 40,40 0 0 1 11.72,28.28 V 321.4 a 40,40 0 0 1 -40,40 H 55 a 40,40 0 0 1 -40,-40 V 85.81 a 40,40 0 0 1 40,-40 H 195.2 a 40,40 0 0 1 28.29,11.71 l 5.07,5.08",
            Color.FromRgb(0x34, 0x88, 0xC9),
            30d,
            PenLineCap.Square,
            PenLineJoin.Round));
        iconDrawing.Children.Add(CreateStrokeDrawing(
            "M 357.72,57.82 a 20,20 0 0 1 0,28.29 l -49.65,49.66 -23,23 a 46,46 0 0 1 -20.61,11.9 l -75.55,20.24 a 1,1 0 0 1 -1.22,-1.23 l 20.24,-75.55 a 46,46 0 0 1 11.91,-20.62 l 72.67,-72.67 a 20,20 0 0 1 28.29,0 Z",
            Color.FromRgb(0xE7, 0x1F, 0x19),
            30d,
            PenLineCap.Flat,
            PenLineJoin.Miter));
        iconDrawing.Children.Add(CreateStrokeDrawing(
            "M 68.9,182.92 A 135.85,135.85 0 0 0 204.75,318.77",
            Color.FromRgb(0x34, 0x88, 0xC9),
            30d,
            PenLineCap.Round,
            PenLineJoin.Miter));
        iconDrawing.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawDrawing(iconDrawing);
        }

        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        _cachedWindowIcon = bitmap;
        return _cachedWindowIcon;
    }

    private static GeometryDrawing CreateStrokeDrawing(
        string geometryData,
        Color color,
        double strokeWidth,
        PenLineCap lineCap,
        PenLineJoin lineJoin)
    {
        var geometry = Geometry.Parse(geometryData);
        geometry.Freeze();

        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new Pen(brush, strokeWidth)
        {
            StartLineCap = lineCap,
            EndLineCap = lineCap,
            LineJoin = lineJoin,
            MiterLimit = 10d
        };
        pen.Freeze();

        var drawing = new GeometryDrawing(null, pen, geometry);
        drawing.Freeze();
        return drawing;
    }

    private static void SetDwmIntAttribute(IntPtr hwnd, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, Marshal.SizeOf<int>());
    }

    private static int ToColorRef(byte r, byte g, byte b) =>
        r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
