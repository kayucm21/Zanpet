using System.IO;
using System.Text.RegularExpressions;
using ZapretUI.Models;

namespace ZapretUI.Services;

public sealed class ClassicPresetImportResult
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();
}

public sealed class ClassicPresetImporter
{
    private static readonly Regex HostlistRegex = new(@"^--hostlist=lists/(.+)\.txt$", RegexOptions.Compiled);
    private static readonly Regex ExcludeRegex = new(@"^--hostlist-exclude=lists/(.+)\.txt$", RegexOptions.Compiled);
    private static readonly Regex IpsetRegex = new(@"^--ipset=lists/(.+)\.txt$", RegexOptions.Compiled);
    private static readonly Regex IpsetExcludeRegex = new(@"^--ipset-exclude=lists/(.+)\.txt$", RegexOptions.Compiled);
    private static readonly Regex BlobRegex = new(@"^--blob=(\w+):@bin/(.+\.bin)$", RegexOptions.Compiled);
    private static readonly Regex WfTcpRegex = new(@"^--wf-tcp-out=", RegexOptions.Compiled);
    private static readonly Regex WfUdpRegex = new(@"^--wf-udp-out=", RegexOptions.Compiled);
    private static readonly Regex SkipLineRegex = new(@"^(#|--ctrack-disable|--ipcache-)", RegexOptions.Compiled);

    /// <summary>
    /// Lua scripts that are auto-loaded by BuildArguments from engine/lua/ —
    /// the importer skips these to avoid duplicates.
    /// </summary>
    private static readonly HashSet<string> EngineProvidedLua = new(StringComparer.OrdinalIgnoreCase)
    {
        "zapret-lib.lua", "zapret-antidpi.lua",
        "zapret-multishake.lua", "zapret-auto.lua",
        "custom_funcs.lua", "custom_diag.lua",
        "zapret-obfs.lua", "zapret-wgobfs.lua",
        "zapret-16kb.lua", "zapret-pcap.lua",
        "zapret-tests.lua", "init_vars.lua",
    };

    public static List<Preset> AutoImport()
    {
        string classicDir = AppPaths.ClassicDataDir;
        string presetsDir = Path.Combine(classicDir, "presets");
        if (!Directory.Exists(presetsDir)) return new();

        var result = ImportFromDirectory(presetsDir);
        CopyClassicData(classicDir);
        return result.Presets;
    }

    public static ClassicPresetImportResult ImportFromDirectory(string presetsDir)
    {
        var result = new ClassicPresetImportResult();

        if (!Directory.Exists(presetsDir))
        {
            result.Errors.Add($"Directory not found: {presetsDir}");
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(presetsDir, "*.txt"))
        {
            try
            {
                var preset = ConvertPresetFile(file);
                if (preset is not null)
                {
                    result.Presets.Add(preset);
                    result.Imported++;
                }
                else
                {
                    result.Skipped++;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                result.Skipped++;
            }
        }

        return result;
    }

    public static Preset? ConvertPresetFile(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        var lines = File.ReadAllLines(filePath);
        var args = new List<string>();
        string? description = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("# Description:") && description is null)
                    description = line["# Description:".Length..].Trim();
                continue;
            }

            if (SkipLineRegex.IsMatch(line)) continue;

            string translated = TranslateLine(line);
            if (translated.Length == 0) continue;

            args.Add(translated);
        }

        if (args.Count == 0) return null;

        return new Preset
        {
            Name = fileName,
            Description = description ?? $"Classic preset: {fileName}",
            Args = args,
            IsBuiltIn = false,
            UsesHostlist = args.Any(a => a.Contains("{HOSTLIST}") || a.Contains("{HOSTLIST:")),
        };
    }

    private static string TranslateLine(string line)
    {
        var m = HostlistRegex.Match(line);
        if (m.Success)
            return $"{{HOSTLIST:{m.Groups[1].Value}}}";

        m = ExcludeRegex.Match(line);
        if (m.Success)
            return $"{{EXCLUDE:{m.Groups[1].Value}}}";

        m = IpsetRegex.Match(line);
        if (m.Success)
        {
            string name = m.Groups[1].Value;
            if (name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase))
                name = name["ipset-".Length..];
            return $"{{IPSET:{name}}}";
        }

        m = IpsetExcludeRegex.Match(line);
        if (m.Success)
        {
            string name = m.Groups[1].Value;
            if (name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase))
                name = name["ipset-".Length..];
            return $"{{IPSET_EXCLUDE:{name}}}";
        }

        m = BlobRegex.Match(line);
        if (m.Success)
            return $"--blob={m.Groups[1].Value}:@{{FILES}}\\fake\\{m.Groups[2].Value}";

        if (line.StartsWith("--lua-init=", StringComparison.Ordinal))
        {
            var luaMatch = Regex.Match(line, @"@lua/(.+)$");
            if (luaMatch.Success && EngineProvidedLua.Contains(luaMatch.Groups[1].Value))
                return "";
            return line;
        }

        if (WfTcpRegex.IsMatch(line))
            return "{WF_TCP}";

        if (WfUdpRegex.IsMatch(line))
            return "{WF_UDP}";

        line = line.Replace("@windivert.filter/", "@{{WF}}\\");

        return line;
    }

    public static void CopyClassicData(string sourceDir)
    {
        if (!Directory.Exists(sourceDir))
            return;

        string listsDir = AppPaths.ListsDir;
        string engineDir = AppPaths.EngineDir;
        string filesFakeDir = Path.Combine(engineDir, "files", "fake");
        string luaDir = AppPaths.LuaDir;
        string wfDir = AppPaths.WinDivertFilterDir;

        AppPaths.EnsureCreated();
        Directory.CreateDirectory(filesFakeDir);

        string srcBin = Path.Combine(sourceDir, "bin");
        if (Directory.Exists(srcBin))
            CopyDirectory(srcBin, filesFakeDir, "*.bin");

        string srcLua = Path.Combine(sourceDir, "lua");
        if (Directory.Exists(srcLua))
            CopyDirectory(srcLua, luaDir, "*.lua");

        string srcWf = Path.Combine(sourceDir, "windivert.filter");
        if (Directory.Exists(srcWf))
            CopyDirectory(srcWf, wfDir, "*.txt");

        string srcLists = Path.Combine(sourceDir, "lists");
        if (Directory.Exists(srcLists))
        {
            foreach (var f in Directory.EnumerateFiles(srcLists, "ipset-*.txt"))
            {
                string dest = Path.Combine(listsDir, Path.GetFileName(f));
                File.Copy(f, dest, overwrite: true);
            }
            foreach (var f in Directory.EnumerateFiles(srcLists, "*.txt"))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(listsDir, name);
                if (!File.Exists(dest))
                    File.Copy(f, dest, overwrite: false);
            }
        }

        string srcExe = Path.Combine(sourceDir, "exe");
        if (Directory.Exists(srcExe))
        {
            foreach (var f in Directory.EnumerateFiles(srcExe, "*.dll"))
            {
                string dest = Path.Combine(engineDir, Path.GetFileName(f));
                File.Copy(f, dest, overwrite: true);
            }
            foreach (var f in Directory.EnumerateFiles(srcExe, "*.sys"))
            {
                string dest = Path.Combine(engineDir, Path.GetFileName(f));
                File.Copy(f, dest, overwrite: true);
            }
            // Copy engine binaries (winws2.exe + cygwin1.dll) so the app works
            // without downloading from GitHub on every fresh install.
            foreach (var name in new[] { "winws2.exe", "cygwin1.dll", "mdig.exe", "ip2net.exe" })
            {
                string src = Path.Combine(srcExe, name);
                string dest = Path.Combine(engineDir, name);
                if (File.Exists(src))
                    File.Copy(src, dest, overwrite: true);
            }

            string bundledTgWs = Path.Combine(srcExe, "TgWsProxy.exe");
            if (File.Exists(bundledTgWs))
            {
                string tgwsDir = Path.Combine(engineDir, "tgws");
                Directory.CreateDirectory(tgwsDir);
                File.Copy(bundledTgWs, Path.Combine(tgwsDir, "TgWsProxy.exe"), overwrite: true);
            }

            string srcVersion = Path.Combine(sourceDir, "installed_version.txt");
            if (File.Exists(srcVersion))
            {
                string destVersion = Path.Combine(engineDir, "installed_version.txt");
                File.Copy(srcVersion, destVersion, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Copy bundled engine binaries and data from ClassicData into %LOCALAPPDATA%\ZapretUI\engine.
    /// Safe to call on every startup — overwrites only engine files.
    /// </summary>
    public static bool EnsureEngineBootstrapped()
    {
        string classicDir = AppPaths.ClassicDataDir;
        if (!Directory.Exists(classicDir))
            return File.Exists(AppPaths.WinwsExe);

        CopyClassicData(classicDir);
        return File.Exists(AppPaths.WinwsExe);
    }

    private static void CopyDirectory(string source, string dest, string pattern)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source, pattern))
        {
            string destFile = Path.Combine(dest, Path.GetFileName(f));
            File.Copy(f, destFile, overwrite: true);
        }
    }
}
