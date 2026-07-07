using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsDddPlugin;

internal static class DddInputLanguageHelper
{
    private const string ChineseKeyboardLayoutId = "00000804";
    private const string EnglishKeyboardLayoutId = "00000409";
    private const uint KlfActivate = 0x00000001;
    private const uint WmInputLangChangeRequest = 0x0050;

    internal static void SwitchToChineseForWindow(Window window, string actionName)
    {
        if (window == null)
            return;

        SwitchInputLanguage(ChineseKeyboardLayoutId, new WindowInteropHelper(window).Handle, actionName);
    }

    internal static void RestoreEnglishToAcadMainWindow(string actionName)
    {
        SwitchInputLanguage(EnglishKeyboardLayoutId, AcAp.MainWindow?.Handle ?? IntPtr.Zero, actionName);
    }

    private static void SwitchInputLanguage(string keyboardLayoutId, IntPtr targetHandle, string actionName)
    {
        try
        {
            var keyboardLayout = LoadKeyboardLayout(keyboardLayoutId, KlfActivate);
            if (keyboardLayout == IntPtr.Zero)
                return;

            _ = ActivateKeyboardLayout(keyboardLayout, 0);
            if (targetHandle != IntPtr.Zero)
                _ = PostMessage(targetHandle, WmInputLangChangeRequest, IntPtr.Zero, keyboardLayout);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(actionName, ex);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadKeyboardLayout(string keyboardLayoutId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr keyboardLayout, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
