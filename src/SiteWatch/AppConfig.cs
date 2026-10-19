using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiteWatch;

/// <summary>Как проверять адрес.</summary>
public enum CheckType
{
    /// <summary>Открыть страницу: ответ 200–399 = работает.</summary>
    Http,
    /// <summary>Подключиться к порту (host:port) — для сервисов без веб-страницы.</summary>
    Tcp,
    /// <summary>Ping хоста — жив ли сам сервер.</summary>
    Ping,
}

public class SiteConfig
{
    public string Name { get; set; } = "";
    public CheckType Type { get; set; } = CheckType.Http;
    /// <summary>Http: https://site.ru/ · Tcp: host:port · Ping: host или IP.</summary>
    public string Address { get; set; } = "";
    /// <summary>Только для Http: текст, который обязан быть на странице (пусто — не проверять).</summary>
    public string Keyword { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public SiteConfig Clone() => (SiteConfig)MemberwiseClone();

    [JsonIgnore]
    public string Key => $"{Type}|{Address}".ToLowerInvariant();
}

public class EmailConfig
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "smtp.yandex.ru";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    /// <summary>Пароль зашифрован Windows (DPAPI): прочитать его можно только под этим пользователем на этом компьютере.</summary>
    public string PasswordProtected { get; set; } = "";
    /// <summary>Адрес отправителя; пусто — как логин.</summary>
    public string From { get; set; } = "";
    /// <summary>Кому писать.</summary>
    public List<string> Recipients { get; set; } = new();

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(PasswordProtected);
        set => PasswordProtected = Secret.Protect(value);
    }
}

/// <summary>GSM-модем с SIM-картой: звонок, когда сайт упал. Пока заготовка.</summary>
public class ModemConfig
{
    public bool Enabled { get; set; }
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    /// <summary>Кому звонить.</summary>
    public List<PhoneContact> Phones { get; set; } = new();
    /// <summary>Сколько секунд держать вызов, прежде чем положить трубку.</summary>
    public int RingSeconds { get; set; } = 30;
}

public class PhoneContact
{
    public string Name { get; set; } = "";
    /// <summary>+79991234567</summary>
    public string Number { get; set; } = "";

    /// <summary>Привести номер к виду +79991234567. null — номер неправильный.</summary>
    public static string Normalize(string raw)
    {
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '8') digits = "7" + digits[1..];
        if (digits.Length == 10 && digits[0] == '9') digits = "7" + digits;
        return digits.Length >= 10 && digits.Length <= 15 ? "+" + digits : null;
    }
}

public class AppConfig
{
    public List<SiteConfig> Sites { get; set; } = new();

    /// <summary>Как часто проверять, секунд.</summary>
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>Сколько ждать ответа, секунд.</summary>
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>Сколько неудачных проверок подряд, чтобы считать сайт упавшим (защита от случайных сбоев).</summary>
    public int FailThreshold { get; set; } = 3;
    /// <summary>Пока сайт лежит — напоминать раз в N минут (0 — не напоминать).</summary>
    public int ReminderMinutes { get; set; } = 60;
    /// <summary>Сообщать, когда сайт снова заработал.</summary>
    public bool NotifyRecovery { get; set; } = true;
    /// <summary>
    /// Контрольный адрес: если не открывается и он, значит пропал интернет у этого
    /// компьютера, а не упали сайты — тогда о сайтах не пишем.
    /// </summary>
    public string ControlUrl { get; set; } = "https://ya.ru/";

    public EmailConfig Email { get; set; } = new();
    public ModemConfig Modem { get; set; } = new();

    // ------------------------------------------------------------------

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SiteWatch");

    public static string FilePath => Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Не удалось прочитать настройки, беру стандартные", ex);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }

        var def = CreateDefault();
        def.Save();
        return def;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), Encoding.UTF8);
        File.Move(tmp, FilePath, true);
    }

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
    }

    public void Normalize()
    {
        Sites ??= new List<SiteConfig>();
        Email ??= new EmailConfig();
        Modem ??= new ModemConfig();
        Email.Recipients ??= new List<string>();
        Modem.Phones ??= new List<PhoneContact>();
        IntervalSeconds = Math.Clamp(IntervalSeconds, 10, 3600);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 3, 120);
        FailThreshold = Math.Clamp(FailThreshold, 1, 20);
        ReminderMinutes = Math.Clamp(ReminderMinutes, 0, 24 * 60);
        Modem.RingSeconds = Math.Clamp(Modem.RingSeconds, 5, 120);
    }

    private static AppConfig CreateDefault() => new();

    /// <summary>Выгрузить настройки в файл — например, чтобы отдать список сайтов коллеге.</summary>
    public void ExportTo(string path)
    {
        var copy = Clone();
        copy.Email.PasswordProtected = ""; // пароль на другом компьютере всё равно не расшифруется
        File.WriteAllText(path, JsonSerializer.Serialize(copy, JsonOptions), Encoding.UTF8);
    }

    public static AppConfig ImportFrom(string path)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidDataException("файл пустой");
        cfg.Normalize();
        return cfg;
    }
}

/// <summary>Шифрование пароля средствами Windows для текущего пользователя.</summary>
internal static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SiteWatch.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return "";
        }
    }
}
