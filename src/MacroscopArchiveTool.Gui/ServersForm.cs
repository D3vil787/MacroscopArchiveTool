using MacroscopArchiveTool.Core.Migration;
using MacroscopArchiveTool.Core.Remote;

namespace MacroscopArchiveTool.Gui;

/// <summary>Other Macroscop servers included in the archive report.</summary>
internal sealed class ServersForm : Form
{
    public static string StorePath => Path.Combine(WindowsSystemOps.DataDir, "servers.json");

    private readonly ServerStore _store = ServerStore.Load(StorePath);
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, CheckBoxes = true,
        Font = Ui.Base, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Button _edit, _remove, _check;
    private bool _filling;

    public ServersForm()
    {
        Text = "Серверы в отчёте";
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 520);
        MinimumSize = new Size(640, 420);
        BackColor = Color.White;

        _list.Columns.Add("Сервер", 260);
        _list.Columns.Add("Учётная запись", 200);
        _list.Columns.Add("Проверка", 300);
        _list.ItemChecked += (_, e) =>
        {
            if (_filling) return;
            ((RemoteServer)e.Item.Tag!).Enabled = e.Item.Checked;
            Save();
        };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => Edit();

        var add = Ui.Button("Добавить…", (_, _) => Add());
        _edit = Ui.Button("Изменить…", (_, _) => Edit());
        _remove = Ui.Button("Удалить", (_, _) => Remove());
        _check = Ui.Button("Проверить", async (_, _) => await CheckSelected());
        var close = Ui.Button("Закрыть", (_, _) => Close());

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(20) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(Ui.Text("Другие серверы Macroscop", Ui.Title), 0, 0);
        root.Controls.Add(Ui.Text(
            "Отчёт по архиву соберёт этот сервер и все отмеченные здесь. На каждый сервер программа копирует mat.exe " +
            $"в {RemoteScanner.RemoteDir} через административную шару C$ и один раз запускает скан задачей Планировщика " +
            "от имени SYSTEM — только чтение, WinRM не нужен. Задача и временные файлы удаляются после скана.\n" +
            "Нужен доступ к \\\\сервер\\C$ под администратором этого сервера. Если логин и пароль администратора на серверах " +
            "совпадают, учётную запись можно не указывать. Пароль хранится зашифрованным (Windows DPAPI) и расшифровывается только " +
            "под вашей учётной записью на этом компьютере.", null, Ui.Muted, 760), 0, 1);
        root.Controls.Add(_list, 0, 2);
        var buttons = Ui.Row(add, _edit, _remove, _check, close);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);

        Fill();
    }

    private void Fill()
    {
        _filling = true;
        var selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null;
        _list.Items.Clear();
        foreach (var s in _store.Servers)
        {
            var item = new ListViewItem(new[] { s.Display, s.HasCredentials ? s.QualifiedUser! : "текущая учётная запись Windows", "" })
            { Tag = s, Checked = s.Enabled };
            _list.Items.Add(item);
            if (ReferenceEquals(s, selected)) item.Selected = true;
        }
        _filling = false;
        UpdateButtons();
    }

    private void UpdateButtons() => _edit.Enabled = _remove.Enabled = _check.Enabled = _list.SelectedItems.Count > 0;

    private RemoteServer? Selected => _list.SelectedItems.Count > 0 ? (RemoteServer)_list.SelectedItems[0].Tag! : null;

    private void Save()
    {
        try { _store.Save(StorePath); }
        catch (Exception ex) { Ui.Error(this, "Не удалось сохранить список серверов: " + ex.Message); }
    }

    private async void Add()
    {
        var s = new RemoteServer();
        using var dlg = new ServerDialog(s);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _store.Servers.Add(s);
        Save(); Fill();
        foreach (ListViewItem i in _list.Items) i.Selected = ReferenceEquals(i.Tag, s);
        await CheckSelected();
    }

    private void Edit()
    {
        if (Selected is not { } s) return;
        using var dlg = new ServerDialog(s);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Save(); Fill();
    }

    private void Remove()
    {
        if (Selected is not { } s || !Ui.Confirm(this, $"Убрать {s.Display} из отчёта?")) return;
        _store.Servers.Remove(s);
        Save(); Fill();
    }

    private async Task CheckSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        var s = (RemoteServer)item.Tag!;
        item.SubItems[2].Text = "проверяю…";
        item.ForeColor = SystemColors.ControlText;
        try
        {
            var text = await Task.Run(() => { using var h = new WindowsRemoteHost(s); h.Connect(); return h.Check(); });
            item.SubItems[2].Text = text;
            item.ForeColor = Ui.Ok;
        }
        catch (Exception ex)
        {
            item.SubItems[2].Text = ex.Message;
            item.ForeColor = Ui.Bad;
        }
    }
}

/// <summary>Add or edit one server.</summary>
internal sealed class ServerDialog : Form
{
    private readonly TextBox _host = new() { Width = 320 }, _title = new() { Width = 320 }, _user = new() { Width = 320 },
                             _password = new() { Width = 320, UseSystemPasswordChar = true };
    private readonly RadioButton _current = new() { Text = "Текущая учётная запись Windows", AutoSize = true },
                                 _other = new() { Text = "Другая учётная запись администратора этого сервера", AutoSize = true };

    public ServerDialog(RemoteServer s)
    {
        Text = string.IsNullOrEmpty(s.Host) ? "Добавить сервер" : "Сервер " + s.Display;
        Font = Ui.Base;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        _host.Text = s.Host; _title.Text = s.Title ?? ""; _user.Text = s.User ?? "";
        _current.Checked = !s.HasCredentials; _other.Checked = s.HasCredentials;
        if (!string.IsNullOrEmpty(s.ProtectedPassword)) _password.PlaceholderText = "сохранён — оставьте пустым, чтобы не менять";

        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20) };
        p.Controls.Add(Ui.Text("Имя или IP-адрес сервера"));
        p.Controls.Add(_host);
        p.Controls.Add(Ui.Text("Название в отчёте (необязательно)", null, null, 0));
        p.Controls.Add(_title);
        p.Controls.Add(Ui.Text("Учётная запись", Ui.Subtitle));
        p.Controls.Add(_current);
        p.Controls.Add(Ui.Text("Подходит, если логин и пароль администратора на серверах совпадают.", Ui.Small, Ui.Muted, 420));
        p.Controls.Add(_other);
        p.Controls.Add(Ui.Text("Пользователь (например, Administrator)", Ui.Small));
        p.Controls.Add(_user);
        p.Controls.Add(Ui.Text("Пароль", Ui.Small));
        p.Controls.Add(_password);
        foreach (Control c in new Control[] { _host, _title, _user, _password }) c.Margin = new Padding(0, 0, 0, 10);

        void Toggle() { _user.Enabled = _password.Enabled = _other.Checked; }
        _other.CheckedChanged += (_, _) => Toggle();
        Toggle();

        var ok = Ui.Button("Сохранить", (_, _) =>
        {
            var host = _host.Text.Trim().TrimStart('\\');
            if (host.Length == 0) { Ui.Error(this, "Укажите имя или IP-адрес сервера."); return; }
            if (_other.Checked && _user.Text.Trim().Length == 0) { Ui.Error(this, "Укажите пользователя."); return; }
            if (_other.Checked && _password.Text.Length == 0 && string.IsNullOrEmpty(s.ProtectedPassword)) { Ui.Error(this, "Укажите пароль."); return; }
            s.Host = host;
            s.Title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : null;
            if (_other.Checked)
            {
                s.User = _user.Text.Trim();
                if (_password.Text.Length > 0) s.ProtectedPassword = Dpapi.Protect(_password.Text);
            }
            else { s.User = null; s.ProtectedPassword = null; }
            DialogResult = DialogResult.OK;
            Close();
        });
        var cancel = Ui.Button("Отмена", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        p.Controls.Add(Ui.Row(ok, cancel));
        Controls.Add(p);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
