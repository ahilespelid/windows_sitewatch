using System.IO.Ports;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace SiteWatch;

public enum AlertKind
{
    Down,
    StillDown,
    Recovered,
    Test,
}

public sealed class Alert
{
    public AlertKind Kind { get; init; }
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>Нужно ли звонить (звоним только на падение, не на «заработал»).</summary>
    public bool Urgent => Kind == AlertKind.Down || Kind == AlertKind.StillDown || Kind == AlertKind.Test;
}

/// <summary>Канал оповещения: почта, модем, в будущем — что угодно ещё.</summary>
public interface IAlertChannel
{
    string Name { get; }
    bool IsEnabled(AppConfig cfg);
    Task SendAsync(AppConfig cfg, Alert alert, CancellationToken ct);
}

/// <summary>Рассылает оповещение во все включённые каналы. Сбой одного не мешает другим.</summary>
public sealed class AlertDispatcher
{
    private readonly List<IAlertChannel> _channels = new()
    {
        new EmailChannel(),
        new ModemChannel(),
    };

    /// <returns>Список ошибок по каналам (пустой — всё отправлено).</returns>
    public async Task<List<string>> SendAsync(AppConfig cfg, Alert alert, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var channel in _channels)
        {
            if (!channel.IsEnabled(cfg)) continue;
            try
            {
                await channel.SendAsync(cfg, alert, ct);
                Logger.Info($"{channel.Name}: отправлено «{alert.Subject}»");
            }
            catch (Exception ex)
            {
                var msg = $"{channel.Name}: {ex.Message}";
                errors.Add(msg);
                Logger.Error($"{channel.Name}: не отправлено «{alert.Subject}»", ex);
            }
        }
        return errors;
    }
}

// ----------------------------------------------------------------------------
// Почта
// ----------------------------------------------------------------------------

public sealed class EmailChannel : IAlertChannel
{
    public string Name => "Почта";

    public bool IsEnabled(AppConfig cfg) => cfg.Email.Enabled;

    public async Task SendAsync(AppConfig cfg, Alert alert, CancellationToken ct)
    {
        var e = cfg.Email;
        var recipients = (e.Recipients ?? new List<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (string.IsNullOrWhiteSpace(e.SmtpHost)) throw new InvalidOperationException("не указан SMTP-сервер");
        if (recipients.Count == 0) throw new InvalidOperationException("не указано, кому отправлять");

        var from = string.IsNullOrWhiteSpace(e.From) ? e.User : e.From;
        using var message = new MailMessage
        {
            From = new MailAddress(from, "SiteWatch"),
            Subject = alert.Subject,
            Body = alert.Body,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        foreach (var r in recipients) message.To.Add(r);
        if (alert.Urgent) message.Priority = MailPriority.High;

        // SmtpClient работает через STARTTLS (порт 587). Порт 465 (SSL сразу)
        // он не умеет — для Яндекса, Mail.ru и Gmail берите 587.
        using var smtp = new SmtpClient(e.SmtpHost.Trim(), e.SmtpPort)
        {
            EnableSsl = e.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30000,
        };
        if (!string.IsNullOrEmpty(e.User))
        {
            smtp.UseDefaultCredentials = false;
            smtp.Credentials = new NetworkCredential(e.User, e.Password);
        }
        await smtp.SendMailAsync(message, ct);
    }
}

// ----------------------------------------------------------------------------
// GSM-модем — ЗАГОТОВКА
// ----------------------------------------------------------------------------

/// <summary>
/// Звонок через GSM-модем с SIM-картой (USB-модем или модуль SIM800/SIM7600 —
/// любой, что понимает AT-команды и виден в Windows как COM-порт).
///
/// Заготовка: команды стандартные (ATD номер; — голосовой вызов, ATH — сброс),
/// но на живом модеме ещё не проверялось. Многие USB-модемы операторов
/// прошиты «только интернет» — голосовые вызовы у них выключены; перед покупкой
/// проверить, что модем поддерживает голос (voice).
///
/// Звонок без звука: телефон звонит, трубку брать не обязательно — сам вызов
/// и есть сигнал «зайди посмотри». Дальше можно добавить SMS (AT+CMGS).
/// </summary>
public sealed class ModemChannel : IAlertChannel
{
    private static readonly SemaphoreSlim Busy = new(1, 1);

    public string Name => "Модем";

    public bool IsEnabled(AppConfig cfg) => cfg.Modem.Enabled;

    public async Task SendAsync(AppConfig cfg, Alert alert, CancellationToken ct)
    {
        if (!alert.Urgent) return; // на «заработал» не звоним
        var phones = (cfg.Modem.Phones ?? new List<PhoneContact>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Number)).ToList();
        if (phones.Count == 0) throw new InvalidOperationException("не указаны номера для звонка");
        await CallAsync(cfg.Modem, phones, ct);
    }

    /// <summary>Позвонить на указанные номера по очереди.</summary>
    public static async Task CallAsync(ModemConfig m, List<PhoneContact> phones, CancellationToken ct)
    {
        // Модем один — звонки строго по очереди.
        await Busy.WaitAsync(ct);
        try
        {
            await Task.Run(() => CallAll(m, phones, ct), ct);
        }
        finally
        {
            Busy.Release();
        }
    }

    /// <summary>Проверка модема без звонка: отвечает ли, какая модель, есть ли SIM и сеть.</summary>
    public static async Task<string> ProbeAsync(ModemConfig m)
    {
        await Busy.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var port = Open(m);
                Command(port, "AT", "модем не отвечает на AT");
                Command(port, "ATE0", null);
                var sb = new StringBuilder();
                sb.AppendLine("Модем отвечает.");
                sb.AppendLine("Модель: " + Command(port, "ATI", null));
                sb.AppendLine("SIM-карта: " + Command(port, "AT+CPIN?", null));
                sb.AppendLine("Сигнал: " + Command(port, "AT+CSQ", null) + "  (второе число 0–31: чем больше, тем лучше; 99 — нет сети)");
                sb.AppendLine("Оператор: " + Command(port, "AT+COPS?", null));
                return sb.ToString();
            });
        }
        finally
        {
            Busy.Release();
        }
    }

    public static string[] AvailablePorts()
    {
        try { return SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static SerialPort Open(ModemConfig m)
    {
        if (string.IsNullOrWhiteSpace(m.PortName)) throw new InvalidOperationException("не выбран COM-порт");
        var port = new SerialPort(m.PortName.Trim(), m.BaudRate, Parity.None, 8, StopBits.One)
        {
            NewLine = "\r",
            ReadTimeout = 3000,
            WriteTimeout = 3000,
            DtrEnable = true,
            RtsEnable = true,
        };
        port.Open();
        return port;
    }

    private static void CallAll(ModemConfig m, List<PhoneContact> phones, CancellationToken ct)
    {
        using var port = Open(m);
        Command(port, "AT", "модем не отвечает на AT");
        Command(port, "ATE0", null);          // без эха команд
        Command(port, "AT+CMEE=2", null);     // ошибки текстом

        var errors = new List<string>();
        foreach (var phone in phones)
        {
            ct.ThrowIfCancellationRequested();
            var number = PhoneContact.Normalize(phone.Number) ?? phone.Number.Trim();
            try
            {
                Logger.Info($"Модем: звоню {phone.Name} {number}");
                Command(port, $"ATD{number};", "модем не принял вызов");
                // Держим вызов, затем кладём трубку.
                for (var i = 0; i < m.RingSeconds && !ct.IsCancellationRequested; i++)
                    Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                errors.Add($"{number}: {ex.Message}");
            }
            finally
            {
                try { Command(port, "ATH", null); } catch { }
                Thread.Sleep(2000);
            }
        }

        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    /// <summary>
    /// Отправить команду и дождаться OK. Возвращает ответ модема (без OK).
    /// errorIfFailed == null — ошибку не бросать.
    /// </summary>
    private static string Command(SerialPort port, string cmd, string errorIfFailed)
    {
        port.DiscardInBuffer();
        port.WriteLine(cmd);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var answer = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            string line;
            try { line = port.ReadLine().Trim(); }
            catch (TimeoutException) { break; }
            if (line.Length == 0 || line == cmd) continue;
            if (line == "OK") return answer.ToString().Trim();
            answer.Append(line).Append(' ');
            if (line.StartsWith("ERROR") || line.StartsWith("+CME ERROR") || line == "NO CARRIER" || line == "BUSY" || line == "NO DIALTONE")
                break;
        }
        if (errorIfFailed != null)
            throw new InvalidOperationException($"{errorIfFailed} ({answer.ToString().Trim()})");
        return answer.Length > 0 ? answer.ToString().Trim() : "нет ответа";
    }
}
