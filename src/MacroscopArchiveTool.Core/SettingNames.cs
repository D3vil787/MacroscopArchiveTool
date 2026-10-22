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
    /// <summary>Alarus.Config.VideoStreamFormats (3 and 4 confirmed on Macroscop 4.7 against camera settings).</summary>
    public static readonly Dictionary<int, string> StreamFormats = new() { [3] = "H.264", [4] = "H.265" };

    private static string Code(Dictionary<int, string> map, int? code, string prefix)
        => code is int c ? map.GetValueOrDefault(c, $"{prefix} {c}") : $"{prefix} ?";

    public static string Mode(int? code) => Code(ArchiveModes, code, "режим");
    public static string Stream(int? code) => Code(StreamTypes, code, "поток");
    public static string Format(int? code) => Code(StreamFormats, code, "кодек");

    /// <summary>One-line summary for the report table.</summary>
    public static string Summary(CameraSettings s)
    {
        if (s.ArchiveEnabled == false) return "запись выключена";
        var parts = new List<string> { Mode(s.ArchiveMode), Stream(s.ArchiveStream), Format(s.StreamFormat) };
        if (s.ArchiveFpsLimitEnabled == true && s.ArchiveFpsLimit is double f) parts.Add($"≤{f.ToString("0.#", CultureInfo.InvariantCulture)} к/с");
        if (s.ThinningEnabled == true) parts.Add("прореживание");
        return string.Join(" · ", parts);
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
            s.UsesCameraFps == true ? "Частота кадров: из настроек камеры" : $"Частота кадров: {s.Fps?.ToString(CultureInfo.InvariantCulture) ?? "?"} к/с (задана в Macroscop)",
            s.UsesCameraResolution == true ? "Разрешение: из настроек камеры" : $"Разрешение: код {s.ResolutionCode?.ToString() ?? "?"} (задано в Macroscop)",
        };
        return string.Join("\n", lines);
    }
}
