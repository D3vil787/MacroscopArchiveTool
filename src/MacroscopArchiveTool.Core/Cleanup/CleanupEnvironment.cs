using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace MacroscopArchiveTool.Core.Cleanup;

public sealed record PostgresService(string Name, int Version, bool Running, string? InstallDir, string? DataDir);

/// <summary>Everything the cleanup reads from or does to the operating system; real implementation: <see cref="WindowsCleanupEnvironment"/>.</summary>
public interface ICleanupEnvironment
{
    /// <summary>Roots of local fixed volumes ("A:\", "C:\", …).</summary>
    IReadOnlyList<string> VolumeRoots();
    /// <summary>C:\ProgramData\Macroscop (log folders Logs_*).</summary>
    string MacroscopDataDir { get; }
    /// <summary>C:\Program Files\PostgreSQL.</summary>
    string PostgresRoot { get; }
    /// <summary>Folder of this tool (reports, files left by updates).</summary>
    string AppDir { get; }
    string TempDir { get; }
    IReadOnlyList<PostgresService> PostgresServices();
    /// <summary>archive_event_* databases with sizes from the running PostgreSQL; null and an error text when not available.</summary>
    IReadOnlyList<(string Name, long Bytes)>? EventDatabases(PostgresService active, string password, out string? error);
    void RunUninstaller(string exe, Action<string> log);
    void DeleteService(string name, Action<string> log);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCleanupEnvironment : ICleanupEnvironment
{
    public string MacroscopDataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Macroscop");
    public string PostgresRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
    public string AppDir { get; } = AppContext.BaseDirectory;
    public string TempDir { get; } = Path.GetTempPath();

    public IReadOnlyList<string> VolumeRoots()
        => DriveInfo.GetDrives().Where(d => { try { return d.IsReady && d.DriveType == DriveType.Fixed; } catch (Exception) { return false; } })
                                .Select(d => d.RootDirectory.FullName).ToList();

    public IReadOnlyList<PostgresService> PostgresServices()
    {
        var list = new List<PostgresService>();
        var outp = Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Service | Where-Object { $_.Name -like 'postgresql*' } | ForEach-Object { $_.Name + '|' + $_.State + '|' + $_.PathName }\"", out _);
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Parse(line) is { } s) list.Add(s);
        return list;
    }

    /// <summary>"postgresql-x64-16|Running|"C:\…\PostgreSQL\16\bin\pg_ctl.exe" runservice -N "…" -D "C:\ProgramData\postgredb16" -w".</summary>
    public static PostgresService? Parse(string line)
    {
        var p = line.Split('|', 3);
        if (p.Length < 3) return null;
        var exe = Regex.Match(p[2], "^\"?(?<exe>[^\"]+?pg_ctl\\.exe)\"?", RegexOptions.IgnoreCase);
        var data = Regex.Match(p[2], "-D\\s+\"(?<d>[^\"]+)\"|-D\\s+(?<d>\\S+)");
        var install = exe.Success ? Path.GetDirectoryName(Path.GetDirectoryName(exe.Groups["exe"].Value)) : null;
        var ver = Regex.Match(install ?? p[0], @"(\d+)(?:\.\d+)?\D*$");
        if (!ver.Success) ver = Regex.Match(p[0], @"(\d+)");
        if (!ver.Success) return null;
        return new PostgresService(p[0], int.Parse(ver.Groups[1].Value), p[1].Equals("Running", StringComparison.OrdinalIgnoreCase),
                                   install, data.Success ? data.Groups["d"].Value : null);
    }

    public IReadOnlyList<(string Name, long Bytes)>? EventDatabases(PostgresService active, string password, out string? error)
    {
        error = null;
        var psql = active.InstallDir == null ? null : Path.Combine(active.InstallDir, "bin", "psql.exe");
        if (psql == null || !File.Exists(psql)) { error = "psql.exe не найден"; return null; }
        var psi = new ProcessStartInfo(psql, "-h localhost -U postgres -d postgres -At -F \"|\" -w -c \"select datname, pg_database_size(datname) from pg_database where datname like 'archive_event%'\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.Environment["PGPASSWORD"] = password;
        psi.Environment["PGCONNECT_TIMEOUT"] = "5";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } error = "PostgreSQL не ответил"; return null; }
        if (p.ExitCode != 0) { error = e.Result.Trim(); return null; }
        return o.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')).Where(a => a.Length == 2 && long.TryParse(a[1], out _))
            .Select(a => (a[0], long.Parse(a[1]))).ToList();
    }

    public void RunUninstaller(string exe, Action<string> log)
    {
        log($"> {exe} --mode unattended");
        var code = RunCode(exe, "--mode unattended", out var output, TimeSpan.FromMinutes(15));
        if (output.Trim().Length > 0) log("  " + output.Trim());
        if (code != 0) throw new IOException($"Деинсталлятор завершился с кодом {code}.");
    }

    public void DeleteService(string name, Action<string> log)
    {
        log($"> sc.exe delete \"{name}\"");
        var code = RunCode("sc.exe", $"delete \"{name}\"", out var output, TimeSpan.FromMinutes(1));
        if (code != 0) throw new IOException($"Не удалось удалить службу {name}: {output.Trim()}");
    }

    private static string Run(string exe, string args, out int code)
    {
        code = RunCode(exe, args, out var o, TimeSpan.FromMinutes(2));
        return o;
    }

    private static int RunCode(string exe, string args, out string output, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = oem; psi.StandardErrorEncoding = oem;
        }
        catch (Exception) { }
        using var p = Process.Start(psi) ?? throw new IOException($"Не удалось запустить {exe}");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch (Exception) { } output = "превышено время ожидания"; return -1; }
        output = o.Result + e.Result;
        return p.ExitCode;
    }
}
