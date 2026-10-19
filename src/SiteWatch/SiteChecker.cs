using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SiteWatch;

public sealed class CheckResult
{
    public bool Ok { get; init; }
    /// <summary>Коротко, что увидели: «200 OK», «таймаут», «нет слова на странице»…</summary>
    public string Details { get; init; } = "";
    public long ElapsedMs { get; init; }
    /// <summary>Домен не найден (DNS): опечатка в адресе или домена не существует.</summary>
    public bool HostNotFound { get; init; }
}

/// <summary>Одна проверка одного адреса. Не бросает исключений.</summary>
public static class SiteChecker
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            // Без кэша соединений: упавший сайт не должен «работать» на старом соединении.
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SiteWatch/0.2 (+uptime monitor)");
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return client;
    }

    public static async Task<CheckResult> CheckAsync(SiteConfig site, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var (ok, details) = site.Type switch
            {
                CheckType.Http => await CheckHttpAsync(site, cts.Token),
                CheckType.Tcp => await CheckTcpAsync(site.Address, cts.Token),
                CheckType.Ping => await CheckPingAsync(site.Address, timeoutSeconds),
                _ => (false, "неизвестный тип проверки"),
            };
            return new CheckResult { Ok = ok, Details = details, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CheckResult { Ok = false, Details = $"нет ответа за {timeoutSeconds} с", ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                Ok = false,
                Details = Describe(ex),
                ElapsedMs = sw.ElapsedMilliseconds,
                HostNotFound = IsHostNotFound(ex),
            };
        }
    }

    private static async Task<(bool, string)> CheckHttpAsync(SiteConfig site, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, site.Address.Trim());
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)response.StatusCode;
        var status = $"{code} {response.ReasonPhrase}".Trim();

        // 401/403 — сайт жив, просто просит войти (админки, WebDAV).
        if (code == 401 || code == 403)
            return (true, $"{status} (нужен вход — сайт отвечает)");
        if (code < 200 || code >= 400)
            return (false, $"ответ {status}");

        if (!string.IsNullOrWhiteSpace(site.Keyword))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.IndexOf(site.Keyword.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return (false, $"{status}, но на странице нет «{site.Keyword.Trim()}»");
        }
        return (true, status);
    }

    private static async Task<(bool, string)> CheckTcpAsync(string address, CancellationToken ct)
    {
        var (host, port) = ParseHostPort(address);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        return (true, $"порт {port} открыт");
    }

    private static async Task<(bool, string)> CheckPingAsync(string host, int timeoutSeconds)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(host.Trim(), timeoutSeconds * 1000);
        return reply.Status == IPStatus.Success
            ? (true, $"ping {reply.RoundtripTime} мс")
            : (false, $"ping: {reply.Status}");
    }

    public static (string host, int port) ParseHostPort(string address)
    {
        var a = (address ?? "").Trim();
        var idx = a.LastIndexOf(':');
        if (idx <= 0 || idx == a.Length - 1 || !int.TryParse(a[(idx + 1)..], out var port) || port < 1 || port > 65535)
            throw new FormatException("адрес порта пишется так: host:port");
        return (a[..idx].Trim('[', ']'), port);
    }

    private static bool IsHostNotFound(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException se && (se.SocketErrorCode == SocketError.HostNotFound || se.SocketErrorCode == SocketError.NoData))
                return true;
        }
        return false;
    }

    /// <summary>Понятное описание ошибки вместо стека .NET.</summary>
    public static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;

        return inner switch
        {
            SocketException se when se.SocketErrorCode == SocketError.HostNotFound => "домен не найден (DNS)",
            SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused => "соединение отклонено (сервис не слушает порт)",
            SocketException se when se.SocketErrorCode == SocketError.TimedOut => "нет ответа (таймаут соединения)",
            SocketException se => $"сеть: {se.SocketErrorCode}",
            System.Security.Authentication.AuthenticationException => "ошибка SSL-сертификата",
            UriFormatException => "неверный адрес",
            FormatException fe => fe.Message,
            _ => inner.Message,
        };
    }
}
