using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ZapretUI.Services;

/// <summary>Ensure the UI runs elevated (WinDivert needs admin). Relaunches with UAC if needed.</summary>
public static class AdminElevation
{
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restart this app with "Run as administrator". Returns false if user cancelled UAC.</summary>
    public static bool TryRelaunchElevated()
    {
        string? exe = ResolveExecutablePath();
        if (exe is null) return false;

        string arguments = BuildArgumentString();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined UAC.
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return path;

        // `dotnet ZapretUI.dll` — prefer built/published ZapretUI.exe next to the DLL.
        string sibling = Path.Combine(AppContext.BaseDirectory, "ZapretUI.exe");
        if (File.Exists(sibling)) return sibling;

        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string BuildArgumentString()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length <= 1) return "";

        return string.Join(' ', args.Skip(1).Select(Quote));
    }

    private static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (!arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\t'))
            return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}
