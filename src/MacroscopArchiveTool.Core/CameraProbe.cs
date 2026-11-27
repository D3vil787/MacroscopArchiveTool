using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace MacroscopArchiveTool.Core;

/// <summary>
/// Read-only frame-rate probe: asks a camera for the parameters of the stream it actually sends
/// (Hikvision ISAPI, /ISAPI/Streaming/channels/101|102). The camera credentials are taken from the
/// Macroscop configuration and are never written to reports, caches or logs.
/// </summary>
public static class CameraProbe
{
    /// <summary>The camera answered and <see cref="CameraSettings.ProbedFps"/> holds its frame rate.</summary>
    public const string SourceIsapi = "isapi";

    /// <summary>The camera was asked but did not answer (offline, wrong credentials, ISAPI disabled).</summary>
    public const string SourceIsapiFailed = "isapi-failed";

    /// <summary>
    /// Hikvision returns the frame rate in 1/100 fps ("maxFrameRate": 1500 = 15.00),
    /// some firmwares also carry a plain "frameRate".
    /// </summary>
    public static bool TryParseHikvisionFps(string xml, out double fps)
    {
        fps = 0;
        try
        {
            var doc = XDocument.Parse(xml);
            double? max = null;
            foreach (var e in doc.Descendants())
            {
                if (e.Name.LocalName == "frameRate"
                    && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) && plain > 0)
                {
                    fps = plain;
                    return true;
                }
                if (e.Name.LocalName == "maxFrameRate"
                    && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hundredths) && hundredths > 0)
                    max = hundredths;
            }
            if (max is double v)
            {
                fps = v / 100.0;
                return fps is > 0 and <= 240;
            }
        }
        catch (System.Xml.XmlException) { /* not an ISAPI answer */ }
        return false;
    }

    /// <summary>Asks a Hikvision camera for the fps of stream 1 (main) or 2 (alternative). Never throws.</summary>
    public static async Task<double?> HikvisionFpsAsync(string ip, string login, string password, int streamNumber,
                                                       int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(login, password),
                PreAuthenticate = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Max(500, timeoutMs)) };
            var url = $"http://{ip}/ISAPI/Streaming/channels/1{streamNumber:00}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return TryParseHikvisionFps(xml, out var fps) ? fps : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}

/// <summary>Frame rates remembered between scans, so cameras are not asked every time.</summary>
internal sealed class FpsCache
{
    public sealed class Entry
    {
        public double? Fps { get; set; }
        public DateTime Utc { get; set; }
    }

    public Dictionary<string, Entry> Cameras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static FpsCache Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<FpsCache>(File.ReadAllText(path)) ?? new FpsCache();
        }
        catch (Exception) { /* a broken cache is just a cache miss */ }
        return new FpsCache();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* read-only folder: the probe still works, just without the cache */ }
    }
}

/// <summary>Fills <see cref="CameraSettings.ProbedFps"/> for cameras that use their own frame-rate setting.</summary>
public static class CameraFpsProbe
{
    public static void Enrich(IReadOnlyList<CameraStat> cameras, RetentionSettings retention, ToolConfig config, CancellationToken ct)
    {
        var targets = cameras
            .Where(c => c.Settings is { UsesCameraFps: true, Disabled: not true })
            .ToList();
        if (targets.Count == 0) return;

        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MacroscopArchiveTool", "camera-fps.json");
        var cache = FpsCache.Load(cachePath);
        var now = DateTime.UtcNow;
        var successTtl = TimeSpan.FromHours(Math.Max(1, config.CameraFpsCacheHours));
        var failureTtl = TimeSpan.FromHours(1);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(config.CameraProbeThreads, 1, 16),
            CancellationToken = ct,
        };

        Parallel.ForEach(targets, options, camera =>
        {
            var mc = retention.ById.GetValueOrDefault(camera.Id);
            if (mc == null || string.IsNullOrWhiteSpace(mc.Ip) || string.IsNullOrEmpty(mc.Login)) return;

            FpsCache.Entry? cached;
            lock (cache) cached = cache.Cameras.GetValueOrDefault(camera.Id);
            if (cached != null)
            {
                var ttl = cached.Fps.HasValue ? successTtl : failureTtl;
                if (now - cached.Utc < ttl)
                {
                    if (cached.Fps is double known) Apply(camera, known);
                    else camera.Settings!.FpsSource = CameraProbe.SourceIsapiFailed;
                    return;
                }
            }

            // The stream written to the archive decides which camera stream to ask about.
            var stream = camera.Settings!.ArchiveStream == 1 ? 2 : 1;
            var fps = CameraProbe.HikvisionFpsAsync(mc.Ip!, mc.Login!, mc.Password ?? "", stream,
                                                    config.CameraProbeTimeoutMs, ct).GetAwaiter().GetResult();

            lock (cache)
                cache.Cameras[camera.Id] = new FpsCache.Entry { Fps = fps, Utc = now };

            if (fps is double value) Apply(camera, value);
            else camera.Settings.FpsSource = CameraProbe.SourceIsapiFailed;
        });

        cache.Save(cachePath);
    }

    private static void Apply(CameraStat camera, double fps)
    {
        camera.Settings!.ProbedFps = fps;
        camera.Settings.FpsSource = CameraProbe.SourceIsapi;
    }
}
