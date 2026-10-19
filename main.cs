// SiteWatch — паук-звонарь: следит за нашими сайтами, при падении пишет на почту
// (и, когда подключим модем, звонит). Один файл, C# 5 / .NET Framework 4.x —
// собирается встроенным в Windows компилятором, см. build.cmd.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("SiteWatch")]
[assembly: AssemblyProduct("SiteWatch")]
[assembly: AssemblyVersion("0.4.0.0")]

namespace SiteWatch
{
    // ===================================================================== запуск

    internal static class Program
    {
        public const string Version = "0.4.0";
        public const string TrayArg = "--tray";

        // Константы проверки — без лишних настроек.
        public const int IntervalSeconds = 60;   // как часто проверять
        public const int TimeoutSeconds = 15;    // сколько ждать ответа
        public const int FailThreshold = 3;      // упал = столько неудач подряд
        public const int ReminderMinutes = 60;   // напоминать, пока лежит
        public const string ControlUrl = "https://ya.ru/"; // есть ли интернет у нас самих

        [STAThread]
        private static void Main(string[] args)
        {
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SiteWatch.Show"))
            {
                bool createdNew;
                using (var mutex = new Mutex(true, @"Local\SiteWatch.Single", out createdNew))
                {
                    if (!createdNew)
                    {
                        showSignal.Set(); // уже запущена — показать её окно
                        return;
                    }
                    try { Native.SetProcessDPIAware(); }
                    catch { }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.ThreadException += (s, e) => Log.Write("ERROR " + e.Exception);
                    TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("ERROR " + e.Exception); e.SetObserved(); };

                    var hidden = args.Any(a => a == TrayArg);
                    Log.Write("Запуск " + Version + (hidden ? " (автозапуск)" : ""));
                    Application.Run(new TrayApp(hidden, showSignal));
                    Log.Write("Выход");
                    GC.KeepAlive(mutex);
                }
            }
        }
    }

    // ===================================================================== Win32

    internal static class Native
    {
        // kernel32: «я работаю, не засыпай» — действует, пока жив поток интерфейса.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint flags);
        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
        }

        /// <summary>Автозапуск вместе с Windows (HKCU\...\Run, без прав администратора).</summary>
        public static void Autostart(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (on) key.SetValue("SiteWatch", "\"" + Application.ExecutablePath + "\" " + Program.TrayArg);
                else if (key.GetValue("SiteWatch") != null) key.DeleteValue("SiteWatch", false);
            }
        }

        public static bool AutostartOn()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                var v = key == null ? null : key.GetValue("SiteWatch") as string;
                return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    // ===================================================================== журнал

    internal static class Log
    {
        private static readonly object Sync = new object();
        public static string Folder { get { return Config.Folder; } }

        public static void Write(string text)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Folder);
                    var file = Path.Combine(Folder, "sitewatch.log");
                    // Журнал не растёт бесконечно: больше 2 МБ — начинаем заново.
                    if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024) File.Delete(file);
                    File.AppendAllText(file, DateTime.Now.ToString("dd.MM HH:mm:ss") + " " + text + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ===================================================================== настройки

    /// <summary>
    /// Настройки в %AppData%\SiteWatch\config.txt, простым текстом по секциям.
    /// Пароль почты зашифрован Windows (DPAPI) — читается только этим пользователем на этом ПК.
    /// </summary>
    internal sealed class Config
    {
        public List<string> Sites = new List<string>
        {
            "https://litewms.ru/",
            "https://lite-box.ru/",
            "https://scanmaster.tech/",
            "https://limaster.ru/",
            "https://support.litewms.ru/",
            "https://ahilespelid.ru/",
        };
        public List<string> MailTo = new List<string> { "dvrh.76@gmail.com" };
        public List<string> Phones = new List<string>();
        public string SmtpHost = "smtp.yandex.ru";
        public int SmtpPort = 587;
        public string SmtpUser = "goog.a@yandex.ru";
        public string SmtpPassword = "";
        public string ComPort = "";
        public bool KeepAwake = true;

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SiteWatch"); }
        }

        private static string FilePath { get { return Path.Combine(Folder, "config.txt"); } }

        public static Config Load()
        {
            var cfg = new Config();
            if (!File.Exists(FilePath)) return cfg;
            try
            {
                var section = "";
                var sites = new List<string>(); var mail = new List<string>(); var phones = new List<string>();
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Trim('[', ']').ToLowerInvariant(); continue; }
                    if (section == "sites") sites.Add(line);
                    else if (section == "mail") mail.Add(line);
                    else if (section == "phones") phones.Add(line);
                    else if (section == "options")
                    {
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        var v = line.Substring(eq + 1).Trim();
                        if (k == "smtp_host") cfg.SmtpHost = v;
                        else if (k == "smtp_port") int.TryParse(v, out cfg.SmtpPort);
                        else if (k == "smtp_user") cfg.SmtpUser = v;
                        else if (k == "smtp_password") cfg.SmtpPassword = Unprotect(v);
                        else if (k == "com_port") cfg.ComPort = v;
                        else if (k == "keep_awake") cfg.KeepAwake = v != "0";
                    }
                }
                cfg.Sites = sites; cfg.MailTo = mail; cfg.Phones = phones;
            }
            catch (Exception ex)
            {
                Log.Write("ERROR настройки не прочитаны: " + ex.Message);
            }
            return cfg;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            var sb = new StringBuilder();
            sb.AppendLine("# SiteWatch. Сайты, почта и телефоны — по одному в строке.");
            sb.AppendLine("[sites]"); foreach (var s in Sites) sb.AppendLine(s);
            sb.AppendLine("[mail]"); foreach (var s in MailTo) sb.AppendLine(s);
            sb.AppendLine("[phones]"); foreach (var s in Phones) sb.AppendLine(s);
            sb.AppendLine("[options]");
            sb.AppendLine("smtp_host=" + SmtpHost);
            sb.AppendLine("smtp_port=" + SmtpPort);
            sb.AppendLine("smtp_user=" + SmtpUser);
            sb.AppendLine("smtp_password=" + Protect(SmtpPassword));
            sb.AppendLine("com_port=" + ComPort);
            sb.AppendLine("keep_awake=" + (KeepAwake ? "1" : "0"));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SiteWatch");

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
        }

        private static string Unprotect(string value)
        {
            try { return string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
    }

    // ===================================================================== проверка

    internal sealed class SiteState
    {
        public string Url;
        public bool? Ok;            // null — ещё не проверяли
        public string Details = "";
        public long Ms;
        public DateTime? LastCheck;
        public int Fails;           // неудач подряд
        public bool IsDown;         // признан упавшим, письмо отправлено
        public DateTime? DownSince;
        public DateTime? LastAlert;
        public bool EverOk;         // хоть раз открылся
        public bool Muted;          // домен не найден и ни разу не открывался — опечатка, не тревожим

        public string Name
        {
            get
            {
                Uri u;
                return Uri.TryCreate(Url, UriKind.Absolute, out u) ? u.Host : Url;
            }
        }

        public SiteState Copy() { return (SiteState)MemberwiseClone(); }
    }

    internal static class Checker
    {
        private static readonly HttpClient Http;

        static Checker()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } // TLS 1.2
            catch { }
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
            Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Program.TimeoutSeconds) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SiteWatch/" + Program.Version);
        }

        /// <summary>Открыть страницу. Работает = ответ 200–399 или 401/403 (просит войти — значит жив).</summary>
        public static async Task<SiteState> CheckAsync(string url)
        {
            var r = new SiteState { Url = url };
            var sw = Stopwatch.StartNew();
            try
            {
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    var code = (int)resp.StatusCode;
                    r.Ok = (code >= 200 && code < 400) || code == 401 || code == 403;
                    r.Details = code + " " + resp.ReasonPhrase;
                }
            }
            catch (TaskCanceledException)
            {
                r.Ok = false;
                r.Details = "нет ответа за " + Program.TimeoutSeconds + " с";
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Details = Describe(ex, out r.Muted);
            }
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        /// <summary>Понятный текст ошибки. dns = домен не найден.</summary>
        public static string Describe(Exception ex, out bool dns)
        {
            dns = false;
            for (var e = ex; e != null; e = e.InnerException)
            {
                var we = e as WebException;
                if (we != null)
                {
                    if (we.Status == WebExceptionStatus.NameResolutionFailure) { dns = true; return "домен не найден (DNS)"; }
                    if (we.Status == WebExceptionStatus.ConnectFailure) return "не удалось подключиться";
                    if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure) return "ошибка SSL-сертификата";
                }
                var se = e as System.Net.Sockets.SocketException;
                if (se != null)
                {
                    if (se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound) { dns = true; return "домен не найден (DNS)"; }
                    if (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) return "соединение отклонено";
                    return "сеть: " + se.SocketErrorCode;
                }
                if (e.InnerException == null) return e.Message;
            }
            return ex.Message;
        }
    }

    /// <summary>Круг проверки раз в минуту и решение, когда тревожить.</summary>
    internal sealed class Watcher
    {
        private readonly object _sync = new object();
        private Dictionary<string, SiteState> _states = new Dictionary<string, SiteState>();
        private Config _cfg;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _stop;

        public bool InternetOk = true;
        public DateTime? LastRound;

        public event Action Changed;
        public event Action<string, bool> Notify; // текст, это проблема

        public Watcher(Config cfg) { Apply(cfg); }

        public void Apply(Config cfg)
        {
            lock (_sync)
            {
                _cfg = cfg;
                var next = new Dictionary<string, SiteState>();
                foreach (var url in cfg.Sites)
                {
                    SiteState old;
                    if (next.ContainsKey(url)) continue;
                    next[url] = _states.TryGetValue(url, out old) ? old : new SiteState { Url = url };
                }
                _states = next;
            }
            Raise();
        }

        public List<SiteState> Snapshot()
        {
            lock (_sync) return _states.Values.Select(s => s.Copy()).ToList();
        }

        public void CheckNow() { _wake.Set(); }

        public void Start()
        {
            var t = new Thread(() =>
            {
                while (!_stop)
                {
                    try { Round().Wait(); }
                    catch (Exception ex) { Log.Write("ERROR круг проверки: " + ex); }
                    _wake.WaitOne(TimeSpan.FromSeconds(Program.IntervalSeconds));
                }
            }) { IsBackground = true, Name = "SiteWatch.Watcher" };
            t.Start();
        }

        public void Stop() { _stop = true; _wake.Set(); }

        private void Raise() { var h = Changed; if (h != null) h(); }
        private void Say(string text, bool problem) { var h = Notify; if (h != null) h(text, problem); }

        private async Task Round()
        {
            List<string> urls; Config cfg;
            lock (_sync) { urls = _states.Keys.ToList(); cfg = _cfg; }

            var results = await Task.WhenAll(urls.Select(u => Checker.CheckAsync(u)));

            // Не открылось ничего или что-то — сначала проверяем свой интернет.
            if (results.Any(r => r.Ok != true))
            {
                var control = await Checker.CheckAsync(Program.ControlUrl);
                if (control.Ok != true)
                {
                    if (InternetOk) { InternetOk = false; Log.Write("Нет интернета у этого компьютера"); Say("Нет интернета у этого компьютера — сайты проверить нельзя", true); }
                    LastRound = DateTime.Now; Raise();
                    return;
                }
            }
            if (!InternetOk) { InternetOk = true; Log.Write("Интернет снова есть"); Say("Интернет снова есть", false); }

            var now = DateTime.Now;
            var down = new List<SiteState>(); var still = new List<SiteState>(); var up = new List<SiteState>();
            lock (_sync)
            {
                foreach (var r in results)
                {
                    SiteState st;
                    if (!_states.TryGetValue(r.Url, out st)) continue;
                    st.Ok = r.Ok; st.Details = r.Details; st.Ms = r.Ms; st.LastCheck = now;

                    if (r.Ok == true)
                    {
                        if (st.IsDown) { up.Add(st.Copy()); Log.Write("РАБОТАЕТ " + st.Url); }
                        st.EverOk = true; st.Muted = false; st.Fails = 0; st.IsDown = false; st.DownSince = null; st.LastAlert = null;
                        continue;
                    }
                    if (r.Muted && !st.EverOk) { st.Muted = true; st.Fails = 0; continue; } // опечатка в адресе
                    st.Muted = false;
                    st.Fails++;
                    if (st.Fails == 1) st.DownSince = now;
                    Log.Write("Сбой " + st.Fails + "/" + Program.FailThreshold + " " + st.Url + " — " + r.Details);

                    if (!st.IsDown && st.Fails >= Program.FailThreshold) { st.IsDown = true; st.LastAlert = now; down.Add(st.Copy()); }
                    else if (st.IsDown && st.LastAlert.HasValue && now - st.LastAlert.Value >= TimeSpan.FromMinutes(Program.ReminderMinutes))
                    { st.LastAlert = now; still.Add(st.Copy()); }
                }
                LastRound = now;
            }
            Raise();

            if (down.Count > 0)
            {
                Say("Не работает: " + Names(down), true);
                await Alert(cfg, "НЕ РАБОТАЕТ: " + Names(down), Body("Перестали отвечать:", down, now), true);
            }
            if (still.Count > 0)
                await Alert(cfg, "ВСЁ ЕЩЁ НЕ РАБОТАЕТ: " + Names(still), Body("По-прежнему не отвечают:", still, now), true);
            if (up.Count > 0)
            {
                Say("Снова работает: " + Names(up), false);
                await Alert(cfg, "Снова работает: " + Names(up), Body("Снова работают:", up, now), false);
            }
        }

        private static string Names(List<SiteState> list) { return string.Join(", ", list.Select(s => s.Name)); }

        private static string Body(string title, List<SiteState> list, DateTime now)
        {
            var sb = new StringBuilder(title + "\n\n");
            foreach (var s in list)
            {
                sb.AppendLine("• " + s.Url);
                sb.AppendLine("  " + s.Details + (s.DownSince.HasValue ? ", сбои с " + s.DownSince.Value.ToString("HH:mm") + " (" + Ago(now - s.DownSince.Value) + ")" : ""));
            }
            sb.AppendLine().AppendLine("— SiteWatch, " + Environment.MachineName + ", " + now.ToString("dd.MM.yyyy HH:mm:ss"));
            return sb.ToString();
        }

        public static string Ago(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + " с";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " мин";
            if (t.TotalDays < 1) return (int)t.TotalHours + " ч " + t.Minutes + " мин";
            return (int)t.TotalDays + " д " + t.Hours + " ч";
        }

        private async Task Alert(Config cfg, string subject, string body, bool urgent)
        {
            try { await Alerts.MailAsync(cfg, subject, body); Log.Write("Письмо: " + subject); }
            catch (Exception ex) { Log.Write("ERROR письмо не ушло: " + ex.Message); Say("Письмо не отправлено: " + ex.Message, true); }

            if (urgent && cfg.Phones.Count > 0 && !string.IsNullOrEmpty(cfg.ComPort))
            {
                try { await Task.Run(() => Alerts.Call(cfg, cfg.Phones)); }
                catch (Exception ex) { Log.Write("ERROR звонок не прошёл: " + ex.Message); Say("Звонок не прошёл: " + ex.Message, true); }
            }
        }
    }

    // ===================================================================== оповещения

    internal static class Alerts
    {
        /// <summary>Письмо через SMTP с STARTTLS (порт 587; 465 SmtpClient не умеет).</summary>
        public static async Task MailAsync(Config cfg, string subject, string body)
        {
            if (cfg.MailTo.Count == 0) throw new InvalidOperationException("не указано, кому писать");
            if (string.IsNullOrEmpty(cfg.SmtpUser) || string.IsNullOrEmpty(cfg.SmtpPassword))
                throw new InvalidOperationException("не указан логин или пароль почты");
            using (var msg = new MailMessage())
            using (var smtp = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort))
            {
                msg.From = new MailAddress(cfg.SmtpUser, "SiteWatch");
                foreach (var to in cfg.MailTo) msg.To.Add(to);
                msg.Subject = subject;
                msg.Body = body;
                msg.SubjectEncoding = msg.BodyEncoding = Encoding.UTF8;
                smtp.EnableSsl = true;
                smtp.Timeout = 30000;
                smtp.Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword);
                await smtp.SendMailAsync(msg);
            }
        }

        /// <summary>
        /// ЗАГОТОВКА. Звонок через GSM-модем (AT-команды по COM-порту): ATD номер; — вызов,
        /// через 30 с ATH — сброс. Трубку брать не нужно, звонок и есть сигнал.
        /// На живом модеме не проверялось; модем нужен с поддержкой голосовых вызовов.
        /// </summary>
        public static void Call(Config cfg, List<string> phones)
        {
            lock (typeof(Alerts)) // модем один — по очереди
            using (var port = new SerialPort(cfg.ComPort, 115200) { NewLine = "\r", ReadTimeout = 3000, WriteTimeout = 3000, DtrEnable = true, RtsEnable = true })
            {
                port.Open();
                At(port, "AT", true);
                At(port, "ATE0", false);
                foreach (var phone in phones)
                {
                    Log.Write("Звоню " + phone);
                    try { At(port, "ATD" + phone + ";", true); Thread.Sleep(30000); }
                    finally { try { At(port, "ATH", false); } catch { } Thread.Sleep(2000); }
                }
            }
        }

        private static string At(SerialPort port, string cmd, bool mustOk)
        {
            port.DiscardInBuffer();
            port.WriteLine(cmd);
            var answer = new StringBuilder();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                string line;
                try { line = port.ReadLine().Trim(); }
                catch (TimeoutException) { break; }
                if (line.Length == 0 || line == cmd) continue;
                if (line == "OK") return answer.ToString().Trim();
                answer.Append(line).Append(' ');
                if (line.StartsWith("ERROR") || line.StartsWith("+CME") || line == "NO CARRIER" || line == "BUSY") break;
            }
            if (mustOk) throw new InvalidOperationException("модем не ответил OK на " + cmd + " (" + answer.ToString().Trim() + ")");
            return answer.ToString().Trim();
        }
    }

    // ===================================================================== значки

    internal static class Icons
    {
        public static readonly Icon App = new Icon(new MemoryStream(Convert.FromBase64String(AppIconBase64)));
        private static readonly Dictionary<Color, Icon> Cache = new Dictionary<Color, Icon>();

        public static readonly Color Green = Color.FromArgb(34, 160, 80);
        public static readonly Color Yellow = Color.FromArgb(230, 160, 20);
        public static readonly Color Red = Color.FromArgb(215, 40, 40);
        public static readonly Color Violet = Color.FromArgb(120, 90, 200);
        public static readonly Color Blue = Color.FromArgb(90, 140, 210);

        /// <summary>Кружок цвета состояния для трея.</summary>
        public static Icon Dot(Color c)
        {
            lock (Cache)
            {
                Icon icon;
                if (Cache.TryGetValue(c, out icon)) return icon;
                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var b = new SolidBrush(c))
                    using (var p = new Pen(Color.White, 2f))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.FillEllipse(b, 2, 2, 28, 28);
                        g.DrawEllipse(p, 2, 2, 28, 28);
                    }
                    icon = Icon.FromHandle(bmp.GetHicon()); // пять значков на всю жизнь программы
                }
                Cache[c] = icon;
                return icon;
            }
        }

        // Значок программы (ICO 16/32/48) в Base64.
        private const string AppIconBase64 = "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";
    }

    // ===================================================================== трей

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Watcher _watcher;
        private readonly MainForm _form;
        private readonly NotifyIcon _tray;
        private volatile bool _exiting;

        public TrayApp(bool hidden, EventWaitHandle showSignal)
        {
            var cfg = Config.Load();
            _watcher = new Watcher(cfg);
            _form = new MainForm(_watcher, cfg, Exit);
            var handle = _form.Handle; // окно нужно сразу: через него события попадают в поток интерфейса

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => ShowForm());
            menu.Items.Add("Проверить сейчас", null, (s, e) => _watcher.CheckNow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => Exit());

            _tray = new NotifyIcon { Icon = Icons.Dot(Icons.Blue), Text = "SiteWatch", ContextMenuStrip = menu, Visible = true };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };
            _tray.BalloonTipClicked += (s, e) => ShowForm();

            _watcher.Changed += () => Ui(() => { UpdateTray(); _form.RefreshView(); });
            _watcher.Notify += (text, problem) => Ui(() => _tray.ShowBalloonTip(8000, "SiteWatch", text, problem ? ToolTipIcon.Error : ToolTipIcon.Info));

            new Thread(() =>
            {
                while (!_exiting) if (showSignal.WaitOne(1000) && !_exiting) Ui(ShowForm);
            }) { IsBackground = true }.Start();

            Native.KeepAwake(cfg.KeepAwake);
            _watcher.Start();
            if (!hidden) ShowForm();
        }

        private void Ui(Action a)
        {
            if (_exiting || _form.IsDisposed || !_form.IsHandleCreated) return;
            try { _form.BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        private void ShowForm()
        {
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        private void UpdateTray()
        {
            var list = _watcher.Snapshot();
            Color c; string text;
            if (!_watcher.InternetOk) { c = Icons.Violet; text = "нет интернета"; }
            else if (list.Any(s => s.IsDown)) { c = Icons.Red; text = "не работает: " + list.Count(s => s.IsDown); }
            else if (list.Any(s => s.Ok == false && !s.Muted)) { c = Icons.Yellow; text = "есть сбои"; }
            else if (list.Any(s => s.Ok == true)) { c = Icons.Green; text = "всё работает (" + list.Count + ")"; }
            else { c = Icons.Blue; text = "проверка…"; }
            _tray.Icon = Icons.Dot(c);
            _tray.Text = "SiteWatch: " + text; // в .NET Framework не длиннее 63 символов
        }

        private void Exit()
        {
            ShowForm();
            if (MessageBox.Show(_form, "Выйти? Пока программа закрыта, о падении сайтов никто не узнает.", "SiteWatch",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _exiting = true;
            _watcher.Stop();
            Native.KeepAwake(false);
            _tray.Visible = false;
            _form.AllowClose = true;
            _form.Close();
            ExitThread();
        }
    }

    // ===================================================================== окно

    internal sealed class MainForm : Form
    {
        private readonly Watcher _watcher;
        private Config _cfg;
        private readonly Action _exit;
        private readonly Label _header = new Label { Dock = DockStyle.Top, Height = 42, ForeColor = Color.White, Padding = new Padding(12, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        private readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        private readonly TextBox _sites = Multi();
        private readonly TextBox _mail = Multi();
        private readonly TextBox _phones = Multi();
        private readonly TextBox _user = new TextBox { Width = 260 };
        private readonly TextBox _password = new TextBox { Width = 260, UseSystemPasswordChar = true };
        private readonly ComboBox _port = new ComboBox { Width = 120 };
        private readonly CheckBox _awake = new CheckBox { Text = "Не давать компьютеру засыпать", AutoSize = true };
        private readonly CheckBox _autostart = new CheckBox { Text = "Запускать вместе с Windows", AutoSize = true };
        private readonly System.Windows.Forms.Timer _ticker = new System.Windows.Forms.Timer { Interval = 5000 };

        public bool AllowClose;

        public MainForm(Watcher watcher, Config cfg, Action exit)
        {
            _watcher = watcher; _cfg = cfg; _exit = exit;
            Text = "SiteWatch " + Program.Version;
            Icon = Icons.App;
            Size = new Size(900, 560);
            MinimumSize = new Size(700, 450);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            _header.Font = new Font("Segoe UI", 12f, FontStyle.Bold);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(StatusTab());
            tabs.TabPages.Add(SettingsTab());
            Controls.Add(tabs);
            Controls.Add(_header);

            _ticker.Tick += (s, e) => { if (Visible) RefreshView(); };
            _ticker.Start();
            LoadFields();
            RefreshView();
        }

        private static TextBox Multi()
        {
            return new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 380, Height = 90, AcceptsReturn = true };
        }

        private TabPage StatusTab()
        {
            var page = new TabPage("Состояние") { Padding = new Padding(8) };
            foreach (var c in new[] { "Сайт|170", "Состояние|130", "Ответ|230", "мс|60", "Проверен|100", "Сбои с|90" })
            {
                var p = c.Split('|');
                _list.Columns.Add(p[0], int.Parse(p[1]));
            }
            _list.DoubleClick += (s, e) =>
            {
                if (_list.SelectedItems.Count > 0) try { Process.Start(((SiteState)_list.SelectedItems[0].Tag).Url); } catch { }
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
            bar.Controls.Add(Btn("Проверить сейчас", () => _watcher.CheckNow()));
            bar.Controls.Add(Btn("Журнал", () => { try { Process.Start(Log.Folder); } catch { } }));
            page.Controls.Add(_list);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage SettingsTab()
        {
            var page = new TabPage("Настройки") { Padding = new Padding(12), AutoScroll = true };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Row(t, "Сайты (по одному в строке)", _sites);
            Row(t, "Кому писать", _mail);
            Row(t, "Почта-отправитель (Яндекс)", _user);
            Row(t, "Пароль приложения", _password);
            Row(t, "Кому звонить (+79…)", _phones);
            _port.Items.AddRange(SerialPort.GetPortNames());
            Row(t, "COM-порт модема", _port);
            Row(t, "", _awake);
            Row(t, "", _autostart);

            var bar = new FlowLayoutPanel { AutoSize = true };
            bar.Controls.Add(Btn("Сохранить", Save));
            bar.Controls.Add(Btn("Тестовое письмо", TestMail));
            bar.Controls.Add(Btn("Тестовый звонок", TestCall));
            bar.Controls.Add(Btn("Выйти из программы", () => _exit()));
            Row(t, "", bar);
            Row(t, "", new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0),
                Text = "Проверка раз в " + Program.IntervalSeconds + " с; упал = " + Program.FailThreshold + " неудачи подряд; " +
                       "пока лежит — напоминание раз в " + Program.ReminderMinutes + " мин. Модем — заготовка, звонит, только если указан COM-порт и номера. " +
                       "Настройки: " + Config.Folder,
            });
            page.Controls.Add(t);
            return page;
        }

        private static void Row(TableLayoutPanel t, string label, Control c)
        {
            var row = t.RowCount++;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, row);
            c.Margin = new Padding(0, 4, 0, 4);
            t.Controls.Add(c, 1, row);
        }

        private static Button Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (s, e) => a();
            return b;
        }

        private static List<string> Lines(TextBox box)
        {
            return box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        }

        private void LoadFields()
        {
            _sites.Text = string.Join("\r\n", _cfg.Sites);
            _mail.Text = string.Join("\r\n", _cfg.MailTo);
            _phones.Text = string.Join("\r\n", _cfg.Phones);
            _user.Text = _cfg.SmtpUser;
            _password.Text = _cfg.SmtpPassword;
            _port.Text = _cfg.ComPort;
            _awake.Checked = _cfg.KeepAwake;
            try { _autostart.Checked = Native.AutostartOn(); }
            catch { }
        }

        private Config Collect()
        {
            var c = new Config
            {
                Sites = Lines(_sites).Select(u => u.Contains("://") ? u : "https://" + u).ToList(),
                MailTo = Lines(_mail),
                Phones = Lines(_phones),
                SmtpHost = _cfg.SmtpHost,
                SmtpPort = _cfg.SmtpPort,
                SmtpUser = _user.Text.Trim(),
                SmtpPassword = _password.Text,
                ComPort = _port.Text.Trim(),
                KeepAwake = _awake.Checked,
            };
            return c;
        }

        private void Save()
        {
            _cfg = Collect();
            try
            {
                _cfg.Save();
                Native.Autostart(_autostart.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не сохранилось: " + ex.Message, "SiteWatch");
                return;
            }
            Native.KeepAwake(_cfg.KeepAwake);
            _watcher.Apply(_cfg);
            _watcher.CheckNow();
            LoadFields();
            MessageBox.Show(this, "Сохранено.", "SiteWatch");
        }

        private async void TestMail()
        {
            var c = Collect();
            try
            {
                await Alerts.MailAsync(c, "SiteWatch: проверка", "Тестовое письмо с компьютера " + Environment.MachineName + ". Оповещения работают.");
                MessageBox.Show(this, "Письмо отправлено. Если не пришло — проверьте «Спам».", "SiteWatch");
            }
            catch (Exception ex)
            {
                bool dns;
                MessageBox.Show(this, "Не отправилось: " + Checker.Describe(ex, out dns) +
                                      "\nНужен пароль приложения Яндекса и включённый доступ почтовых программ (IMAP).", "SiteWatch");
            }
        }

        private async void TestCall()
        {
            var c = Collect();
            if (c.Phones.Count == 0 || c.ComPort.Length == 0) { MessageBox.Show(this, "Укажите номер и COM-порт.", "SiteWatch"); return; }
            try
            {
                await Task.Run(() => Alerts.Call(c, c.Phones.Take(1).ToList()));
                MessageBox.Show(this, "Звонок выполнен.", "SiteWatch");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Звонок не прошёл: " + ex.Message, "SiteWatch");
            }
        }

        public void RefreshView()
        {
            if (IsDisposed) return;
            var list = _watcher.Snapshot();
            var downCount = list.Count(s => s.IsDown);
            if (!_watcher.InternetOk) { _header.Text = "Нет интернета у этого компьютера"; _header.BackColor = Icons.Violet; }
            else if (downCount > 0) { _header.Text = "Не работает: " + string.Join(", ", list.Where(s => s.IsDown).Select(s => s.Name)); _header.BackColor = Icons.Red; }
            else if (list.Any(s => s.Ok == true)) { _header.Text = "Всё работает (" + list.Count + ")" + (_watcher.LastRound.HasValue ? " · проверено в " + _watcher.LastRound.Value.ToString("HH:mm:ss") : ""); _header.BackColor = Icons.Green; }
            else { _header.Text = list.Count == 0 ? "Сайтов нет — добавьте в «Настройках»" : "Идёт проверка…"; _header.BackColor = Icons.Blue; }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var s in list.OrderByDescending(x => x.IsDown).ThenBy(x => x.Name))
            {
                string state; Color back;
                if (s.Ok == null) { state = "ждём"; back = Color.White; }
                else if (s.IsDown) { state = "НЕ РАБОТАЕТ"; back = Color.FromArgb(255, 215, 215); }
                else if (s.Muted) { state = "адрес не найден"; back = Color.FromArgb(235, 235, 235); }
                else if (s.Ok == false) { state = "сбой " + s.Fails + "/" + Program.FailThreshold; back = Color.FromArgb(255, 240, 200); }
                else { state = "работает"; back = Color.FromArgb(225, 245, 230); }

                var item = new ListViewItem(s.Name) { Tag = s, BackColor = back };
                item.SubItems.Add(state);
                item.SubItems.Add(s.Details);
                item.SubItems.Add(s.LastCheck.HasValue ? s.Ms.ToString() : "");
                item.SubItems.Add(s.LastCheck.HasValue ? Watcher.Ago(DateTime.Now - s.LastCheck.Value) + " назад" : "");
                item.SubItems.Add(s.DownSince.HasValue ? s.DownSince.Value.ToString("HH:mm") : "");
                _list.Items.Add(item);
            }
            _list.EndUpdate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик прячет в трей — проверка продолжается.
            if (e.CloseReason == CloseReason.UserClosing && !AllowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
