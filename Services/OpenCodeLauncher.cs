using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ZapretUI.Services;

/// <summary>Starts OpenCode HTTP server — visible terminal or background.</summary>
public static class OpenCodeLauncher
{
    private const string ScriptName = "Start-OpenCodeServe.cmd";

    public static async Task<bool> TryEnsureRunningAsync(string baseUrl, CancellationToken ct = default)
    {
        var api = new OpenCodeApiService();
        var state = await ProbeAsync(api, baseUrl, ct).ConfigureAwait(false);
        if (state == OpenCodeServerState.Running)
            return true;

        if (state == OpenCodeServerState.PortBlocked)
            return await WaitForHealthAsync(api, baseUrl, ct).ConfigureAwait(false);

        var (installed, _, exe) = await OpenCodeInstaller.EnsureInstalledAsync(ct: ct).ConfigureAwait(false);
        if (!installed || exe is null)
            return false;

        string host = ParseHost(baseUrl);
        int port = ParsePort(baseUrl);
        if (!TryStartServeHidden(exe, host, port))
            return false;

        return await WaitForHealthAsync(api, baseUrl, ct).ConfigureAwait(false);
    }

    /// <summary>Opens a normal Windows cmd window with opencode serve (for manual use).</summary>
    public static async Task<(bool Ok, string Message)> LaunchInTerminalAsync(
        string? baseUrl = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string url = baseUrl ?? VoiceBackendDefaults.LocalOpenCodeUrl;
        string host = ParseHost(url);
        int port = ParsePort(url);

        var api = new OpenCodeApiService();
        var state = await ProbeAsync(api, url, ct).ConfigureAwait(false);

        if (state == OpenCodeServerState.Running)
        {
            string? resolvedExe = OpenCodeResolver.Resolve();
            if (resolvedExe is not null)
                WriteBundledScript(resolvedExe, host, port, url);
            return (true, $"OpenCode уже работает на {url}. Голосовой помощник подключён — второй сервер не нужен.");
        }

        if (state == OpenCodeServerState.PortBlocked)
        {
            return (false,
                $"Порт {port} занят, но сервер не отвечает. Закройте OpenCode Desktop или выполните: taskkill /F /IM opencode.exe");
        }

        var (installed, installMsg, exe) = await OpenCodeInstaller.EnsureInstalledAsync(progress, ct)
            .ConfigureAwait(false);
        if (!installed || exe is null)
            return (false, installMsg);

        string script = WriteBundledScript(exe, host, port, url);
        if (script.Length == 0)
            return (false, "Не удалось создать Start-OpenCodeServe.cmd");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? ""
            });
            return (true, "OpenCode запускается. Дождитесь строки listening в окне cmd.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<(bool Ok, string Message)> GetServerStatusAsync(string? baseUrl = null,
        CancellationToken ct = default)
    {
        string url = baseUrl ?? VoiceBackendDefaults.LocalOpenCodeUrl;
        var api = new OpenCodeApiService();
        var state = await ProbeAsync(api, url, ct).ConfigureAwait(false);
        return state switch
        {
            OpenCodeServerState.Running => (true, $"OpenCode работает: {url}"),
            OpenCodeServerState.PortBlocked => (false, "Порт занят, сервер не отвечает"),
            _ => (false, "OpenCode не запущен")
        };
    }

    public static string GetBundledScriptPath() =>
        Path.Combine(GetAppDirectory(), ScriptName);

    private enum OpenCodeServerState
    {
        NotRunning,
        Running,
        PortBlocked
    }

    private static async Task<OpenCodeServerState> ProbeAsync(OpenCodeApiService api, string baseUrl,
        CancellationToken ct)
    {
        var (healthy, _) = await api.CheckHealthAsync(baseUrl, null, null, ct).ConfigureAwait(false);
        if (healthy)
            return OpenCodeServerState.Running;

        string host = ParseHost(baseUrl);
        int port = ParsePort(baseUrl);
        if (IsPortBusy(host, port))
            return OpenCodeServerState.PortBlocked;

        return OpenCodeServerState.NotRunning;
    }

    private static string WriteBundledScript(string opencodeExe, string host, int port, string healthUrl)
    {
        string path = GetBundledScriptPath();
        string quoted = QuoteForCmd(opencodeExe);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string psCheck =
            "try { $r=Invoke-RestMethod '" + healthUrl + "/global/health' -TimeoutSec 2; " +
            "if($r.healthy){exit 0} else {exit 1} } catch { exit 1 }";

        string body =
            "@echo off\r\n" +
            "setlocal\r\n" +
            "title OpenCode Server (Zapret UI)\r\n" +
            "powershell -NoProfile -Command \"" + psCheck + "\" >nul 2>&1\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "    echo.\r\n" +
            "    echo OpenCode is ALREADY running at " + healthUrl + "\r\n" +
            "    echo Voice assistant can connect now. Do not start a second server.\r\n" +
            "    echo.\r\n" +
            "    pause\r\n" +
            "    exit /b 0\r\n" +
            ")\r\n" +
            "set OPENCODE_CLIENT=\r\n" +
            "set OPENCODE_SERVER_PASSWORD=\r\n" +
            "set OPENCODE_SERVER_USERNAME=\r\n" +
            "cd /d \"" + home + "\"\r\n" +
            "echo.\r\n" +
            "echo OpenCode server - Zapret UI\r\n" +
            "echo " + healthUrl + "\r\n" +
            "echo Close this window to stop the server.\r\n" +
            "echo.\r\n" +
            quoted + " serve --port " + port + " --hostname " + host + "\r\n" +
            "if errorlevel 1 (\r\n" +
            "    echo.\r\n" +
            "    echo ERROR: could not start - port " + port + " may already be in use.\r\n" +
            "    echo If voice assistant works, server is already running.\r\n" +
            "    echo To restart: taskkill /F /IM opencode.exe\r\n" +
            ")\r\n" +
            "echo.\r\n" +
            "echo Server stopped.\r\n" +
            "pause\r\n";

        try
        {
            File.WriteAllText(path, body);
            return path;
        }
        catch
        {
            return "";
        }
    }

    private static async Task<bool> WaitForHealthAsync(OpenCodeApiService api, string baseUrl, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            var (ok, _) = await api.CheckHealthAsync(baseUrl, null, null, ct).ConfigureAwait(false);
            if (ok) return true;
        }
        return false;
    }

    private static bool IsPortBusy(string host, int port)
    {
        try
        {
            string ip = host is "localhost" or "127.0.0.1" ? "127.0.0.1" : host;
            using var listener = new TcpListener(IPAddress.Parse(ip), port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string QuoteForCmd(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    private static string GetAppDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppDomain.CurrentDomain.BaseDirectory;

    private static bool TryStartServeHidden(string exe, string host, int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"serve --port {port} --hostname {host}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            psi.Environment["OPENCODE_CLIENT"] = "";
            psi.Environment.Remove("OPENCODE_SERVER_PASSWORD");
            psi.Environment.Remove("OPENCODE_SERVER_USERNAME");
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ParsePort(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl.TrimEnd('/'));
            return uri.Port > 0 ? uri.Port : 4096;
        }
        catch
        {
            return 4096;
        }
    }

    private static string ParseHost(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl.TrimEnd('/'));
            return string.IsNullOrWhiteSpace(uri.Host) ? "127.0.0.1" : uri.Host;
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
