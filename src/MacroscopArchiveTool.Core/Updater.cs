using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MacroscopArchiveTool.Core;

public sealed record UpdateInfo(string Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Self-update from GitHub Releases: finds the latest release, downloads its zip, checks the SHA-256 published by GitHub,
/// and replaces the program files. Running files cannot be overwritten on Windows but can be renamed, so each old file is
/// renamed to *.old first and removed on the next start. mat.json, reports and saved jobs are never touched.
/// </summary>
public static class Updater
{
    public const string Repository = "D3vil787/MacroscopArchiveTool";
    public const string AssetName = "MacroscopArchiveTool-win-x64.zip";
    private const string OldSuffix = ".old";

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct, string? currentVersion = null)
    {
        using var http = Client(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub ответил {(int)resp.StatusCode} {resp.ReasonPhrase}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement, currentVersion ?? ToolInfo.Version);
    }

    /// <summary>Release JSON → update info, or null when it is not newer than <paramref name="currentVersion"/>.</summary>
    public static UpdateInfo? Parse(JsonElement release, string currentVersion)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, currentVersion)) return null;
        if (release.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.GetString() is string s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = s[7..].ToLowerInvariant();
            return new UpdateInfo(version, tag,
                release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                a.GetProperty("browser_download_url").GetString() ?? "",
                a.GetProperty("size").GetInt64(), sha,
                release.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
        }
        return null;
    }

    public static bool IsNewer(string candidate, string current)
        => Version.TryParse(Normalize(candidate), out var a) && Version.TryParse(Normalize(current), out var b) && a > b;

    private static string Normalize(string v)
    {
        var core = v.Split('-', '+')[0];
        return core.Count(c => c == '.') == 0 ? core + ".0" : core;
    }

    public static async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MacroscopArchiveTool_update_" + u.Version);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, AssetName);
        using var http = Client(TimeSpan.FromMinutes(30));
        using var resp = await http.GetAsync(u.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? u.Size;
        using (var src = await resp.Content.ReadAsStreamAsync(ct))
        using (var dst = File.Create(zip))
        {
            var buf = new byte[1 << 16];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }
        Verify(zip, u);
        return zip;
    }

    public static void Verify(string zip, UpdateInfo u)
    {
        var len = new FileInfo(zip).Length;
        if (u.Size > 0 && len != u.Size) throw new InvalidDataException($"Размер загруженного файла {len} не совпадает с опубликованным {u.Size}.");
        if (u.Sha256 != null)
        {
            using var s = File.OpenRead(zip);
            var actual = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            if (actual != u.Sha256) throw new InvalidDataException("Контрольная сумма загруженного обновления не совпадает с опубликованной на GitHub.");
        }
        using var archive = ZipFile.OpenRead(zip);
        if (!archive.Entries.Any(e => e.FullName.Equals("MacroscopArchiveTool.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("В архиве обновления нет MacroscopArchiveTool.exe.");
    }

    /// <summary>Puts the files of <paramref name="zip"/> into <paramref name="appDir"/>; returns the replaced file names.</summary>
    public static List<string> Apply(string zip, string appDir)
    {
        var staging = Path.Combine(Path.GetDirectoryName(zip)!, "files");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        var replaced = new List<string>();
        var moved = new List<(string Target, string Old)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, file);
                if (rel.Contains("..")) continue;
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var old = target + OldSuffix;
                    if (File.Exists(old)) { try { File.Delete(old); } catch (Exception) { old = target + "." + Guid.NewGuid().ToString("N")[..8] + OldSuffix; } }
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                File.Copy(file, target);
                replaced.Add(rel);
            }
        }
        catch
        {
            // Put the previous version back so the program keeps working.
            foreach (var (target, old) in moved)
            {
                try { if (File.Exists(target)) File.Delete(target); File.Move(old, target); } catch (Exception) { }
            }
            throw;
        }
        try { Directory.Delete(staging, true); } catch (Exception) { }
        return replaced;
    }

    /// <summary>Removes files left by the previous update (they were still running at that moment).</summary>
    public static void CleanupOldFiles(string appDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(appDir, "*" + OldSuffix, SearchOption.AllDirectories))
                try { File.Delete(f); } catch (Exception) { }
        }
        catch (Exception) { }
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MacroscopArchiveTool", ToolInfo.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
