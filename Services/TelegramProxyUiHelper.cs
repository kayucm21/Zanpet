using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretUI.Services;

/// <summary>
/// Auto-confirms Telegram Desktop's native "enable proxy?" dialog (Qt + Win32).
/// </summary>
internal static class TelegramProxyUiHelper
{
    private const int BmClick = 0x00F5;
    private const int WmLbuttonDown = 0x0201;
    private const int WmLbuttonUp = 0x0202;

    private static readonly Regex ConfirmButton = new(
        @"(подключ|connect|включ|enable|использ|use proxy|ok|да|yes)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryClickOnce()
    {
        bool clicked = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsTelegramProxyUi(hwnd)) return true;
            if (TryClickConfirm(hwnd))
            {
                clicked = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return clicked;
    }

    public static void RunFor(TimeSpan duration, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try { TryClickOnce(); }
            catch { /* best-effort */ }
            Thread.Sleep(350);
        }
    }

    private static bool IsTelegramProxyUi(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;

        string title = GetWindowText(hwnd);
        if (title.Contains("прокси", StringComparison.OrdinalIgnoreCase)
            || title.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            return true;

        var cls = GetClassName(hwnd);
        if (!cls.StartsWith("Qt", StringComparison.Ordinal)
            && cls is not ("#32770" or "Qt51517QWindowIcon" or "Qt6QWindowIcon" or "Qt5QWindowIcon"))
            return false;

        return ContainsProxyPrompt(hwnd);
    }

    private static bool ContainsProxyPrompt(IntPtr hwnd)
    {
        bool found = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            string t = GetWindowText(child);
            if (t.Contains("127.0.0.1", StringComparison.Ordinal)
                || t.Contains("MTProto", StringComparison.OrdinalIgnoreCase)
                || t.Contains("прокси", StringComparison.OrdinalIgnoreCase)
                || t.Contains("proxy", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Прокси-сервер", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool TryClickConfirm(IntPtr hwnd)
    {
        bool clicked = false;
        EnumChildWindows(hwnd, (child, _) =>
        {
            string label = GetWindowText(child).Trim();
            if (label.Length == 0 || !ConfirmButton.IsMatch(label)) return true;

            if (GetClassName(child) == "Button")
                SendMessage(child, BmClick, IntPtr.Zero, IntPtr.Zero);
            else
                ClickAtCenter(child);

            clicked = true;
            return false;
        }, IntPtr.Zero);

        if (clicked) return true;

        // Qt: button text may live on a nested label — click the largest visible child button area.
        return TryClickBottomPrimaryButton(hwnd);
    }

    private static bool TryClickBottomPrimaryButton(IntPtr hwnd)
    {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;

        EnumChildWindows(hwnd, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            if (!GetWindowRect(child, out var r)) return true;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w < 80 || h < 24 || h > 80) return true;
            int area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                best = child;
            }
            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero) return false;
        ClickAtCenter(best);
        return true;
    }

    private static void ClickAtCenter(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        int x = (r.Left + r.Right) / 2;
        int y = (r.Top + r.Bottom) / 2;
        int lParam = (y << 16) | (x & 0xFFFF);
        SendMessage(hwnd, WmLbuttonDown, (IntPtr)1, (IntPtr)lParam);
        SendMessage(hwnd, WmLbuttonUp, IntPtr.Zero, (IntPtr)lParam);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
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

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
