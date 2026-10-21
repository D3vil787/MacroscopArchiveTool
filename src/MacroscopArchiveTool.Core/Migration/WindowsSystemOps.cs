using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MacroscopArchiveTool.Core.Migration;

/// <summary>
/// Windows implementation on built-in tools only (diskpart, mountvol, PowerShell Storage cmdlets, schtasks),
/// so nothing extra has to be installed on a Macroscop server. Every command is logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemOps : ISystemOps
{
    static WindowsSystemOps() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacroscopArchiveTool");

    public string RootOf(char letter) => $"{char.ToUpperInvariant(letter)}:\\";

    public bool IsAdministrator()
        => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var virt = VirtualDiskLetters();
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var letter = char.ToUpperInvariant(d.Name[0]);
                virt.TryGetValue(letter, out var file);
                list.Add(new VolumeInfo(letter, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, file != null, file));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    public char FindFreeLetter(IEnumerable<char> avoid)
    {
        var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        foreach (var a in avoid) used.Add(char.ToUpperInvariant(a));
        for (var c = 'Z'; c >= 'F'; c--) if (!used.Contains(c)) return c;
        throw new InvalidOperationException("Нет свободной буквы диска.");
    }

    public void CreateVhdx(string vhdxPath, long maxBytes, string label, char letter, Action<string> log)
    {
        if (File.Exists(vhdxPath)) throw new IOException($"Файл уже существует: {vhdxPath}");
        var mb = Math.Max(1024, maxBytes / (1024 * 1024));
        Diskpart(log,
            $"create vdisk file=\"{vhdxPath}\" maximum={mb} type=expandable",
            $"select vdisk file=\"{vhdxPath}\"",
            "attach vdisk",
            "online disk noerr",
            "attributes disk clear readonly noerr",
            "convert gpt",
            "create partition primary",
            $"format fs=ntfs label=\"{Sanitize(label)}\" unit=64K quick",
            $"assign letter={letter}");
    }

    public void AttachVhdx(string vhdxPath, Action<string> log)
        => Diskpart(log, $"select vdisk file=\"{vhdxPath}\"", "attach vdisk", "online disk noerr");

    public void DetachVhdx(string vhdxPath, Action<string> log)
        => Diskpart(log, $"select vdisk file=\"{vhdxPath}\"", "detach vdisk");

    public void EnsureVhdxMounted(string vhdxPath, char letter, Action<string> log)
    {
        if (Directory.Exists(RootOf(letter))) return;
        log($"Подключаю виртуальный диск {vhdxPath} как {letter}:");
        Directory.CreateDirectory(DataDir);
        var script = Path.Combine(DataDir, $"mount_{letter}.ps1");
        File.WriteAllText(script, AttachScript(vhdxPath, letter), new UTF8Encoding(true));
        Checked(log, "powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"");
        for (int i = 0; i < 20 && !Directory.Exists(RootOf(letter)); i++) Thread.Sleep(500);
        if (!Directory.Exists(RootOf(letter))) throw new IOException($"Виртуальный диск подключён, но том {letter}: не появился. Журнал: {Path.Combine(DataDir, "attach.log")}");
    }

    public DiskInfo? DiskOf(char letter)
    {
        try
        {
            var outp = Ps($"$p = Get-Partition -DriveLetter {letter} -ErrorAction Stop; $d = $p | Get-Disk; " +
                          $"$o = @(Get-Partition -DiskNumber $d.Number | Where-Object {{ $_.DriveLetter -and $_.DriveLetter -ne '{letter}' }}).Count; " +
                          "\"$($d.UniqueId)|$($d.Number)|$o|$($d.Location)\"").Trim();
            var parts = outp.Split('|', 4);
            if (parts.Length < 4 || parts[0].Length == 0) return null;
            return new DiskInfo(parts[0], int.Parse(parts[1]), parts[3], int.Parse(parts[2]));
        }
        catch (Exception) { return null; }
    }

    public void SetDiskOffline(string uniqueId, bool offline, Action<string> log)
    {
        var id = uniqueId.Replace("'", "''");
        var find = $"$d = Get-Disk | Where-Object UniqueId -eq '{id}' | Select-Object -First 1; if (-not $d) {{ throw 'Диск {id} не найден' }}; ";
        if (offline)
            PsChecked(log, find + "Set-Disk -Number $d.Number -IsOffline $true");
        else
            PsChecked(log, find + "Set-Disk -Number $d.Number -IsOffline $false; Set-Disk -Number $d.Number -IsReadOnly $false; Start-Sleep -Seconds 2; " +
                           "Get-Partition -DiskNumber $d.Number | Where-Object DriveLetter | ForEach-Object { " +
                           "Remove-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $_.PartitionNumber -AccessPath ('{0}:\\' -f $_.DriveLetter) }");
    }

    public string GetVolumeId(char letter)
    {
        var (code, output) = Run("mountvol.exe", $"{letter}:\\ /L");
        var m = Regex.Match(output, @"\\\\\?\\Volume\{[0-9a-fA-F-]+\}\\");
        if (code != 0 || !m.Success) throw new IOException($"Не удалось определить том на {letter}: ({output.Trim()})");
        return m.Value;
    }

    public void RemoveLetter(char letter, Action<string> log) => Checked(log, "mountvol.exe", $"{letter}:\\ /D");

    public void AssignLetter(char letter, string volumeId, Action<string> log) => Checked(log, "mountvol.exe", $"{letter}:\\ {volumeId}");

    public IReadOnlyList<string> RunningMacroscop()
    {
        var list = new List<string>();
        foreach (var name in new[] { "Macroscop", "MacroscopServer" })
            foreach (var p in Process.GetProcessesByName(name)) { list.Add($"{name}.exe (PID {p.Id})"); p.Dispose(); }
        return list;
    }

    public bool TryStopMacroscopService(Action<string> log)
    {
        var services = Ps("Get-CimInstance Win32_Service | Where-Object { $_.PathName -match 'MacroscopServer\\.exe' -and $_.State -eq 'Running' } | ForEach-Object { $_.Name }")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (services.Length == 0) return false;
        foreach (var s in services) PsChecked(log, $"Stop-Service -Name '{s}' -Force");
        return true;
    }

    public IReadOnlyList<string> StopPostgres(Action<string> log)
    {
        var names = Ps("Get-Service -Name 'postgresql*' | Where-Object Status -eq 'Running' | ForEach-Object { $_.Name }")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var n in names) PsChecked(log, $"Stop-Service -Name '{n}' -Force");
        return names;
    }

    public void StartServices(IEnumerable<string> names, Action<string> log)
    {
        foreach (var n in names) PsChecked(log, $"Start-Service -Name '{n}'");
    }

    public void CreateAttachTask(string taskName, string vhdxPath, char letter, Action<string> log)
    {
        Directory.CreateDirectory(DataDir);
        var script = Path.Combine(DataDir, $"attach_{letter}.ps1");
        File.WriteAllText(script, AttachScript(vhdxPath, letter), new UTF8Encoding(true));
        log($"Скрипт автоподключения: {script}");
        Checked(log, "schtasks.exe",
            $"/Create /TN \"{taskName}\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F /TR \"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \\\"{script}\\\"\"");
    }

    public void DeleteTask(string taskName, Action<string> log) => Checked(log, "schtasks.exe", $"/Delete /TN \"{taskName}\" /F");

    public bool TaskExists(string taskName) => Run("schtasks.exe", $"/Query /TN \"{taskName}\"").Code == 0;

    /// <summary>PowerShell run at start-up: attach the VHDX if needed and make sure the volume is at the letter.</summary>
    public static string AttachScript(string vhdxPath, char letter) => $$"""
# MacroscopArchiveTool: attaches a retired Macroscop archive disk at Windows start-up.
$ErrorActionPreference = 'Stop'
$img = '{{vhdxPath.Replace("'", "''")}}'
$letter = '{{letter}}'
$log = Join-Path $env:ProgramData 'MacroscopArchiveTool\attach.log'
try {
    $di = Get-DiskImage -ImagePath $img
    if (-not $di.Attached) { Mount-DiskImage -ImagePath $img -StorageType VHDX | Out-Null; Start-Sleep -Seconds 2; $di = Get-DiskImage -ImagePath $img }
    $disk = $di | Get-Disk
    if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
    if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
    $part = $disk | Get-Partition | Where-Object { $_.Type -ne 'Reserved' -and $_.Size -gt 1GB } | Select-Object -First 1
    if ([string]$part.DriveLetter -ne $letter) {
        if ($part.DriveLetter) { Remove-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber -AccessPath ("{0}:\" -f $part.DriveLetter) }
        Set-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber -NewDriveLetter $letter
    }
    "$(Get-Date -Format s) OK: $img -> ${letter}:" | Add-Content -Path $log
} catch {
    "$(Get-Date -Format s) ERROR: $img -> ${letter}: $($_.Exception.Message)" | Add-Content -Path $log
    exit 1
}
""";

    private static Dictionary<char, string> VirtualDiskLetters()
    {
        var map = new Dictionary<char, string>();
        try
        {
            var outp = Ps("Get-Disk | Where-Object BusType -eq 'File Backed Virtual' | ForEach-Object { $d = $_; Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Where-Object DriveLetter | ForEach-Object { \"$($_.DriveLetter)|$($d.Location)\" } }");
            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|', 2);
                if (parts.Length == 2 && parts[0].Length == 1) map[char.ToUpperInvariant(parts[0][0])] = parts[1];
            }
        }
        catch (Exception) { /* informational only */ }
        return map;
    }

    private static void Diskpart(Action<string> log, params string[] lines)
    {
        var script = Path.Combine(Path.GetTempPath(), $"mat_diskpart_{Guid.NewGuid():N}.txt");
        File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        try
        {
            foreach (var l in lines) log("diskpart> " + l);
            var (code, output) = Run("diskpart.exe", $"/s \"{script}\"");
            foreach (var l in output.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0)) log("  " + l);
            if (code != 0) throw new IOException($"diskpart завершился с кодом {code}. Подробности — в журнале выше.");
        }
        finally { try { File.Delete(script); } catch { } }
    }

    private static string Sanitize(string label) => new string(label.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(32).ToArray());

    private static void Checked(Action<string> log, string exe, string args)
    {
        log($"> {exe} {args}");
        var (code, output) = Run(exe, args);
        foreach (var l in output.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0)) log("  " + l);
        if (code != 0) throw new IOException($"{exe} завершился с кодом {code}: {output.Trim()}");
    }

    private static string Ps(string command)
    {
        var (code, output) = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(command));
        if (code != 0) throw new IOException($"PowerShell: {output.Trim()}");
        return output;
    }

    private static void PsChecked(Action<string> log, string command)
    {
        log("PS> " + command);
        Ps(command);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        // Console tools print in the OEM code page; read them with it so Russian messages stay readable.
        try { var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); psi.StandardOutputEncoding = oem; psi.StandardErrorEncoding = oem; }
        catch (Exception) { }
        using var p = Process.Start(psi) ?? throw new IOException($"Не удалось запустить {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
