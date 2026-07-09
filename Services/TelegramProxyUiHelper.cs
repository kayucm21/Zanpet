using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretUI.Services;

/// <summary>
/// Auto-confirms Telegram Desktop's native "enable proxy?" dialog so the user never has to.
/// </summary>
internal static class TelegramProxyUiHelper
{
    private const int BmClick = 0x00F5;

    private static readonly Regex ConfirmButton = new(
        @"^(Connect|OK|Use|Enable|Подключ|Включ|Использ|Да|Yes)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void RunFor(TimeSpan duration, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                EnumWindows((hwnd, _) =>
                {
                    if (!IsTelegramProxyDialog(hwnd)) return true;
                    if (TryClickConfirmButton(hwnd)) return false;
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* best-effort */ }

            Thread.Sleep(400);
        }
    }

    private static bool IsTelegramProxyDialog(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;

        var cls = GetClassName(hwnd);
        if (cls is not ("#32770" or "Qt51517QWindowIcon" or "Qt6QWindowIcon" or "Qt5QWindowIcon"))
        {
            // Telegram main window may host the proxy prompt as Qt overlay.
            if (!cls.StartsWith("Qt", StringComparison.Ordinal)) return false;
        }

        string title = GetWindowText(hwnd);
        if (title.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
            && (title.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                || title.Contains("прокси", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Standard Qt/Telegram: dialog text is in child static labels.
        return ContainsProxyPrompt(hwnd);
    }

    private static bool ContainsProxyPrompt(IntPtr hwnd)
    {
        bool found = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            string t = GetWindowText(child);
            if (t.Contains("MTProto", StringComparison.OrdinalIgnoreCase)
                || t.Contains("127.0.0.1", StringComparison.Ordinal)
                || t.Contains("прокси", StringComparison.OrdinalIgnoreCase)
                || t.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool TryClickConfirmButton(IntPtr hwnd)
    {
        bool clicked = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (GetClassName(child) != "Button") return true;
            string label = GetWindowText(child);
            if (!ConfirmButton.IsMatch(label.Trim())) return true;
            SendMessage(child, BmClick, IntPtr.Zero, IntPtr.Zero);
            clicked = true;
            return false;
        }, IntPtr.Zero);
        return clicked;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
