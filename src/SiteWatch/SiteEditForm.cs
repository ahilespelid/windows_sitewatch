namespace SiteWatch;

/// <summary>Окно «Добавить / изменить сайт».</summary>
public sealed class SiteEditForm : Form
{
    private static readonly (CheckType type, string title, string example)[] Kinds =
    {
        (CheckType.Http, "Сайт — открыть страницу", "https://site.ru/"),
        (CheckType.Tcp, "Порт — сервис без сайта (база, API)", "1.2.3.4:5432"),
        (CheckType.Ping, "Ping — жив ли сам сервер", "1.2.3.4"),
    };

    private readonly TextBox _name = new() { Width = 420 };
    private readonly ComboBox _type = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _address = new() { Width = 420 };
    private readonly Label _example = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox _keyword = new() { Width = 420 };
    private readonly Label _keywordLabel = new() { Text = "Слово на странице", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
    private readonly Label _keywordHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(420, 0),
        ForeColor = SystemColors.GrayText,
        Text = "Необязательно. Если указать — сайт считается рабочим, только когда этот текст есть на странице " +
               "(так не пропустим заглушку nginx или страницу с ошибкой).",
    };
    private readonly CheckBox _enabled = new() { Text = "Проверять", AutoSize = true, Checked = true };
    private readonly int _timeoutSeconds;
    private readonly Func<SiteConfig, bool> _isDuplicate;

    public SiteConfig Result { get; private set; }

    public SiteEditForm(SiteConfig site, int timeoutSeconds, Func<SiteConfig, bool> isDuplicate)
    {
        _timeoutSeconds = timeoutSeconds;
        _isDuplicate = isDuplicate;

        Text = site == null ? "Добавить сайт" : "Изменить сайт";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(12);

        foreach (var k in Kinds) _type.Items.Add(k.title);

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(t, new Label { Text = "Что проверять", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, _type);
        AddRow(t, new Label { Text = "Адрес", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, Stack(_address, _example));
        AddRow(t, new Label { Text = "Название", AutoSize = true, Margin = new Padding(0, 8, 8, 0) },
            Stack(_name, new Label { Text = "Как сайт будет называться в письмах. Пусто — как адрес.", AutoSize = true, ForeColor = SystemColors.GrayText }));
        AddRow(t, _keywordLabel, Stack(_keyword, _keywordHint));
        AddRow(t, new Label(), _enabled);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "Сохранить", AutoSize = true };
        var test = new Button { Text = "Проверить сейчас", AutoSize = true };
        ok.Click += (_, _) => Save();
        test.Click += async (_, _) => await TestAsync(test);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(test);
        t.Controls.Add(buttons, 0, t.RowCount);
        t.SetColumnSpan(buttons, 2);
        t.RowCount++;

        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;

        _type.SelectedIndexChanged += (_, _) => UpdateKind();

        var s = site ?? new SiteConfig();
        _type.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.type == s.Type));
        _address.Text = s.Address;
        _name.Text = s.Name;
        _keyword.Text = s.Keyword;
        _enabled.Checked = site == null || s.Enabled;
        UpdateKind();
    }

    private CheckType SelectedType => Kinds[Math.Max(0, _type.SelectedIndex)].type;

    private void UpdateKind()
    {
        var kind = Kinds[Math.Max(0, _type.SelectedIndex)];
        _example.Text = "Например: " + kind.example;
        var http = kind.type == CheckType.Http;
        _keyword.Enabled = http;
        _keywordLabel.Enabled = http;
        _keywordHint.Enabled = http;
    }

    private SiteConfig Collect()
    {
        var type = SelectedType;
        var address = _address.Text.Trim();
        // Забыли https:// — допишем сами.
        if (type == CheckType.Http && address.Length > 0 && !address.Contains("://"))
            address = "https://" + address;
        return new SiteConfig
        {
            Type = type,
            Address = address,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? address : _name.Text.Trim(),
            Keyword = type == CheckType.Http ? _keyword.Text.Trim() : "",
            Enabled = _enabled.Checked,
        };
    }

    /// <summary>null — всё верно, иначе текст ошибки.</summary>
    public static string Check(SiteConfig s)
    {
        if (string.IsNullOrWhiteSpace(s.Address)) return "Укажите адрес.";
        switch (s.Type)
        {
            case CheckType.Http:
                if (!Uri.TryCreate(s.Address, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return "Адрес сайта должен быть вида https://site.ru/";
                break;
            case CheckType.Tcp:
                try { SiteChecker.ParseHostPort(s.Address); }
                catch { return "Адрес порта пишется так: 1.2.3.4:5432 (сервер:порт)"; }
                break;
            case CheckType.Ping:
                if (s.Address.Contains('/') || s.Address.Contains(' '))
                    return "Для ping нужен только сервер: 1.2.3.4 или site.ru";
                break;
        }
        return null;
    }

    private void Save()
    {
        var s = Collect();
        var error = Check(s);
        if (error == null && _isDuplicate(s)) error = "Такой адрес с такой проверкой уже есть в списке.";
        if (error != null)
        {
            MessageBox.Show(this, error, "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _address.Text = s.Address;
        Result = s;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task TestAsync(Button button)
    {
        var s = Collect();
        var error = Check(s);
        if (error != null)
        {
            MessageBox.Show(this, error, "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _address.Text = s.Address;
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var r = await SiteChecker.CheckAsync(s, _timeoutSeconds, CancellationToken.None);
            MessageBox.Show(this, $"{s.Address}\n\n{(r.Ok ? "РАБОТАЕТ" : "НЕ РАБОТАЕТ")}: {r.Details}\nВремя ответа: {r.ElapsedMs} мс",
                "Проверка", MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            button.Enabled = true;
        }
    }

    private static void AddRow(TableLayoutPanel t, Control left, Control right)
    {
        var row = t.RowCount;
        t.Controls.Add(left, 0, row);
        t.Controls.Add(right, 1, row);
        t.RowCount = row + 1;
    }

    private static Control Stack(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 6),
        };
        foreach (var c in controls)
        {
            c.Margin = new Padding(0, 0, 0, 2);
            p.Controls.Add(c);
        }
        return p;
    }
}
