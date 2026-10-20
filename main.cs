// SiteWatch — паук-звонарь: следит за нашими сайтами, при падении пишет на почту,
// а через GSM-модем шлёт SMS и звонит. Один файл, C# 5 / .NET Framework 4.x —
// собирается встроенным в Windows компилятором, см. build.cmd.
//
// Как это устроено, в двух словах:
//   • Program.Main — точка входа: не даёт запустить вторую копию и поднимает значок в трее.
//   • TrayApp — значок в области уведомлений; связывает «сторожа» (Watcher) и окно (MainForm).
//   • Watcher — фоновый поток: раз в IntervalSeconds обходит все сайты (Checker)
//     и решает, когда тревожить (Alerts: письмо, а через модем — SMS и звонок).
//   • MainForm — окно с двумя вкладками: «Состояние» и «Настройки».
//   • Config — настройки в %AppData%\SiteWatch\config.txt; Log — журнал там же.
//   • Native — обращения к Windows API и реестру; Icons — значки.
// Всё долгое (сеть, почта, модем) выполняется вне потока интерфейса, а в интерфейс
// результаты попадают через BeginInvoke (см. TrayApp.Ui).

// Базовые типы: String, DateTime, TimeSpan, Exception, Action, Uri, Convert и т. п.
using System;
// List<T> и Dictionary<K,V> — списки сайтов, адресов, телефонов и состояние сайтов.
using System.Collections.Generic;
// Stopwatch — замер времени ответа; Process.Start — открыть сайт в браузере или папку в Проводнике.
using System.Diagnostics;
// Color, Font, Size, Icon, Bitmap, Graphics — цвета, шрифты, размеры, рисование значков.
using System.Drawing;
// SmoothingMode — сглаживание краёв при рисовании кружков-значков.
using System.Drawing.Drawing2D;
// File, Directory, Path, FileInfo, MemoryStream — файлы настроек и журнала, значок из памяти.
using System.IO;
// SerialPort — общение с GSM-модемом через COM-порт.
using System.IO.Ports;
// LINQ: Any, Select, Where, Count, OrderBy, Distinct… — короткие запросы к спискам.
using System.Linq;
// ServicePointManager, WebException, NetworkCredential, DecompressionMethods.
using System.Net;
// HttpClient — сама проверка сайтов. В .NET Framework это отдельная сборка System.Net.Http.dll,
// при компиляции на неё нужна ссылка.
using System.Net.Http;
// SmtpClient, MailMessage, MailAddress — отправка писем.
using System.Net.Mail;
// Атрибуты сборки AssemblyTitle / AssemblyProduct / AssemblyVersion (чуть ниже).
using System.Reflection;
// DllImport — вызов функций Windows из kernel32.dll и user32.dll.
using System.Runtime.InteropServices;
// ProtectedData (DPAPI) — шифрование пароля почты. Живёт в сборке System.Security.dll.
using System.Security.Cryptography;
// StringBuilder и Encoding (UTF-8) — сборка текстов писем и файлов, кодировки.
using System.Text;
// Thread, Mutex, EventWaitHandle, AutoResetEvent — потоки и сигналы между потоками и процессами.
using System.Threading;
// Task и async/await — параллельные проверки сайтов и отправка писем без блокировки.
using System.Threading.Tasks;
// Окна, кнопки, таблицы, значок в трее (NotifyIcon), Application.
using System.Windows.Forms;
// Registry — запись автозапуска в реестр Windows.
using Microsoft.Win32;

// Сведения о сборке — видны в «Свойствах» exe-файла на вкладке «Подробно».
// «Описание файла».
[assembly: AssemblyTitle("SiteWatch")]
// «Название продукта».
[assembly: AssemblyProduct("SiteWatch")]
// Версия сборки. Раз AssemblyFileVersion не задан, она же попадает в «Версию файла».
// Держать в согласии с Program.Version.
[assembly: AssemblyVersion("0.5.0.0")]

// Всё приложение — в одном пространстве имён. Все классы internal: снаружи exe они никому не нужны.
namespace SiteWatch
{
    // ===================================================================== запуск

    // Точка входа и общие константы. Класс static — экземпляров не бывает.
    internal static class Program
    {
        // Версия: заголовок окна, журнал, заголовок User-Agent в запросах к сайтам.
        public const string Version = "0.5.0";
        // Ключ командной строки «запуститься свёрнутым в трей». Его дописывает автозапуск (Native.Autostart).
        public const string TrayArg = "--tray";

        // Константы проверки — без лишних настроек.
        // Пауза между кругами проверки. Отсчитывается от конца круга, так что реальный период —
        // 60 с плюс длительность самого круга (до TimeoutSeconds, плюс письма и звонки, если были).
        public const int IntervalSeconds = 30;   // как часто проверять
        // Общий таймаут HttpClient на один запрос: DNS, соединение, TLS и получение заголовков ответа.
        public const int TimeoutSeconds = 15;    // сколько ждать ответа
        // Сайт признаётся упавшим после стольких неудачных кругов подряд. При интервале 60 с
        // тревога уходит примерно через 4–5 минут после первого сбоя — короткие «моргания» не тревожат.
        public const int FailThreshold = 3;      // упал = столько неудач подряд
        // Пока сайт лежит, повторное письмо (и звонок) — не чаще, чем раз в столько минут.
        public const int ReminderMinutes = 5;   // напоминать, пока лежит
        // Контрольный адрес. Если не открывается и он — проблема у нас, а не у сайтов, тревогу не поднимаем.
        public const string ControlUrl = "https://ya.ru/"; // есть ли интернет у нас самих

        // WinForms требует для главного потока однопоточный «квартирный» режим COM (STA):
        // без него не работают буфер обмена, стандартные диалоги, drag & drop.
        [STAThread]
        // Точка входа. args — аргументы командной строки (смотрим только --tray).
        private static void Main(string[] args)
        {
            // Именованное событие Windows — «звонок» от второй копии программы к первой.
            // Префикс Local\ — имя видно только в текущем сеансе входа (у другого пользователя на том же ПК — своя копия).
            // AutoReset — услышав сигнал, первая копия его «съедает», он сам сбрасывается.
            // Если событие с таким именем уже есть (первая копия жива), конструктор просто откроет существующее.
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SiteWatch.Show"))
            {
                // Станет true, если мьютекс создали мы, — значит, мы первая и единственная копия.
                bool createdNew;
                // Именованный мьютекс — замок «одна копия на сеанс». initiallyOwned = true: если создаём мы, мы же и владеем.
                using (var mutex = new Mutex(true, @"Local\SiteWatch.Single", out createdNew))
                {
                    // Мьютекс уже существовал — программа уже запущена.
                    if (!createdNew)
                    {
                        // Будим первую копию: её фоновый поток ждёт этот сигнал и показывает окно (см. конструктор TrayApp).
                        showSignal.Set(); // уже запущена — показать её окно
                        // А сами тихо завершаемся, ничего не открывая.
                        return;
                    }
                    // Сообщаем Windows, что сами учитываем масштаб экрана (DPI). Иначе при масштабе 125–150 %
                    // система растягивает окно как картинку, и оно выглядит размытым.
                    // try/catch — на очень старых Windows (XP) такой функции нет.
                    try { Native.SetProcessDPIAware(); }
                    catch { }
                    // Добавляем TLS 1.2 к разрешённым протоколам — для всего процесса сразу: и для проверки сайтов
                    // (HttpClient), и для почты (SmtpClient). Старый .NET Framework по умолчанию может предлагать
                    // только TLS 1.0 / SSL 3, а современные сайты и Gmail их уже не принимают.
                    // Делается здесь, при запуске, — до первого письма, даже если список сайтов пуст.
                    // 3072 — числовое значение SecurityProtocolType.Tls12 (по имени константа может быть недоступна при сборке).
                    try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } // TLS 1.2
                    catch { }
                    // Современный вид кнопок, вкладок и списков — по теме Windows, а не классический серый.
                    Application.EnableVisualStyles();
                    // Текст рисуется через GDI (TextRenderer), а не GDI+: так он чётче.
                    // Вызывать до создания первого окна, иначе исключение.
                    Application.SetCompatibleTextRenderingDefault(false);
                    // Необработанное исключение в потоке интерфейса — пишем в журнал вместо стандартного окна ошибки .NET
                    // и продолжаем работать.
                    Application.ThreadException += (s, e) => Log.Write("ERROR " + e.Exception);
                    // Исключение в задаче (Task), результат которой никто не дождался. SetObserved — «обработано»,
                    // чтобы оно ни при каких настройках .NET не уронило процесс.
                    TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("ERROR " + e.Exception); e.SetObserved(); };

                    // Запуск с ключом --tray (автозапуск вместе с Windows) — окно не показываем, сразу работаем в трее.
                    var hidden = args.Any(a => a == TrayArg);
                    // Первая запись в журнал: версия и способ запуска.
                    Log.Write("Запуск " + Version + (hidden ? " (автозапуск)" : ""));
                    // Создаём «приложение в трее» и запускаем цикл обработки сообщений Windows.
                    // Run не вернёт управление, пока TrayApp не вызовет ExitThread (пункт «Выход»).
                    Application.Run(new TrayApp(hidden, showSignal));
                    // Сюда попадаем только после выхода из программы.
                    Log.Write("Выход");
                    // Мьютекс должен дожить до этой строки, иначе «замок» освободится и сможет запуститься вторая копия.
                    // using и так это гарантирует; строка — явная страховка.
                    GC.KeepAlive(mutex);
                }
            }
        }
    }

    // ===================================================================== Win32

    // Обёртки над функциями Windows (P/Invoke) и работа с реестром.
    internal static class Native
    {
        // kernel32: «я работаю, не засыпай» — действует, пока жив поток интерфейса.
        // Объявление функции из kernel32.dll: сообщает системе, что программе нужен работающий компьютер.
        // Возвращает прежнее состояние флагов (0 — ошибка); здесь результат не проверяется.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint flags);
        // ES_CONTINUOUS — «запомнить» состояние до следующего вызова. Без него флаг сработал бы один раз —
        // лишь сбросил бы таймер простоя.
        public const uint ES_CONTINUOUS = 0x80000000;
        // ES_SYSTEM_REQUIRED — не уходить в сон по таймеру простоя. Экран при этом гаснуть может
        // (для экрана есть отдельный флаг ES_DISPLAY_REQUIRED).
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // Из user32.dll: процесс сам учитывает DPI (см. Main).
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        // on = true — запрет сна по простою; on = false — один ES_CONTINUOUS, то есть запрет снимается.
        // Запрет привязан к вызывающему потоку — вызывать из потока интерфейса, который живёт всю работу программы.
        // Сон по команде человека (кнопка «Сон», закрытая крышка ноутбука) это не отменяет.
        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
        }

        /// <summary>Автозапуск вместе с Windows (HKCU\...\Run, без прав администратора).</summary>
        // Раздел HKCU\Software\Microsoft\Windows\CurrentVersion\Run: всё, что в нём записано, Windows запускает
        // при входе этого пользователя. HKCU, а не HKLM, — поэтому без прав администратора и только для себя.
        public static void Autostart(bool on)
        {
            // CreateSubKey открывает раздел на запись (создаёт, если его нет). using — закрыть ключ после работы.
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                // Включение: значение «SiteWatch» = путь к exe в кавычках (на случай пробелов в пути) + ключ --tray,
                // чтобы при входе в Windows стартовать свёрнутой.
                if (on) key.SetValue("SiteWatch", "\"" + Application.ExecutablePath + "\" " + Program.TrayArg);
                // Выключение: удаляем значение, если оно есть. false — не бросать исключение, если его вдруг уже нет.
                else if (key.GetValue("SiteWatch") != null) key.DeleteValue("SiteWatch", false);
            }
        }

        // Включён ли автозапуск именно этого exe. Если программу переложили в другую папку,
        // старая запись указывает не туда — вернётся false.
        public static bool AutostartOn()
        {
            // OpenSubKey с одним аргументом открывает только для чтения; если раздела нет — вернёт null.
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                // Значения может не быть или оно не строковое — тогда v = null.
                var v = key == null ? null : key.GetValue("SiteWatch") as string;
                // Сравниваем без учёта регистра: пути в Windows регистронезависимы.
                return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    // ===================================================================== журнал

    // Простой текстовый журнал %AppData%\SiteWatch\sitewatch.log. Пишут в него разные потоки — поэтому замок.
    internal static class Log
    {
        // Объект-замок: в файл одновременно пишет только один поток.
        private static readonly object Sync = new object();
        // Журнал лежит в той же папке, что и настройки.
        public static string Folder { get { return Config.Folder; } }

        // Дописать строку в журнал. Ошибки записи глушатся — журнал не должен ронять программу.
        public static void Write(string text)
        {
            try
            {
                lock (Sync)
                {
                    // Папку «создаём» при каждой записи: если её нет — появится, если есть — ничего не произойдёт.
                    Directory.CreateDirectory(Folder);
                    // Полный путь к файлу журнала.
                    var file = Path.Combine(Folder, "sitewatch.log");
                    // Журнал не растёт бесконечно: больше 2 МБ — начинаем заново.
                    // Старые записи при этом пропадают целиком — архива нет.
                    if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024) File.Delete(file);
                    // Формат строки: «дд.ММ чч:мм:сс текст» (без года). \r\n — перевод строки по-виндовому, для Блокнота.
                    File.AppendAllText(file, DateTime.Now.ToString("dd.MM HH:mm:ss") + " " + text + "\r\n", Encoding.UTF8);
                }
            }
            // Любая ошибка (файл занят, нет места, нет прав) — молча пропускаем.
            catch { }
        }
    }

    // ===================================================================== настройки

    /// <summary>
    /// Настройки в %AppData%\SiteWatch\config.txt, простым текстом по секциям.
    /// Пароль почты зашифрован Windows (DPAPI) — читается только этим пользователем на этом ПК.
    /// </summary>
    // Поля ниже — значения по умолчанию: действуют, пока файла настроек ещё нет (первый запуск).
    // Как только файл есть, списки берутся из него целиком — даже пустые.
    internal sealed class Config
    {
        // Сайты, которые проверяем. Можно с путём (https://site.ru/page) — проверяется именно этот адрес.
        public List<string> Sites = new List<string>
        {
            "https://litewms.ru/",
            "https://lite-box.ru/",
            "https://scanmaster.tech/",
            "https://limaster.ru/",
            "https://support.litewms.ru/",
        };
        // Кому слать письма о падении и восстановлении.
        public List<string> MailTo = new List<string>
        {
            "ahilespelid@yandex.ru",
            "brand.korolev@gmail.com",
            "dvrh.76@gmail.com",
        };
        // Номера для звонков через модем; пусто — не звоним.
        public List<string> Phones = new List<string>();
        // SMTP-сервер отправителя. Для Gmail и Яндекса подбирается сам по адресу отправителя (см. HostFor);
        // для прочих почт — берётся из config.txt (smtp_host). В окне не редактируется.
        public string SmtpHost = "smtp.gmail.com";
        // 587 — SMTP с STARTTLS; так работают и Gmail, и Яндекс. Меняется только в файле (smtp_port).
        public int SmtpPort = 587;
        // Ящик-отправитель, он же логин SMTP: письмо уходит от его имени.
        public string SmtpUser = "fflitebox@gmail.com";
        // Пароль приложения (не основной пароль ящика). В коде его нет — вводится в окне «Настройки».
        // В памяти — открытым текстом, на диске — зашифрован (см. Protect).
        public string SmtpPassword = "";
        // COM-порт GSM-модема, например COM3; пусто — модема нет.
        public string ComPort = "";
        // Не давать компьютеру засыпать, пока программа работает.
        public bool KeepAwake = true;

        // Папка настроек: %AppData%\SiteWatch (Roaming-профиль текущего пользователя),
        // обычно C:\Users\<Имя>\AppData\Roaming\SiteWatch.
        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SiteWatch"); }
        }

        // Полный путь к файлу настроек.
        private static string FilePath { get { return Path.Combine(Folder, "config.txt"); } }

        // SMTP-сервер Gmail.
        public const string GmailHost = "smtp.gmail.com";

        // Подобрать SMTP-сервер по домену ящика-отправителя: gmail.com → smtp.gmail.com, yandex.ru/ya.ru → smtp.yandex.ru.
        // Для прочих доменов (в том числе своих доменов на Google Workspace) возвращает fallback — то, что задано в файле.
        public static string HostFor(string user, string fallback)
        {
            // Домен — всё после последнего «@», в нижнем регистре. Нет «@» — домена нет.
            var at = (user ?? "").LastIndexOf('@');
            var domain = at < 0 ? "" : user.Substring(at + 1).Trim().ToLowerInvariant();
            // Обычный Gmail (googlemail.com — его старое имя в некоторых странах).
            if (domain == "gmail.com" || domain == "googlemail.com") return GmailHost;
            // Яндекс.Почта.
            if (domain == "yandex.ru" || domain == "ya.ru" || domain == "yandex.com") return "smtp.yandex.ru";
            // Неизвестная почта — оставляем сервер из настроек.
            return fallback;
        }

        // Прочитать настройки. Файла нет — значения по умолчанию; ошибка чтения — тоже, плюс запись в журнал.
        public static Config Load()
        {
            // Начинаем со значений по умолчанию — для опций, которых в файле не окажется.
            var cfg = new Config();
            // Первый запуск: файла ещё нет — отдаём умолчания как есть.
            if (!File.Exists(FilePath)) return cfg;
            try
            {
                // Текущая секция файла: sites, mail, phones или options.
                var section = "";
                // Списки собираем отдельно и подставляем в самом конце: если чтение сорвётся посередине,
                // списки по умолчанию не пострадают.
                var sites = new List<string>(); var mail = new List<string>(); var phones = new List<string>();
                // Читаем файл целиком, построчно, в UTF-8 (метка BOM, если есть, учитывается сама).
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    // Пробелы по краям строки не важны.
                    var line = raw.Trim();
                    // Пустые строки и комментарии (#…) пропускаем.
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    // Строка вида [имя] — начало новой секции. Регистр не важен.
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Trim('[', ']').ToLowerInvariant(); continue; }
                    // В секциях-списках каждая строка — отдельный элемент.
                    if (section == "sites") sites.Add(line);
                    else if (section == "mail") mail.Add(line);
                    else if (section == "phones") phones.Add(line);
                    // Секция options — пары ключ=значение.
                    else if (section == "options")
                    {
                        // Позиция первого «=». Нет «=» или он первым символом (пустой ключ) — строка битая, пропускаем.
                        var eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        // Ключ — всё до первого «=», без пробелов, в нижнем регистре; значение — всё после него.
                        // Внутри значения «=» допустимы — это важно для пароля в base64, который часто ими заканчивается.
                        var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        var v = line.Substring(eq + 1).Trim();
                        // Разбираем известные ключи; неизвестные молча игнорируются.
                        if (k == "smtp_host") cfg.SmtpHost = v;
                        // Внимание: если число не разобралось, TryParse запишет в порт 0, а не оставит 587.
                        else if (k == "smtp_port") int.TryParse(v, out cfg.SmtpPort);
                        else if (k == "smtp_user") cfg.SmtpUser = v;
                        // Пароль в файле зашифрован — расшифровываем. Не вышло (файл с другого ПК или от другого
                        // пользователя Windows) — получится пустая строка.
                        else if (k == "smtp_password") cfg.SmtpPassword = Unprotect(v);
                        else if (k == "com_port") cfg.ComPort = v;
                        // Всё, кроме «0», считается «включено».
                        else if (k == "keep_awake") cfg.KeepAwake = v != "0";
                    }
                }
                // Подставляем собранные списки. Если секции в файле не было, список будет пустым.
                cfg.Sites = sites; cfg.MailTo = mail; cfg.Phones = phones;
            }
            // Файл занят, нет прав, битая строка — пишем в журнал и работаем с тем, что успели прочитать.
            catch (Exception ex)
            {
                Log.Write("ERROR настройки не прочитаны: " + ex.Message);
            }
            // Сервер — по адресу отправителя: если в файле остался smtp.yandex.ru, а ящик уже gmail.com,
            // будет использован smtp.gmail.com (и при следующем сохранении он же запишется в файл).
            cfg.SmtpHost = HostFor(cfg.SmtpUser, cfg.SmtpHost);
            return cfg;
        }

        // Записать настройки в файл. Файл перезаписывается целиком: комментарии, добавленные руками, пропадут.
        public void Save()
        {
            // Папки может ещё не быть (первое сохранение).
            Directory.CreateDirectory(Folder);
            var sb = new StringBuilder();
            // Первая строка — подсказка для того, кто откроет файл руками.
            sb.AppendLine("# SiteWatch. Сайты, почта и телефоны — по одному в строке.");
            // Каждая секция — заголовок [имя] и по элементу в строке.
            sb.AppendLine("[sites]"); foreach (var s in Sites) sb.AppendLine(s);
            sb.AppendLine("[mail]"); foreach (var s in MailTo) sb.AppendLine(s);
            sb.AppendLine("[phones]"); foreach (var s in Phones) sb.AppendLine(s);
            // Опции — ключ=значение; ключи те же, что понимает Load.
            sb.AppendLine("[options]");
            sb.AppendLine("smtp_host=" + SmtpHost);
            sb.AppendLine("smtp_port=" + SmtpPort);
            sb.AppendLine("smtp_user=" + SmtpUser);
            // Пароль — только в зашифрованном виде (base64 от DPAPI).
            sb.AppendLine("smtp_password=" + Protect(SmtpPassword));
            sb.AppendLine("com_port=" + ComPort);
            sb.AppendLine("keep_awake=" + (KeepAwake ? "1" : "0"));
            // UTF8Encoding(false) — UTF-8 без метки BOM.
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }

        // Дополнительная «соль» для DPAPI: расшифровать можно, только зная её. Отделяет наши данные
        // от чужих, секретом не является.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SiteWatch");

        // Зашифровать строку средствами Windows (DPAPI). Ключ берётся из учётной записи Windows
        // текущего пользователя — хранить его самим не нужно.
        private static string Protect(string plain)
        {
            // Пустой пароль не шифруем — пишем пустую строку.
            if (string.IsNullOrEmpty(plain)) return "";
            // Строка → байты UTF-8 → шифр DPAPI → base64 (чтобы в файле был печатный текст).
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
        }

        // Обратное превращение: base64 → расшифровка DPAPI → строка.
        private static string Unprotect(string value)
        {
            try { return string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
            // Не вышло (файл перенесён с другого ПК, сменился пользователь, испорчен base64) — считаем, что пароля нет.
            catch { return ""; }
        }
    }

    // ===================================================================== проверка

    // Состояние одного сайта. Используется в двух ролях:
    //   • итог одной проверки — Checker.CheckAsync заполняет Url, Ok, Details, Ms, Muted;
    //   • накопленная история сайта в Watcher — счётчик сбоев, «упал», когда напоминали и т. д.
    internal sealed class SiteState
    {
        // Адрес как в настройках; он же ключ в словаре Watcher._states.
        public string Url;
        // Итог последней проверки: true — работает, false — нет.
        public bool? Ok;            // null — ещё не проверяли
        // Пояснение для людей: «200 OK», «нет ответа за 15 с», «домен не найден (DNS)»…
        public string Details = "";
        // Время ответа в миллисекундах (для неудачных — сколько ждали до ошибки).
        public long Ms;
        // Когда проверяли последний раз (null — ещё ни разу).
        public DateTime? LastCheck;
        // Сбрасывается в 0 при первом же успехе.
        public int Fails;           // неудач подряд
        // Становится true, когда Fails дошёл до FailThreshold; сбрасывается при успехе.
        public bool IsDown;         // признан упавшим, письмо отправлено
        // Время первого сбоя текущей серии — «лежит с …».
        public DateTime? DownSince;
        // Когда последний раз слали тревогу — для напоминаний раз в ReminderMinutes.
        public DateTime? LastAlert;
        // Нужен, чтобы отличить опечатку в адресе от реально пропавшего домена.
        public bool EverOk;         // хоть раз открылся
        public bool Muted;          // домен не найден и ни разу не открывался — опечатка, не тревожим

        // Короткое имя для таблицы и писем — только домен (litewms.ru). Если адрес не разбирается — как есть.
        public string Name
        {
            get
            {
                Uri u;
                return Uri.TryCreate(Url, UriKind.Absolute, out u) ? u.Host : Url;
            }
        }

        // Копия для передачи в другой поток. Все поля — значения или неизменяемые строки,
        // так что поверхностной копии (MemberwiseClone) достаточно.
        public SiteState Copy() { return (SiteState)MemberwiseClone(); }
    }

    // Проверка одного адреса по HTTP(S). Один общий HttpClient на всю программу:
    // так он переиспользует соединения и не плодит сокеты.
    internal static class Checker
    {
        // Общий клиент; создаётся в статическом конструкторе ниже.
        private static readonly HttpClient Http;

        // Статический конструктор — выполняется один раз, при первом обращении к Checker.
        static Checker()
        {
            // TLS 1.2 к этому моменту уже включён в Program.Main.
            // Настройки «транспорта» HttpClient.
            var handler = new HttpClientHandler
            {
                // Следовать перенаправлениям (http → https, без www → с www…) — проверяется итоговая страница.
                AllowAutoRedirect = true,
                // Разрешаем сжатый ответ (gzip / deflate), как обычный браузер.
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                // Куки не нужны: каждая проверка — с чистого листа, без входа в аккаунт.
                UseCookies = false,
            };
            // Таймаут на весь запрос — Program.TimeoutSeconds.
            Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Program.TimeoutSeconds) };
            // Представляемся как «SiteWatch/0.4.0» — в логах веб-сервера видно, что это наш сторож.
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SiteWatch/" + Program.Version);
        }

        /// <summary>Открыть страницу. Работает = ответ 200–399 или 401/403 (просит войти — значит жив).</summary>
        // Возвращает новый SiteState только с итогом этой проверки (история сайта хранится в Watcher).
        // Исключения наружу не выпускает: любая ошибка превращается в Ok = false с понятным текстом.
        public static async Task<SiteState> CheckAsync(string url)
        {
            // Заготовка результата.
            var r = new SiteState { Url = url };
            // Секундомер — для столбца «мс».
            var sw = Stopwatch.StartNew();
            try
            {
                // GET-запрос. ResponseHeadersRead — ждём только заголовки ответа и не скачиваем страницу целиком:
                // быстрее и меньше трафика. using — закрыть ответ (соединение вернётся в пул) сразу после разбора.
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    // Код ответа HTTP числом: 200, 301, 404, 502…
                    var code = (int)resp.StatusCode;
                    // 2xx — успех; 3xx — перенаправление (обычно уже пройдено автоматически); 401/403 — сервер жив,
                    // просто требует входа. Всё остальное (404, 500, 502, 503…) — сбой.
                    r.Ok = (code >= 200 && code < 400) || code == 401 || code == 403;
                    // Например «200 OK» или «502 Bad Gateway» — в таблицу и письма.
                    r.Details = code + " " + resp.ReasonPhrase;
                }
            }
            // Сработал таймаут HttpClient: сервер не ответил за TimeoutSeconds.
            catch (TaskCanceledException)
            {
                r.Ok = false;
                r.Details = "нет ответа за " + Program.TimeoutSeconds + " с";
            }
            // Прочие ошибки сети: DNS, отказ в соединении, SSL… Describe переводит их на человеческий язык
            // и заодно сообщает, не DNS ли это (флаг Muted).
            catch (Exception ex)
            {
                r.Ok = false;
                r.Details = Describe(ex, out r.Muted);
            }
            // Время до ответа или до ошибки.
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        /// <summary>Понятный текст ошибки. dns = домен не найден.</summary>
        // Исключения .NET вложены друг в друга (HttpRequestException → WebException → SocketException),
        // поэтому идём по цепочке InnerException вглубь и ищем знакомую причину.
        public static string Describe(Exception ex, out bool dns)
        {
            // По умолчанию — не DNS.
            dns = false;
            // Перебор: само исключение, затем внутреннее, затем внутреннее внутреннего…
            for (var e = ex; e != null; e = e.InnerException)
            {
                // Ошибки уровня WebRequest — у них есть точный код Status.
                var we = e as WebException;
                if (we != null)
                {
                    // Имя сайта не превратилось в IP-адрес: домена нет, не продлён или опечатка.
                    if (we.Status == WebExceptionStatus.NameResolutionFailure) { dns = true; return "домен не найден (DNS)"; }
                    // Адрес найден, но подключиться не вышло: сервер выключен, порт закрыт, мешает файрвол.
                    if (we.Status == WebExceptionStatus.ConnectFailure) return "не удалось подключиться";
                    // Не удалось установить защищённое соединение: сертификат просрочен, выдан на другое имя или самоподписан,
                    // либо несовместимы протоколы.
                    if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure) return "ошибка SSL-сертификата";
                }
                // Ошибки сокетов — самый нижний уровень.
                var se = e as System.Net.Sockets.SocketException;
                if (se != null)
                {
                    // Тоже «домен не найден».
                    if (se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound) { dns = true; return "домен не найден (DNS)"; }
                    // Сервер активно отказал в соединении (на порту никто не слушает).
                    if (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused) return "соединение отклонено";
                    // Прочие сетевые ошибки — показываем системный код (TimedOut, NetworkUnreachable…).
                    return "сеть: " + se.SocketErrorCode;
                }
                // Дошли до дна цепочки и ничего знакомого не нашли — отдаём текст самой глубокой ошибки
                // (обычно он самый конкретный).
                if (e.InnerException == null) return e.Message;
            }
            // Формально недостижимо: цикл всегда возвращает результат раньше, на последнем исключении цепочки.
            return ex.Message;
        }
    }

    /// <summary>Круг проверки раз в минуту и решение, когда тревожить.</summary>
    // Работает в собственном фоновом потоке. Наружу сообщает двумя событиями:
    //   Changed — что-то обновилось, пора перерисовать окно и значок;
    //   Notify  — показать всплывающее уведомление в трее.
    // Обработчики вызываются в потоке сторожа, поэтому TrayApp перекидывает их в поток интерфейса.
    internal sealed class Watcher
    {
        // Замок для _states и _cfg: их читает и меняет и поток проверки, и поток интерфейса.
        private readonly object _sync = new object();
        // Состояние сайтов: адрес → SiteState. Живёт между кругами — отсюда счётчики сбоев.
        private Dictionary<string, SiteState> _states = new Dictionary<string, SiteState>();
        // Текущие настройки: адреса, почта, телефоны, модем.
        private Config _cfg;
        // «Будильник»: Set() досрочно прерывает паузу между кругами
        // («Проверить сейчас», сохранение настроек, выход).
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        // Флаг остановки. volatile — чтобы поток проверки сразу увидел изменение, сделанное другим потоком.
        private volatile bool _stop;

        // Есть ли интернет у этого компьютера (по ControlUrl). Пишет поток проверки, читает интерфейс.
        public bool InternetOk = true;
        // Время окончания последнего круга — для плашки «проверено в …».
        public DateTime? LastRound;
        // Сколько кругов подряд у этого компьютера нет интернета и ушло ли об этом SMS через модем.
        private int _offlineRounds;
        private bool _offlineSmsSent;

        // Событие «данные изменились».
        public event Action Changed;
        // Событие для всплывающего уведомления: текст и признак «это проблема» (true — значок ошибки).
        public event Action<string, bool> Notify; // текст, это проблема

        // Конструктор — сразу применяем настройки (заводим пустые состояния для всех сайтов).
        public Watcher(Config cfg) { Apply(cfg); }

        // Применить новые настройки. Сайты, оставшиеся в списке, сохраняют историю;
        // удалённые забываются, новые начинают с нуля.
        public void Apply(Config cfg)
        {
            lock (_sync)
            {
                // Запоминаем настройки целиком.
                _cfg = cfg;
                // Собираем новый словарь в порядке нового списка.
                var next = new Dictionary<string, SiteState>();
                foreach (var url in cfg.Sites)
                {
                    SiteState old;
                    // Повтор адреса в списке пропускаем.
                    if (next.ContainsKey(url)) continue;
                    // Был такой сайт — переносим его состояние (счётчики, «упал», время сбоя); новый — пустое состояние.
                    next[url] = _states.TryGetValue(url, out old) ? old : new SiteState { Url = url };
                }
                // Подменяем словарь целиком.
                _states = next;
            }
            // Сообщаем интерфейсу — список мог измениться. Вне замка, чтобы обработчики его не держали.
            Raise();
        }

        // Безопасный снимок для интерфейса: копии состояний, которые можно спокойно читать без замка,
        // пока поток проверки меняет оригиналы.
        public List<SiteState> Snapshot()
        {
            lock (_sync) return _states.Values.Select(s => s.Copy()).ToList();
        }

        // Проверить сейчас — просто будим поток, не дожидаясь конца паузы.
        public void CheckNow() { _wake.Set(); }

        // Запустить бесконечный цикл проверки в отдельном потоке.
        public void Start()
        {
            var t = new Thread(() =>
            {
                // Крутимся, пока не попросили остановиться.
                while (!_stop)
                {
                    // Один круг. Wait() синхронно дожидается асинхронного круга: у этого потока нет контекста
                    // синхронизации, так что взаимной блокировки (deadlock) не будет.
                    try { Round().Wait(); }
                    // Любая ошибка круга — в журнал, а цикл продолжается: сторож не должен умереть из-за одного сбоя.
                    catch (Exception ex) { Log.Write("ERROR круг проверки: " + ex); }
                    // Пауза до следующего круга. Заканчивается досрочно, если кто-то вызвал _wake.Set().
                    _wake.WaitOne(TimeSpan.FromSeconds(Program.IntervalSeconds));
                }
            // IsBackground — поток не мешает процессу завершиться; Name — видно в отладчике.
            }) { IsBackground = true, Name = "SiteWatch.Watcher" };
            t.Start();
        }

        // Остановка: ставим флаг и будим поток, чтобы он не досиживал паузу. Идущий в этот момент круг
        // не прерывается — фоновый поток просто завершится вместе с процессом.
        public void Stop() { _stop = true; _wake.Set(); }

        // Вызвать подписчиков Changed. Делегат копируется в локальную переменную — защита на случай,
        // если между проверкой на null и вызовом кто-то отпишется.
        private void Raise() { var h = Changed; if (h != null) h(); }
        // То же для Notify.
        private void Say(string text, bool problem) { var h = Notify; if (h != null) h(text, problem); }

        // Один круг: все сайты параллельно → проверка своего интернета → обновление счётчиков → письма и звонки.
        private async Task Round()
        {
            List<string> urls; Config cfg;
            // Под замком берём снимок списка адресов и настроек — дальше работаем с ним,
            // даже если настройки поменяют посреди круга.
            lock (_sync) { urls = _states.Keys.ToList(); cfg = _cfg; }

            // Запускаем проверки всех сайтов одновременно и ждём все. Круг длится столько, сколько
            // самый медленный сайт (не дольше TimeoutSeconds).
            var results = await Task.WhenAll(urls.Select(u => Checker.CheckAsync(u)));

            // Не открылось ничего или что-то — сначала проверяем свой интернет.
            // Если все сайты открылись, интернет заведомо есть — лишний запрос не делаем.
            if (results.Any(r => r.Ok != true))
            {
                // Контрольный запрос к ControlUrl (ya.ru).
                var control = await Checker.CheckAsync(Program.ControlUrl);
                // Не открылся и он — нет интернета у нас самих. Сайты в этом круге не судим: счётчики сбоев
                // не трогаем, писем не шлём (их всё равно не отправить).
                if (control.Ok != true)
                {
                    // Сообщаем только при переходе «был интернет → пропал», а не каждый круг.
                    if (InternetOk) { InternetOk = false; Log.Write("Нет интернета у этого компьютера"); Say("Нет интернета у этого компьютера — сайты проверить нельзя", true); }
                    // Отмечаем время круга, обновляем окно (фиолетовое состояние). Результаты сайтов отбрасываются.
                    LastRound = DateTime.Now; Raise();
                    // Интернета нет FailThreshold кругов подряд — сайты никто не проверяет, а письмо не отправить.
                    // Модем ходит через сотовую сеть, интернет компьютера ему не нужен — одно SMS об этом.
                    _offlineRounds++;
                    if (_offlineRounds == Program.FailThreshold)
                        _offlineSmsSent = await Modem(cfg, "SiteWatch (" + Environment.MachineName + "): у компьютера нет интернета, сайты не проверяются. " + DateTime.Now.ToString("HH:mm"), false);
                    return;
                }
            }
            // Интернет вернулся после перерыва — одно уведомление. Дальше — обычная обработка.
            if (!InternetOk) { InternetOk = true; Log.Write("Интернет снова есть"); Say("Интернет снова есть", false); }
            // Если о пропаже интернета уходило SMS — сообщаем, что проверка снова идёт.
            _offlineRounds = 0;
            if (_offlineSmsSent)
            {
                _offlineSmsSent = false;
                await Modem(cfg, "SiteWatch (" + Environment.MachineName + "): интернет снова есть, сайты проверяются. " + DateTime.Now.ToString("HH:mm"), false);
            }

            // Единое время на весь круг — одинаковое в таблице, журнале и письмах.
            var now = DateTime.Now;
            // Три корзины для оповещений: только что упали; всё ещё лежат (пора напомнить); поднялись.
            var down = new List<SiteState>(); var still = new List<SiteState>(); var up = new List<SiteState>();
            // Обновляем историю сайтов под замком.
            lock (_sync)
            {
                foreach (var r in results)
                {
                    SiteState st;
                    // Пока шла проверка, сайт могли удалить из настроек — тогда его результат просто пропускаем.
                    if (!_states.TryGetValue(r.Url, out st)) continue;
                    // Переносим итог проверки в состояние сайта.
                    st.Ok = r.Ok; st.Details = r.Details; st.Ms = r.Ms; st.LastCheck = now;

                    // Сайт открылся.
                    if (r.Ok == true)
                    {
                        // До этого он считался упавшим — значит, это восстановление: будет письмо «Снова работает».
                        // Копия снимается до сброса, поэтому в письме видно, с какого времени и как долго он лежал.
                        if (st.IsDown) { up.Add(st.Copy()); Log.Write("РАБОТАЕТ " + st.Url); }
                        // Полный сброс: был успех, серия сбоев закончилась, опечатки в адресе точно нет.
                        st.EverOk = true; st.Muted = false; st.Fails = 0; st.IsDown = false; st.DownSince = null; st.LastAlert = null;
                        // К следующему сайту.
                        continue;
                    }
                    // DNS не нашёл домен, а сайт ни разу не открывался — скорее всего, опечатка в адресе.
                    // Сбоем не считаем и не тревожим (в таблице — «адрес не найден»). Если же сайт раньше открывался,
                    // а потом домен пропал (например, не продлили) — это настоящая авария, идём дальше.
                    if (r.Muted && !st.EverOk) { st.Muted = true; st.Fails = 0; continue; } // опечатка в адресе
                    // Сюда попадаем при настоящем сбое.
                    st.Muted = false;
                    // +1 к серии неудач подряд.
                    st.Fails++;
                    // Первая неудача серии — запоминаем, с какого момента сайт сбоит.
                    if (st.Fails == 1) st.DownSince = now;
                    // Каждый сбой — в журнал, вида «Сбой 2/5 https://… — 502 Bad Gateway».
                    Log.Write("Сбой " + st.Fails + "/" + Program.FailThreshold + " " + st.Url + " — " + r.Details);

                    // Порог достигнут впервые: сайт признан упавшим → письмо «НЕ РАБОТАЕТ» (и звонок).
                    if (!st.IsDown && st.Fails >= Program.FailThreshold) { st.IsDown = true; st.LastAlert = now; down.Add(st.Copy()); }
                    // Уже лежит, и с прошлой тревоги прошло не меньше ReminderMinutes → напоминание «ВСЁ ЕЩЁ НЕ РАБОТАЕТ».
                    // Проверка идёт раз в круг, так что на деле напоминания приходят чуть реже — примерно раз в 5–6 минут.
                    else if (st.IsDown && st.LastAlert.HasValue && now - st.LastAlert.Value >= TimeSpan.FromMinutes(Program.ReminderMinutes))
                    { st.LastAlert = now; still.Add(st.Copy()); }
                }
                // Круг завершён.
                LastRound = now;
            }
            // Сообщаем интерфейсу — обновить таблицу и значок.
            Raise();

            // Оповещения отправляются уже вне замка: письмо и тем более звонок идут долго, а интерфейс в это время
            // должен свободно читать состояние. Следующий круг не начнётся, пока они не закончатся.
            if (down.Count > 0)
            {
                // Всплывающее уведомление в трее.
                Say("Не работает: " + Names(down), true);
                // Письмо, SMS с причиной и звонок: одно оповещение на все сайты, упавшие в этом круге.
                await Alert(cfg, "НЕ РАБОТАЕТ: " + Names(down), Body("Перестали отвечать:", down, now), SmsText("НЕ РАБОТАЕТ", down, now, true), true);
            }
            // Напоминание — письмо и звонок (причина уже была в первом SMS, лишние SMS не шлём);
            // всплывающее уведомление не показываем, чтобы не надоедать.
            if (still.Count > 0)
                await Alert(cfg, "ВСЁ ЕЩЁ НЕ РАБОТАЕТ: " + Names(still), Body("По-прежнему не отвечают:", still, now), null, true);
            // Восстановление: уведомление, письмо и SMS, без звонка.
            if (up.Count > 0)
            {
                Say("Снова работает: " + Names(up), false);
                await Alert(cfg, "Снова работает: " + Names(up), Body("Снова работают:", up, now), SmsText("Снова работает", up, now, false), false);
            }
        }

        // Короткий текст SMS: «НЕ РАБОТАЕТ: litewms.ru (502 Bad Gateway), lite-box.ru (нет ответа за 15 с). 14:05».
        // Причины — только для упавших. Длинный текст модем отправит частями, телефон склеит их в одно сообщение.
        private static string SmsText(string title, List<SiteState> list, DateTime now, bool reasons)
        {
            return title + ": " + string.Join(", ", list.Select(s => reasons ? s.Name + " (" + s.Details + ")" : s.Name)) + ". " + now.ToString("HH:mm");
        }

        // Домены через запятую — для темы письма и уведомлений.
        private static string Names(List<SiteState> list) { return string.Join(", ", list.Select(s => s.Name)); }

        // Текст письма: заголовок, затем по каждому сайту адрес и причина со временем начала сбоя,
        // в конце подпись с именем компьютера и временем.
        private static string Body(string title, List<SiteState> list, DateTime now)
        {
            // Заголовок и пустая строка после него.
            var sb = new StringBuilder(title + "\n\n");
            // По каждому сайту — две строки.
            foreach (var s in list)
            {
                // Адрес сайта.
                sb.AppendLine("• " + s.Url);
                // Например: «  502 Bad Gateway, сбои с 14:05 (12 мин)».
                sb.AppendLine("  " + s.Details + (s.DownSince.HasValue ? ", сбои с " + s.DownSince.Value.ToString("HH:mm") + " (" + Ago(now - s.DownSince.Value) + ")" : ""));
            }
            // Подпись: с какого компьютера и когда отправлено — удобно, если таких сторожей несколько.
            sb.AppendLine().AppendLine("— SiteWatch, " + Environment.MachineName + ", " + now.ToString("dd.MM.yyyy HH:mm:ss"));
            return sb.ToString();
        }

        // «Сколько прошло» по-человечески: 45 с / 12 мин / 3 ч 5 мин / 2 д 4 ч.
        // Используется в письмах и в таблице («проверен … назад»).
        public static string Ago(TimeSpan t)
        {
            // Меньше минуты — секунды.
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + " с";
            // Меньше часа — минуты.
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " мин";
            // Меньше суток — часы и минуты.
            if (t.TotalDays < 1) return (int)t.TotalHours + " ч " + t.Minutes + " мин";
            // Иначе — дни и часы.
            return (int)t.TotalDays + " д " + t.Hours + " ч";
        }

        // Отправить оповещение: письмо и модем (SMS, звонок) — одновременно. Если почта «висит» без связи
        // с сервером, SMS и звонок не ждут её. sms = null — без SMS; call — звонить ли. Сбой одного не отменяет другое.
        private async Task Alert(Config cfg, string subject, string body, string sms, bool call)
        {
            await Task.WhenAll(Mail(cfg, subject, body), Modem(cfg, sms, call));
        }

        // Письмо. Не ушло (нет пароля, сервер не принял, нет связи) — в журнал целиком и всплывающим уведомлением,
        // чтобы человек у компьютера узнал, что тревога не дошла.
        private async Task Mail(Config cfg, string subject, string body)
        {
            try { await Alerts.MailAsync(cfg, subject, body); Log.Write("Письмо: " + subject); }
            catch (Exception ex)
            {
                Log.Write("ERROR письмо не ушло (" + cfg.SmtpHost + ":" + cfg.SmtpPort + "): " + ex);
                Say("Письмо не отправлено: " + Alerts.Explain(ex), true);
            }
        }

        // SMS и/или звонок на все номера через модем — если указаны номера и COM-порт. Вернёт true, если SMS ушло.
        // Модем работает медленно (SMS — секунды, звонок — до 30 с на номер), поэтому всё — в пуле потоков через Task.Run.
        private async Task<bool> Modem(Config cfg, string sms, bool call)
        {
            if (cfg.Phones.Count == 0 || string.IsNullOrEmpty(cfg.ComPort)) return false;
            var sent = false;
            if (sms != null)
            {
                try { await Task.Run(() => Alerts.Sms(cfg.ComPort, cfg.Phones, sms)); sent = true; }
                catch (Exception ex) { Log.Write("ERROR SMS не ушло: " + ex.Message); Say("SMS не отправлено: " + ex.Message, true); }
            }
            if (call)
            {
                try { await Task.Run(() => Alerts.Call(cfg.ComPort, cfg.Phones)); }
                catch (Exception ex) { Log.Write("ERROR звонок не прошёл: " + ex.Message); Say("Звонок не прошёл: " + ex.Message, true); }
            }
            return sent;
        }
    }

    // ===================================================================== оповещения

    // Способы оповещения: письмо через SMTP, SMS и звонок через GSM-модем.
    internal static class Alerts
    {
        /// <summary>
        /// Письмо через SMTP. Основной путь — системный curl.exe (есть в Windows 10 1803+ и 11): встроенный в .NET
        /// SmtpClient на части компьютеров обрывает TLS-соединение с Gmail («Сервер нарушил протокол»), а curl
        /// работает через ту же системную защиту соединений Windows (Schannel) без этой ошибки. Нет curl.exe —
        /// отправляем через SmtpClient. Порт 587 — STARTTLS, 465 — сразу TLS (его умеет только curl).
        /// </summary>
        // Бросает исключение при любой проблеме — его ловит вызывающий (Watcher.Alert или кнопка «Тестовое письмо»).
        public static async Task MailAsync(Config cfg, string subject, string body)
        {
            // Защита от пустых настроек — внятная ошибка вместо непонятной от SMTP-сервера.
            if (cfg.MailTo.Count == 0) throw new InvalidOperationException("не указано, кому писать");
            if (string.IsNullOrEmpty(cfg.SmtpUser) || string.IsNullOrEmpty(cfg.SmtpPassword))
                throw new InvalidOperationException("не указан логин или пароль почты");
            var curl = CurlPath();
            if (curl != null)
            {
                // curl — отдельная программа; ждём её в пуле потоков, чтобы не держать вызывающий поток.
                await Task.Run(() => MailCurl(curl, cfg, subject, body));
                return;
            }
            // Запасной путь — встроенный SmtpClient (только STARTTLS, порт 587).
            // MailMessage и SmtpClient — освобождаемые ресурсы; using закроет их после отправки.
            using (var msg = new MailMessage())
            using (var smtp = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort))
            {
                // Отправитель — тот же ящик, под которым входим: Gmail чужой адрес подменит на свой, Яндекс — отклонит.
                // «SiteWatch» — отображаемое имя.
                msg.From = new MailAddress(cfg.SmtpUser, "SiteWatch");
                // Все получатели — в поле «Кому».
                foreach (var to in cfg.MailTo) msg.To.Add(to);
                msg.Subject = subject;
                msg.Body = body;
                // Тема и текст в UTF-8 — иначе кириллица может превратиться в «кракозябры».
                msg.SubjectEncoding = msg.BodyEncoding = Encoding.UTF8;
                // EnableSsl — включить STARTTLS: соединение начинается открытым и переходит в шифрованное.
                smtp.EnableSsl = true;
                // 30 секунд на всю отправку.
                smtp.Timeout = 30000;
                // Логин и пароль приложения.
                smtp.Credentials = new NetworkCredential(cfg.SmtpUser, cfg.SmtpPassword);
                // Асинхронная отправка — вызывающий поток не блокируется.
                await smtp.SendMailAsync(msg);
            }
        }

        // Путь к curl.exe из Windows или null, если его нет (Windows старше 10 1803).
        private static string CurlPath()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            return File.Exists(path) ? path : null;
        }

        // Отправка через curl.exe: письмо целиком пишется во временный файл, curl передаёт его серверу.
        // Логин и пароль идут curl через стандартный ввод (--config -), а не в командной строке —
        // так их не видно в списке процессов. Блокирующий метод — вызывать через Task.Run.
        private static void MailCurl(string curl, Config cfg, string subject, string body)
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file, Encoding.UTF8.GetBytes(BuildMessage(cfg.SmtpUser, cfg.MailTo, subject, body)));

                // 465 — защищённое соединение сразу (smtps://), иначе — обычное с переходом в TLS (STARTTLS, --ssl-reqd).
                var args = new List<string>
                {
                    "--silent", "--show-error", "--ssl-reqd", "--max-time", "60",
                    "--url", (cfg.SmtpPort == 465 ? "smtps://" : "smtp://") + cfg.SmtpHost + ":" + cfg.SmtpPort,
                    "--mail-from", cfg.SmtpUser,
                };
                foreach (var to in cfg.MailTo) { args.Add("--mail-rcpt"); args.Add(to); }
                args.AddRange(new[] { "--upload-file", file, "--config", "-" });

                var psi = new ProcessStartInfo(curl, string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    // Вывод читаем параллельно, чтобы curl не застрял на заполненном буфере.
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEndAsync();
                    // Строка конфигурации curl: user = "логин:пароль" (кавычки и обратные косые черты экранируются).
                    var credentials = (cfg.SmtpUser + ":" + cfg.SmtpPassword).Replace("\\", "\\\\").Replace("\"", "\\\"");
                    p.StandardInput.Write("user = \"" + credentials + "\"\n");
                    p.StandardInput.Close();
                    if (!p.WaitForExit(90000))
                    {
                        try { p.Kill(); } catch { }
                        throw new InvalidOperationException("сервер почты не ответил за 90 с");
                    }
                    output.Wait(5000);
                    err.Wait(5000);
                    if (p.ExitCode != 0) throw new InvalidOperationException(CurlError(p.ExitCode, err.IsCompleted ? err.Result.Trim() : ""));
                }
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        // Коды завершения curl — по-человечески; в скобках — текст самого curl.
        private static string CurlError(int code, string raw)
        {
            string text;
            switch (code)
            {
                case 67: text = "сервер не принял логин или пароль"; break;
                case 6: text = "сервер почты не найден (DNS)"; break;
                case 7: text = "не удалось подключиться к серверу почты"; break;
                case 28: text = "сервер почты не ответил вовремя"; break;
                case 35: case 60: text = "ошибка защищённого соединения с сервером почты"; break;
                case 55: case 56: text = "соединение с сервером почты оборвалось"; break;
                case 64: text = "сервер почты не поддерживает защищённое соединение"; break;
                default: text = "curl завершился с кодом " + code; break;
            }
            return raw.Length == 0 ? text : text + " (" + raw + ")";
        }

        // Письмо в формате RFC 5322: заголовки с кириллицей — в UTF-8/base64 (=?UTF-8?B?…?=), текст — base64, строки через CRLF.
        private static string BuildMessage(string from, List<string> to, string subject, string body)
        {
            // Дата вида «Tue, 20 Oct 2026 21:03:00 +0300».
            var date = DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);
            date = date.Remove(date.Length - 3, 1);
            // Переводы строк в тексте — единообразно CRLF.
            var text = body.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var sb = new StringBuilder();
            sb.Append("From: ").Append(EncodeWord("SiteWatch")).Append(" <").Append(from).Append(">\r\n");
            sb.Append("To: ").Append(string.Join(", ", to)).Append("\r\n");
            sb.Append("Subject: ").Append(EncodeWord(subject)).Append("\r\n");
            sb.Append("Date: ").Append(date).Append("\r\n");
            sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append("@sitewatch>\r\n");
            sb.Append("MIME-Version: 1.0\r\n");
            sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n");
            sb.Append("\r\n");
            sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(text), Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
            return sb.ToString();
        }

        // Заголовок кусками по ≤45 байт UTF-8 (=?UTF-8?B?…?=), не разрывая символы; куски — на отдельных строках.
        private static string EncodeWord(string s)
        {
            var words = new List<string>();
            var chunk = new StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                // Суррогатная пара (эмодзи) — один символ из двух char, её не разрываем.
                var ch = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? s.Substring(i++, 2) : s[i].ToString();
                if (chunk.Length > 0 && Encoding.UTF8.GetByteCount(chunk.ToString() + ch) > 45)
                {
                    words.Add(chunk.ToString());
                    chunk.Clear();
                }
                chunk.Append(ch);
            }
            if (chunk.Length > 0) words.Add(chunk.ToString());
            return string.Join("\r\n ", words.Select(w => "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(w)) + "?="));
        }

        // Понятный текст ошибки письма: вся цепочка причин, у SMTP — ещё и код ответа сервера.
        // Например: «Сбой при отправке письма → Не удалось подключиться к удалённому серверу».
        public static string Explain(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                var msg = e.Message.Trim();
                var smtp = e as SmtpException;
                if (smtp != null && smtp.StatusCode != SmtpStatusCode.GeneralFailure) msg += " [" + smtp.StatusCode + "]";
                if (msg.Length > 0 && !parts.Contains(msg)) parts.Add(msg);
            }
            return string.Join(" → ", parts);
        }

        // ----------------------------------------------------------------- GSM-модем

        // Модем один на всю программу — тревоги и тестовые кнопки работают с ним строго по очереди.
        private static readonly object ModemLock = new object();
        // Номер «склейки» для SMS из нескольких частей — случайный, чтобы телефон не перепутал разные сообщения.
        private static readonly Random Rnd = new Random();

        /// <summary>
        /// SMS на все номера через GSM-модем (AT-команды по COM-порту, PDU-режим, кодировка UCS-2 — кириллица).
        /// Длинный текст уходит частями, телефон показывает его одним сообщением. Блокирующий метод — вызывать через Task.Run.
        /// Ошибка на одном номере не мешает остальным; в конце — исключение со списком номеров, куда не ушло.
        /// </summary>
        public static void Sms(string comPort, List<string> phones, string text)
        {
            lock (ModemLock)
            using (var port = OpenPort(comPort))
            {
                Prepare(port);
                // Модем мог только что включиться и ещё ищет сеть — ждём регистрацию до 30 с.
                if (!WaitNetwork(port, 30)) Log.Write("Модем: нет регистрации в сети, всё равно пробую отправить SMS");
                // PDU-режим: так SMS уходит в UCS-2 и длинный текст можно отправить частями.
                At(port, "AT+CMGF=0", true);
                var errors = new List<string>();
                foreach (var phone in phones)
                {
                    try
                    {
                        var parts = SmsPdu.Encode(phone, text, (byte)Rnd.Next(256));
                        foreach (var pdu in parts) SendPdu(port, pdu);
                        Log.Write("SMS → " + phone + (parts.Count > 1 ? " (" + parts.Count + " части)" : ""));
                    }
                    catch (Exception ex)
                    {
                        errors.Add(phone + ": " + ex.Message);
                        Log.Write("ERROR SMS → " + phone + ": " + ex.Message);
                    }
                }
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
            }
        }

        // Одна часть SMS: AT+CMGS=<длина> → приглашение «>» → PDU и Ctrl+Z → «+CMGS: n» и OK.
        private static void SendPdu(SerialPort port, string pdu)
        {
            // Длина — в байтах и без первого байта PDU (адрес SMS-центра «00» = взять из SIM-карты).
            var length = pdu.Length / 2 - 1;
            port.DiscardInBuffer();
            port.Write("AT+CMGS=" + length + "\r");
            try { WaitFor(port, ">", 10); }
            catch
            {
                // Приглашения не дождались — Esc отменяет ввод, чтобы модем не ждал текст вечно.
                try { port.Write("\x1B"); } catch { }
                throw;
            }
            port.Write(pdu + "\x1A");
            // Сеть может подтверждать SMS до минуты.
            var answer = WaitFor(port, "OK", 60);
            if (answer.IndexOf("+CMGS", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("модем не подтвердил отправку (" + Clean(answer) + ")");
        }

        /// <summary>
        /// Звонок на все номера через GSM-модем: ATD номер; — вызов, до 30 с ожидания, ATH — сброс.
        /// Трубку брать не нужно, звонок и есть сигнал; сбросили или занято — модем сразу переходит к следующему номеру.
        /// Блокирующий метод (до ~32 с на номер) — вызывать через Task.Run.
        /// </summary>
        public static void Call(string comPort, List<string> phones)
        {
            lock (ModemLock)
            using (var port = OpenPort(comPort))
            {
                Prepare(port);
                WaitNetwork(port, 30);
                // OK на ATD — сразу, не дожидаясь, пока снимут трубку.
                At(port, "AT+COLP=0", false);
                var errors = new List<string>();
                foreach (var raw in phones)
                {
                    var phone = SmsPdu.Clean(raw);
                    Log.Write("Звоню " + phone);
                    try
                    {
                        // ATD<номер>; — голосовой вызов (точка с запятой = голос, без неё был бы звонок для данных).
                        At(port, "ATD" + phone + ";", true);
                        var end = WaitCallEnd(port, 30);
                        if (end.Length > 0) Log.Write("Звонок " + phone + ": " + end);
                    }
                    catch (Exception ex) { errors.Add(phone + ": " + ex.Message); }
                    // В любом случае кладём трубку и даём модему 2 с передышки перед следующим номером.
                    finally { try { At(port, "ATH", false); } catch { } Thread.Sleep(2000); }
                }
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
            }
        }

        // Ждать конца вызова до seconds секунд. Сбросили, занято, не дозвонились — вернуть это слово; иначе пустую строку.
        private static string WaitCallEnd(SerialPort port, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                string line;
                try { line = port.ReadLine().Trim(); }
                catch (TimeoutException) { continue; }
                if (line == "NO CARRIER" || line == "BUSY" || line == "NO ANSWER" || line == "NO DIALTONE") return line;
            }
            return "";
        }

        /// <summary>
        /// «Найти модем»: опросить COM-порты. Для каждого — пара { порт, что на нём }; модем — где ответили на AT,
        /// строка тогда начинается со слова «модем». Порт, который долго не открывается (бывает у Bluetooth), — не дольше 8 с.
        /// </summary>
        public static List<string[]> Probe(string[] ports)
        {
            var result = new List<string[]>();
            foreach (var name in ports)
            {
                var p = name;
                var task = Task.Run(() => ProbeOne(p));
                result.Add(new[] { p, task.Wait(TimeSpan.FromSeconds(8)) ? task.Result : "не отвечает" });
            }
            return result;
        }

        // Один порт: AT → модель (AT+CGMM), SIM-карта (AT+CPIN?), регистрация в сети. Без замка ModemLock:
        // если модем сейчас звонит, порт просто окажется занят. Исключения не выпускает.
        private static string ProbeOne(string name)
        {
            try
            {
                using (var port = OpenPort(name))
                {
                    port.ReadTimeout = 1000;
                    try { At(port, "AT", true); }
                    catch (Exception)
                    {
                        Thread.Sleep(300);
                        try { At(port, "AT", true); } catch (Exception) { return "не модем (нет ответа на AT)"; }
                    }
                    At(port, "ATE0", false);
                    var model = At(port, "AT+CGMM", false);
                    var pin = At(port, "AT+CPIN?", false);
                    var sim = pin.IndexOf("READY", StringComparison.OrdinalIgnoreCase) >= 0 ? "SIM готова" : "SIM: " + (pin.Length > 0 ? pin : "нет ответа");
                    return "модем " + (model.Length > 0 ? model : "(модель не назвал)") + ", " + sim + ", " + (Registered(port) ? "сеть есть" : "сети нет");
                }
            }
            catch (UnauthorizedAccessException) { return "занят другой программой"; }
            catch (Exception ex) { return "ошибка: " + ex.Message; }
        }

        // Открыть COM-порт модема: 115200 бод; ответы заканчиваются \r; таймауты чтения и записи по 3 с;
        // DTR и RTS включены — многие модемы без них молчат. Не открылся — порт закрываем и выпускаем исключение.
        private static SerialPort OpenPort(string name)
        {
            var port = new SerialPort(name, 115200) { NewLine = "\r", ReadTimeout = 3000, WriteTimeout = 3000, DtrEnable = true, RtsEnable = true, Encoding = Encoding.ASCII };
            try { port.Open(); }
            catch { port.Dispose(); throw; }
            return port;
        }

        // Подготовка: модем на связи, эхо выключено, ошибки — словами (AT+CMEE=2), SIM-карта готова.
        private static void Prepare(SerialPort port)
        {
            // Первый AT после открытия порта иногда теряется (модем подстраивает скорость) — даём вторую попытку.
            try { At(port, "AT", true); }
            catch (InvalidOperationException) { Thread.Sleep(500); At(port, "AT", true); }
            At(port, "ATE0", false);
            At(port, "AT+CMEE=2", false);
            var pin = At(port, "AT+CPIN?", false);
            if (pin.IndexOf("READY", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("SIM-карта не готова: " + (pin.Length > 0 ? pin : "нет ответа") + " — вставлена ли SIM, не стоит ли на ней PIN-код");
        }

        // Ждать регистрации в сети до seconds секунд (опрос раз в 2 с). true — зарегистрирован.
        private static bool WaitNetwork(SerialPort port, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (Registered(port)) return true;
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(2000);
            }
        }

        // Зарегистрирован ли модем в сети: «+CREG: 0,1» — домашняя сеть, «0,5» — роуминг.
        // 4G-модемы могут сообщать регистрацию только в +CEREG — проверяем оба.
        private static bool Registered(SerialPort port)
        {
            return Stat(At(port, "AT+CREG?", false), "+CREG:") || Stat(At(port, "AT+CEREG?", false), "+CEREG:");
        }

        private static bool Stat(string answer, string prefix)
        {
            var i = answer.IndexOf(prefix, StringComparison.Ordinal);
            if (i < 0) return false;
            var parts = answer.Substring(i + prefix.Length).Split(',');
            if (parts.Length < 2) return false;
            var stat = parts[1].Trim().Trim('"');
            return stat == "1" || stat == "5";
        }

        // Ждать в ответе модема token (например, «>» или «OK») до seconds секунд. ERROR — исключение с текстом модема.
        private static string WaitFor(SerialPort port, string token, int seconds)
        {
            var got = new StringBuilder();
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                var chunk = port.ReadExisting();
                if (chunk.Length == 0) { Thread.Sleep(100); continue; }
                got.Append(chunk);
                var s = got.ToString();
                if (s.Contains("ERROR")) throw new InvalidOperationException("модем: " + Clean(s));
                if (s.Contains(token)) return s;
            }
            throw new InvalidOperationException("модем не ответил вовремя (" + Clean(got.ToString()) + ")");
        }

        // Ответ модема одной строкой: без пустых строк и переводов строки.
        private static string Clean(string s)
        {
            return string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));
        }

        // Отправить AT-команду и собрать ответ до «OK». mustOk = true — без OK бросить исключение;
        // false — вернуть то, что успели прочитать.
        private static string At(SerialPort port, string cmd, bool mustOk)
        {
            // Выбрасываем «мусор» во входном буфере — остатки прошлых ответов и событий модема (RING и т. п.).
            port.DiscardInBuffer();
            // Команда + \r (NewLine порта).
            port.WriteLine(cmd);
            // Сюда копим строки ответа (кроме эха и OK).
            var answer = new StringBuilder();
            // На весь ответ — не больше 5 секунд.
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                string line;
                // Читаем строку; если за время ReadTimeout ничего не пришло — прекращаем ждать.
                try { line = port.ReadLine().Trim(); }
                catch (TimeoutException) { break; }
                // Пустые строки (модем любит \r\n) и эхо нашей же команды пропускаем.
                if (line.Length == 0 || line == cmd) continue;
                // OK — команда выполнена, возвращаем накопленный ответ.
                if (line == "OK") return answer.ToString().Trim();
                // Любую другую строку запоминаем.
                answer.Append(line).Append(' ');
                // Явные отказы — ждать дальше нечего: ERROR, +CME ERROR (SIM-карта, сеть), +CMS ERROR (SMS),
                // NO CARRIER (не соединилось), BUSY (занято).
                if (line.StartsWith("ERROR") || line.StartsWith("+CME") || line.StartsWith("+CMS") || line == "NO CARRIER" || line == "BUSY") break;
            }
            // OK так и не пришёл: если он был обязателен — исключение с тем, что модем всё-таки ответил.
            if (mustOk) throw new InvalidOperationException("модем не ответил OK на " + cmd + " (" + answer.ToString().Trim() + ")");
            // Иначе возвращаем то, что есть.
            return answer.ToString().Trim();
        }
    }

    // ===================================================================== SMS (PDU)

    /// <summary>
    /// Сборка SMS в формате PDU (3GPP TS 23.040, SMS-SUBMIT) — в UCS-2, чтобы шла кириллица.
    /// Одна SMS — до 70 символов; длиннее — части по 67 с заголовком склейки (телефон соберёт их в одно сообщение).
    /// </summary>
    internal static class SmsPdu
    {
        // Больше 4 частей (268 символов) не шлём — остальное обрезается многоточием.
        private const int MaxParts = 4;

        // Номер без пробелов, скобок и дефисов; «+» в начале сохраняется: «+7 (989) 700-48-20» → «+79897004820».
        public static string Clean(string phone)
        {
            var s = (phone ?? "").Trim();
            return (s.StartsWith("+") ? "+" : "") + new string(s.Where(char.IsDigit).ToArray());
        }

        // Готовые PDU (шестнадцатеричные строки) — по одной на каждую часть SMS.
        public static List<string> Encode(string phone, string text, byte reference)
        {
            var number = Clean(phone);
            var digits = number.TrimStart('+');
            if (digits.Length == 0) throw new InvalidOperationException("пустой номер телефона");

            // Режем текст на части, не разрывая суррогатные пары (эмодзи и т. п.).
            var chunks = new List<string>();
            if (text.Length <= 70) chunks.Add(text);
            else
            {
                var pos = 0;
                while (pos < text.Length && chunks.Count < MaxParts)
                {
                    var len = Math.Min(67, text.Length - pos);
                    if (len < text.Length - pos && char.IsHighSurrogate(text[pos + len - 1])) len--;
                    chunks.Add(text.Substring(pos, len));
                    pos += len;
                }
                if (pos < text.Length)
                {
                    var last = chunks[chunks.Count - 1];
                    chunks[chunks.Count - 1] = last.Substring(0, last.Length - 1) + "…";
                }
            }

            var result = new List<string>();
            var multi = chunks.Count > 1;
            for (var i = 0; i < chunks.Count; i++)
            {
                // Данные: [заголовок склейки 05 00 03 <номер склейки> <всего частей> <эта часть>] + текст в UCS-2.
                var ud = new List<byte>();
                if (multi) ud.AddRange(new byte[] { 0x05, 0x00, 0x03, reference, (byte)chunks.Count, (byte)(i + 1) });
                ud.AddRange(Encoding.BigEndianUnicode.GetBytes(chunks[i]));

                var sb = new StringBuilder();
                sb.Append("00");                                // адрес SMS-центра не указываем — модем возьмёт его из SIM-карты
                sb.Append(multi ? "41" : "01");                 // SMS-SUBMIT; +0x40 — в данных есть заголовок склейки
                sb.Append("00");                                // номер сообщения назначит модем
                sb.Append(digits.Length.ToString("X2"));        // длина номера получателя в цифрах
                sb.Append(number.StartsWith("+") ? "91" : "81"); // 91 — международный формат (+7…), 81 — как набран (8…)
                sb.Append(Swap(digits));                        // цифры парами наоборот, нечётная — с F на конце
                sb.Append("00");                                // протокол — обычная SMS
                sb.Append("08");                                // кодировка UCS-2
                sb.Append(ud.Count.ToString("X2"));             // длина данных в байтах
                foreach (var b in ud) sb.Append(b.ToString("X2"));
                result.Add(sb.ToString());
            }
            return result;
        }

        // «79897004820» → «9798074028F0»: в PDU цифры номера идут парами, переставленными местами.
        private static string Swap(string digits)
        {
            if (digits.Length % 2 == 1) digits += "F";
            var sb = new StringBuilder();
            for (var i = 0; i < digits.Length; i += 2) sb.Append(digits[i + 1]).Append(digits[i]);
            return sb.ToString();
        }
    }

    // ===================================================================== значки

    // Значки: значок программы (из Base64 в конце класса) и цветные кружки для трея, рисуемые на лету.
    internal static class Icons
    {
        // Значок окна: раскодируем Base64 в байты .ico и загружаем. Создаётся один раз, при первом обращении к Icons.
        public static readonly Icon App = new Icon(new MemoryStream(Convert.FromBase64String(AppIconBase64)));
        // Кэш нарисованных кружков: цвет → значок. Каждый цвет рисуется один раз.
        private static readonly Dictionary<Color, Icon> Cache = new Dictionary<Color, Icon>();

        // Цвета состояний (для трея и для плашки в окне):
        // зелёный — всё работает;
        public static readonly Color Green = Color.FromArgb(34, 160, 80);
        // жёлтый — есть сбои, но порог ещё не достигнут;
        public static readonly Color Yellow = Color.FromArgb(230, 160, 20);
        // красный — сайт признан упавшим;
        public static readonly Color Red = Color.FromArgb(215, 40, 40);
        // фиолетовый — нет интернета у этого компьютера;
        public static readonly Color Violet = Color.FromArgb(120, 90, 200);
        // синий — ещё не проверяли / идёт первая проверка.
        public static readonly Color Blue = Color.FromArgb(90, 140, 210);

        /// <summary>Кружок цвета состояния для трея.</summary>
        // Замок на кэш — на случай вызова из разных потоков.
        public static Icon Dot(Color c)
        {
            lock (Cache)
            {
                Icon icon;
                // Этот цвет уже рисовали — отдаём готовый значок.
                if (Cache.TryGetValue(c, out icon)) return icon;
                // Холст 32×32; Windows сама уменьшит его до размера значка в трее.
                using (var bmp = new Bitmap(32, 32))
                {
                    // Графика, кисть цвета состояния и белое перо для обводки — всё освобождается после рисования.
                    using (var g = Graphics.FromImage(bmp))
                    using (var b = new SolidBrush(c))
                    using (var p = new Pen(Color.White, 2f))
                    {
                        // Сглаживание — ровный круг без «лесенки».
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        // Закрашенный круг с отступом 2 пикселя от края.
                        g.FillEllipse(b, 2, 2, 28, 28);
                        // Белая обводка — кружок виден и на тёмной, и на цветной панели задач.
                        g.DrawEllipse(p, 2, 2, 28, 28);
                    }
                    // Превращаем картинку в значок Windows. GetHicon создаёт системный дескриптор, который здесь
                    // никогда не освобождается (DestroyIcon). Это утечка, но значков всего пять и живут они до выхода.
                    icon = Icon.FromHandle(bmp.GetHicon()); // пять значков на всю жизнь программы
                }
                // Запоминаем в кэше.
                Cache[c] = icon;
                return icon;
            }
        }

        // Хранится прямо в коде, чтобы программа была одним exe-файлом без отдельных ресурсов.
        // Менять только целиком, заменив всю строку. Внутри — три картинки PNG, Windows выбирает подходящий размер.
        // Значок программы (ICO 16/32/48) в Base64.
        private const string AppIconBase64 = "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";
    }

    // ===================================================================== трей

    // «Приложение без главного окна»: ApplicationContext держит цикл сообщений, пока жив значок в трее,
    // даже когда окно скрыто. Связывает Watcher (данные), MainForm (окно) и NotifyIcon (значок).
    internal sealed class TrayApp : ApplicationContext
    {
        // Сторож — фоновые проверки.
        private readonly Watcher _watcher;
        // Главное окно. Создаётся один раз и потом только прячется и показывается.
        private readonly MainForm _form;
        // Значок в области уведомлений.
        private readonly NotifyIcon _tray;
        // Флаг «выходим»: после него никаких обращений к окну. volatile — его читает фоновый поток сигнала.
        private volatile bool _exiting;

        // hidden — запуск с --tray (окно не показывать); showSignal — сигнал от второй копии программы.
        public TrayApp(bool hidden, EventWaitHandle showSignal)
        {
            // Читаем настройки с диска.
            var cfg = Config.Load();
            // Сторож с этими настройками (поток ещё не запущен).
            _watcher = new Watcher(cfg);
            // Окно. Exit передаём, чтобы кнопка «Выйти из программы» в настройках шла через тот же выход с подтверждением.
            _form = new MainForm(_watcher, cfg, Exit);
            // Принудительно создаём дескриптор окна, хотя оно ещё не показано: без него BeginInvoke не работает,
            // а события сторожа могут прийти раньше, чем окно впервые откроют (при запуске в трей — может, и никогда).
            var handle = _form.Handle; // окно нужно сразу: через него события попадают в поток интерфейса

            // Меню по правому клику на значке.
            var menu = new ContextMenuStrip();
            // Показать окно.
            menu.Items.Add("Открыть", null, (s, e) => ShowForm());
            // Внеочередная проверка всех сайтов.
            menu.Items.Add("Проверить сейчас", null, (s, e) => _watcher.CheckNow());
            // Разделитель — чтобы «Выход» не нажали случайно.
            menu.Items.Add(new ToolStripSeparator());
            // Выход (с подтверждением).
            menu.Items.Add("Выход", null, (s, e) => Exit());

            // Значок: синий кружок «ещё проверяем», подсказка при наведении, меню; Visible = true — сразу появляется в трее.
            _tray = new NotifyIcon { Icon = Icons.Dot(Icons.Blue), Text = "SiteWatch", ContextMenuStrip = menu, Visible = true };
            // Левый клик по значку — открыть окно (правый — меню, оно обрабатывается само).
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };
            // Клик по всплывающему уведомлению — тоже открыть окно.
            _tray.BalloonTipClicked += (s, e) => ShowForm();

            // Сторож сообщил об изменениях → в потоке интерфейса обновляем значок и окно.
            _watcher.Changed += () => Ui(() => { UpdateTray(); _form.RefreshView(); });
            // Сторож просит уведомление → всплывашка на 8 с (на Windows 10/11 время задаёт система, параметр игнорируется);
            // значок уведомления — «ошибка» или «информация».
            _watcher.Notify += (text, problem) => Ui(() => _tray.ShowBalloonTip(8000, "SiteWatch", text, problem ? ToolTipIcon.Error : ToolTipIcon.Info));

            // Отдельный фоновый поток слушает сигнал от второй копии программы: пришёл — показать окно.
            // Ждём кусками по 1 с, чтобы регулярно проверять флаг выхода и не висеть вечно.
            new Thread(() =>
            {
                while (!_exiting) if (showSignal.WaitOne(1000) && !_exiting) Ui(ShowForm);
            // IsBackground — поток не держит процесс при выходе.
            }) { IsBackground = true }.Start();

            // Запрет сна (если включён в настройках). Вызывается в потоке интерфейса — он живёт всю работу программы.
            Native.KeepAwake(cfg.KeepAwake);
            // Запускаем фоновые проверки; первый круг начнётся сразу.
            _watcher.Start();
            // Обычный запуск (не автозапуск) — сразу показать окно.
            if (!hidden) ShowForm();
        }

        // Выполнить действие в потоке интерфейса. Элементы WinForms можно трогать только из потока, который их создал,
        // поэтому всё из фоновых потоков идёт через BeginInvoke: он ставит действие в очередь сообщений окна и не ждёт.
        private void Ui(Action a)
        {
            // Выходим или окно уже уничтожено — отправлять некуда, молча пропускаем.
            if (_exiting || _form.IsDisposed || !_form.IsHandleCreated) return;
            try { _form.BeginInvoke(a); }
            // Окно успело закрыться между проверкой и вызовом — тоже не страшно.
            catch (InvalidOperationException) { }
        }

        // Показать окно и вывести его на передний план.
        private void ShowForm()
        {
            // Сделать видимым (если было спрятано в трей).
            _form.Show();
            // Если было свёрнуто на панель задач — развернуть в обычный размер.
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            // Активировать — окно получает фокус. (Windows может вместо этого лишь мигнуть кнопкой на панели задач —
            // так она защищает от «выскакивающих» окон.)
            _form.Activate();
        }

        // Цвет значка и подсказка по общему состоянию. Порядок проверок — это приоритет:
        // нет интернета > упал > сбои > всё хорошо > ждём.
        private void UpdateTray()
        {
            // Снимок состояний (копии — безопасно читать).
            var list = _watcher.Snapshot();
            Color c; string text;
            // Нет интернета у нас — остальное неважно, сайты сейчас не проверяются.
            if (!_watcher.InternetOk) { c = Icons.Violet; text = "нет интернета"; }
            // Хоть один сайт признан упавшим — красный, в подсказке их число.
            else if (list.Any(s => s.IsDown)) { c = Icons.Red; text = "не работает: " + list.Count(s => s.IsDown); }
            // Есть сбои, но порог ещё не достигнут (опечатки в адресах не в счёт) — жёлтый.
            else if (list.Any(s => s.Ok == false && !s.Muted)) { c = Icons.Yellow; text = "есть сбои"; }
            // Хоть что-то открылось и аварий нет — зелёный.
            else if (list.Any(s => s.Ok == true)) { c = Icons.Green; text = "всё работает (" + list.Count + ")"; }
            // Ни одной проверки ещё не было (или список пуст) — синий.
            else { c = Icons.Blue; text = "проверка…"; }
            // Меняем картинку (из кэша — без новых утечек).
            _tray.Icon = Icons.Dot(c);
            // Подсказка при наведении. Длиннее 63 символов — исключение, поэтому текст короткий.
            _tray.Text = "SiteWatch: " + text; // в .NET Framework не длиннее 63 символов
        }

        // Выход из программы — через подтверждение: после выхода никто не узнает о падениях.
        private void Exit()
        {
            // Сначала показать окно: диалог привязан к нему и не спрячется за другими окнами.
            ShowForm();
            // Вопрос «Выйти?». Кнопка по умолчанию — «Нет» (Button2): случайный Enter не закроет сторожа.
            if (MessageBox.Show(_form, "Выйти? Пока программа закрыта, о падении сайтов никто не узнает.", "SiteWatch",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            // Дальше — только если ответили «Да». Ставим флаг: больше никаких обращений к окну из фоновых потоков.
            _exiting = true;
            // Останавливаем сторожа.
            _watcher.Stop();
            // Снимаем запрет сна.
            Native.KeepAwake(false);
            // Убираем значок из трея сразу — иначе он «висит», пока над ним не проведут мышью.
            _tray.Visible = false;
            // Разрешаем окну закрыться по-настоящему, а не спрятаться (см. OnFormClosing).
            _form.AllowClose = true;
            // Закрываем окно.
            _form.Close();
            // Завершаем цикл сообщений — Application.Run в Main вернёт управление.
            ExitThread();
        }
    }

    // ===================================================================== окно

    // Главное окно: цветная плашка-заголовок сверху и две вкладки — «Состояние» (таблица сайтов) и «Настройки».
    // Закрытие крестиком не завершает программу, а прячет окно в трей.
    internal sealed class MainForm : Form
    {
        // Источник данных для таблицы.
        private readonly Watcher _watcher;
        // Текущие (последние сохранённые) настройки.
        private Config _cfg;
        // Выход из программы (метод TrayApp.Exit).
        private readonly Action _exit;
        // Плашка-заголовок: прижата к верху, высота 42, белый текст с отступом слева 12, по центру по вертикали.
        // Цвет фона задаётся по состоянию (RefreshView).
        private readonly Label _header = new Label { Dock = DockStyle.Top, Height = 42, ForeColor = Color.White, Padding = new Padding(12, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        // Таблица сайтов: занимает всё оставшееся место; режим «таблица с колонками», выделение всей строки, линии сетки.
        private readonly ListView _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        // Многострочные поля для списков: сайты,
        private readonly TextBox _sites = Multi();
        // адреса для писем,
        private readonly TextBox _mail = Multi();
        // телефоны.
        private readonly TextBox _phones = Multi();
        // Логин ящика-отправителя.
        private readonly TextBox _user = new TextBox { Width = 260 };
        // Пароль приложения — показывается точками.
        private readonly TextBox _password = new TextBox { Width = 260, UseSystemPasswordChar = true };
        // COM-порт: выбор из найденных портов или ввод вручную.
        private readonly ComboBox _port = new ComboBox { Width = 120 };
        // Флажок «не спать».
        private readonly CheckBox _awake = new CheckBox { Text = "Не давать компьютеру засыпать", AutoSize = true };
        // Флажок автозапуска. Его состояние хранится не в config.txt, а в реестре.
        private readonly CheckBox _autostart = new CheckBox { Text = "Запускать вместе с Windows", AutoSize = true };
        // Таймер интерфейса: раз в 5 с обновляет таблицу, чтобы «проверен … назад» не застывало.
        // Полное имя — потому что Timer есть и в System.Threading.
        private readonly System.Windows.Forms.Timer _ticker = new System.Windows.Forms.Timer { Interval = 5000 };

        // true — закрытие разрешено по-настоящему (ставит TrayApp.Exit); false — крестик прячет окно.
        public bool AllowClose;

        // Собираем окно.
        public MainForm(Watcher watcher, Config cfg, Action exit)
        {
            // Запоминаем сторожа, настройки и действие «выход».
            _watcher = watcher; _cfg = cfg; _exit = exit;
            // Заголовок окна с версией.
            Text = "SiteWatch " + Program.Version;
            // Значок окна и кнопки на панели задач.
            Icon = Icons.App;
            // Начальный размер: узкое высокое окно, в котором вкладка «Настройки» видна целиком, без прокрутки.
            // 700 по ширине — это и есть минимум; в Windows 10/11 сюда входят невидимые рамки по ~7 пикселей.
            Size = new Size(700, 770);
            // Меньше не сжать — иначе всё налезет друг на друга.
            MinimumSize = new Size(700, 450);
            // Открывается по центру экрана.
            StartPosition = FormStartPosition.CenterScreen;
            // Шрифт для всего окна — дочерние элементы наследуют его, если свой не задан.
            Font = new Font("Segoe UI", 9.5f);
            // Плашке — крупный жирный.
            _header.Font = new Font("Segoe UI", 12f, FontStyle.Bold);

            // Вкладки занимают всё место под плашкой.
            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(StatusTab());
            tabs.TabPages.Add(SettingsTab());
            // Порядок добавления важен: WinForms раскладывает пристыкованные (Dock) элементы с конца коллекции.
            // Плашка добавлена последней → раскладывается первой и занимает верх, вкладки (Fill) получают остаток.
            Controls.Add(tabs);
            Controls.Add(_header);

            // Тик таймера: обновлять, только когда окно видно — в трее перерисовывать незачем.
            _ticker.Tick += (s, e) => { if (Visible) RefreshView(); };
            // Запускаем таймер.
            _ticker.Start();
            // Заполнить поля настроек текущими значениями.
            LoadFields();
            // И сразу показать таблицу.
            RefreshView();
        }

        // Заготовка многострочного поля: Enter переносит строку (AcceptsReturn), вертикальная прокрутка, 380×90.
        private static TextBox Multi()
        {
            return new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 380, Height = 90, AcceptsReturn = true };
        }

        // Вкладка «Состояние»: таблица сайтов и панель кнопок снизу.
        private TabPage StatusTab()
        {
            var page = new TabPage("Состояние") { Padding = new Padding(8) };
            // Колонки таблицы в виде «название|ширина в пикселях» — компактно задать шесть колонок.
            // В сумме 650 — помещаются в окно шириной 700 без горизонтальной прокрутки.
            foreach (var c in new[] { "Сайт|150", "Состояние|110", "Ответ|180", "мс|50", "Проверен|90", "Сбои с|70" })
            {
                // Делим на название и ширину.
                var p = c.Split('|');
                // Добавляем колонку.
                _list.Columns.Add(p[0], int.Parse(p[1]));
            }
            // Двойной клик по строке — открыть сайт в браузере по умолчанию. Адрес берём из SiteState в Tag строки.
            _list.DoubleClick += (s, e) =>
            {
                if (_list.SelectedItems.Count > 0) try { Process.Start(((SiteState)_list.SelectedItems[0].Tag).Url); } catch { }
            };
            // Нижняя панель с кнопками; FlowLayoutPanel раскладывает их в ряд слева направо.
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
            // «Проверить сейчас» — разбудить сторожа.
            bar.Controls.Add(Btn("Проверить сейчас", () => _watcher.CheckNow()));
            // «Журнал» — открыть в Проводнике папку с журналом (там же и настройки).
            bar.Controls.Add(Btn("Журнал", () => { try { Process.Start(Log.Folder); } catch { } }));
            // Опять порядок: таблица (Fill) добавлена первой, панель (Bottom) — второй,
            // поэтому панель занимает низ, а таблица — всё остальное.
            page.Controls.Add(_list);
            page.Controls.Add(bar);
            return page;
        }

        // Вкладка «Настройки»: таблица «подпись — поле» в две колонки и кнопки.
        private TabPage SettingsTab()
        {
            // AutoScroll — при маленьком окне появится прокрутка.
            var page = new TabPage("Настройки") { Padding = new Padding(12), AutoScroll = true };
            // Двухколоночная раскладка.
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            // Левая колонка (подписи) — 200 пикселей.
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            // Правая (поля) — вся остальная ширина.
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // Сайты — по адресу в строке; без https:// он допишется сам (см. Collect).
            Row(t, "Сайты (по одному в строке)", _sites);
            // Адреса получателей писем.
            Row(t, "Кому писать", _mail);
            // Ящик, с которого шлём (логин SMTP).
            Row(t, "Почта-отправитель (Gmail)", _user);
            // Пароль приложения Google — 16 букв, создаётся на myaccount.google.com/apppasswords
            // (нужна включённая двухэтапная аутентификация). Не обычный пароль от почты. Пробелы можно не убирать.
            Row(t, "Пароль приложения", _password);
            // Телефоны в международном формате: на них уходят SMS и звонки.
            Row(t, "Телефоны: SMS и звонок (+79…)", _phones);
            // COM-порты, найденные в системе, — в выпадающий список. Список обновляется при каждом раскрытии:
            // модем можно подключить, не перезапуская программу.
            RefreshPorts();
            _port.DropDown += (s, e) => RefreshPorts();
            // Строка выбора порта: список и кнопка «Найти модем» — опросит порты и выберет тот, где ответил модем.
            var portRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            portRow.Controls.Add(_port);
            portRow.Controls.Add(Btn("Найти модем", FindModem));
            Row(t, "COM-порт модема", portRow);
            // Флажки — без подписи слева, текст у них свой.
            Row(t, "", _awake);
            Row(t, "", _autostart);

            // Ряд кнопок; AutoSize — по размеру содержимого.
            var bar = new FlowLayoutPanel { AutoSize = true };
            // Сохранить настройки и сразу применить.
            bar.Controls.Add(Btn("Сохранить", Save));
            // Пробное письмо с тем, что введено сейчас (даже без сохранения).
            bar.Controls.Add(Btn("Тестовое письмо", TestMail));
            // Пробные SMS и звонок — на первый номер из списка.
            bar.Controls.Add(Btn("Тестовое SMS", TestSms));
            bar.Controls.Add(Btn("Тестовый звонок", TestCall));
            // Вернуть config.txt к значениям по умолчанию из кода (с подтверждением).
            bar.Controls.Add(Btn("Сбросить настройки", ResetConfig));
            // Тот же выход с подтверждением, что и в меню трея.
            bar.Controls.Add(Btn("Выйти из программы", () => _exit()));
            // Кнопки — отдельной строкой.
            Row(t, "", bar);
            // Серая справка внизу: как часто проверяем, когда «упал», где лежат настройки.
            // MaximumSize по ширине — текст переносится по словам.
            Row(t, "", new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0),
                Text = "Проверка раз в " + Program.IntervalSeconds + " с; упал = " + Program.FailThreshold + " неудачи подряд; " +
                       "пока лежит — напоминание раз в " + Program.ReminderMinutes + " мин. " +
                       "Модем (если указаны COM-порт и номера): сайт упал — SMS с причиной и звонок, напоминания — звонком, " +
                       "сайт поднялся — SMS; у компьютера пропал интернет — SMS. " +
                       "Настройки: " + Config.Folder,
            });
            // Кладём таблицу на вкладку.
            page.Controls.Add(t);
            return page;
        }

        // Добавить строку в таблицу настроек: подпись слева, элемент справа.
        private static void Row(TableLayoutPanel t, string label, Control c)
        {
            // Номер новой строки = текущее число строк; заодно увеличиваем счётчик.
            var row = t.RowCount++;
            // Подпись; отступ сверху 8 — чтобы стояла на одной линии с текстом поля.
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, row);
            // Отступы элемента сверху и снизу по 4.
            c.Margin = new Padding(0, 4, 0, 4);
            // Элемент — во вторую колонку той же строки.
            t.Controls.Add(c, 1, row);
        }

        // Создать кнопку с текстом и действием по нажатию; AutoSize — ширина по тексту.
        private static Button Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (s, e) => a();
            return b;
        }

        // Строки многострочного поля: без пробелов по краям, без пустых и без повторов.
        private static List<string> Lines(TextBox box)
        {
            return box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        }

        // Перенести текущие настройки (_cfg) в поля окна.
        private void LoadFields()
        {
            // Списки — по одному элементу в строке.
            _sites.Text = string.Join("\r\n", _cfg.Sites);
            _mail.Text = string.Join("\r\n", _cfg.MailTo);
            _phones.Text = string.Join("\r\n", _cfg.Phones);
            // Логин ящика.
            _user.Text = _cfg.SmtpUser;
            // Пароль — расшифрованным; на экране он всё равно скрыт точками.
            _password.Text = _cfg.SmtpPassword;
            // Порт — текстом (может быть и не из списка).
            _port.Text = _cfg.ComPort;
            // Флажок «не спать».
            _awake.Checked = _cfg.KeepAwake;
            // Автозапуск читаем из реестра — его могли поменять и снаружи.
            try { _autostart.Checked = Native.AutostartOn(); }
            // Нет доступа к реестру — оставляем флажок как есть.
            catch { }
        }

        // Собрать настройки из полей окна в новый объект Config (ещё не сохраняя).
        private Config Collect()
        {
            // Логин — без пробелов по краям.
            var user = _user.Text.Trim();
            // Сервер подбираем по адресу отправителя (gmail.com → smtp.gmail.com); для неизвестной почты — из текущих настроек.
            var host = Config.HostFor(user, _cfg.SmtpHost);
            // Пароль — как есть (в обычном пароле пробелы могут быть его частью)…
            var password = _password.Text;
            // …но Google показывает пароль приложения группами «abcd efgh ijkl mnop», и при копировании пробелы
            // попадают в поле. В самом пароле Google их нет (только 16 букв), поэтому для Gmail убираем все пробелы.
            if (host == Config.GmailHost) password = new string(password.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            var c = new Config
            {
                // Сайты: если схема не указана (litewms.ru) — дописываем https://.
                Sites = Lines(_sites).Select(u => u.Contains("://") ? u : "https://" + u).ToList(),
                // Получатели писем.
                MailTo = Lines(_mail),
                // Телефоны.
                Phones = Lines(_phones),
                // Сервер — подобранный выше; порт в окне не редактируется — переносим из текущих настроек (config.txt).
                SmtpHost = host,
                SmtpPort = _cfg.SmtpPort,
                // Логин и пароль — подготовленные выше.
                SmtpUser = user,
                SmtpPassword = password,
                // COM-порт — что выбрано или вписано.
                ComPort = _port.Text.Trim(),
                // Флажок «не спать».
                KeepAwake = _awake.Checked,
            };
            return c;
        }

        // Кнопка «Сохранить».
        private void Save()
        {
            // Собираем настройки из полей. Внимание: _cfg заменяется сразу, ещё до записи на диск.
            _cfg = Collect();
            try
            {
                // Пишем config.txt.
                _cfg.Save();
                // Включаем или выключаем автозапуск в реестре.
                Native.Autostart(_autostart.Checked);
            }
            // Не удалось (нет прав, диск недоступен) — сообщаем, сторожу ничего не передаём.
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не сохранилось: " + ex.Message, "SiteWatch");
                return;
            }
            // Применяем «не спать» сразу.
            Native.KeepAwake(_cfg.KeepAwake);
            // Отдаём сторожу новый список сайтов и адресов (история оставшихся сайтов сохранится).
            _watcher.Apply(_cfg);
            // И сразу проверяем — новые сайты не ждут целую минуту.
            _watcher.CheckNow();
            // Перечитываем поля: показать нормализованные значения (дописанные https://, убранные повторы).
            LoadFields();
            // Подтверждение.
            MessageBox.Show(this, "Сохранено.", "SiteWatch");
        }

        // Кнопка «Сбросить настройки»: config.txt перезаписывается значениями по умолчанию —
        // теми, что заданы в полях класса Config. Автозапуск (он в реестре) не трогается.
        private void ResetConfig()
        {
            // Подтверждение. Кнопка по умолчанию — «Нет» (Button2), чтобы не сбросить случайно.
            if (MessageBox.Show(this,
                    "Сбросить все настройки к значениям по умолчанию?\n\n" +
                    "Список сайтов, адреса, телефоны и COM-порт вернутся к исходным, " +
                    "пароль почты будет стёрт — его нужно будет ввести заново, иначе письма уходить не будут.",
                    "SiteWatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            // Новый Config — это и есть значения по умолчанию. Сразу пишем их в config.txt.
            var defaults = new Config();
            try { defaults.Save(); }
            // Не удалось записать (нет прав, файл занят) — сообщаем и ничего не меняем.
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не сбросилось: " + ex.Message, "SiteWatch");
                return;
            }

            // Файл записан — применяем умолчания так же, как при «Сохранить»:
            // запоминаем как текущие настройки,
            _cfg = defaults;
            // включаем или снимаем запрет сна,
            Native.KeepAwake(_cfg.KeepAwake);
            // отдаём сторожу исходный список сайтов и адресов,
            _watcher.Apply(_cfg);
            // и сразу проверяем.
            _watcher.CheckNow();
            // Показываем сброшенные значения в полях окна (пароль — пустой).
            LoadFields();
            // Пометка в журнале.
            Log.Write("Настройки сброшены к значениям по умолчанию");
            // Напоминание про пароль.
            MessageBox.Show(this, "Настройки сброшены. Введите пароль приложения и нажмите «Сохранить».", "SiteWatch");
        }

        // Кнопка «Тестовое письмо». async void — так устроены обработчики событий; все ошибки ловятся внутри.
        private async void TestMail()
        {
            // Берём то, что введено сейчас, — пароль можно проверить до сохранения.
            var c = Collect();
            try
            {
                // Отправляем пробное письмо; await — окно не зависает, пока идёт отправка.
                await Alerts.MailAsync(c, "SiteWatch: проверка", "Тестовое письмо с компьютера " + Environment.MachineName + ". Оповещения работают.");
                // Сервер принял письмо. Попадание во «Входящие» не гарантировано — отсюда подсказка про «Спам».
                MessageBox.Show(this, "Письмо отправлено. Если не пришло — проверьте «Спам».", "SiteWatch");
            }
            catch (Exception ex)
            {
                // Вся ошибка, со стеком, — в журнал: по ней видно, на каком шаге оборвалось.
                Log.Write("ERROR тестовое письмо (" + c.SmtpHost + ":" + c.SmtpPort + "): " + ex);
                // Подсказка о самой частой причине отказа — своя для каждой почты.
                var hint = c.SmtpHost == Config.GmailHost
                    ? "\nДля Gmail нужен пароль приложения (16 букв) с myaccount.google.com/apppasswords — " +
                      "обычный пароль от почты не подойдёт; двухэтапная аутентификация должна быть включена."
                    : c.SmtpHost == "smtp.yandex.ru"
                        ? "\nНужен пароль приложения Яндекса и включённый доступ почтовых программ (IMAP)."
                        : "\nПроверьте логин, пароль приложения и smtp_host / smtp_port в config.txt.";
                // Сервер, порт и вся цепочка причин — сразу видно, где оборвалось; плюс подсказка.
                MessageBox.Show(this, "Не отправилось через " + c.SmtpHost + ":" + c.SmtpPort + ":\n" + Alerts.Explain(ex) + "\n" + hint, "SiteWatch");
            }
        }

        // Кнопка «Тестовое SMS» — на первый номер из списка, через выбранный COM-порт (даже без сохранения).
        private async void TestSms()
        {
            var c = Collect();
            if (c.Phones.Count == 0 || c.ComPort.Length == 0) { MessageBox.Show(this, "Укажите номер телефона и COM-порт (кнопка «Найти модем»).", "SiteWatch"); return; }
            try
            {
                // В фоновом потоке — окно не замирает, пока модем ищет сеть и отправляет.
                await Task.Run(() => Alerts.Sms(c.ComPort, c.Phones.Take(1).ToList(), "SiteWatch: тестовое SMS с компьютера " + Environment.MachineName + ". Оповещения работают."));
                MessageBox.Show(this, "SMS отправлено на " + c.Phones[0] + ".", "SiteWatch");
            }
            catch (Exception ex)
            {
                // Порт занят, модем молчит, SIM не готова, сеть отказала — показываем причину словами модема.
                MessageBox.Show(this, "SMS не ушло: " + ex.Message, "SiteWatch");
            }
        }

        // Кнопка «Тестовый звонок» — на первый номер из списка.
        private async void TestCall()
        {
            // Берём то, что введено сейчас.
            var c = Collect();
            // Без номера или порта звонить некуда или нечем.
            if (c.Phones.Count == 0 || c.ComPort.Length == 0) { MessageBox.Show(this, "Укажите номер телефона и COM-порт (кнопка «Найти модем»).", "SiteWatch"); return; }
            try
            {
                // Звоним только на первый номер, в фоновом потоке — окно не замирает на полминуты.
                await Task.Run(() => Alerts.Call(c.ComPort, c.Phones.Take(1).ToList()));
                // Модем принял команды. Дошёл ли звонок — видно только по телефону.
                MessageBox.Show(this, "Модем позвонил на " + c.Phones[0] + " — телефон должен был звонить до 30 секунд.", "SiteWatch");
            }
            catch (Exception ex)
            {
                // Порт занят, модем молчит, вернул ошибку — показываем причину.
                MessageBox.Show(this, "Звонок не прошёл: " + ex.Message, "SiteWatch");
            }
        }

        // Кнопка «Найти модем»: опросить все COM-порты и выбрать тот, где ответил модем.
        private async void FindModem()
        {
            var ports = PortNames();
            if (ports.Length == 0)
            {
                MessageBox.Show(this, "В системе нет ни одного COM-порта: модем не подключён или для него не установлен драйвер " +
                                      "(для модемов на SIM800C — драйвер CH340).", "SiteWatch");
                return;
            }
            List<string[]> found;
            Cursor = Cursors.WaitCursor;
            try { found = await Task.Run(() => Alerts.Probe(ports)); }
            finally { Cursor = Cursors.Default; }

            var text = string.Join("\n", found.Select(f => f[0] + " — " + f[1]));
            var best = found.Where(f => f[1].StartsWith("модем")).Select(f => f[0]).FirstOrDefault();
            if (best == null)
            {
                MessageBox.Show(this, text + "\n\nМодем не найден: ни один порт не ответил на AT. Проверьте драйвер модема " +
                                      "и что его порт не занят другой программой.", "SiteWatch");
                return;
            }
            RefreshPorts();
            _port.Text = best;
            MessageBox.Show(this, text + "\n\nВыбран " + best + ". Нажмите «Сохранить», затем «Тестовое SMS».", "SiteWatch");
        }

        // COM-порты системы без повторов, по порядку номеров: COM3 раньше COM10.
        private static string[] PortNames()
        {
            return SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p).ToArray();
        }

        // Перезаполнить выпадающий список портов, не теряя то, что выбрано или вписано.
        private void RefreshPorts()
        {
            var current = _port.Text;
            _port.Items.Clear();
            _port.Items.AddRange(PortNames());
            _port.Text = current;
        }

        // Перерисовать плашку и таблицу по текущему состоянию. Вызывается в потоке интерфейса:
        // по событию сторожа и по таймеру раз в 5 с.
        public void RefreshView()
        {
            // Окно уже уничтожено — нечего рисовать.
            if (IsDisposed) return;
            // Снимок состояний.
            var list = _watcher.Snapshot();
            // Сколько сайтов признано упавшими.
            var downCount = list.Count(s => s.IsDown);
            // Плашка, по приоритету: нет интернета (фиолетовая)…
            if (!_watcher.InternetOk) { _header.Text = "Нет интернета у этого компьютера"; _header.BackColor = Icons.Violet; }
            // …есть упавшие (красная, с их именами)…
            else if (downCount > 0) { _header.Text = "Не работает: " + string.Join(", ", list.Where(s => s.IsDown).Select(s => s.Name)); _header.BackColor = Icons.Red; }
            // …хоть что-то работает (зелёная, со временем последнего круга). Жёлтого состояния, как у значка в трее,
            // у плашки нет: при сбоях ниже порога здесь всё ещё «Всё работает»…
            else if (list.Any(s => s.Ok == true)) { _header.Text = "Всё работает (" + list.Count + ")" + (_watcher.LastRound.HasValue ? " · проверено в " + _watcher.LastRound.Value.ToString("HH:mm:ss") : ""); _header.BackColor = Icons.Green; }
            // …иначе синяя: список пуст или первая проверка ещё не закончилась.
            else { _header.Text = list.Count == 0 ? "Сайтов нет — добавьте в «Настройках»" : "Идёт проверка…"; _header.BackColor = Icons.Blue; }

            // BeginUpdate / EndUpdate — не перерисовывать таблицу после каждой строки (без мерцания).
            _list.BeginUpdate();
            // Строим таблицу заново целиком (сайтов немного — так проще всего). Побочный эффект:
            // выделение строки сбрасывается при каждом обновлении.
            _list.Items.Clear();
            // Сначала упавшие, затем по алфавиту доменов.
            foreach (var s in list.OrderByDescending(x => x.IsDown).ThenBy(x => x.Name))
            {
                // Текст столбца «Состояние» и цвет фона строки.
                string state; Color back;
                // Ещё не проверяли — белый.
                if (s.Ok == null) { state = "ждём"; back = Color.White; }
                // Признан упавшим — красноватый.
                else if (s.IsDown) { state = "НЕ РАБОТАЕТ"; back = Color.FromArgb(255, 215, 215); }
                // Похоже на опечатку в адресе — серый.
                else if (s.Muted) { state = "адрес не найден"; back = Color.FromArgb(235, 235, 235); }
                // Сбоит, но порог не достигнут — жёлтый, со счётчиком «сбой 2/5».
                else if (s.Ok == false) { state = "сбой " + s.Fails + "/" + Program.FailThreshold; back = Color.FromArgb(255, 240, 200); }
                // Работает — зелёный.
                else { state = "работает"; back = Color.FromArgb(225, 245, 230); }

                // Первый столбец — домен; в Tag — само состояние (нужно для двойного клика).
                var item = new ListViewItem(s.Name) { Tag = s, BackColor = back };
                // «Состояние».
                item.SubItems.Add(state);
                // «Ответ»: код HTTP или описание ошибки.
                item.SubItems.Add(s.Details);
                // «мс» — только если проверка была.
                item.SubItems.Add(s.LastCheck.HasValue ? s.Ms.ToString() : "");
                // «Проверен»: сколько времени назад.
                item.SubItems.Add(s.LastCheck.HasValue ? Watcher.Ago(DateTime.Now - s.LastCheck.Value) + " назад" : "");
                // «Сбои с»: время начала текущей серии сбоев.
                item.SubItems.Add(s.DownSince.HasValue ? s.DownSince.Value.ToString("HH:mm") : "");
                // Строку — в таблицу.
                _list.Items.Add(item);
            }
            // Конец пакетного обновления — таблица перерисуется один раз.
            _list.EndUpdate();
        }

        // Закрытие окна.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик прячет в трей — проверка продолжается.
            // UserClosing — крестик, Alt+F4, «Закрыть окно» на панели задач. Выход не разрешён —
            // отменяем закрытие и просто прячем окно.
            if (e.CloseReason == CloseReason.UserClosing && !AllowClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            // Иначе (выход через TrayApp.Exit, завершение работы Windows и т. п.) — закрываемся по-настоящему.
            base.OnFormClosing(e);
        }
    }
}
