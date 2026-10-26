using System.Globalization;
using System.Net;
using System.Text;

namespace MacroscopArchiveTool.Core;

/// <summary>Self-contained HTML report for one or several servers (no external files or CDNs).</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <param name="history">Earlier scans per machine name (see <see cref="ReportHistory"/>), shown as "how it changes".</param>
    public static string Render(IReadOnlyList<ScanReport> reports, IReadOnlyDictionary<string, List<HistoryPoint>>? history = null)
    {
        var sb = new StringBuilder(64 * 1024);
        var title = reports.Count == 1 ? $"Архив Macroscop — {reports[0].ServerName}" : "Архив Macroscop — сводный отчёт";
        sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(H(title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<header><h1>").Append(H(title)).Append("</h1><p class=\"muted\">Сформирован ")
          .Append(H(DateTime.Now.ToString("dd.MM.yyyy HH:mm", Ru)))
          .Append(" · MacroscopArchiveTool ").Append(H(reports.FirstOrDefault()?.ToolVersion ?? ToolInfo.Version))
          .Append("</p></header>");

        if (reports.Count > 1) RenderOverall(sb, reports);
        for (int i = 0; i < reports.Count; i++)
        {
            RenderServer(sb, reports[i], reports.Count > 1, i);
            if (history != null && history.TryGetValue(reports[i].MachineName, out var h)) RenderHistory(sb, reports[i], h);
        }
        RenderLost(sb, reports);
        RenderCameras(sb, reports);
        RenderNotes(sb);

        sb.Append("</main><script>").Append(Js).Append("</script></body></html>");
        return sb.ToString();
    }

    private static void RenderOverall(StringBuilder sb, IReadOnlyList<ScanReport> reports)
    {
        var total = reports.Sum(r => r.Drives.Sum(d => d.MediaBytes));
        var rate = reports.Sum(ReportAnalyzer.DailyRate);
        sb.Append("<section><h2>Все серверы</h2><div class=\"cards\">");
        Card(sb, "Серверов", reports.Count.ToString(Ru));
        Card(sb, "Камер", reports.Sum(r => r.Cameras.Count).ToString(Ru));
        Card(sb, "Видео в архиве", Bytes(total));
        Card(sb, "Пишется в сутки", Bytes((long)rate));
        sb.Append("</div></section>");
    }

    private static void RenderServer(StringBuilder sb, ScanReport r, bool multi, int index)
    {
        var rate = ReportAnalyzer.DailyRate(r);
        var headroom = ReportAnalyzer.HeadroomDays(r);
        var maxDepth = r.Cameras.Count > 0 ? r.Cameras.Max(c => c.DepthDays) : 0;
        var heavy = r.Cameras.Count(c => c.IsHeavy);
        var over = r.Cameras.Count(c => c.ExceedsRetention);

        sb.Append("<section><h2>").Append(H(multi ? $"Сервер «{r.ServerName}»" : "Сводка")).Append("</h2>")
          .Append("<p class=\"muted\">").Append(H(r.MachineName)).Append(" · скан ")
          .Append(H(r.ScannedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru)))
          .Append(" · ").Append(r.ScanSeconds.ToString("0.#", Ru)).Append(" с</p><div class=\"cards\">");
        Card(sb, "Видео в архиве", Bytes(r.Drives.Sum(d => d.MediaBytes)), $"{r.Cameras.Count} камер, {r.Drives.Count} дисков");
        Card(sb, "Пишется в сутки", Bytes((long)rate), $"≈ {(rate * 8 / 86400 / 1e6).ToString("0.#", Ru)} Мбит/с");
        Card(sb, "Макс. глубина", $"{maxDepth.ToString("0", Ru)} дн.");
        var sizesKnown = r.Drives.Any(d => d.IsActive && d.TotalBytes > 0);
        Card(sb, "Свободно на активных дисках", sizesKnown ? Bytes(ReportAnalyzer.ActiveFreeBytes(r)) : "—",
             sizesKnown && headroom.HasValue ? $"≈ {headroom.Value.ToString("0", Ru)} дн. до заполнения" : null);
        Card(sb, "Тяжёлые камеры", heavy.ToString(Ru), heavy > 0 ? $"пишут в {r.HeavyFactor.ToString("0.#", Ru)} раза больше медианы" : null, heavy > 0 ? "warn" : null);
        Card(sb, "Сверх срока хранения", over.ToString(Ru), over > 0 ? "есть записи старше лимита" : "нарушений нет", over > 0 ? "bad" : "ok");
        var lost = r.Cameras.Count(c => ReportAnalyzer.IsProblem(c.LostKind));
        Card(sb, "Камеры без записи", lost.ToString(Ru), lost > 0 ? "см. раздел «Потерянные камеры»" : "все пишут", lost > 0 ? "warn" : "ok");
        sb.Append("</div>");

        if (!string.IsNullOrEmpty(r.RetentionSource))
            sb.Append("<p class=\"muted\">Сроки хранения: ").Append(H(r.RetentionSource)).Append("</p>");
        foreach (var w in r.Warnings) sb.Append("<p class=\"warning\">⚠ ").Append(H(w)).Append("</p>");

        bool hasDays = r.Drives.Any(d => d.DayCameraBytes.Count > 0);
        sb.Append("<h3>Диски</h3>");
        if (hasDays) sb.Append("<p class=\"muted\">Щёлкните строку диска, чтобы увидеть объём по дням, а затем день — чтобы увидеть камеры.</p>");
        sb.Append("<div class=\"scroll\"><table class=\"sortable drives\" data-s=\"").Append(index).Append("\"><thead><tr>")
          .Append(Th("Диск")).Append(Th("Метка")).Append(Th("Роль")).Append(Th("Размер", true)).Append(Th("Свободно", true))
          .Append(Th("Заполнение", true)).Append(Th("Видео", true)).Append(Th("БД", true)).Append(Th("Прочее в архиве", true))
          .Append(Th("Вне архива", true)).Append(Th("Камер", true)).Append(Th("Самая старая", true)).Append(Th("Последняя запись", true))
          .Append(Th("Статус")).Append("</tr></thead><tbody>");
        foreach (var d in r.Drives)
        {
            var known = d.TotalBytes > 0;
            var pct = known ? (double)d.UsedBytes / d.TotalBytes * 100 : 0;
            sb.Append(d.DayCameraBytes.Count > 0 ? $"<tr class=\"drv\" tabindex=\"0\" data-d=\"{H(d.Name)}\">" : "<tr>")
              .Append(Td((d.DayCameraBytes.Count > 0 ? "▸ " : "") + d.Name, cls: "strong")).Append(Td(d.VolumeLabel))
              .Append(d.Role == null ? Td("—", cls: "muted")
                      : Td(d.Role, cls: d.Role.StartsWith("Резерв") || d.Role == "Не используется" ? "warn" : null, title: Limits(d)))
              .Append(TdN(known ? Bytes(d.TotalBytes) : "—", d.TotalBytes)).Append(TdN(known ? Bytes(d.FreeBytes) : "—", d.FreeBytes));
            if (known)
                sb.Append("<td class=\"num\" data-v=\"").Append(Inv(pct)).Append("\"><div class=\"bar\"><span style=\"width:")
                  .Append(Inv(Math.Min(100, pct))).Append("%\"").Append(pct >= 95 ? " class=\"full\"" : "").Append("></span></div>")
                  .Append(pct.ToString("0", Ru)).Append("%</td>");
            else sb.Append("<td class=\"num muted\" data-v=\"-1\">—</td>");
            sb.Append(TdN(Bytes(d.MediaBytes), d.MediaBytes)).Append(TdN(Bytes(d.DbBytes), d.DbBytes))
              .Append(TdN(Bytes(d.OtherArchiveBytes), d.OtherArchiveBytes))
              .Append(TdN(known ? Bytes(d.NonArchiveBytes) : "—", d.NonArchiveBytes,
                          known && d.MediaBytes > 0 && d.NonArchiveBytes > d.TotalBytes * 0.1 ? "warn" : null))
              .Append(TdN(d.CameraCount.ToString(Ru), d.CameraCount))
              .Append(TdDate(d.OldestUtc)).Append(TdDate(d.NewestUtc, withTime: true))
              .Append(d.IsActive ? Td("пишется", cls: "ok")
                      : d.MediaBytes == 0 ? Td("без видео", cls: "muted")
                      : d.DrainForecastUtc is DateTime f
                          ? "<td class=\"muted status2\" title=\"Когда истечёт срок хранения последней записи на диске (по срокам камер). Новые записи на диск не попадают, если его роль — резервный архив или «Не используется».\">без записи &gt; 24 ч<br>опустеет ≈ <b>" +
                            H(f.ToLocalTime().ToString("dd.MM.yyyy", Ru)) + "</b></td>"
                          : Td("без записи > 24 ч", cls: "muted"))
              .Append("</tr>");
        }
        sb.Append("</tbody></table></div>");
        if (hasDays) AppendDayData(sb, r, index);
        sb.Append("</section>");
    }

    /// <summary>
    /// Compact JSON for the drill-down: camera names once, then per drive [[day, total, [[camIndex, bytes], …]], …], newest day first.
    /// </summary>
    private static void AppendDayData(StringBuilder sb, ScanReport r, int index)
    {
        var names = r.Cameras.ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);
        var camIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var camList = new List<string>();
        int Cam(string id)
        {
            if (!camIndex.TryGetValue(id, out var i)) { i = camList.Count; camIndex[id] = i; camList.Add(names.GetValueOrDefault(id) ?? CameraNames.ToDashedGuid(id) ?? id); }
            return i;
        }
        var drives = new Dictionary<string, object>();
        foreach (var d in r.Drives.Where(d => d.DayCameraBytes.Count > 0))
            drives[d.Name] = d.DayCameraBytes.OrderByDescending(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new object[]
                {
                    kv.Key, kv.Value.Values.Sum(),
                    kv.Value.OrderByDescending(c => c.Value).Select(c => new long[] { Cam(c.Key), c.Value }).ToArray(),
                }).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(new { cams = camList, drives });
        sb.Append("<script type=\"application/json\" id=\"days-").Append(index).Append("\">")
          .Append(json.Replace("</", "<\\/")).Append("</script>");
    }

    private static void RenderCameras(StringBuilder sb, IReadOnlyList<ScanReport> reports)
    {
        bool multi = reports.Count > 1;
        sb.Append("<section><h2>Камеры</h2><p class=\"muted\">Отсортировано по объёму записи в сутки. Щёлкните заголовок, чтобы сменить сортировку.</p>")
          .Append("<input id=\"filter\" type=\"search\" placeholder=\"Фильтр по имени камеры…\" aria-label=\"Фильтр по имени камеры\">")
          .Append("<div class=\"scroll\"><table class=\"sortable\" id=\"cams\"><thead><tr>")
          .Append(Th("Камера"));
        if (multi) sb.Append(Th("Сервер"));
        sb.Append(Th("В сутки", true)).Append(Th("Ср. поток", true)).Append(Th(multi ? "Доля на сервере" : "Доля", true))
          .Append(Th("Объём", true)).Append(Th("Глубина", true)).Append(Th("Срок", true)).Append(Th("Запись"))
          .Append(Th("Самая старая", true)).Append(Th("Дисков", true)).Append(Th("Отметки"))
          .Append("</tr></thead><tbody>");

        foreach (var (r, c) in reports.SelectMany(r => r.Cameras.Select(c => (r, c))).OrderByDescending(x => x.c.BytesPerDay))
        {
            var flags = new List<string>();
            if (c.IsHeavy) flags.Add("<span class=\"tag warn\">тяжёлая</span>");
            if (c.ExceedsRetention) flags.Add("<span class=\"tag bad\">сверх срока</span>");
            if (ReportAnalyzer.IsProblem(c.LostKind)) flags.Add($"<span class=\"tag warn\">{H(LostShort(c.LostKind))}</span>");
            sb.Append("<tr").Append(c.ExceedsRetention ? " class=\"rowbad\"" : c.IsHeavy ? " class=\"rowwarn\"" : "").Append(">")
              .Append(Td(c.Name, cls: "strong camname", title: CameraNames.ToDashedGuid(c.Id) ?? c.Id));
            if (multi) sb.Append(Td(r.ServerName));
            sb.Append(TdN(Bytes((long)c.BytesPerDay), c.BytesPerDay))
              .Append(TdN(c.AvgMbitPerSec.ToString("0.0", Ru) + " Мбит/с", c.AvgMbitPerSec))
              .Append("<td class=\"num\" data-v=\"").Append(Inv(c.SharePercent)).Append("\"><div class=\"bar small\"><span style=\"width:")
              .Append(Inv(Math.Min(100, c.SharePercent * 3))).Append("%\"></span></div>").Append(c.SharePercent.ToString("0.0", Ru)).Append("%</td>")
              .Append(TdN(Bytes(c.Bytes), c.Bytes))
              .Append(TdN(c.DepthDays.ToString("0", Ru) + " дн.", c.DepthDays, c.ExceedsRetention ? "bad" : null))
              .Append(RetentionCell(c))
              .Append(c.Settings != null ? Td(SettingNames.Summary(c.Settings), cls: "small rec", title: SettingNames.Details(c.Settings)) : Td("—", cls: "muted"))
              .Append(TdDate(c.OldestUtc))
              .Append(TdN(c.BytesByDrive.Count > 0 ? c.BytesByDrive.Count.ToString(Ru) : "—", c.BytesByDrive.Count))
              .Append("<td class=\"flags\">").Append(string.Join(" ", flags)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></section>");
    }

    private static string LostShort(string? kind) => kind switch
    {
        "silent" => "не пишет", "no-archive" => "нет архива", "deleted" => "удалена", "disabled" => "отключена",
        "moved" => "на другом сервере", "recording-off" => "запись выключена", _ => "",
    };

    /// <summary>Cameras that occupy a licence or disk space for nothing, with what to do about each.</summary>
    private static void RenderLost(StringBuilder sb, IReadOnlyList<ScanReport> reports)
    {
        var rows = reports.SelectMany(r => r.Cameras.Where(c => c.LostKind != null).Select(c => (r, c)))
            .OrderBy(x => x.c.LostKind switch { "no-archive" => 0, "silent" => 1, "disabled" => 2, "deleted" => 3, "moved" => 4, _ => 5 })
            .ThenByDescending(x => x.c.SilentDays ?? double.MaxValue).ToList();
        bool multi = reports.Count > 1;
        sb.Append("<section><h2>Потерянные камеры</h2>");
        if (rows.Count == 0) { sb.Append("<p class=\"ok\">Все камеры этого отчёта пишут в архив.</p></section>"); return; }
        sb.Append("<p class=\"muted\">Камеры, которые не пишут или от которых остался только архив. Камера, у которой за весь срок хранения нет ни одной записи, ")
          .Append("скорее всего снята или заменена — её стоит удалить из Конфигуратора, чтобы освободить лицензию. Порог «не пишет»: ")
          .Append(reports[0].SilentDaysContinuous.ToString("0.#", Ru)).Append(" дн. при постоянной записи, ")
          .Append(reports[0].SilentDaysMotion.ToString("0.#", Ru)).Append(" дн. при записи по движению (mat.json: silentDaysContinuous, silentDaysMotion).</p>")
          .Append("<div class=\"scroll\"><table class=\"sortable\"><thead><tr>").Append(Th("Камера"));
        if (multi) sb.Append(Th("Сервер"));
        sb.Append(Th("Что с ней")).Append(Th("Последняя запись", true)).Append(Th("Без записи", true)).Append(Th("Архив хранится до", true))
          .Append(Th("Объём", true)).Append(Th("Что делать")).Append("</tr></thead><tbody>");
        foreach (var (r, c) in rows)
        {
            var until = c.ArchiveUntilUtc is DateTime u ? u.ToLocalTime().ToString("dd.MM.yyyy", Ru) : "—";
            var (what, todo) = c.LostKind switch
            {
                "no-archive" => ("за весь срок хранения нет ни одной записи", "Камера не пишет дольше срока хранения. Если её сняли или заменили — удалите из Конфигуратора, освободится лицензия."),
                "silent" => ($"не пишет {c.SilentDays?.ToString("0", Ru)} дн.", $"Проверьте камеру и сеть. Если её сняли — архив можно смотреть до {until}, после этого удалите камеру из Конфигуратора."),
                "disabled" => ("отключена в Macroscop («Камера активна» выкл.)", c.Bytes > 0 ? $"Архив хранится до {until}; после этого камеру можно удалить из Конфигуратора." : "Архива нет — если камера не нужна, удалите её из Конфигуратора."),
                "deleted" => ("удалена из конфигурации, остался архив", $"Лицензию не занимает. Архив должен удалиться по сроку хранения до {until} — если он остаётся дольше, его можно убрать вручную."),
                "moved" => ("привязана к другому серверу", "Здесь остался её прежний архив; он удалится по сроку хранения."),
                _ => ("запись в архив выключена", "Только живое видео. Если камера не нужна — удалите её из Конфигуратора."),
            };
            var cls = c.LostKind is "no-archive" or "silent" ? " class=\"rowwarn\"" : "";
            sb.Append("<tr").Append(cls).Append('>').Append(Td(c.Name, cls: "strong", title: CameraNames.ToDashedGuid(c.Id) ?? c.Id));
            if (multi) sb.Append(Td(r.ServerName));
            sb.Append("<td class=\"wrap\">").Append(H(what)).Append("</td>").Append(TdDate(c.NewestUtc, withTime: true))
              .Append(TdN(c.SilentDays is double sd ? sd.ToString("0", Ru) + " дн." : "—", c.SilentDays ?? 99999))
              .Append(c.ArchiveUntilUtc is DateTime uu ? TdDate(uu) : TdN("—", 0)).Append(TdN(Bytes(c.Bytes), c.Bytes))
              .Append("<td class=\"small todo\">").Append(H(todo)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div></section>");
    }

    private static void RenderNotes(StringBuilder sb) => sb.Append(
        "<section class=\"notes\"><h2>Как читать отчёт</h2><ul>" +
        "<li><b>В сутки / Ср. поток</b> — объём архива камеры, делённый на глубину. Для записи по движению это среднее, пиковый поток выше.</li>" +
        "<li><b>Глубина</b> — сколько дней назад сделана самая старая запись камеры. Перерывы в записи не учитываются.</li>" +
        "<li><b>Тяжёлая</b> — камера пишет в сутки в heavyFactor раз больше медианной камеры своего сервера (по умолчанию 2,5; задаётся в mat.json).</li>" +
        "<li><b>Срок</b> — время хранения камеры из конфигурации Macroscop (если мин и макс различаются — «мин–макс»). Звёздочка — срок взят из mat.json. Наведите курсор, чтобы увидеть источник.</li>" +
        "<li><b>Запись</b> — настройки камеры из конфигурации Macroscop: режим записи, поток, кодек, ограничение fps архива, прореживание. Наведите курсор, чтобы увидеть детекторы движения, пред- и постзапись, fps и разрешение.</li>" +
        "<li><b>Сверх срока</b> — самая старая запись старше максимального срока хранения камеры больше чем на сутки.</li>" +
        "<li><b>Роль</b> — роль диска в Конфигураторе Macroscop (из Server.SrvConf). Наведите курсор, чтобы увидеть «Мин. своб. место» и «Макс. размер архива». Резервный архив пишется только при отказе всех основных дисков.</li>" +
        "<li><b>Опустеет</b> — для дисков без записи: когда истечёт срок хранения последней записи (по срокам камер).</li>" +
        "<li><b>Вне архива</b> — место на томе, занятое не папкой MacroscopArchive. Большие значения часто означают забытые данные.</li>" +
        "<li><b>До заполнения</b> — грубая оценка по свободному месту дисков, куда шла запись за сутки. Лимиты Macroscop «Мин. своб. место» и «Макс. размер архива» не учитываются.</li>" +
        "</ul></section>");

    private static void RenderHistory(StringBuilder sb, ScanReport r, List<HistoryPoint> points)
    {
        var all = points.Where(p => p.ScannedAtUtc < r.ScannedAtUtc.AddMinutes(-1)).Append(ReportHistory.Point(r)).OrderBy(p => p.ScannedAtUtc).ToList();
        if (all.Count < 2) return;
        sb.Append("<section><h2>Динамика").Append(reports(r)).Append("</h2><p class=\"muted\">По сохранённым отчётам в папке reports: ")
          .Append(all.Count).Append(" сканов с ").Append(H(all[0].ScannedAtUtc.ToLocalTime().ToString("dd.MM.yyyy", Ru))).Append(".</p>");
        Spark(sb, "Видео в архиве", all.Select(p => (p.ScannedAtUtc, (double)p.VideoBytes)).ToList(), v => Bytes((long)v));
        Spark(sb, "Свободно на активных дисках", all.Select(p => (p.ScannedAtUtc, (double)p.ActiveFreeBytes)).ToList(), v => Bytes((long)v));
        Spark(sb, "Макс. глубина, дн.", all.Select(p => (p.ScannedAtUtc, p.MaxDepthDays)).ToList(), v => v.ToString("0", Ru));
        sb.Append("<div class=\"scroll\"><table><thead><tr>").Append(Th("Скан")).Append(Th("Видео", true)).Append(Th("Пишется в сутки", true))
          .Append(Th("Макс. глубина", true)).Append(Th("Свободно на активных", true)).Append(Th("До заполнения", true)).Append(Th("Сверх срока", true))
          .Append("</tr></thead><tbody>");
        foreach (var p in Enumerable.Reverse(all).Take(30))
            sb.Append("<tr>").Append(TdDate(p.ScannedAtUtc, withTime: true)).Append(TdN(Bytes(p.VideoBytes), p.VideoBytes))
              .Append(TdN(Bytes((long)p.DailyBytes), p.DailyBytes)).Append(TdN(p.MaxDepthDays.ToString("0", Ru) + " дн.", p.MaxDepthDays))
              .Append(TdN(Bytes(p.ActiveFreeBytes), p.ActiveFreeBytes)).Append(TdN(p.HeadroomDays is double hd ? hd.ToString("0", Ru) + " дн." : "—", p.HeadroomDays ?? -1))
              .Append(TdN(p.OverRetention.ToString(Ru), p.OverRetention, p.OverRetention > 0 ? "bad" : null)).Append("</tr>");
        sb.Append("</tbody></table></div></section>");

        static string reports(ScanReport x) => string.IsNullOrEmpty(x.ServerName) ? "" : $" — {H(x.ServerName)}";
    }

    /// <summary>Small inline SVG line chart (no scripts, prints well).</summary>
    private static void Spark(StringBuilder sb, string title, List<(DateTime T, double V)> pts, Func<double, string> fmt)
    {
        const int w = 420, h = 70, pad = 4;
        double t0 = pts[0].T.Ticks, t1 = Math.Max(pts[^1].T.Ticks, t0 + 1);
        double min = pts.Min(p => p.V), max = pts.Max(p => p.V);
        if (max - min < 1e-9) { min -= 1; max += 1; }
        string P((DateTime T, double V) p) =>
            Inv(pad + (p.T.Ticks - t0) / (t1 - t0) * (w - 2 * pad)) + "," + Inv(h - pad - (p.V - min) / (max - min) * (h - 2 * pad));
        sb.Append("<div class=\"spark\"><div class=\"label\">").Append(H(title)).Append(": <b>").Append(H(fmt(pts[^1].V))).Append("</b> <span class=\"muted\">(было ")
          .Append(H(fmt(pts[0].V))).Append(")</span></div><svg viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" role=\"img\" aria-label=\"").Append(H(title))
          .Append("\"><polyline style=\"fill:none;stroke:var(--accent);stroke-width:2\" points=\"").Append(string.Join(" ", pts.Select(P))).Append("\"/>");
        foreach (var p in pts) { var xy = P(p).Split(','); sb.Append("<circle r=\"2.5\" style=\"fill:var(--accent)\" cx=\"").Append(xy[0]).Append("\" cy=\"").Append(xy[1]).Append("\"/>"); }
        sb.Append("</svg></div>");
    }

    private static string Limits(DriveStat d)
    {
        var parts = new List<string>();
        if (d.MinFreeBytes is long mf) parts.Add($"Мин. свободное место: {Bytes(mf)}");
        if (d.MaxArchiveBytes is long ma) parts.Add($"Макс. размер архива: {Bytes(ma)}");
        return parts.Count > 0 ? string.Join("\n", parts) : "Лимиты не заданы";
    }

    private static string RetentionCell(CameraStat c)
    {
        if (!c.RetentionDays.HasValue) return "<td class=\"num\" data-v=\"0\">—</td>";
        var max = c.RetentionDays.Value;
        var text = c.RetentionMinDays is double mn && Math.Round(mn) != max ? $"{Math.Round(mn)}–{max} дн." : $"{max} дн.";
        var src = c.RetentionSource switch
        {
            "macroscop" => "из конфигурации Macroscop" + (c.RetentionMinDays.HasValue ? " (мин–макс)" : ""),
            "mat.json" => "задан в mat.json",
            _ => "по умолчанию из mat.json",
        };
        var mark = c.RetentionSource == "macroscop" ? "" : "*";
        return $"<td class=\"num\" data-v=\"{max}\" title=\"{H(src)}\">{H(text + mark)}</td>";
    }

    // ---- helpers ----
    private static void Card(StringBuilder sb, string label, string value, string? sub = null, string? cls = null)
    {
        sb.Append("<div class=\"card").Append(cls != null ? " " + cls : "").Append("\"><div class=\"label\">").Append(H(label))
          .Append("</div><div class=\"value\">").Append(H(value)).Append("</div>");
        if (sub != null) sb.Append("<div class=\"sub\">").Append(H(sub)).Append("</div>");
        sb.Append("</div>");
    }

    private static string Th(string text, bool numeric = false)
        => $"<th{(numeric ? " class=\"num\"" : "")} tabindex=\"0\">{H(text)}</th>";

    private static string Td(string text, string? cls = null, string? title = null)
        => $"<td{(cls != null ? $" class=\"{cls}\"" : "")}{(title != null ? $" title=\"{H(title)}\"" : "")}>{H(text)}</td>";

    private static string TdN(string text, double sortValue, string? extraCls = null)
        => $"<td class=\"num{(extraCls != null ? " " + extraCls : "")}\" data-v=\"{Inv(sortValue)}\">{H(text)}</td>";

    private static string TdDate(DateTime? utc, bool withTime = false)
        => utc.HasValue
            ? $"<td class=\"num\" data-v=\"{utc.Value.Ticks}\">{H(utc.Value.ToLocalTime().ToString(withTime ? "dd.MM.yyyy HH:mm" : "dd.MM.yyyy", Ru))}</td>"
            : "<td class=\"num\" data-v=\"0\">—</td>";

    private static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Escapes only HTML-significant characters, so Cyrillic and «» stay readable in the source.</summary>
    private static string H(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => ch.ToString() });
        return sb.ToString();
    }

    public static string Bytes(long b)
    {
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
        double v = b; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(v >= 100 || i == 0 ? "0" : "0.#", Ru) + " " + u[i];
    }

    private const string Css = """
:root{--bg:#f6f7f9;--panel:#fff;--text:#1d2330;--muted:#6a7383;--line:#e3e6eb;--accent:#2a6fdb;--warn:#b7791f;--warn-bg:#fdf3e1;--bad:#c53030;--bad-bg:#fdecec;--ok:#2f855a;--bar:#d7dde6}
@media (prefers-color-scheme:dark){:root{--bg:#14171c;--panel:#1c2027;--text:#e6e9ef;--muted:#9aa3b2;--line:#2c323c;--accent:#6aa0ff;--warn:#f0b35a;--warn-bg:#3a2e17;--bad:#ff7b7b;--bad-bg:#3d1f22;--ok:#5cc490;--bar:#343b47}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI",system-ui,-apple-system,Roboto,sans-serif}
main{max-width:1400px;margin:0 auto;padding:24px 16px 48px}h1{font-size:22px;margin:0 0 4px}h2{font-size:18px;margin:0 0 8px}h3{font-size:15px;margin:20px 0 8px}
section{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:18px;margin:16px 0}header{margin-bottom:8px}
.muted{color:var(--muted)}.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px;margin-top:12px}
.card{border:1px solid var(--line);border-radius:8px;padding:12px}.card .label{color:var(--muted);font-size:12px}.card .value{font-size:22px;font-weight:600;margin-top:2px;font-variant-numeric:tabular-nums}
.card .sub{color:var(--muted);font-size:12px;margin-top:2px}.card.warn{border-color:var(--warn)}.card.bad{border-color:var(--bad)}.card.bad .value{color:var(--bad)}.card.ok .value{color:var(--ok)}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}th,td{padding:7px 10px;border-bottom:1px solid var(--line);text-align:left;white-space:nowrap}
th{position:sticky;top:0;background:var(--panel);font-weight:600;font-size:12px;color:var(--muted);cursor:pointer;user-select:none}th:hover,th:focus{color:var(--text);outline:none}
th.asc::after{content:" ▲"}th.desc::after{content:" ▼"}.num{text-align:right}.strong{font-weight:600}.ok{color:var(--ok)}td.warn{color:var(--warn);font-weight:600}td.bad{color:var(--bad);font-weight:600}
tr.rowbad{background:var(--bad-bg)}tr.rowwarn{background:var(--warn-bg)}.tag{display:inline-block;padding:1px 7px;border-radius:999px;font-size:11px;font-weight:600}
.tag.warn{background:var(--warn-bg);color:var(--warn);border:1px solid var(--warn)}.tag.bad{background:var(--bad-bg);color:var(--bad);border:1px solid var(--bad)}
.bar{display:inline-block;width:70px;height:7px;background:var(--bar);border-radius:4px;margin-right:6px;vertical-align:middle;overflow:hidden}.bar.small{width:50px}
.bar span{display:block;height:100%;background:var(--accent)}.bar span.full{background:var(--bad)}.warning{color:var(--warn);margin:6px 0}
#filter{width:100%;max-width:360px;padding:7px 10px;margin:4px 0 10px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--text);font:inherit}
td.small{font-size:12px;color:var(--muted)}.notes ul{margin:0;padding-left:20px}.notes li{margin:4px 0}
.spark{display:inline-block;vertical-align:top;margin:6px 18px 10px 0;width:420px;max-width:100%}.spark svg{width:100%;height:70px;display:block;background:var(--bg);border-radius:6px}.spark .label{font-size:12px;color:var(--muted);margin-bottom:4px}.spark .label b{color:var(--text)}
td.wrap{white-space:normal;min-width:170px;max-width:260px}td.camname{white-space:normal;min-width:140px;max-width:230px}td.rec{white-space:normal;min-width:120px;max-width:150px}td.flags{white-space:normal;min-width:90px}td.flags .tag{white-space:nowrap;margin:1px 0}td.todo{white-space:normal;min-width:220px;max-width:340px}
td.status2{white-space:normal;line-height:1.3;min-width:150px}td.status2 b{color:var(--text);font-weight:600}
tr.drv{cursor:pointer}tr.drv:hover,tr.drv.open{background:var(--bg)}tr.drv:focus{outline:2px solid var(--accent);outline-offset:-2px}
tr.detail>td{padding:4px 10px 14px 28px;background:var(--bg);white-space:normal}
table.days,table.dcams{width:auto;min-width:520px}table.days td,table.dcams td{padding:4px 10px}table.days th{cursor:default}
tr.day{cursor:pointer}tr.day:hover,tr.day.open{background:var(--panel)}tr.camrow>td{padding:2px 0 10px 24px}
.bar.wide{width:220px}.bar.mid{width:140px}
@media print{section{break-inside:avoid}#filter{display:none}}
""";

    private const string Js = """
document.querySelectorAll('table.sortable').forEach(function(t){
  t.querySelectorAll('th').forEach(function(th,i){
    function sort(){
      var asc=!th.classList.contains('asc');
      t.querySelectorAll('th').forEach(function(x){x.classList.remove('asc','desc')});
      th.classList.add(asc?'asc':'desc');
      var b=t.tBodies[0];
      [].forEach.call(b.querySelectorAll('tr.detail'),function(r){r.parentNode.removeChild(r)});
      [].forEach.call(b.querySelectorAll('tr.drv.open'),function(r){r.classList.remove('open');arrow(r,false)});
      var rows=[].slice.call(b.rows);
      rows.sort(function(a,c){
        var x=a.cells[i],y=c.cells[i];
        var vx=x.dataset.v!==undefined?parseFloat(x.dataset.v):x.textContent.toLowerCase();
        var vy=y.dataset.v!==undefined?parseFloat(y.dataset.v):y.textContent.toLowerCase();
        return (vx<vy?-1:vx>vy?1:0)*(asc?1:-1);
      });
      rows.forEach(function(r){b.appendChild(r)});
    }
    th.addEventListener('click',sort);
    th.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();sort();}});
  });
});
function arrow(tr,open){var c=tr.cells[0];c.textContent=(open?'▾ ':'▸ ')+c.textContent.replace(/^[▸▾] /,'');}
function fmt(b){var u=['Б','КБ','МБ','ГБ','ТБ'],i=0;while(b>=1024&&i<u.length-1){b/=1024;i++}return (b>=100||i===0?Math.round(b)+'':b.toFixed(1).replace('.',','))+' '+u[i];}
var dows=['вс','пн','вт','ср','чт','пт','сб'];
function dayLabel(s){var p=s.split('-'),d=new Date(+p[0],p[1]-1,+p[2]);return p[2]+'.'+p[1]+'.'+p[0]+', '+dows[d.getDay()];}
function esc(s){return String(s).replace(/[&<>"]/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]});}
function pct(v,max){return max>0?Math.max(1,Math.round(v/max*100)):0;}
document.querySelectorAll('table.drives').forEach(function(t){
  var el=document.getElementById('days-'+t.dataset.s);if(!el)return;
  var data=JSON.parse(el.textContent),cols=t.tHead.rows[0].cells.length;
  function toggleDrive(tr){
    var next=tr.nextElementSibling;
    if(next&&next.classList.contains('detail')){next.parentNode.removeChild(next);tr.classList.remove('open');arrow(tr,false);return;}
    var days=data.drives[tr.dataset.d]||[],max=0,total=0;
    days.forEach(function(d){if(d[1]>max)max=d[1];total+=d[1];});
    var h='<p class="muted">'+days.length+' дн., в среднем '+fmt(days.length?total/days.length:0)+' в сутки. Щёлкните день, чтобы увидеть камеры.</p>'+
      '<table class="days"><thead><tr><th>День</th><th class="num">Объём</th><th class="num">Камер</th></tr></thead><tbody>';
    days.forEach(function(d,i){h+='<tr class="day" tabindex="0" data-i="'+i+'"><td>▸ '+dayLabel(d[0])+'</td><td class="num"><div class="bar wide"><span style="width:'+pct(d[1],max)+'%"></span></div>'+fmt(d[1])+'</td><td class="num">'+d[2].length+'</td></tr>';});
    h+='</tbody></table>';
    var row=document.createElement('tr');row.className='detail';
    row.innerHTML='<td colspan="'+cols+'">'+h+'</td>';
    tr.parentNode.insertBefore(row,tr.nextSibling);tr.classList.add('open');arrow(tr,true);
    var body=row.querySelector('table.days tbody');
    function toggleDay(dr){
      var n=dr.nextElementSibling;
      if(n&&n.classList.contains('camrow')){n.parentNode.removeChild(n);dr.classList.remove('open');arrow(dr,false);return;}
      var d=days[+dr.dataset.i],cmax=d[2].length?d[2][0][1]:0;
      var c='<table class="dcams"><tbody>';
      d[2].forEach(function(x){c+='<tr><td>'+esc(data.cams[x[0]])+'</td><td class="num"><div class="bar mid"><span style="width:'+pct(x[1],cmax)+'%"></span></div>'+fmt(x[1])+'</td><td class="num muted">'+(d[1]>0?(x[1]/d[1]*100).toFixed(1).replace('.',','):'0')+'%</td></tr>';});
      c+='</tbody></table>';
      var cr=document.createElement('tr');cr.className='camrow';cr.innerHTML='<td colspan="3">'+c+'</td>';
      dr.parentNode.insertBefore(cr,dr.nextSibling);dr.classList.add('open');arrow(dr,true);
    }
    body.addEventListener('click',function(e){var dr=e.target.closest('tr.day');if(dr)toggleDay(dr);});
    body.addEventListener('keydown',function(e){var dr=e.target.closest('tr.day');if(dr&&(e.key==='Enter'||e.key===' ')){e.preventDefault();toggleDay(dr);}});
  }
  t.tBodies[0].addEventListener('click',function(e){if(e.target.closest('tr.detail'))return;var tr=e.target.closest('tr.drv');if(tr)toggleDrive(tr);});
  t.tBodies[0].addEventListener('keydown',function(e){var tr=e.target.closest('tr.drv');if(tr&&e.target===tr&&(e.key==='Enter'||e.key===' ')){e.preventDefault();toggleDrive(tr);}});
});
var f=document.getElementById('filter');
if(f){f.addEventListener('input',function(){var q=f.value.toLowerCase();
  document.querySelectorAll('#cams tbody tr').forEach(function(r){r.style.display=r.cells[0].textContent.toLowerCase().indexOf(q)>=0?'':'none'});});}
""";
}
