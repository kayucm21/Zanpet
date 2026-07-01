using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using ZapretUI_Mobile.Models;

namespace ZapretUI_Mobile.Services;

public class XrayService
{
    private Process? _xrayProcess;
    private string _xrayPath = string.Empty;
    private string _configPath = string.Empty;

    public bool IsRunning => _xrayProcess != null && !_xrayProcess.HasExited;

    public async Task<bool> StartAsync(VpnServer server)
    {
        try
        {
            await CopyXrayBinaryAsync();
            GenerateConfig(server);
            await StartXrayProcessAsync();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"XrayService.Start error: {ex.Message}");
            return false;
        }
    }

    public async Task StopAsync()
    {
        StopXrayProcess();
        await Task.CompletedTask;
    }

    private async Task CopyXrayBinaryAsync()
    {
        var cacheDir = global::Android.App.Application.Context.CacheDir?.AbsolutePath ?? 
            throw new InvalidOperationException("Cannot access cache directory");

        _xrayPath = Path.Combine(cacheDir, "xray");

        if (File.Exists(_xrayPath))
            return;

        try
        {
            using var inputStream = global::Android.App.Application.Context.Assets?.Open("xray");
            if (inputStream == null)
                throw new FileNotFoundException("xray binary not found in assets");

            using var outputStream = File.Create(_xrayPath);
            await inputStream.CopyToAsync(outputStream);

            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x {_xrayPath}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to copy xray binary: {ex.Message}");
        }
    }

    private void GenerateConfig(VpnServer server)
    {
        var cacheDir = global::Android.App.Application.Context.CacheDir?.AbsolutePath ?? 
            throw new InvalidOperationException("Cannot access cache directory");

        _configPath = Path.Combine(cacheDir, "config.json");

        var streamSettings = new Dictionary<string, object>
        {
            ["network"] = server.Network,
            ["security"] = server.Security,
            ["realitySettings"] = new
            {
                serverName = server.Sni,
                fingerprint = server.Fingerprint,
                publicKey = server.PublicKey,
                shortId = server.ShortId,
                serverNames = new[] { server.Sni }
            }
        };

        if (server.Network == "xhttp")
        {
            streamSettings["xhttpSettings"] = new
            {
                path = server.Path,
                mode = server.Mode
            };
        }

        var config = new
        {
            log = new
            {
                loglevel = "warning"
            },
            inbounds = new object[]
            {
                new
                {
                    tag = "socks-in",
                    port = 10808,
                    listen = "127.0.0.1",
                    protocol = "socks",
                    settings = new
                    {
                        udp = true
                    },
                    sniffing = new
                    {
                        enabled = true,
                        destOverride = new[] { "http", "tls" }
                    }
                },
                new
                {
                    tag = "http-in",
                    port = 10809,
                    listen = "127.0.0.1",
                    protocol = "http"
                }
            },
            outbounds = new object[]
            {
                new
                {
                    tag = "proxy",
                    protocol = "vless",
                    settings = new
                    {
                        vnext = new object[]
                        {
                            new
                            {
                                address = server.Address,
                                port = server.Port,
                                users = new object[]
                                {
                                    new
                                    {
                                        id = server.Id,
                                        flow = server.Flow,
                                        encryption = "none",
                                        level = 0
                                    }
                                }
                            }
                        }
                    },
                    streamSettings = streamSettings
                },
                new
                {
                    tag = "direct",
                    protocol = "freedom"
                }
            },
            routing = new
            {
                domainStrategy = "IPIfNonMatch",
                rules = new object[]
                {
                    new
                    {
                        type = "field",
                        ip = new[] { "geoip:private" },
                        outboundTag = "direct"
                    }
                }
            }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(_configPath, json);
    }

    private async Task StartXrayProcessAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _xrayPath,
            Arguments = $"run -c {_configPath}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_xrayPath)
        };

        _xrayProcess = new Process { StartInfo = psi };
        _xrayProcess.Start();

        _ = Task.Run(async () =>
        {
            while (!_xrayProcess.StandardOutput.EndOfStream)
            {
                var line = await _xrayProcess.StandardOutput.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                    System.Diagnostics.Debug.WriteLine($"[XRAY] {line}");
            }
        });

        _ = Task.Run(async () =>
        {
            while (!_xrayProcess.StandardError.EndOfStream)
            {
                var line = await _xrayProcess.StandardError.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                    System.Diagnostics.Debug.WriteLine($"[XRAY ERR] {line}");
            }
        });

        await Task.Delay(500);
    }

    private void StopXrayProcess()
    {
        try
        {
            if (_xrayProcess != null && !_xrayProcess.HasExited)
            {
                _xrayProcess.Kill();
                _xrayProcess.WaitForExit(2000);
            }
        }
        catch { }

        _xrayProcess?.Dispose();
        _xrayProcess = null;
    }
}
