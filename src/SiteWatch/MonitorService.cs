using System.Text;

namespace SiteWatch;

/// <summary>Что сейчас известно о сайте.</summary>
public sealed class SiteStatus
{
    public SiteConfig Site { get; set; }
    /// <summary>null — ещё не проверяли.</summary>
    public bool? LastOk { get; set; }
    public string LastDetails { get; set; } = "";
    public long LastElapsedMs { get; set; }
    public DateTime? LastCheck { get; set; }
    public int ConsecutiveFails { get; set; }
    /// <summary>Признан упавшим (набралось FailThreshold неудач подряд) и об этом сообщили.</summary>
    public bool IsDown { get; set; }
    public DateTime? DownSince { get; set; }
    public DateTime? LastAlertAt { get; set; }
    /// <summary>Хоть раз открылся с момента добавления (или запуска программы).</summary>
    public bool EverOk { get; set; }
    /// <summary>
    /// Адрес не найден, и сайт ни разу не открывался — похоже на опечатку или
    /// несуществующий домен. Ошибку показываем, тревогу не поднимаем.
    /// </summary>
    public bool Muted { get; set; }

    public SiteStatus Copy() => (SiteStatus)MemberwiseClone();
}

public enum OverallState
{
    Unknown,
    AllOk,
    /// <summary>Есть сбои, но порог ещё не набран.</summary>
    Warning,
    Down,
    /// <summary>У этого компьютера нет интернета — о сайтах ничего не известно.</summary>
    NoInternet,
    Paused,
}

/// <summary>Фоновая проверка сайтов по кругу и решение, когда слать оповещение.</summary>
public sealed class MonitorService : IDisposable
{
    private readonly object _sync = new();
    private readonly AlertDispatcher _alerts = new();
    private readonly CancellationTokenSource _stop = new();
    private Dictionary<string, SiteStatus> _states = new();
    private AppConfig _cfg;
    private CancellationTokenSource _wake = new();
    private Task _loop;
    private bool _paused;
    private bool _internetOk = true;
    private DateTime? _internetLostAt;

    /// <summary>Что-то поменялось — обновить окно и значок. Вызывается из фонового потока.</summary>
    public event Action Changed;

    /// <summary>Всплывающее уведомление у часов: (заголовок, текст, это проблема).</summary>
    public event Action<string, string, bool> Notify;

    public MonitorService(AppConfig cfg)
    {
        ApplyConfig(cfg, wake: false);
    }

    public DateTime? LastRoundAt { get; private set; }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Logger.Info(value ? "Проверка на паузе" : "Проверка возобновлена");
            Changed?.Invoke();
            if (!value) CheckNow();
        }
    }

    public bool InternetOk => _internetOk;

    public void Start()
    {
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Новые настройки: состояние сайтов, которые остались, сохраняем.</summary>
    public void ApplyConfig(AppConfig cfg, bool wake = true)
    {
        lock (_sync)
        {
            _cfg = cfg.Clone();
            var next = new Dictionary<string, SiteStatus>();
            foreach (var site in _cfg.Sites.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Address)))
            {
                if (next.ContainsKey(site.Key)) continue;
                if (_states.TryGetValue(site.Key, out var old))
                {
                    old.Site = site.Clone();
                    next[site.Key] = old;
                }
                else
                {
                    next[site.Key] = new SiteStatus { Site = site.Clone() };
                }
            }
            _states = next;
        }
        Changed?.Invoke();
        if (wake) CheckNow();
    }

    public AppConfig CurrentConfig()
    {
        lock (_sync) return _cfg.Clone();
    }

    public List<SiteStatus> Snapshot()
    {
        lock (_sync) return _states.Values.Select(s => s.Copy()).ToList();
    }

    public OverallState Overall()
    {
        if (_paused) return OverallState.Paused;
        if (!_internetOk) return OverallState.NoInternet;
        var list = Snapshot();
        if (list.Count == 0 || list.All(s => s.LastOk == null)) return OverallState.Unknown;
        if (list.Any(s => s.IsDown)) return OverallState.Down;
        if (list.Any(s => s.LastOk == false && !s.Muted)) return OverallState.Warning;
        return OverallState.AllOk;
    }

    /// <summary>Проверить прямо сейчас, не дожидаясь интервала.</summary>
    public void CheckNow()
    {
        lock (_sync)
        {
            try { _wake.Cancel(); } catch { }
        }
    }

    public Task<List<string>> SendTestAsync(AppConfig cfg, CancellationToken ct = default)
    {
        var alert = new Alert
        {
            Kind = AlertKind.Test,
            Subject = "SiteWatch: проверка оповещений",
            Body = $"Это тестовое сообщение от SiteWatch с компьютера {Environment.MachineName}.\n" +
                   $"Время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}\n\nЕсли вы его получили — оповещения работают.",
        };
        return _alerts.SendAsync(cfg, alert, ct);
    }

    // ------------------------------------------------------------------

    private async Task LoopAsync()
    {
        Logger.Info("Проверка запущена");
        while (!_stop.IsCancellationRequested)
        {
            if (!_paused)
            {
                try
                {
                    await RoundAsync(_stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("Ошибка в круге проверки", ex);
                }
            }

            int interval;
            CancellationTokenSource wake;
            var wokenDuringRound = false;
            lock (_sync)
            {
                interval = _cfg.IntervalSeconds;
                if (_wake.IsCancellationRequested)
                {
                    // «Проверить сейчас» нажали, пока шла проверка, — сразу новый круг.
                    _wake.Dispose();
                    _wake = new CancellationTokenSource();
                    wokenDuringRound = true;
                }
                wake = _wake;
            }
            if (wokenDuringRound) continue;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, wake.Token);
                await Task.Delay(TimeSpan.FromSeconds(interval), linked.Token);
            }
            catch (OperationCanceledException)
            {
                // разбудили «Проверить сейчас» или выходим
            }
            lock (_sync)
            {
                if (_wake.IsCancellationRequested)
                {
                    _wake.Dispose();
                    _wake = new CancellationTokenSource();
                }
            }
        }
        Logger.Info("Проверка остановлена");
    }

    private async Task RoundAsync(CancellationToken ct)
    {
        AppConfig cfg;
        List<SiteStatus> targets;
        lock (_sync)
        {
            cfg = _cfg;
            targets = _states.Values.ToList();
        }
        if (targets.Count == 0)
        {
            LastRoundAt = DateTime.Now;
            Changed?.Invoke();
            return;
        }

        // Все сайты параллельно, но не больше 8 одновременно.
        using var gate = new SemaphoreSlim(8);
        var tasks = targets.Select(async st =>
        {
            await gate.WaitAsync(ct);
            try { return (st, await SiteChecker.CheckAsync(st.Site, cfg.TimeoutSeconds, ct)); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(tasks);

        // Что-то не открылось — сначала убеждаемся, что интернет у нас самих есть.
        // Иначе при отключении интернета на этом компьютере «упали» бы все сайты разом.
        if (results.Any(r => !r.Item2.Ok))
        {
            var online = await IsInternetOkAsync(cfg, ct);
            if (!online)
            {
                if (_internetOk)
                {
                    _internetOk = false;
                    _internetLostAt = DateTime.Now;
                    Logger.Warn("Нет интернета на этом компьютере — состояние сайтов неизвестно");
                    Notify?.Invoke("Нет интернета", "У этого компьютера пропал интернет. Сайты сейчас проверить нельзя.", true);
                }
                LastRoundAt = DateTime.Now;
                Changed?.Invoke();
                return;
            }
        }
        if (!_internetOk)
        {
            _internetOk = true;
            var lost = _internetLostAt.HasValue ? $" (не было {FormatDuration(DateTime.Now - _internetLostAt.Value)})" : "";
            Logger.Info("Интернет снова есть" + lost);
            Notify?.Invoke("Интернет снова есть", "Проверка сайтов продолжается" + lost, false);
            _internetLostAt = null;
        }

        var downAlerts = new List<SiteStatus>();
        var reminderAlerts = new List<SiteStatus>();
        var recovered = new List<(SiteStatus st, TimeSpan downFor)>();
        var now = DateTime.Now;

        lock (_sync)
        {
            foreach (var (st, res) in results)
            {
                // Сайт могли удалить из настроек, пока шла проверка.
                if (!_states.TryGetValue(st.Site.Key, out var live) || !ReferenceEquals(live, st)) continue;

                st.LastOk = res.Ok;
                st.LastDetails = res.Details;
                st.LastElapsedMs = res.ElapsedMs;
                st.LastCheck = now;

                if (res.Ok)
                {
                    st.EverOk = true;
                    st.Muted = false;
                    if (st.IsDown)
                    {
                        recovered.Add((st, now - (st.DownSince ?? now)));
                        Logger.Info($"РАБОТАЕТ: {st.Site.Name} ({st.Site.Address}) — {res.Details}");
                    }
                    st.ConsecutiveFails = 0;
                    st.IsDown = false;
                    st.DownSince = null;
                    st.LastAlertAt = null;
                    continue;
                }

                // Адрес не найден, а сайт ни разу не открывался — это опечатка или
                // несуществующий домен, а не падение. Показываем ошибку, не тревожим.
                // Если домен работал и пропал (например, истёк) — это уже тревога.
                if (res.HostNotFound && !st.EverOk)
                {
                    st.Muted = true;
                    st.ConsecutiveFails = 0;
                    st.IsDown = false;
                    st.DownSince = null;
                    continue;
                }
                st.Muted = false;

                st.ConsecutiveFails++;
                if (st.ConsecutiveFails == 1)
                    st.DownSince = now;
                Logger.Warn($"Сбой {st.ConsecutiveFails}/{cfg.FailThreshold}: {st.Site.Name} ({st.Site.Address}) — {res.Details}");

                if (!st.IsDown && st.ConsecutiveFails >= cfg.FailThreshold)
                {
                    st.IsDown = true;
                    st.LastAlertAt = now;
                    downAlerts.Add(st.Copy());
                }
                else if (st.IsDown && cfg.ReminderMinutes > 0 && st.LastAlertAt.HasValue &&
                         now - st.LastAlertAt.Value >= TimeSpan.FromMinutes(cfg.ReminderMinutes))
                {
                    st.LastAlertAt = now;
                    reminderAlerts.Add(st.Copy());
                }
            }
            LastRoundAt = now;
        }
        Changed?.Invoke();

        // Несколько сайтов упали в одном круге — одно письмо, не пачка.
        if (downAlerts.Count > 0)
        {
            var names = string.Join(", ", downAlerts.Select(s => s.Site.Name));
            Notify?.Invoke("Сайт не работает", names, true);
            await SendAsync(cfg, new Alert
            {
                Kind = AlertKind.Down,
                Subject = downAlerts.Count == 1
                    ? $"НЕ РАБОТАЕТ: {downAlerts[0].Site.Name}"
                    : $"НЕ РАБОТАЮТ ({downAlerts.Count}): {names}",
                Body = BuildBody("Перестали отвечать:", downAlerts, cfg, now),
            }, ct);
        }

        if (reminderAlerts.Count > 0)
        {
            var names = string.Join(", ", reminderAlerts.Select(s => s.Site.Name));
            await SendAsync(cfg, new Alert
            {
                Kind = AlertKind.StillDown,
                Subject = $"ВСЁ ЕЩЁ НЕ РАБОТАЕТ: {names}",
                Body = BuildBody("По-прежнему не отвечают:", reminderAlerts, cfg, now),
            }, ct);
        }

        if (recovered.Count > 0)
        {
            var names = string.Join(", ", recovered.Select(r => r.st.Site.Name));
            Notify?.Invoke("Сайт снова работает", names, false);
            if (cfg.NotifyRecovery)
            {
                var sb = new StringBuilder("Снова работают:\n\n");
                foreach (var (st, downFor) in recovered)
                    sb.AppendLine($"• {st.Site.Name} — {st.Site.Address}\n  не работал {FormatDuration(downFor)}, сейчас: {st.LastDetails}");
                sb.AppendLine().AppendLine(Footer(now));
                await SendAsync(cfg, new Alert
                {
                    Kind = AlertKind.Recovered,
                    Subject = $"Снова работает: {names}",
                    Body = sb.ToString(),
                }, ct);
            }
        }
    }

    private async Task SendAsync(AppConfig cfg, Alert alert, CancellationToken ct)
    {
        var errors = await _alerts.SendAsync(cfg, alert, ct);
        if (errors.Count > 0)
            Notify?.Invoke("Оповещение не отправлено", string.Join("\n", errors), true);
    }

    private static async Task<bool> IsInternetOkAsync(AppConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ControlUrl)) return true;
        var control = new SiteConfig { Name = "контроль", Type = CheckType.Http, Address = cfg.ControlUrl };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var r = await SiteChecker.CheckAsync(control, cfg.TimeoutSeconds, ct);
            if (r.Ok) return true;
        }
        return false;
    }

    private static string BuildBody(string title, List<SiteStatus> list, AppConfig cfg, DateTime now)
    {
        var sb = new StringBuilder(title).AppendLine().AppendLine();
        foreach (var st in list)
        {
            var since = st.DownSince.HasValue ? $"сбои с {st.DownSince:HH:mm:ss} ({FormatDuration(now - st.DownSince.Value)})" : "";
            sb.AppendLine($"• {st.Site.Name} — {st.Site.Address}");
            sb.AppendLine($"  ошибка: {st.LastDetails}");
            sb.AppendLine($"  {since}, неудачных проверок подряд: {st.ConsecutiveFails}");
        }
        sb.AppendLine();
        if (cfg.ReminderMinutes > 0)
            sb.AppendLine($"Пока не заработает, напомню через {cfg.ReminderMinutes} мин.");
        sb.AppendLine(Footer(now));
        return sb.ToString();
    }

    private static string Footer(DateTime now) =>
        $"— SiteWatch, компьютер {Environment.MachineName}, {now:dd.MM.yyyy HH:mm:ss}";

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} с";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} мин";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours} ч {t.Minutes} мин";
        return $"{(int)t.TotalDays} д {t.Hours} ч";
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(3000); } catch { }
        _stop.Dispose();
    }
}
