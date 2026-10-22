using System.Text;
using MacroscopArchiveTool.Core;
using MacroscopArchiveTool.Core.Remote;

namespace MacroscopArchiveTool.Gui;

/// <summary>
/// Read-only archive scan of this server and of every enabled remote server (in parallel),
/// then one HTML report — combined when there is more than one server.
/// </summary>
internal sealed class ReportForm : Form
{
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Ui.Small, BackColor = Color.White };
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Top, Height = 18, MarqueeAnimationSpeed = 30 };
    private readonly Label _status = Ui.Text("Сканирование архива… Это только чтение, Macroscop может работать.", Ui.Subtitle);
    private readonly FlowLayoutPanel _servers = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 8) };
    private readonly Button _cancel, _openReport, _openFolder;
    private readonly CancellationTokenSource _cts = new();
    private string? _htmlPath;
    private bool _running;

    public static string ReportsDir => Path.Combine(AppContext.BaseDirectory, "reports");

    public ReportForm()
    {
        Text = "Отчёт по архиву";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 560);
        MinimumSize = new Size(600, 400);
        BackColor = Color.White;

        _cancel = Ui.Button("Остановить", (_, _) => { if (_running) _cts.Cancel(); else Close(); });
        _openReport = Ui.Button("Открыть отчёт", (_, _) => { if (_htmlPath != null) Ui.Open(_htmlPath); });
        _openFolder = Ui.Button("Папка с отчётами", (_, _) => Ui.Open(ReportsDir));
        _openReport.Visible = _openFolder.Visible = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(20) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_bar, 0, 1);
        root.Controls.Add(_servers, 0, 2);
        root.Controls.Add(_log, 0, 3);
        root.Controls.Add(Ui.Row(_openReport, _openFolder, _cancel), 0, 4);
        Controls.Add(root);

        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, e) =>
        {
            if (!_running) return;
            if (Ui.Confirm(this, "Остановить сканирование?")) _cts.Cancel();
            e.Cancel = true;
        };
    }

    private sealed record Job(string Name, Label Line, Func<IProgress<string>, CancellationToken, ScanReport> Run);

    private async Task RunAsync()
    {
        _running = true;
        var config = Ui.LoadConfig();
        var remotes = ServerStore.Load(ServersForm.StorePath).Servers.Where(s => s.Enabled).ToList();
        Directory.CreateDirectory(ReportsDir);

        var jobs = new List<Job>
        {
            new($"Этот сервер ({Environment.MachineName})", Line(), (p, ct) =>
                ReportRunner.Run(config, null, null, ReportsDir, remotes.Count == 0, p, ct).Report),
        };
        foreach (var s in remotes)
        {
            var server = s;
            jobs.Add(new(server.Display, Line(), (p, ct) =>
            {
                using var host = new WindowsRemoteHost(server);
                var r = new RemoteScanner().Run(host, p, ct);
                ReportStore.SaveJson(r, Path.Combine(ReportsDir, ReportStore.BaseFileName(r) + ".json"));
                return r;
            }));
        }

        // Progress objects are created here, on the window thread, so their callbacks update the window safely.
        var tasks = jobs.Select(job =>
        {
            var progress = new Progress<string>(m => { Append($"[{job.Name}] {m}"); job.Line.Text = $"●  {job.Name}: {m}"; });
            return Task.Run(() => RunJob(job, progress));
        }).ToList();
        var results = await Task.WhenAll(tasks);
        var reports = results.Where(r => r != null).Cast<ScanReport>().ToList();
        int failed = results.Count(r => r == null);

        _running = false;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = _bar.Maximum;
        _cancel.Text = "Закрыть";
        if (_cts.IsCancellationRequested) { _status.Text = "Сканирование остановлено."; _status.ForeColor = Ui.Warn; return; }
        if (reports.Count == 0) { _status.Text = "Отчёт не получен — подробности ниже."; _status.ForeColor = Ui.Bad; return; }

        try
        {
            if (reports.Count == 1 && remotes.Count == 0)
                _htmlPath = Path.Combine(ReportsDir, ReportStore.BaseFileName(reports[0]) + ".html");
            else
            {
                _htmlPath = Path.Combine(ReportsDir, $"macroscop_сводный_{DateTime.Now:yyyyMMdd_HHmm}.html");
                File.WriteAllText(_htmlPath, HtmlReport.Render(reports), new UTF8Encoding(true));
            }
            _status.Text = $"Готово: {reports.Count} сервер(а), {reports.Sum(r => r.Cameras.Count)} камер, видео {Ui.Bytes(reports.Sum(r => r.Drives.Sum(d => d.MediaBytes)))}." +
                           (failed > 0 ? $" Не удалось: {failed} — см. ниже." : "");
            _status.ForeColor = failed > 0 ? Ui.Warn : Ui.Ok;
            foreach (var w in reports.SelectMany(r => r.Warnings.Select(w => $"{r.ServerName}: {w}"))) Append("! " + w);
            Append("Отчёт: " + _htmlPath);
            _openReport.Visible = _openFolder.Visible = true;
            Ui.Open(_htmlPath);
        }
        catch (Exception ex) { _status.Text = "Ошибка: " + ex.Message; _status.ForeColor = Ui.Bad; }
    }

    private ScanReport? RunJob(Job job, IProgress<string> progress)
    {
        void Show(string text, Color color) => BeginInvoke(() => { job.Line.Text = $"●  {job.Name}: {text}"; job.Line.ForeColor = color; });
        try
        {
            Show("запуск…", Ui.Accent);
            var r = job.Run(progress, _cts.Token);
            Show($"готово — {r.Cameras.Count} камер, {r.Drives.Count} дисков", Ui.Ok);
            return r;
        }
        catch (OperationCanceledException) { Show("остановлено", Ui.Warn); return null; }
        catch (Exception ex)
        {
            Show("ошибка — " + ex.Message, Ui.Bad);
            BeginInvoke(() => Append($"[{job.Name}] ОШИБКА: {ex.Message}"));
            return null;
        }
    }

    private Label Line()
    {
        var l = Ui.Text("●  …", null, Ui.Muted, 780);
        l.Margin = new Padding(0, 0, 0, 4);
        _servers.Controls.Add(l);
        return l;
    }

    private void Append(string line) => _log.AppendText(line + Environment.NewLine);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
