using MacroscopArchiveTool.Core;

namespace MacroscopArchiveTool.Gui;

/// <summary>Runs the read-only archive scan and opens the HTML report.</summary>
internal sealed class ReportForm : Form
{
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Ui.Small, BackColor = Color.White };
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Top, Height = 18, MarqueeAnimationSpeed = 30 };
    private readonly Label _status = Ui.Text("Сканирование архива… Это только чтение, Macroscop может работать.", Ui.Subtitle);
    private readonly Button _cancel, _openReport, _openFolder;
    private readonly CancellationTokenSource _cts = new();
    private ReportRunner.Result? _result;
    private bool _running;

    public static string ReportsDir => Path.Combine(AppContext.BaseDirectory, "reports");

    public ReportForm()
    {
        Text = "Отчёт по архиву";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 480);
        MinimumSize = new Size(560, 360);
        BackColor = Color.White;

        _cancel = Ui.Button("Остановить", (_, _) => { if (_running) _cts.Cancel(); else Close(); });
        _openReport = Ui.Button("Открыть отчёт", (_, _) => { if (_result?.HtmlPath != null) Ui.Open(_result.HtmlPath); });
        _openFolder = Ui.Button("Папка с отчётами", (_, _) => Ui.Open(ReportsDir));
        _openReport.Visible = _openFolder.Visible = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(20) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_status, 0, 0);
        root.Controls.Add(_bar, 0, 1);
        root.Controls.Add(_log, 0, 2);
        root.Controls.Add(Ui.Row(_openReport, _openFolder, _cancel), 0, 3);
        Controls.Add(root);

        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, e) =>
        {
            if (!_running) return;
            if (Ui.Confirm(this, "Остановить сканирование?")) _cts.Cancel();
            e.Cancel = true;
        };
    }

    private async Task RunAsync()
    {
        _running = true;
        var progress = new Progress<string>(m => _log.AppendText(m + Environment.NewLine));
        try
        {
            var config = Ui.LoadConfig();
            _result = await Task.Run(() => ReportRunner.Run(config, null, null, ReportsDir, true, progress, _cts.Token));
            var r = _result.Report;
            _status.Text = $"Готово: {r.Cameras.Count} камер, {r.Drives.Count} дисков, видео {Ui.Bytes(r.Drives.Sum(d => d.MediaBytes))}.";
            _status.ForeColor = Ui.Ok;
            foreach (var w in r.Warnings) _log.AppendText("! " + w + Environment.NewLine);
            _log.AppendText($"Отчёт: {_result.HtmlPath}{Environment.NewLine}");
            _openReport.Visible = _openFolder.Visible = true;
            if (_result.HtmlPath != null) Ui.Open(_result.HtmlPath);
        }
        catch (OperationCanceledException) { _status.Text = "Сканирование остановлено."; _status.ForeColor = Ui.Warn; }
        catch (Exception ex) { _status.Text = "Ошибка: " + ex.Message; _status.ForeColor = Ui.Bad; }
        finally
        {
            _running = false;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = _bar.Maximum;
            _cancel.Text = "Закрыть";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
