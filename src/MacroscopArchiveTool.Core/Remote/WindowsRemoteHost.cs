using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MacroscopArchiveTool.Core.Remote;

/// <summary>
/// SMB session to \\host\IPC$ (WNetAddConnection2 — the password never appears on a command line), admin share for files,
/// schtasks /S for the one-time SYSTEM task. Task Scheduler RPC reuses the same session, so it runs with the same account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteHost : IRemoteHost
{
    private readonly RemoteServer _server;
    private readonly Action<string>? _log;
    private string? _connected;

    public WindowsRemoteHost(RemoteServer server, Action<string>? log = null) { _server = server; _log = log; }

    private string Host => _server.Host.Trim().TrimStart('\\');

    public void Connect()
    {
        if (!_server.HasCredentials) return;   // current Windows account (works when both servers share the admin login and password)
        var ipc = $@"\\{Host}\IPC$";
        var password = Dpapi.Unprotect(_server.ProtectedPassword);
        var nr = new NETRESOURCE { dwType = 0, lpRemoteName = ipc };
        int rc = WNetAddConnection2(ref nr, password, _server.QualifiedUser, 0);
        if (rc == 1219)   // already connected to this server with other credentials
        {
            WNetCancelConnection2(ipc, 0, true);
            rc = WNetAddConnection2(ref nr, password, _server.QualifiedUser, 0);
        }
        if (rc != 0) throw new IOException($"Не удалось подключиться к {Host}: {new Win32Exception(rc).Message} (код {rc})");
        _connected = ipc;
    }

    public string Unc(string remoteLocalPath)
    {
        if (remoteLocalPath.Length < 3 || remoteLocalPath[1] != ':') throw new ArgumentException(remoteLocalPath);
        return $@"\\{Host}\{char.ToUpperInvariant(remoteLocalPath[0])}${remoteLocalPath[2..]}";
    }

    public void RunTask(string taskName, string remoteCommand)
    {
        Schtasks($"/Create /S {Host} /TN \"{taskName}\" /TR \"{remoteCommand}\" /SC ONCE /ST 00:00 /RU SYSTEM /RL HIGHEST /F");
        Schtasks($"/Run /S {Host} /TN \"{taskName}\"");
    }

    /// <summary>Checks the admin share, the Task Scheduler and Macroscop presence; returns a short description.</summary>
    public string Check()
    {
        if (!Directory.Exists(Unc(@"C:\ProgramData"))) throw new IOException($"Нет доступа к \\\\{Host}\\C$ — нужна учётная запись администратора этого сервера.");
        Schtasks($"/Query /S {Host} /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\"");
        var macroscop = Directory.Exists(Unc(ToolConfig.DefaultLogsDir));
        return macroscop ? "доступ есть, Macroscop найден" : "доступ есть, но журналы Macroscop не найдены (имена камер будут GUID)";
    }

    public void EndTask(string taskName) { try { Schtasks($"/End /S {Host} /TN \"{taskName}\""); } catch (Exception) { } }

    public void DeleteTask(string taskName) => Schtasks($"/Delete /S {Host} /TN \"{taskName}\" /F");

    public void Dispose()
    {
        if (_connected != null) { WNetCancelConnection2(_connected, 0, true); _connected = null; }
    }

    private void Schtasks(string args)
    {
        _log?.Invoke("schtasks " + args);
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = oem; psi.StandardErrorEncoding = oem;
        }
        catch (Exception) { }
        using var p = Process.Start(psi) ?? throw new IOException("Не удалось запустить schtasks.exe");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"Планировщик на {Host}: {(e.Result + " " + o.Result).Trim()}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope, dwType, dwDisplayType, dwUsage;
        public string? lpLocalName, lpRemoteName, lpComment, lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}

/// <summary>Windows DPAPI for the current user: only this account on this computer can decrypt.</summary>
[SupportedOSPlatform("windows")]
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MacroscopArchiveTool/servers");

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Call(data, protect: true));
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try { return Encoding.UTF8.GetString(Call(Convert.FromBase64String(protectedBase64), protect: false)); }
        catch (Exception ex) { throw new InvalidOperationException("Сохранённый пароль не расшифровывается (он был сохранён другим пользователем Windows). Введите пароль заново.", ex); }
    }

    private static byte[] Call(byte[] input, bool protect)
    {
        var inBlob = Blob(input);
        var entBlob = Blob(Entropy);
        try
        {
            DATA_BLOB outBlob;
            bool ok = protect
                ? CryptProtectData(ref inBlob, "MacroscopArchiveTool", ref entBlob, IntPtr.Zero, IntPtr.Zero, 1, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, 1, out outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.pbData); Marshal.FreeHGlobal(entBlob.pbData); }
    }

    private static DATA_BLOB Blob(byte[] data)
    {
        var p = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        Marshal.Copy(data, 0, p, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = p };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
