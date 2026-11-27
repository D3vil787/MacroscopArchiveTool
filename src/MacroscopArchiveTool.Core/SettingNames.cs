using System.Globalization;

namespace MacroscopArchiveTool.Core;

/// <summary>
/// Human-readable text for Macroscop recording settings. Enumerations are stored as numeric codes in the
/// configuration; codes are mapped here once they are confirmed against the Configurator, unknown codes are shown as is.
/// </summary>
public static class SettingNames
{
    /// <summary>Alarus.Config.ChannelArchiveModes (confirmed on Macroscop 4.7 against the Configurator).</summary>
    public static readonly Dictionary<int, string> ArchiveModes = new() { [1] = "по движению", [2] = "постоянно" };
    /// <summary>Alarus.StreamTypes (stream written to the archive).</summary>
    public static readonly Dictionary<int, string> StreamTypes = new() { [0] = "основной поток", [1] = "альтернативный поток" };
    /// <summary>Alarus.Config.VideoStreamFormats (2, 3 and 4 confirmed on Macroscop 4.7 against camera settings).</summary>
    public static readonly Dictionary<int, string> StreamFormats = new() { [2] = "MPEG-4 Part 2", [3] = "H.264", [4] = "H.265" };

    private static string Code(Dictionary<int, string> map, int? code, string prefix)
        => code is int c ? map.GetValueOrDefault(c, $"{prefix} {c}") : $"{prefix} ?";

    public static string Mode(int? code) => Code(ArchiveModes, code, "режим");
    public static string Stream(int? code) => Code(StreamTypes, code, "поток");
    public static string Format(int? code) => Code(StreamFormats, code, "кодек");

    /// <summary>
    /// FPS to show: the value measured on the camera when it answered, otherwise Macroscop's own
    /// "Частота кадров" setting, which is only meaningful when "use the camera's frame rate" is off.
    /// </summary>
    public static double? EffectiveFps(CameraSettings s)
        => Positive(s.ProbedFps) ?? (s.UsesCameraFps == true ? null : Positive(s.Fps));

    private static double? Positive(double? v) => v is double d && d > 0 ? d : null;
    private static string Number(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>One-line summary for the report table.</summary>
    public static string Summary(CameraSettings s)
    {
        if (s.ArchiveEnabled == false) return "запись выключена";
        var parts = new List<string> { Mode(s.ArchiveMode), Stream(s.ArchiveStream), Format(s.StreamFormat) };
        if (EffectiveFps(s) is double fps) parts.Add($"{Number(fps)} к/с");
        if (s.ArchiveFpsLimitEnabled == true && s.ArchiveFpsLimit is double f) parts.Add($"≤{Number(f)} к/с");
        if (s.ThinningEnabled == true) parts.Add("прореживание");
        return string.Join(" · ", parts);
    }

    /// <summary>Where the fps came from — or why there is no number.</summary>
    private static string FpsText(CameraSettings s)
    {
        if (Positive(s.ProbedFps) is double probed) return $"{Number(probed)} к/с (из камеры, ISAPI)";
        if (s.UsesCameraFps == true)
            return s.FpsSource == CameraProbe.SourceIsapiFailed
                ? "из настроек камеры (камера не ответила)"
                : "из настроек камеры";
        return Positive(s.Fps) is double own ? $"{Number(own)} к/с (задана в Macroscop)" : "?";
    }

    /// <summary>Multi-line details for a tooltip.</summary>
    public static string Details(CameraSettings s)
    {
        string YN(bool? b) => b == null ? "?" : b.Value ? "да" : "нет";
        var lines = new List<string>
        {
            $"Запись в архив: {YN(s.ArchiveEnabled)}",
            $"Режим записи: {Mode(s.ArchiveMode)}",
            $"Поток в архив: {Stream(s.ArchiveStream)}",
            $"Кодек: {Format(s.StreamFormat)}",
            $"Ограничение fps архива: {(s.ArchiveFpsLimitEnabled == true ? $"{s.ArchiveFpsLimit} к/с" : "нет")}",
            $"Детектор движения Macroscop: {YN(s.MacroscopMotionDetector)}, детектор камеры: {YN(s.CameraMotionDetector)}",
            $"Предзапись/постзапись: {s.PreRecordSec?.ToString() ?? "?"}/{s.PostRecordSec?.ToString() ?? "?"} с",
            $"Прореживание архива: {YN(s.ThinningEnabled)}",
            $"Частота кадров: {FpsText(s)}",
            s.UsesCameraResolution == true ? "Разрешение: из настроек камеры" : $"Разрешение: код {s.ResolutionCode?.ToString() ?? "?"} (задано в Macroscop)",
        };
        return string.Join("\n", lines);
    }
}
