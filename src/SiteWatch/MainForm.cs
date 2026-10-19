using System.Net.Mail;

namespace SiteWatch;

/// <summary>
/// Главное окно: сайты, почта, звонки, настройки. Всё сохраняется сразу —
/// кнопки «Сохранить» нет. Закрытие окна прячет программу в трей, проверка
/// продолжается.
/// </summary>
public sealed class MainForm : Form
{
    private readonly MonitorService _monitor;
    private readonly AppConfig _cfg;
    private readonly Action _exit;
    private readonly System.Windows.Forms.Timer _ticker;
    private readonly System.Windows.Forms.Timer _saveDebounce;
    private bool _loading;
    private bool _trayHintShown;

    private readonly Label _header = new()
    {
        Dock = DockStyle.Top,
        Height = 46,
        Padding = new Padding(14, 0, 14, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
        ForeColor = Color.White,
    };

    // Сайты
    private readonly ListView _sites = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        CheckBoxes = true,
        MultiSelect = false,
    };

    // Почта
    private readonly CheckBox _mailEnabled = new() { Text = "Отправлять письма, когда сайт упал", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    private readonly ListBox _mailList = new() { Width = 420, Height = 130, IntegralHeight = false };
    private readonly TextBox _mailNew = new() { Width = 300, PlaceholderText = "name@company.ru" };
    private readonly ComboBox _smtpPreset = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _smtpHost = new() { Width = 300 };
    private readonly NumericUpDown _smtpPort = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    private readonly CheckBox _smtpSsl = new() { Text = "Шифрование (STARTTLS)", AutoSize = true };
    private readonly TextBox _smtpUser = new() { Width = 300, PlaceholderText = "monitor@yandex.ru" };
    private readonly TextBox _smtpPassword = new() { Width = 300, UseSystemPasswordChar = true };

    // Звонки
    private readonly CheckBox _modemEnabled = new() { Text = "Звонить, когда сайт упал", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    private readonly ListView _phoneList = new()
    {
        Width = 420,
        Height = 130,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
    };
    private readonly TextBox _phoneName = new() { Width = 140, PlaceholderText = "Имя" };
    private readonly TextBox _phoneNumber = new() { Width = 150, PlaceholderText = "+7 999 123-45-67" };
    private readonly ComboBox _modemPort = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _modemBaud = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly NumericUpDown _modemRing = new() { Minimum = 5, Maximum = 120, Width = 80 };

    // Настройки
    private readonly NumericUpDown _interval = new() { Minimum = 10, Maximum = 3600, Width = 90 };
    private readonly NumericUpDown _timeout = new() { Minimum = 3, Maximum = 120, Width = 90 };
    private readonly NumericUpDown _threshold = new() { Minimum = 1, Maximum = 20, Width = 90 };
    private readonly NumericUpDown _reminder = new() { Minimum = 0, Maximum = 1440, Width = 90 };
    private readonly CheckBox _notifyRecovery = new() { Text = "Писать, когда сайт снова заработал", AutoSize = true };
    private readonly TextBox _controlUrl = new() { Width = 300 };
    private readonly CheckBox _autostart = new() { Text = "Запускать вместе с Windows (сразу в трей)", AutoSize = true };

    private static readonly (string name, string host, int port)[] SmtpPresets =
    {
        ("Яндекс", "smtp.yandex.ru", 587),
        ("Mail.ru", "smtp.mail.ru", 587),
        ("Gmail", "smtp.gmail.com", 587),
        ("Другой", "", 587),
    };

    public MainForm(MonitorService monitor, Action exit)
    {
        _monitor = monitor;
        _exit = exit;
        _cfg = monitor.CurrentConfig();

        Text = "SiteWatch — мониторинг сайтов";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1060, 640);
        MinimumSize = new Size(820, 520);
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(SitesTab());
        tabs.TabPages.Add(MailTab());
        tabs.TabPages.Add(CallsTab());
        tabs.TabPages.Add(SettingsTab());

        Controls.Add(tabs);
        Controls.Add(_header);

        _saveDebounce = new System.Windows.Forms.Timer { Interval = 700 };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            CommitFromFields();
        };

        _ticker = new System.Windows.Forms.Timer { Interval = 5000 };
        _ticker.Tick += (_, _) =>
        {
            // «N с назад» обновляем и без новой проверки.
            if (!Visible) return;
            RefreshStatus();
            RefreshSites();
        };
        _ticker.Start();

        LoadFields();
        RefreshSites();
        RefreshStatus();
    }

    // ================================================================== вкладка «Сайты»

    private TabPage SitesTab()
    {
        var page = new TabPage("Сайты") { Padding = new Padding(10) };

        _sites.Columns.Add("Вкл", 46);
        _sites.Columns.Add("Состояние", 115);
        _sites.Columns.Add("Название", 170);
        _sites.Columns.Add("Адрес", 250);
        _sites.Columns.Add("Ответ", 210);
        _sites.Columns.Add("мс", 55, HorizontalAlignment.Right);
        _sites.Columns.Add("Проверен", 95);
        _sites.Columns.Add("Сбои с", 105);
        _sites.DoubleClick += (_, _) => EditSite();
        _sites.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSite();
            if (e.KeyCode == Keys.Enter) EditSite();
        };
        _sites.ItemChecked += (_, e) =>
        {
            if (_loading || e.Item.Tag is not SiteConfig site) return;
            if (site.Enabled == e.Item.Checked) return;
            site.Enabled = e.Item.Checked;
            Commit(sitesChanged: true);
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 0, 0, 6) };
        buttons.Controls.Add(Btn("+ Добавить сайт", AddSite, bold: true));
        buttons.Controls.Add(Btn("Изменить", EditSite));
        buttons.Controls.Add(Btn("Удалить", DeleteSite));
        buttons.Controls.Add(Btn("Проверить все сейчас", () => _monitor.CheckNow()));
        buttons.Controls.Add(Btn("Открыть в браузере", OpenSelectedSite));

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Галочка «Вкл» — проверять сайт или нет. Двойной клик — изменить. Окно можно закрыть: проверка продолжится, значок у часов.",
        };

        page.Controls.Add(_sites);
        page.Controls.Add(hint);
        page.Controls.Add(buttons);
        return page;
    }

    private bool IsDuplicate(SiteConfig candidate, SiteConfig except) =>
        _cfg.Sites.Any(s => !ReferenceEquals(s, except) && s.Key == candidate.Key);

    private void AddSite()
    {
        using var f = new SiteEditForm(null, _cfg.TimeoutSeconds, s => IsDuplicate(s, null));
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _cfg.Sites.Add(f.Result);
        Commit(sitesChanged: true);
        RefreshSites(selectSite: f.Result);
    }

    private SiteConfig SelectedSite =>
        _sites.SelectedItems.Count > 0 ? _sites.SelectedItems[0].Tag as SiteConfig : null;

    private void EditSite()
    {
        var site = SelectedSite;
        if (site == null) return;
        using var f = new SiteEditForm(site, _cfg.TimeoutSeconds, s => IsDuplicate(s, site));
        if (f.ShowDialog(this) != DialogResult.OK) return;
        var idx = _cfg.Sites.IndexOf(site);
        _cfg.Sites[idx] = f.Result;
        Commit(sitesChanged: true);
        RefreshSites(selectSite: f.Result);
    }

    private void DeleteSite()
    {
        var site = SelectedSite;
        if (site == null) return;
        if (MessageBox.Show(this, $"Удалить «{site.Name}» из списка?", "SiteWatch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _cfg.Sites.Remove(site);
        Commit(sitesChanged: true);
        RefreshSites();
    }

    private void OpenSelectedSite()
    {
        var site = SelectedSite;
        if (site == null || site.Type != CheckType.Http) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(site.Address) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Не открылся адрес " + site.Address, ex);
        }
    }

    /// <summary>Перестроить таблицу сайтов (список из настроек + состояние из проверки).</summary>
    private void RefreshSites(SiteConfig selectSite = null)
    {
        selectSite ??= SelectedSite;
        var states = _monitor.Snapshot().ToDictionary(s => s.Site.Key);

        var ordered = _cfg.Sites
            .OrderByDescending(s => s.Enabled && states.TryGetValue(s.Key, out var state) && state.IsDown)
            .ThenByDescending(s => s.Enabled)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Тот же список в том же порядке — обновляем строки на месте, чтобы не
        // сбрасывать прокрутку и выделение каждые 5 секунд.
        var sameRows = _sites.Items.Count == ordered.Count &&
                       ordered.Select((x, n) => ReferenceEquals(_sites.Items[n].Tag, x)).All(x => x);

        _loading = true;
        _sites.BeginUpdate();
        try
        {
            if (!sameRows) _sites.Items.Clear();
            for (var i = 0; i < ordered.Count; i++)
            {
                var site = ordered[i];
                states.TryGetValue(site.Key, out var st);
                var (label, color) = !site.Enabled
                    ? ("выключен", Color.FromArgb(235, 235, 235))
                    : st switch
                    {
                        { IsDown: true } => ("НЕ РАБОТАЕТ", Color.FromArgb(255, 215, 215)),
                        { Muted: true } => ("адрес не найден", Color.FromArgb(235, 235, 235)),
                        { LastOk: false } => ($"сбой {st.ConsecutiveFails}/{_cfg.FailThreshold}", Color.FromArgb(255, 240, 200)),
                        { LastOk: true } => ("работает", Color.FromArgb(225, 245, 230)),
                        _ => ("ждём проверку", Color.White),
                    };
                var cells = new[]
                {
                    "",
                    label,
                    site.Name,
                    site.Address,
                    site.Enabled ? st?.LastDetails ?? "" : "",
                    site.Enabled && st?.LastCheck != null ? st.LastElapsedMs.ToString() : "",
                    site.Enabled && st?.LastCheck != null ? Ago(st.LastCheck.Value) : "",
                    site.Enabled && st?.DownSince != null ? st.DownSince.Value.ToString("dd.MM HH:mm") : "",
                };

                ListViewItem item;
                if (sameRows)
                {
                    item = _sites.Items[i];
                    for (var c = 1; c < cells.Length; c++)
                        if (item.SubItems[c].Text != cells[c]) item.SubItems[c].Text = cells[c];
                }
                else
                {
                    item = new ListViewItem(cells[0]) { Tag = site };
                    for (var c = 1; c < cells.Length; c++) item.SubItems.Add(cells[c]);
                    _sites.Items.Add(item);
                    if (ReferenceEquals(site, selectSite)) item.Selected = true;
                }
                if (item.Checked != site.Enabled) item.Checked = site.Enabled;
                if (item.BackColor != color) item.BackColor = color;
            }
        }
        finally
        {
            _sites.EndUpdate();
            _loading = false;
        }
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds} с назад" : MonitorService.FormatDuration(d) + " назад";
    }

    // ================================================================== вкладка «Почта»

    private TabPage MailTab()
    {
        var page = new TabPage("Почта") { Padding = new Padding(14), AutoScroll = true };
        var col = Column();

        col.Controls.Add(_mailEnabled);
        _mailEnabled.CheckedChanged += (_, _) => FieldChanged();

        // Кому
        col.Controls.Add(Title("Кому писать"));
        var addRow = Row();
        addRow.Controls.Add(_mailNew);
        addRow.Controls.Add(Btn("Добавить", AddMail));
        col.Controls.Add(addRow);
        _mailNew.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddMail();
        };
        col.Controls.Add(_mailList);
        var mailButtons = Row();
        mailButtons.Controls.Add(Btn("Удалить выбранный", RemoveMail));
        col.Controls.Add(mailButtons);

        // От кого
        col.Controls.Add(Title("С какого ящика отправлять"));
        col.Controls.Add(Hint("Заведите отдельный ящик для уведомлений. Нужен «пароль приложения» — " +
                              "его создают в настройках безопасности почты; обычный пароль от ящика не подойдёт."));
        foreach (var p in SmtpPresets) _smtpPreset.Items.Add(p.name);
        _smtpPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _smtpPreset.SelectedIndex < 0) return;
            var p = SmtpPresets[_smtpPreset.SelectedIndex];
            if (p.host.Length > 0)
            {
                _smtpHost.Text = p.host;
                _smtpPort.Value = p.port;
                _smtpSsl.Checked = true;
            }
            FieldChanged();
        };
        col.Controls.Add(Labeled("Почта", _smtpPreset));
        col.Controls.Add(Labeled("SMTP-сервер", _smtpHost));
        col.Controls.Add(Labeled("Порт", _smtpPort, "587. Порт 465 не поддерживается."));
        col.Controls.Add(Labeled("", _smtpSsl));
        col.Controls.Add(Labeled("Логин (ящик)", _smtpUser));
        col.Controls.Add(Labeled("Пароль приложения", _smtpPassword));
        foreach (var c in new Control[] { _smtpHost, _smtpUser, _smtpPassword }) c.TextChanged += (_, _) => FieldChanged();
        _smtpPort.ValueChanged += (_, _) => FieldChanged();
        _smtpSsl.CheckedChanged += (_, _) => FieldChanged();

        var test = Btn("Отправить тестовое письмо", null, bold: true);
        test.Click += async (_, _) => await TestMailAsync(test);
        col.Controls.Add(test);

        page.Controls.Add(col);
        return page;
    }

    private void AddMail()
    {
        var text = _mailNew.Text.Trim();
        if (text.Length == 0) return;
        foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string address;
            try { address = new MailAddress(raw.Trim()).Address; }
            catch
            {
                MessageBox.Show(this, $"«{raw}» — это не адрес почты.", "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_cfg.Email.Recipients.Contains(address, StringComparer.OrdinalIgnoreCase))
                _cfg.Email.Recipients.Add(address);
        }
        _mailNew.Clear();
        // Добавили первый адрес — значит, письма нужны.
        if (!_cfg.Email.Enabled && _cfg.Email.Recipients.Count > 0) _mailEnabled.Checked = true;
        RefreshMailList();
        Commit();
    }

    private void RemoveMail()
    {
        if (_mailList.SelectedItem is not string address) return;
        _cfg.Email.Recipients.Remove(address);
        RefreshMailList();
        Commit();
    }

    private void RefreshMailList()
    {
        _mailList.Items.Clear();
        foreach (var r in _cfg.Email.Recipients) _mailList.Items.Add(r);
    }

    private async Task TestMailAsync(Button button)
    {
        CommitFromFields();
        if (_cfg.Email.Recipients.Count == 0)
        {
            MessageBox.Show(this, "Сначала добавьте адрес, куда писать.", "SiteWatch");
            return;
        }
        await Busy(button, async () =>
        {
            try
            {
                await new EmailChannel().SendAsync(_cfg, TestAlert(), CancellationToken.None);
                MessageBox.Show(this, "Письмо отправлено на:\n" + string.Join("\n", _cfg.Email.Recipients) +
                                      "\n\nЕсли не пришло — проверьте папку «Спам».",
                    "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("Тестовое письмо", ex);
                MessageBox.Show(this, "Письмо не отправилось:\n" + SiteChecker.Describe(ex) +
                                      "\n\nЧастые причины: обычный пароль вместо пароля приложения, неверный логин, порт 465 вместо 587.",
                    "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    // ================================================================== вкладка «Звонки»

    private TabPage CallsTab()
    {
        var page = new TabPage("Звонки") { Padding = new Padding(14), AutoScroll = true };
        var col = Column();

        var note = Hint("Звонок через GSM-модем с SIM-картой. Это заготовка: на настоящем модеме ещё не проверялось.\n" +
                        "Программа просто звонит по списку — брать трубку не нужно, сам звонок и есть сигнал «сайт упал». " +
                        "На «снова работает» не звонит.\n" +
                        "Нужен модем с поддержкой голосовых вызовов (например SIM800 / SIM7600 через USB), видный в Windows как COM-порт. " +
                        "Обычные USB-модемы операторов часто звонить не умеют.");
        note.ForeColor = Color.FromArgb(150, 90, 0);
        col.Controls.Add(note);

        col.Controls.Add(_modemEnabled);
        _modemEnabled.CheckedChanged += (_, _) => FieldChanged();

        // Кому звонить
        col.Controls.Add(Title("Кому звонить"));
        var addRow = Row();
        addRow.Controls.Add(_phoneName);
        addRow.Controls.Add(_phoneNumber);
        addRow.Controls.Add(Btn("Добавить", AddPhone));
        col.Controls.Add(addRow);
        _phoneNumber.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddPhone();
        };
        _phoneList.Columns.Add("Имя", 180);
        _phoneList.Columns.Add("Номер", 200);
        col.Controls.Add(_phoneList);
        var phoneButtons = Row();
        phoneButtons.Controls.Add(Btn("Удалить выбранный", RemovePhone));
        var testCall = Btn("Позвонить на выбранный (тест)", null);
        testCall.Click += async (_, _) => await TestCallAsync(testCall);
        phoneButtons.Controls.Add(testCall);
        col.Controls.Add(phoneButtons);

        // Модем
        col.Controls.Add(Title("Модем"));
        var portRow = Row();
        portRow.Controls.Add(_modemPort);
        portRow.Controls.Add(Btn("Обновить список", RefreshPorts));
        col.Controls.Add(Labeled("COM-порт", portRow));
        _modemBaud.Items.AddRange(new object[] { "9600", "19200", "57600", "115200" });
        col.Controls.Add(Labeled("Скорость порта", _modemBaud));
        col.Controls.Add(Labeled("Держать вызов, секунд", _modemRing));
        _modemPort.TextChanged += (_, _) => FieldChanged();
        _modemBaud.TextChanged += (_, _) => FieldChanged();
        _modemRing.ValueChanged += (_, _) => FieldChanged();

        var probe = Btn("Проверить модем (без звонка)", null, bold: true);
        probe.Click += async (_, _) => await ProbeModemAsync(probe);
        col.Controls.Add(probe);

        RefreshPorts();
        page.Controls.Add(col);
        return page;
    }

    private void AddPhone()
    {
        var number = PhoneContact.Normalize(_phoneNumber.Text);
        if (number == null)
        {
            MessageBox.Show(this, "Номер не похож на телефонный. Пример: +7 999 123-45-67", "SiteWatch",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_cfg.Modem.Phones.Any(p => p.Number == number))
        {
            MessageBox.Show(this, "Этот номер уже в списке.", "SiteWatch");
            return;
        }
        _cfg.Modem.Phones.Add(new PhoneContact { Name = _phoneName.Text.Trim(), Number = number });
        _phoneName.Clear();
        _phoneNumber.Clear();
        RefreshPhoneList();
        Commit();
    }

    private PhoneContact SelectedPhone =>
        _phoneList.SelectedItems.Count > 0 ? _phoneList.SelectedItems[0].Tag as PhoneContact : null;

    private void RemovePhone()
    {
        var p = SelectedPhone;
        if (p == null) return;
        _cfg.Modem.Phones.Remove(p);
        RefreshPhoneList();
        Commit();
    }

    private void RefreshPhoneList()
    {
        _phoneList.Items.Clear();
        foreach (var p in _cfg.Modem.Phones)
        {
            var item = new ListViewItem(p.Name) { Tag = p };
            item.SubItems.Add(p.Number);
            _phoneList.Items.Add(item);
        }
    }

    private void RefreshPorts()
    {
        var current = _modemPort.Text;
        _modemPort.Items.Clear();
        _modemPort.Items.AddRange(ModemChannel.AvailablePorts());
        _modemPort.Text = current;
    }

    private async Task TestCallAsync(Button button)
    {
        CommitFromFields();
        var p = SelectedPhone;
        if (p == null)
        {
            MessageBox.Show(this, "Выберите номер в списке.", "SiteWatch");
            return;
        }
        await Busy(button, async () =>
        {
            try
            {
                await ModemChannel.CallAsync(_cfg.Modem, new List<PhoneContact> { p }, CancellationToken.None);
                MessageBox.Show(this, $"Вызов на {p.Number} выполнен.", "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Error("Тестовый звонок", ex);
                MessageBox.Show(this, "Звонок не прошёл:\n" + ex.Message, "SiteWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    private async Task ProbeModemAsync(Button button)
    {
        CommitFromFields();
        await Busy(button, async () =>
        {
            try
            {
                var info = await ModemChannel.ProbeAsync(_cfg.Modem);
                MessageBox.Show(this, info, "SiteWatch — модем", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Модем не отвечает:\n" + ex.Message +
                                      "\n\nПроверьте, что модем подключён и выбран правильный COM-порт.",
                    "SiteWatch — модем", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
    }

    // ================================================================== вкладка «Настройки»

    private TabPage SettingsTab()
    {
        var page = new TabPage("Настройки") { Padding = new Padding(14), AutoScroll = true };
        var col = Column();

        col.Controls.Add(Title("Проверка"));
        col.Controls.Add(Labeled("Проверять каждые, секунд", _interval));
        col.Controls.Add(Labeled("Ждать ответа, секунд", _timeout));
        col.Controls.Add(Labeled("Упал — после стольких сбоев подряд", _threshold,
            "Защита от ложных тревог: одиночный сбой бывает у любого сайта."));
        col.Controls.Add(Labeled("Напоминать, пока лежит, раз в N минут", _reminder, "0 — не напоминать."));
        col.Controls.Add(Labeled("", _notifyRecovery));
        col.Controls.Add(Labeled("Контроль интернета", _controlUrl,
            "Если не открывается и этот адрес — пропал интернет у этого компьютера, а не сайты; тогда тревогу не поднимаем."));
        foreach (var n in new[] { _interval, _timeout, _threshold, _reminder }) n.ValueChanged += (_, _) => FieldChanged();
        _notifyRecovery.CheckedChanged += (_, _) => FieldChanged();
        _controlUrl.TextChanged += (_, _) => FieldChanged();

        col.Controls.Add(Title("Запуск"));
        col.Controls.Add(_autostart);
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { Autostart.Set(_autostart.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось изменить автозапуск:\n" + ex.Message, "SiteWatch");
            }
        };
        var startRow = Row();
        startRow.Controls.Add(Btn("Создать ярлык на рабочем столе", CreateShortcut));
        col.Controls.Add(startRow);

        col.Controls.Add(Title("Файлы"));
        col.Controls.Add(Hint("Настройки хранятся отдельно у каждого пользователя Windows:\n" + AppConfig.FilePath));
        var files = Row();
        files.Controls.Add(Btn("Открыть журнал", () => TrayContext.OpenFolder(Logger.Folder)));
        files.Controls.Add(Btn("Папка настроек", () => TrayContext.OpenFolder(AppConfig.Folder)));
        files.Controls.Add(Btn("Выгрузить настройки…", ExportConfig));
        files.Controls.Add(Btn("Загрузить настройки…", ImportConfig));
        col.Controls.Add(files);
        col.Controls.Add(Hint("Выгрузка — чтобы передать список сайтов, адресов и номеров на другой компьютер. Пароль почты не выгружается."));

        col.Controls.Add(Title("Программа"));
        col.Controls.Add(Hint($"SiteWatch {Application.ProductVersion}"));
        var exitRow = Row();
        exitRow.Controls.Add(Btn("Выйти из программы", () => _exit()));
        col.Controls.Add(exitRow);

        page.Controls.Add(col);
        return page;
    }

    private void CreateShortcut()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, "SiteWatch.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = Environment.ProcessPath;
            lnk.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            lnk.Description = "SiteWatch — мониторинг сайтов";
            lnk.Save();
            MessageBox.Show(this, "Ярлык создан на рабочем столе.", "SiteWatch");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось создать ярлык:\n" + ex.Message, "SiteWatch");
        }
    }

    private void ExportConfig()
    {
        CommitFromFields();
        using var dlg = new SaveFileDialog
        {
            Filter = "Настройки SiteWatch (*.json)|*.json",
            FileName = "sitewatch-settings.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _cfg.ExportTo(dlg.FileName);
            MessageBox.Show(this, "Настройки выгружены.", "SiteWatch");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось выгрузить:\n" + ex.Message, "SiteWatch");
        }
    }

    private void ImportConfig()
    {
        using var dlg = new OpenFileDialog { Filter = "Настройки SiteWatch (*.json)|*.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AppConfig imported;
        try
        {
            imported = AppConfig.ImportFrom(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Файл не подходит:\n" + ex.Message, "SiteWatch");
            return;
        }
        if (MessageBox.Show(this,
                $"В файле: сайтов {imported.Sites.Count}, адресов почты {imported.Email.Recipients.Count}, номеров {imported.Modem.Phones.Count}.\n" +
                "Заменить текущие настройки? Пароль почты останется прежним.",
                "SiteWatch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var keepPassword = _cfg.Email.PasswordProtected;
        _cfg.Sites = imported.Sites;
        _cfg.IntervalSeconds = imported.IntervalSeconds;
        _cfg.TimeoutSeconds = imported.TimeoutSeconds;
        _cfg.FailThreshold = imported.FailThreshold;
        _cfg.ReminderMinutes = imported.ReminderMinutes;
        _cfg.NotifyRecovery = imported.NotifyRecovery;
        _cfg.ControlUrl = imported.ControlUrl;
        _cfg.Email = imported.Email;
        _cfg.Email.PasswordProtected = keepPassword;
        _cfg.Modem = imported.Modem;
        LoadFields();
        Commit(sitesChanged: true);
        RefreshSites();
    }

    // ================================================================== сохранение

    /// <summary>Поля → настройки.</summary>
    private void LoadFields()
    {
        _loading = true;
        try
        {
            var e = _cfg.Email;
            _mailEnabled.Checked = e.Enabled;
            _smtpHost.Text = e.SmtpHost;
            _smtpPort.Value = Math.Clamp(e.SmtpPort, 1, 65535);
            _smtpSsl.Checked = e.UseSsl;
            _smtpUser.Text = e.User;
            _smtpPassword.Text = e.Password;
            var preset = Array.FindIndex(SmtpPresets, p => string.Equals(p.host, e.SmtpHost, StringComparison.OrdinalIgnoreCase));
            _smtpPreset.SelectedIndex = preset >= 0 ? preset : SmtpPresets.Length - 1;
            RefreshMailList();

            var m = _cfg.Modem;
            _modemEnabled.Checked = m.Enabled;
            _modemPort.Text = m.PortName;
            _modemBaud.Text = m.BaudRate.ToString();
            _modemRing.Value = Math.Clamp(m.RingSeconds, 5, 120);
            RefreshPhoneList();

            _interval.Value = _cfg.IntervalSeconds;
            _timeout.Value = _cfg.TimeoutSeconds;
            _threshold.Value = _cfg.FailThreshold;
            _reminder.Value = _cfg.ReminderMinutes;
            _notifyRecovery.Checked = _cfg.NotifyRecovery;
            _controlUrl.Text = _cfg.ControlUrl;
            _autostart.Checked = Autostart.IsEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Поменяли поле — сохраним чуть позже, когда перестанут печатать.</summary>
    private void FieldChanged()
    {
        if (_loading) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void CommitFromFields()
    {
        _saveDebounce.Stop();
        var e = _cfg.Email;
        e.Enabled = _mailEnabled.Checked;
        e.SmtpHost = _smtpHost.Text.Trim();
        e.SmtpPort = (int)_smtpPort.Value;
        e.UseSsl = _smtpSsl.Checked;
        e.User = _smtpUser.Text.Trim();
        if (e.Password != _smtpPassword.Text) e.Password = _smtpPassword.Text;

        var m = _cfg.Modem;
        m.Enabled = _modemEnabled.Checked;
        m.PortName = _modemPort.Text.Trim();
        m.BaudRate = int.TryParse(_modemBaud.Text.Trim(), out var baud) && baud > 0 ? baud : 115200;
        m.RingSeconds = (int)_modemRing.Value;

        var thresholdChanged = _cfg.FailThreshold != (int)_threshold.Value;
        _cfg.IntervalSeconds = (int)_interval.Value;
        _cfg.TimeoutSeconds = (int)_timeout.Value;
        _cfg.FailThreshold = (int)_threshold.Value;
        _cfg.ReminderMinutes = (int)_reminder.Value;
        _cfg.NotifyRecovery = _notifyRecovery.Checked;
        _cfg.ControlUrl = _controlUrl.Text.Trim();

        Commit();
        if (thresholdChanged) RefreshSites();
    }

    /// <summary>Записать настройки на диск и отдать проверке.</summary>
    private void Commit(bool sitesChanged = false)
    {
        try
        {
            _cfg.Normalize();
            _cfg.Save();
            _monitor.ApplyConfig(_cfg, wake: sitesChanged);
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось сохранить настройки", ex);
            MessageBox.Show(this, "Не удалось сохранить настройки:\n" + ex.Message, "SiteWatch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ================================================================== состояние

    /// <summary>Пришли новые результаты проверки.</summary>
    public void OnMonitorChanged()
    {
        if (IsDisposed) return;
        RefreshStatus();
        RefreshSites();
    }

    private void RefreshStatus()
    {
        var state = _monitor.Overall();
        var list = _monitor.Snapshot();
        var last = _monitor.LastRoundAt.HasValue ? $"   ·   проверено в {_monitor.LastRoundAt:HH:mm:ss}" : "";
        var down = list.Where(s => s.IsDown).Select(s => s.Site.Name).ToList();
        _header.Text = state switch
        {
            OverallState.AllOk => $"Все сайты работают ({list.Count}){last}",
            OverallState.Down => $"Не работает: {string.Join(", ", down)}{last}",
            OverallState.Warning => $"Есть сбои — следим, тревогу пока не поднимаем{last}",
            OverallState.NoInternet => "Нет интернета у этого компьютера — сайты сейчас не проверить",
            OverallState.Paused => "Проверка на паузе",
            _ => list.Count == 0 ? "Сайтов пока нет — нажмите «+ Добавить сайт»" : "Идёт первая проверка…",
        };
        _header.BackColor = Icons.ColorFor(state);

    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Крестик — не выход: прячемся в трей, проверка продолжается.
        if (e.CloseReason == CloseReason.UserClosing && !AllowClose)
        {
            e.Cancel = true;
            CommitFromFields();
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                TrayHint?.Invoke();
            }
            return;
        }
        CommitFromFields();
        base.OnFormClosing(e);
    }

    /// <summary>true — закрыть по-настоящему (выход из программы), а не прятать в трей.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Подсказать один раз, что программа ушла в трей.</summary>
    public event Action TrayHint;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ticker.Dispose();
            _saveDebounce.Dispose();
        }
        base.Dispose(disposing);
    }

    // ================================================================== разметка

    private static Alert TestAlert() => new()
    {
        Kind = AlertKind.Test,
        Subject = "SiteWatch: проверка оповещений",
        Body = $"Тестовое сообщение от SiteWatch с компьютера {Environment.MachineName}, {DateTime.Now:dd.MM.yyyy HH:mm:ss}.\n" +
               "Если вы его получили — оповещения работают.",
    };

    private async Task Busy(Button button, Func<Task> action)
    {
        var text = button.Text;
        button.Enabled = false;
        button.Text = "Подождите…";
        UseWaitCursor = true;
        try { await action(); }
        finally
        {
            UseWaitCursor = false;
            button.Text = text;
            button.Enabled = true;
        }
    }

    private static Button Btn(string text, Action onClick, bool bold = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 0, 8, 4),
        };
        if (bold) b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    private static FlowLayoutPanel Column() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
    };

    private static FlowLayoutPanel Row() => new()
    {
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = new Padding(0, 2, 0, 4),
    };

    private static Label Title(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 11f, FontStyle.Bold),
        Margin = new Padding(0, 14, 0, 6),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(760, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>Строка «подпись — поле (— подсказка)».</summary>
    private static Control Labeled(string label, Control control, string hint = null)
    {
        var row = Row();
        row.Controls.Add(new Label
        {
            Text = label,
            Width = 270,
            Margin = new Padding(0, 6, 8, 0),
        });
        if (hint == null)
        {
            row.Controls.Add(control);
            return row;
        }
        var stack = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        stack.Controls.Add(control);
        var h = Hint(hint);
        h.MaximumSize = new Size(460, 0);
        stack.Controls.Add(h);
        row.Controls.Add(stack);
        return row;
    }
}
