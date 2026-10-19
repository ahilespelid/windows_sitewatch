using System.Diagnostics;

namespace SiteWatch;

/// <summary>
/// Жизнь программы: главное окно + значок в трее. Окно можно закрыть —
/// программа остаётся в трее и продолжает проверять. Выход — только из меню.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly MonitorService _monitor;
    private readonly MainForm _main;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _summaryItem;
    private readonly EventWaitHandle _showSignal;
    private readonly Thread _showListener;
    private volatile bool _exiting;

    public TrayContext(bool startHidden, EventWaitHandle showSignal)
    {
        _showSignal = showSignal;

        var cfg = AppConfig.Load();
        _monitor = new MonitorService(cfg);

        _main = new MainForm(_monitor, Exit);
        _main.TrayHint += () => _tray.ShowBalloonTip(6000, "SiteWatch работает",
            "Окно закрыто, но проверка продолжается. Значок здесь, у часов.", ToolTipIcon.Info);
        // Создаём окно сразу, даже если оно скрыто: через него события проверки попадают в поток интерфейса.
        _ = _main.Handle;

        _summaryItem = new ToolStripMenuItem("Запуск…") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => _monitor.Paused = !_monitor.Paused);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_summaryItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Открыть SiteWatch", null, (_, _) => ShowMain()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Проверить сейчас", null, (_, _) => _monitor.CheckNow()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Exit()));
        menu.Opening += (_, _) =>
        {
            _pauseItem.Checked = _monitor.Paused;
            _summaryItem.Text = Summary();
        };

        _tray = new NotifyIcon
        {
            Icon = Icons.For(OverallState.Unknown),
            Text = "SiteWatch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMain();
        };
        _tray.BalloonTipClicked += (_, _) => ShowMain();

        _monitor.Changed += () => OnUi(() =>
        {
            RefreshTray();
            _main.OnMonitorChanged();
        });
        _monitor.Notify += (title, text, problem) => OnUi(() =>
            _tray.ShowBalloonTip(8000, title, string.IsNullOrEmpty(text) ? " " : text,
                problem ? ToolTipIcon.Error : ToolTipIcon.Info));

        // Повторный двойной клик по программе — показываем уже запущенное окно.
        _showListener = new Thread(ListenShowSignal) { IsBackground = true, Name = "SiteWatch.Show" };
        _showListener.Start();

        _monitor.Start();

        if (!startHidden) ShowMain();
    }

    private void ListenShowSignal()
    {
        try
        {
            while (!_exiting)
            {
                if (_showSignal.WaitOne(1000) && !_exiting) OnUi(ShowMain);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnUi(Action action)
    {
        if (_exiting || _main.IsDisposed || !_main.IsHandleCreated) return;
        try { _main.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void ShowMain()
    {
        if (_main.IsDisposed) return;
        if (!_main.Visible) _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        _main.BringToFront();
    }

    private string Summary()
    {
        var list = _monitor.Snapshot();
        var down = list.Count(s => s.IsDown);
        return _monitor.Overall() switch
        {
            OverallState.Paused => "На паузе",
            OverallState.NoInternet => "Нет интернета у этого компьютера",
            OverallState.Unknown when list.Count == 0 => "Сайты не добавлены",
            OverallState.Unknown => "Идёт первая проверка…",
            OverallState.Down => $"Не работает: {down} из {list.Count}",
            OverallState.Warning => $"Есть сбои, следим ({list.Count})",
            _ => $"Всё работает ({list.Count})",
        };
    }

    private void RefreshTray()
    {
        _tray.Icon = Icons.For(_monitor.Overall());
        var text = "SiteWatch — " + Summary();
        var downNames = _monitor.Snapshot().Where(s => s.IsDown).Select(s => s.Site.Name).ToList();
        if (downNames.Count > 0) text += ": " + string.Join(", ", downNames);
        _tray.Text = text.Length > 120 ? text[..117] + "…" : text;
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Не открылась папка " + path, ex);
        }
    }

    private void Exit()
    {
        ShowMain();
        if (MessageBox.Show(_main,
                "Выйти из SiteWatch?\nПока программа закрыта, о падении сайтов никто не узнает.",
                "SiteWatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        _exiting = true;
        _tray.Visible = false;
        _monitor.Dispose();
        _main.AllowClose = true;
        _main.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _tray.Dispose();
            _main.Dispose();
        }
        base.Dispose(disposing);
    }
}
