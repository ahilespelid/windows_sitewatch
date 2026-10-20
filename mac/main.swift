// SiteWatch для macOS — паук-звонарь: следит за нашими сайтами, при падении пишет на почту.
// Та же логика, что у Windows-версии (main.cs), но родными средствами Мака:
//   • значок в строке меню вместо значка в трее;
//   • пароль почты — в «Связке ключей» вместо DPAPI;
//   • уведомления — в Центре уведомлений;
//   • запрет сна — через IOKit (то же, что делает caffeinate);
//   • автозапуск — LaunchAgent в ~/Library/LaunchAgents вместо реестра;
//   • письма уходят через системный curl (SMTP + STARTTLS), своих библиотек не нужно.
// Один файл Swift (AppKit + SwiftUI), собирается build.sh без Xcode-проекта. Нужна macOS 13 Ventura или новее.
// Звонков через GSM-модем здесь нет — эта заготовка пока только в Windows-версии.

import AppKit
import SwiftUI
import Security
import IOKit.pwr_mgt
import UserNotifications

// ===================================================================== константы

enum Const {
    // Версия: заголовок окна, журнал, User-Agent. Держать в согласии с Info.plist.
    static let version = "0.4.2"
    // Идентификатор приложения — такой же стоит в Info.plist (CFBundleIdentifier).
    static let bundleID = "ru.litewms.sitewatch"
    // Ключ запуска «свёрнутым в строку меню» — его передаёт автозапуск.
    static let trayArg = "--tray"
    // Как часто проверять, секунд. Отсчёт — от конца круга.
    static let intervalSeconds: Double = 30
    // Сколько ждать ответа сайта, секунд.
    static let timeoutSeconds: Double = 15
    // Упал = столько неудач подряд.
    static let failThreshold = 3
    // Пока лежит — напоминать не чаще, чем раз в столько минут.
    static let reminderMinutes: Double = 5
    // Контрольный адрес: не открывается и он — значит, нет интернета у нас самих.
    static let controlURL = "https://ya.ru/"
    // Сигнал «покажи окно» от второй копии программы к первой.
    static let showSignal = "ru.litewms.sitewatch.show"
}

// Цвета состояний — те же, что в Windows-версии.
enum Palette {
    static let green = NSColor(srgbRed: 34 / 255, green: 160 / 255, blue: 80 / 255, alpha: 1)
    static let yellow = NSColor(srgbRed: 230 / 255, green: 160 / 255, blue: 20 / 255, alpha: 1)
    static let red = NSColor(srgbRed: 215 / 255, green: 40 / 255, blue: 40 / 255, alpha: 1)
    static let violet = NSColor(srgbRed: 120 / 255, green: 90 / 255, blue: 200 / 255, alpha: 1)
    static let blue = NSColor(srgbRed: 90 / 255, green: 140 / 255, blue: 210 / 255, alpha: 1)
}

// Общая ошибка с понятным текстом.
struct AppError: LocalizedError {
    let text: String
    init(_ text: String) { self.text = text }
    var errorDescription: String? { text }
}

// Форматирование времени для таблицы, писем и журнала.
enum Fmt {
    static let hm: DateFormatter = make("HH:mm")
    static let hms: DateFormatter = make("HH:mm:ss")
    static let full: DateFormatter = make("dd.MM.yyyy HH:mm:ss")
    static let log: DateFormatter = make("dd.MM HH:mm:ss")

    private static func make(_ format: String) -> DateFormatter {
        let f = DateFormatter()
        f.locale = Locale(identifier: "ru_RU")
        f.dateFormat = format
        return f
    }

    // «Сколько прошло» по-человечески: 45 с / 12 мин / 3 ч 5 мин / 2 д 4 ч.
    static func ago(_ seconds: TimeInterval) -> String {
        let s = Int(max(0, seconds))
        if s < 60 { return "\(s) с" }
        if s < 3600 { return "\(s / 60) мин" }
        if s < 86400 { return "\(s / 3600) ч \((s % 3600) / 60) мин" }
        return "\(s / 86400) д \((s % 86400) / 3600) ч"
    }
}

// Имя этого компьютера — для подписи в письмах.
enum Machine {
    static let name: String = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
}

// ===================================================================== журнал

// Текстовый журнал ~/Library/Application Support/SiteWatch/sitewatch.log. Пишут разные потоки — поэтому замок.
enum Log {
    private static let lock = NSLock()

    static func write(_ text: String) {
        lock.lock()
        defer { lock.unlock() }
        let fm = FileManager.default
        try? fm.createDirectory(at: Config.folder, withIntermediateDirectories: true)
        let file = Config.folder.appendingPathComponent("sitewatch.log")
        // Журнал не растёт бесконечно: больше 2 МБ — начинаем заново.
        if let attrs = try? fm.attributesOfItem(atPath: file.path),
           let size = attrs[.size] as? NSNumber, size.intValue > 2 * 1024 * 1024 {
            try? fm.removeItem(at: file)
        }
        guard let data = (Fmt.log.string(from: Date()) + " " + text + "\n").data(using: .utf8) else { return }
        if let h = try? FileHandle(forWritingTo: file) {
            h.seekToEndOfFile()
            h.write(data)
            h.closeFile()
        } else {
            try? data.write(to: file)
        }
    }
}

// ===================================================================== «Связка ключей»

// Пароль почты хранится в «Связке ключей» (служба «SiteWatch», учётная запись = адрес отправителя),
// а не в config.txt. Читать его может только эта программа (остальные — с разрешения пользователя).
enum Keychain {
    static let service = "SiteWatch"

    private static func base(_ account: String?) -> [String: Any] {
        var q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service]
        if let account = account { q[kSecAttrAccount as String] = account }
        return q
    }

    // Прочитать пароль; нет — пустая строка.
    static func get(_ account: String) -> String {
        var q = base(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return "" }
        return String(data: data, encoding: .utf8) ?? ""
    }

    // Записать пароль (старый удаляется). Пустой пароль — просто удалить.
    static func set(_ account: String, _ password: String) throws {
        SecItemDelete(base(account) as CFDictionary)
        if password.isEmpty || account.isEmpty { return }
        var add = base(account)
        add[kSecValueData as String] = Data(password.utf8)
        add[kSecAttrLabel as String] = "SiteWatch: пароль приложения почты"
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess {
            let text = (SecCopyErrorMessageString(status, nil) as String?) ?? "код \(status)"
            throw AppError("«Связка ключей» не сохранила пароль: " + text)
        }
    }

    // Удалить все пароли SiteWatch (для «Сбросить настройки»).
    static func deleteAll() {
        // На macOS удаление может снимать по одной записи — повторяем, пока есть что удалять.
        for _ in 0..<50 where SecItemDelete(base(nil) as CFDictionary) == errSecSuccess {}
    }
}

// ===================================================================== настройки

// Настройки в ~/Library/Application Support/SiteWatch/config.txt — тот же формат, что у Windows-версии,
// только без телефонов и COM-порта. Пароль — в «Связке ключей».
struct Config {
    // Поля ниже — значения по умолчанию: первый запуск и «Сбросить настройки».
    var sites: [String] = [
        "https://litewms.ru/",
        "https://lite-box.ru/",
        "https://scanmaster.tech/",
        "https://limaster.ru/",
        "https://support.litewms.ru/",
    ]
    var mailTo: [String] = [
        "ahilespelid@yandex.ru",
        "brand.korolev@gmail.com",
        "dvrh.76@gmail.com",
    ]
    var smtpHost = "smtp.gmail.com"
    var smtpPort = 587
    var smtpUser = "brand.korolev@gmail.com"
    var smtpPassword = ""
    var keepAwake = true

    static let gmailHost = "smtp.gmail.com"
    static let yandexHost = "smtp.yandex.ru"

    // Папка настроек и журнала: ~/Library/Application Support/SiteWatch.
    static var folder: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("SiteWatch", isDirectory: true)
    }

    static var fileURL: URL { folder.appendingPathComponent("config.txt") }

    // SMTP-сервер по домену отправителя; для незнакомых доменов — то, что задано в config.txt.
    static func hostFor(_ user: String, fallback: String) -> String {
        guard let at = user.lastIndex(of: "@") else { return fallback }
        let domain = user[user.index(after: at)...].trimmingCharacters(in: .whitespaces).lowercased()
        if domain == "gmail.com" || domain == "googlemail.com" { return gmailHost }
        if domain == "yandex.ru" || domain == "ya.ru" || domain == "yandex.com" { return yandexHost }
        return fallback
    }

    // Прочитать настройки. Файла нет — значения по умолчанию.
    static func load() -> Config {
        var cfg = Config()
        if let text = try? String(contentsOf: fileURL, encoding: .utf8) {
            var section = ""
            var sites: [String] = []
            var mail: [String] = []
            for raw in text.components(separatedBy: .newlines) {
                let line = raw.trimmingCharacters(in: .whitespaces)
                if line.isEmpty || line.hasPrefix("#") { continue }
                if line.hasPrefix("[") && line.hasSuffix("]") {
                    section = line.trimmingCharacters(in: CharacterSet(charactersIn: "[]")).lowercased()
                    continue
                }
                switch section {
                case "sites":
                    sites.append(line)
                case "mail":
                    mail.append(line)
                case "options":
                    guard let eq = line.firstIndex(of: "="), eq != line.startIndex else { continue }
                    let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
                    let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
                    switch key {
                    case "smtp_host": cfg.smtpHost = value
                    case "smtp_port": if let p = Int(value) { cfg.smtpPort = p }
                    case "smtp_user": cfg.smtpUser = value
                    case "keep_awake": cfg.keepAwake = value != "0"
                    default: break
                    }
                default:
                    break
                }
            }
            cfg.sites = sites
            cfg.mailTo = mail
        }
        cfg.smtpHost = hostFor(cfg.smtpUser, fallback: cfg.smtpHost)
        cfg.smtpPassword = Keychain.get(cfg.smtpUser)
        return cfg
    }

    // Записать настройки в файл (целиком) и пароль — в «Связку ключей».
    func save() throws {
        try FileManager.default.createDirectory(at: Config.folder, withIntermediateDirectories: true)
        var s = "# SiteWatch. Сайты и почта — по одному в строке. Пароль хранится в «Связке ключей», не здесь.\n"
        s += "[sites]\n" + sites.map { $0 + "\n" }.joined()
        s += "[mail]\n" + mailTo.map { $0 + "\n" }.joined()
        s += "[options]\n"
        s += "smtp_host=\(smtpHost)\n"
        s += "smtp_port=\(smtpPort)\n"
        s += "smtp_user=\(smtpUser)\n"
        s += "keep_awake=\(keepAwake ? "1" : "0")\n"
        try s.write(to: Config.fileURL, atomically: true, encoding: .utf8)
        try Keychain.set(smtpUser, smtpPassword)
    }
}

// ===================================================================== проверка

// Состояние одного сайта: итог последней проверки и накопленная история (счётчик сбоев, «упал»…).
struct SiteState: Identifiable {
    var url: String
    var ok: Bool? = nil          // nil — ещё не проверяли
    var details = ""             // «200 OK», «нет ответа за 15 с», «домен не найден (DNS)»…
    var ms = 0                   // время ответа, мс
    var lastCheck: Date? = nil
    var fails = 0                // неудач подряд
    var isDown = false           // признан упавшим, письмо отправлено
    var downSince: Date? = nil   // начало текущей серии сбоев
    var lastAlert: Date? = nil   // когда последний раз тревожили
    var everOk = false           // хоть раз открылся
    var muted = false            // домен не найден и ни разу не открывался — опечатка, не тревожим

    var id: String { url }
    var name: String { URL(string: url)?.host ?? url }
}

// Итог одной проверки.
struct CheckResult {
    var url: String
    var ok: Bool
    var details: String
    var ms: Int
    var dns: Bool
}

// Проверка по HTTP(S). Один общий URLSession на всю программу.
// Страницу целиком не скачиваем: как только пришли заголовки ответа, запрос отменяется.
final class Checker: NSObject, URLSessionDataDelegate {
    static let shared = Checker()

    private final class Pending {
        let url: String
        let start = Date()
        let done: (CheckResult) -> Void
        init(url: String, done: @escaping (CheckResult) -> Void) {
            self.url = url
            self.done = done
        }
    }

    // Копилка результатов круга (класс — чтобы безопасно передавать между потоками под замком).
    private final class Results {
        let lock = NSLock()
        var byURL: [String: CheckResult] = [:]
    }

    private var session: URLSession!
    private let lock = NSLock()
    private var pending: [Int: Pending] = [:]

    override init() {
        super.init()
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = Const.timeoutSeconds
        c.timeoutIntervalForResource = Const.timeoutSeconds
        c.requestCachePolicy = .reloadIgnoringLocalCacheData
        c.urlCache = nil
        c.httpCookieStorage = nil
        c.httpShouldSetCookies = false
        c.httpAdditionalHeaders = ["User-Agent": "SiteWatch/\(Const.version) (macOS)"]
        session = URLSession(configuration: c, delegate: self, delegateQueue: nil)
    }

    // Проверить один адрес. done вызывается в фоновом потоке.
    func check(_ url: String, done: @escaping (CheckResult) -> Void) {
        guard let u = URL(string: url), u.scheme != nil, u.host != nil else {
            done(CheckResult(url: url, ok: false, details: "неверный адрес", ms: 0, dns: true))
            return
        }
        let task = session.dataTask(with: URLRequest(url: u))
        lock.lock()
        pending[task.taskIdentifier] = Pending(url: url, done: done)
        lock.unlock()
        task.resume()
    }

    // Проверить все адреса одновременно; done — когда закончатся все, в порядке списка.
    func checkAll(_ urls: [String], done: @escaping ([CheckResult]) -> Void) {
        if urls.isEmpty {
            done([])
            return
        }
        let group = DispatchGroup()
        let results = Results()
        for url in urls {
            group.enter()
            check(url) { r in
                results.lock.lock()
                results.byURL[url] = r
                results.lock.unlock()
                group.leave()
            }
        }
        group.notify(queue: .global()) {
            results.lock.lock()
            let list = urls.compactMap { results.byURL[$0] }
            results.lock.unlock()
            done(list)
        }
    }

    // Пришли заголовки ответа — дальше не качаем. Код ответа потом возьмём из task.response.
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        completionHandler(.cancel)
    }

    // Запрос завершён: успехом, ошибкой или нашей отменой после заголовков.
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        let p = pending.removeValue(forKey: task.taskIdentifier)
        lock.unlock()
        guard let p = p else { return }
        let ms = Int(Date().timeIntervalSince(p.start) * 1000)
        if let http = task.response as? HTTPURLResponse {
            let code = http.statusCode
            // Работает = 200–399 или 401/403 (просит войти — значит, жив).
            let ok = (200..<400).contains(code) || code == 401 || code == 403
            p.done(CheckResult(url: p.url, ok: ok, details: Checker.status(code), ms: ms, dns: false))
            return
        }
        let (text, dns) = Checker.describe(error)
        p.done(CheckResult(url: p.url, ok: false, details: text, ms: ms, dns: dns))
    }

    // Код ответа с пояснением, как в Windows-версии: «200 OK», «502 Bad Gateway».
    static func status(_ code: Int) -> String {
        let names: [Int: String] = [
            200: "OK", 201: "Created", 204: "No Content", 301: "Moved Permanently", 302: "Found",
            303: "See Other", 304: "Not Modified", 307: "Temporary Redirect", 308: "Permanent Redirect",
            400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found", 405: "Method Not Allowed",
            408: "Request Timeout", 429: "Too Many Requests", 500: "Internal Server Error", 502: "Bad Gateway",
            503: "Service Unavailable", 504: "Gateway Timeout",
        ]
        if let name = names[code] { return "\(code) \(name)" }
        return "\(code)"
    }

    // Понятный текст ошибки; второй элемент — «домен не найден» (DNS).
    static func describe(_ error: Error?) -> (String, Bool) {
        guard let e = error as NSError? else { return ("нет ответа", false) }
        if e.domain == NSURLErrorDomain {
            switch e.code {
            case NSURLErrorCannotFindHost, NSURLErrorDNSLookupFailed:
                return ("домен не найден (DNS)", true)
            case NSURLErrorTimedOut:
                return ("нет ответа за \(Int(Const.timeoutSeconds)) с", false)
            case NSURLErrorCannotConnectToHost:
                return ("не удалось подключиться", false)
            case NSURLErrorNetworkConnectionLost:
                return ("соединение оборвалось", false)
            case NSURLErrorNotConnectedToInternet:
                return ("нет сети", false)
            case NSURLErrorSecureConnectionFailed, NSURLErrorServerCertificateHasBadDate,
                 NSURLErrorServerCertificateUntrusted, NSURLErrorServerCertificateHasUnknownRoot,
                 NSURLErrorServerCertificateNotYetValid:
                return ("ошибка SSL-сертификата", false)
            default:
                break
            }
        }
        return (e.localizedDescription, false)
    }
}

// Снимок состояния для интерфейса.
struct Snapshot {
    var sites: [SiteState] = []
    var internetOk = true
    var lastRound: Date? = nil
}

// Общее состояние: по нему красится значок и плашка. Порядок = приоритет.
enum Overall {
    case noInternet, down, failing, ok, waiting
}

extension Snapshot {
    var overall: Overall {
        if !internetOk { return .noInternet }
        if sites.contains(where: { $0.isDown }) { return .down }
        if sites.contains(where: { $0.ok == false && !$0.muted }) { return .failing }
        if sites.contains(where: { $0.ok == true }) { return .ok }
        return .waiting
    }

    // Текст и цвет плашки в окне.
    var headline: (String, NSColor) {
        switch overall {
        case .noInternet:
            return ("Нет интернета у этого компьютера", Palette.violet)
        case .down:
            return ("Не работает: " + sites.filter { $0.isDown }.map { $0.name }.joined(separator: ", "), Palette.red)
        case .failing:
            return ("Есть сбои: " + sites.filter { $0.ok == false && !$0.muted }.map { $0.name }.joined(separator: ", "), Palette.yellow)
        case .ok:
            let at = lastRound.map { " · проверено в " + Fmt.hms.string(from: $0) } ?? ""
            return ("Всё работает (\(sites.count))" + at, Palette.green)
        case .waiting:
            return (sites.isEmpty ? "Сайтов нет — добавьте в «Настройках»" : "Идёт проверка…", Palette.blue)
        }
    }

    // Подсказка у значка в строке меню.
    var tooltip: String {
        switch overall {
        case .noInternet: return "SiteWatch: нет интернета"
        case .down: return "SiteWatch: не работает: \(sites.filter { $0.isDown }.count)"
        case .failing: return "SiteWatch: есть сбои"
        case .ok: return "SiteWatch: всё работает (\(sites.count))"
        case .waiting: return "SiteWatch: проверка…"
        }
    }
}

// «Сторож»: круг проверки и решение, когда тревожить. Всё состояние живёт на своей очереди q.
final class Watcher {
    private let q = DispatchQueue(label: "SiteWatch.Watcher")
    private var states: [String: SiteState] = [:]
    private var order: [String] = []
    private var cfg: Config
    private var internetOk = true
    private var lastRound: Date?
    private var running = false
    private var again = false
    private var generation = 0

    // Вызываются в главном потоке.
    var onChange: ((Snapshot) -> Void)?
    var onNotify: ((String, Bool) -> Void)?

    init(_ cfg: Config) {
        self.cfg = cfg
        applyNow(cfg)
    }

    // Новые настройки: история оставшихся сайтов сохраняется, удалённые забываются.
    func apply(_ cfg: Config) {
        q.async {
            self.applyNow(cfg)
            self.publish()
        }
    }

    private func applyNow(_ cfg: Config) {
        self.cfg = cfg
        var next: [String: SiteState] = [:]
        var ord: [String] = []
        for url in cfg.sites where next[url] == nil {
            next[url] = states[url] ?? SiteState(url: url)
            ord.append(url)
        }
        states = next
        order = ord
    }

    func start() { q.async { self.round() } }

    // Проверить сейчас; если круг уже идёт — сразу после него.
    func checkNow() {
        q.async {
            if self.running { self.again = true } else { self.round() }
        }
    }

    private func publish() {
        let snap = Snapshot(sites: order.compactMap { states[$0] }, internetOk: internetOk, lastRound: lastRound)
        DispatchQueue.main.async { self.onChange?(snap) }
    }

    private func say(_ text: String, _ problem: Bool) {
        DispatchQueue.main.async { self.onNotify?(text, problem) }
    }

    // Один круг: все сайты параллельно → (если что-то не открылось) проверка своего интернета → итоги.
    private func round() {
        running = true
        generation += 1
        let urls = order
        Checker.shared.checkAll(urls) { results in
            if results.contains(where: { !$0.ok }) {
                Checker.shared.check(Const.controlURL) { control in
                    self.q.async { self.finish(results, internet: control.ok) }
                }
            } else {
                self.q.async { self.finish(results, internet: true) }
            }
        }
    }

    private func finish(_ results: [CheckResult], internet: Bool) {
        let now = Date()
        // Нет интернета у нас самих — сайты в этом круге не судим.
        if !internet {
            if internetOk {
                internetOk = false
                Log.write("Нет интернета у этого компьютера")
                say("Нет интернета у этого компьютера — сайты проверить нельзя", true)
            }
            lastRound = now
            publish()
            roundDone()
            return
        }
        if !internetOk {
            internetOk = true
            Log.write("Интернет снова есть")
            say("Интернет снова есть", false)
        }

        var down: [SiteState] = []
        var still: [SiteState] = []
        var up: [SiteState] = []
        for r in results {
            guard var st = states[r.url] else { continue }
            st.ok = r.ok
            st.details = r.details
            st.ms = r.ms
            st.lastCheck = now
            if r.ok {
                // Был упавшим — это восстановление (копия до сброса: в письме видно, сколько лежал).
                if st.isDown {
                    up.append(st)
                    Log.write("РАБОТАЕТ " + st.url)
                }
                st.everOk = true
                st.muted = false
                st.fails = 0
                st.isDown = false
                st.downSince = nil
                st.lastAlert = nil
            } else if r.dns && !st.everOk {
                // Домен не найден, а сайт ни разу не открывался — скорее всего, опечатка. Не тревожим.
                st.muted = true
                st.fails = 0
            } else {
                st.muted = false
                st.fails += 1
                if st.fails == 1 { st.downSince = now }
                Log.write("Сбой \(st.fails)/\(Const.failThreshold) \(st.url) — \(r.details)")
                if !st.isDown && st.fails >= Const.failThreshold {
                    st.isDown = true
                    st.lastAlert = now
                    down.append(st)
                } else if st.isDown, let last = st.lastAlert, now.timeIntervalSince(last) >= Const.reminderMinutes * 60 {
                    st.lastAlert = now
                    still.append(st)
                }
            }
            states[r.url] = st
        }
        lastRound = now
        publish()

        // Письма — вне очереди сторожа: отправка идёт долго. Следующий круг — после них.
        let cfg = self.cfg
        let downList = down, stillList = still, upList = up
        DispatchQueue.global().async {
            if !downList.isEmpty {
                self.say("Не работает: " + Watcher.names(downList), true)
                self.alert(cfg, "НЕ РАБОТАЕТ: " + Watcher.names(downList), Watcher.body("Перестали отвечать:", downList, now))
            }
            if !stillList.isEmpty {
                self.alert(cfg, "ВСЁ ЕЩЁ НЕ РАБОТАЕТ: " + Watcher.names(stillList), Watcher.body("По-прежнему не отвечают:", stillList, now))
            }
            if !upList.isEmpty {
                self.say("Снова работает: " + Watcher.names(upList), false)
                self.alert(cfg, "Снова работает: " + Watcher.names(upList), Watcher.body("Снова работают:", upList, now))
            }
            self.q.async { self.roundDone() }
        }
    }

    // Круг закончен: если просили проверить ещё раз — сразу, иначе через IntervalSeconds.
    private func roundDone() {
        running = false
        if again {
            again = false
            round()
            return
        }
        let gen = generation
        q.asyncAfter(deadline: .now() + Const.intervalSeconds) {
            if gen == self.generation && !self.running { self.round() }
        }
    }

    private func alert(_ cfg: Config, _ subject: String, _ body: String) {
        do {
            try Mailer.send(cfg, subject: subject, body: body)
            Log.write("Письмо: " + subject)
        } catch {
            Log.write("ERROR письмо не ушло: " + error.localizedDescription)
            say("Письмо не отправлено: " + error.localizedDescription, true)
        }
    }

    static func names(_ list: [SiteState]) -> String {
        list.map { $0.name }.joined(separator: ", ")
    }

    static func body(_ title: String, _ list: [SiteState], _ now: Date) -> String {
        var s = title + "\n\n"
        for st in list {
            s += "• " + st.url + "\n"
            var line = "  " + st.details
            if let since = st.downSince {
                line += ", сбои с " + Fmt.hm.string(from: since) + " (" + Fmt.ago(now.timeIntervalSince(since)) + ")"
            }
            s += line + "\n"
        }
        s += "\n— SiteWatch, " + Machine.name + " (macOS), " + Fmt.full.string(from: now) + "\n"
        return s
    }
}

// ===================================================================== почта

// Письмо через системный /usr/bin/curl: SMTP + STARTTLS (порт 587), как у Windows-версии.
// Пароль передаётся curl через стандартный ввод, а не в командной строке — его не видно в списке процессов.
enum Mailer {
    static func send(_ cfg: Config, subject: String, body: String) throws {
        if cfg.mailTo.isEmpty { throw AppError("не указано, кому писать") }
        if cfg.smtpUser.isEmpty || cfg.smtpPassword.isEmpty { throw AppError("не указан логин или пароль почты") }

        let file = FileManager.default.temporaryDirectory.appendingPathComponent("sitewatch-\(UUID().uuidString).eml")
        try Data(message(from: cfg.smtpUser, to: cfg.mailTo, subject: subject, body: body).utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }

        var args = ["--silent", "--show-error", "--ssl-reqd", "--max-time", "30",
                    "--url", "smtp://\(cfg.smtpHost):\(cfg.smtpPort)", "--mail-from", cfg.smtpUser]
        for rcpt in cfg.mailTo { args += ["--mail-rcpt", rcpt] }
        args += ["--upload-file", file.path, "--config", "-"]

        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/curl")
        p.arguments = args
        let input = Pipe()
        let errors = Pipe()
        p.standardInput = input
        p.standardError = errors
        p.standardOutput = FileHandle.nullDevice
        try p.run()

        let credentials = (cfg.smtpUser + ":" + cfg.smtpPassword)
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        input.fileHandleForWriting.write(Data("user = \"\(credentials)\"\n".utf8))
        input.fileHandleForWriting.closeFile()

        let errData = errors.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        if p.terminationStatus != 0 {
            let text = (String(data: errData, encoding: .utf8) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            throw AppError(explain(p.terminationStatus, text))
        }
    }

    // Коды curl — по-человечески.
    static func explain(_ code: Int32, _ raw: String) -> String {
        let known: String
        switch code {
        case 67: known = "сервер не принял логин или пароль"
        case 6: known = "сервер почты не найден (DNS)"
        case 7: known = "не удалось подключиться к серверу почты"
        case 28: known = "сервер почты не ответил вовремя"
        case 35, 60: known = "ошибка защищённого соединения с сервером почты"
        case 55, 56: known = "соединение с сервером почты оборвалось"
        default: known = "curl завершился с кодом \(code)"
        }
        return raw.isEmpty ? known : known + " (" + raw + ")"
    }

    // Подсказка о самой частой причине отказа.
    static func hint(_ host: String) -> String {
        if host == Config.gmailHost {
            return "\n\nДля Gmail нужен пароль приложения (16 букв) с myaccount.google.com/apppasswords — обычный пароль от почты не подойдёт; двухэтапная аутентификация должна быть включена."
        }
        if host == Config.yandexHost {
            return "\n\nНужен пароль приложения Яндекса и включённый доступ почтовых программ (IMAP)."
        }
        return "\n\nПроверьте логин, пароль приложения и smtp_host / smtp_port в config.txt."
    }

    // Письмо в формате RFC 5322: заголовки в UTF-8 (base64), текст — base64, строки через CRLF.
    static func message(from: String, to: [String], subject: String, body: String) -> String {
        let date = DateFormatter()
        date.locale = Locale(identifier: "en_US_POSIX")
        date.dateFormat = "EEE, dd MMM yyyy HH:mm:ss Z"
        let text = Data(body.utf8).base64EncodedString(options: [.lineLength76Characters, .endLineWithCarriageReturn, .endLineWithLineFeed])
        var h = ""
        h += "From: \(encodeHeader("SiteWatch")) <\(from)>\r\n"
        h += "To: \(to.joined(separator: ", "))\r\n"
        h += "Subject: \(encodeHeader(subject))\r\n"
        h += "Date: \(date.string(from: Date()))\r\n"
        h += "MIME-Version: 1.0\r\n"
        h += "Content-Type: text/plain; charset=UTF-8\r\n"
        h += "Content-Transfer-Encoding: base64\r\n"
        h += "\r\n"
        return h + text + "\r\n"
    }

    // Заголовок кусками по ≤45 байт UTF-8 (=?UTF-8?B?…?=), не разрывая символы.
    static func encodeHeader(_ s: String) -> String {
        var words: [String] = []
        var chunk = ""
        for ch in s {
            if chunk.utf8.count + String(ch).utf8.count > 45 {
                words.append(chunk)
                chunk = ""
            }
            chunk.append(ch)
        }
        if !chunk.isEmpty { words.append(chunk) }
        return words.map { "=?UTF-8?B?" + Data($0.utf8).base64EncodedString() + "?=" }.joined(separator: "\r\n ")
    }
}

// ===================================================================== система

// «Не давать компьютеру засыпать» — то же, что caffeinate -i: запрет сна по простою.
// Закрытую крышку ноутбука и «Сон» из меню это не отменяет.
enum Power {
    private static var assertion: IOPMAssertionID = 0
    private static var held = false

    static func keepAwake(_ on: Bool) {
        if on && !held {
            var id: IOPMAssertionID = 0
            let r = IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleSystemSleep as CFString,
                                                IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                                "SiteWatch следит за сайтами" as CFString, &id)
            if r == kIOReturnSuccess {
                assertion = id
                held = true
            }
        } else if !on && held {
            IOPMAssertionRelease(assertion)
            held = false
        }
    }
}

// Автозапуск при входе: файл ~/Library/LaunchAgents/ru.litewms.sitewatch.plist (аналог HKCU\...\Run).
enum Autostart {
    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(Const.bundleID).plist")
    }

    static var exePath: String { Bundle.main.executablePath ?? "" }

    // Запущено из «карантинной» копии (macOS временно перекладывает скачанные программы) — путь не постоянный.
    static var translocated: Bool { exePath.contains("/AppTranslocation/") }

    static func set(_ on: Bool) throws {
        let fm = FileManager.default
        if on {
            try fm.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let dict: [String: Any] = [
                "Label": Const.bundleID,
                "ProgramArguments": [exePath, Const.trayArg],
                "RunAtLoad": true,
                "ProcessType": "Interactive",
            ]
            let data = try PropertyListSerialization.data(fromPropertyList: dict, format: .xml, options: 0)
            try data.write(to: plistURL)
        } else if fm.fileExists(atPath: plistURL.path) {
            try fm.removeItem(at: plistURL)
        }
    }

    // Включён ли автозапуск именно этой копии программы.
    static func isOn() -> Bool {
        guard let data = try? Data(contentsOf: plistURL),
              let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil),
              let dict = plist as? [String: Any],
              let args = dict["ProgramArguments"] as? [String],
              let first = args.first else { return false }
        return first == exePath
    }
}

// Уведомления в Центре уведомлений. Не разрешены — запасной путь через osascript.
enum Notifier {
    private static var fallback = false

    static func setup() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, error in
            if !granted || error != nil { fallback = true }
        }
    }

    static func show(_ text: String, problem: Bool) {
        if fallback {
            osascript(text)
            return
        }
        let content = UNMutableNotificationContent()
        content.title = "SiteWatch"
        content.body = text
        if problem { content.sound = .default }
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            if error != nil { osascript(text) }
        }
    }

    private static func osascript(_ text: String) {
        let esc = text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        p.arguments = ["-e", "display notification \"\(esc)\" with title \"SiteWatch\""]
        try? p.run()
    }
}

// Кружок цвета состояния для строки меню.
enum Dot {
    static func image(_ color: NSColor) -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            color.setFill()
            NSBezierPath(ovalIn: NSRect(x: 3, y: 3, width: 12, height: 12)).fill()
            return true
        }
        img.isTemplate = false
        return img
    }
}

// Окна-сообщения.
func showInfo(_ text: String) {
    NSApp.activate(ignoringOtherApps: true)
    let a = NSAlert()
    a.messageText = "SiteWatch"
    a.informativeText = text
    a.addButton(withTitle: "OK")
    _ = a.runModal()
}

// Вопрос «да/нет». Кнопка по умолчанию — «Нет», чтобы случайный Enter ничего не сломал.
func askYesNo(_ title: String, _ text: String) -> Bool {
    NSApp.activate(ignoringOtherApps: true)
    let a = NSAlert()
    a.alertStyle = .warning
    a.messageText = title
    a.informativeText = text
    a.addButton(withTitle: "Нет")
    a.addButton(withTitle: "Да")
    return a.runModal() == .alertSecondButtonReturn
}

// ===================================================================== модель окна

final class AppModel: ObservableObject {
    @Published var snapshot = Snapshot()
    @Published var sitesText = ""
    @Published var mailText = ""
    @Published var user = ""
    @Published var password = ""
    @Published var keepAwake = true
    @Published var autostart = false
    @Published var sending = false

    private(set) var cfg: Config
    unowned let app: AppDelegate

    init(cfg: Config, app: AppDelegate) {
        self.cfg = cfg
        self.app = app
        loadFields()
    }

    // Строки окна, отсортированные: сначала упавшие, затем по алфавиту.
    var sortedSites: [SiteState] {
        snapshot.sites.sorted { a, b in a.isDown != b.isDown ? a.isDown : a.name < b.name }
    }

    // Перенести настройки в поля окна.
    func loadFields() {
        sitesText = cfg.sites.joined(separator: "\n")
        mailText = cfg.mailTo.joined(separator: "\n")
        user = cfg.smtpUser
        password = cfg.smtpPassword
        keepAwake = cfg.keepAwake
        autostart = Autostart.isOn()
    }

    // Строки поля: без пробелов по краям, без пустых и без повторов.
    static func lines(_ text: String) -> [String] {
        var seen = Set<String>()
        return text.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && seen.insert($0).inserted }
    }

    // Собрать настройки из полей окна (ещё не сохраняя).
    func collect() -> Config {
        var c = Config()
        c.sites = AppModel.lines(sitesText).map { $0.contains("://") ? $0 : "https://" + $0 }
        c.mailTo = AppModel.lines(mailText)
        c.smtpUser = user.trimmingCharacters(in: .whitespaces)
        c.smtpHost = Config.hostFor(c.smtpUser, fallback: cfg.smtpHost)
        c.smtpPort = cfg.smtpPort
        // Google показывает пароль приложения группами через пробел — в самом пароле пробелов нет.
        c.smtpPassword = c.smtpHost == Config.gmailHost ? String(password.filter { !$0.isWhitespace }) : password
        c.keepAwake = keepAwake
        return c
    }

    func save() {
        let c = collect()
        if autostart && Autostart.translocated {
            showInfo("Чтобы включить автозапуск, сначала перенесите SiteWatch.app в папку «Программы» и запустите оттуда.")
            return
        }
        do {
            try c.save()
            try Autostart.set(autostart)
        } catch {
            showInfo("Не сохранилось: " + error.localizedDescription)
            return
        }
        apply(c)
        showInfo("Сохранено.")
    }

    func testMail() {
        let c = collect()
        sending = true
        DispatchQueue.global().async {
            let failure: String?
            do {
                try Mailer.send(c, subject: "SiteWatch: проверка",
                                body: "Тестовое письмо с компьютера \(Machine.name) (macOS). Оповещения работают.")
                failure = nil
            } catch {
                failure = error.localizedDescription
            }
            DispatchQueue.main.async {
                self.sending = false
                if let f = failure {
                    showInfo("Не отправилось: " + f + Mailer.hint(c.smtpHost))
                } else {
                    showInfo("Письмо отправлено. Если не пришло — проверьте «Спам».")
                }
            }
        }
    }

    func reset() {
        guard askYesNo("Сбросить все настройки к значениям по умолчанию?",
                       "Список сайтов и адреса вернутся к исходным, пароль почты будет удалён из «Связки ключей» — его нужно будет ввести заново, иначе письма уходить не будут.") else { return }
        let d = Config()
        do {
            Keychain.deleteAll()
            try d.save()
        } catch {
            showInfo("Не сбросилось: " + error.localizedDescription)
            return
        }
        Log.write("Настройки сброшены к значениям по умолчанию")
        apply(d)
        showInfo("Настройки сброшены. Введите пароль приложения и нажмите «Сохранить».")
    }

    // Применить настройки: запрет сна, новый список сторожу, сразу проверить, обновить поля.
    private func apply(_ c: Config) {
        cfg = c
        Power.keepAwake(c.keepAwake)
        app.watcher.apply(c)
        app.watcher.checkNow()
        loadFields()
    }
}

// ===================================================================== окно (SwiftUI)

struct MainView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            HeaderBar(snapshot: model.snapshot)
            TabView {
                StatusView(model: model)
                    .tabItem { Text("Состояние") }
                SettingsView(model: model)
                    .tabItem { Text("Настройки") }
            }
            .padding(8)
        }
        .frame(minWidth: 660, maxWidth: .infinity, minHeight: 450, maxHeight: .infinity)
    }
}

// Цветная плашка сверху.
struct HeaderBar: View {
    let snapshot: Snapshot

    var body: some View {
        let line = snapshot.headline
        Text(line.0)
            .font(.system(size: 15, weight: .bold))
            .foregroundColor(.white)
            .lineLimit(1)
            .truncationMode(.tail)
            .frame(maxWidth: .infinity, minHeight: 42, alignment: .leading)
            .padding(.horizontal, 12)
            .background(Color(nsColor: line.1))
    }
}

// Вкладка «Состояние»: таблица сайтов и кнопки.
struct StatusView: View {
    @ObservedObject var model: AppModel
    static let widths: [CGFloat] = [140, 100, 160, 45, 80, 55]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            SiteRow(cells: ["Сайт", "Состояние", "Ответ", "мс", "Проверен", "Сбои с"], bold: true, color: .clear)
            Divider()
            // Раз в 5 секунд перерисовываем, чтобы «проверен … назад» не застывало.
            TimelineView(.periodic(from: Date(), by: 5)) { _ in
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(model.sortedSites) { s in
                            SiteRow(cells: StatusView.cells(s), bold: false, color: StatusView.rowColor(s))
                                .contentShape(Rectangle())
                                .onTapGesture(count: 2) {
                                    if let u = URL(string: s.url) { NSWorkspace.shared.open(u) }
                                }
                        }
                    }
                }
            }
            Divider()
            HStack {
                Button("Проверить сейчас") { model.app.watcher.checkNow() }
                Button("Журнал") { NSWorkspace.shared.open(Config.folder) }
                Spacer()
            }
            .padding(.top, 8)
        }
        .padding(8)
    }

    static func cells(_ s: SiteState) -> [String] {
        [
            s.name,
            stateText(s),
            s.details,
            s.lastCheck == nil ? "" : "\(s.ms)",
            s.lastCheck.map { Fmt.ago(Date().timeIntervalSince($0)) + " назад" } ?? "",
            s.downSince.map { Fmt.hm.string(from: $0) } ?? "",
        ]
    }

    static func stateText(_ s: SiteState) -> String {
        if s.ok == nil { return "ждём" }
        if s.isDown { return "НЕ РАБОТАЕТ" }
        if s.muted { return "адрес не найден" }
        if s.ok == false { return "сбой \(s.fails)/\(Const.failThreshold)" }
        return "работает"
    }

    static func rowColor(_ s: SiteState) -> Color {
        if s.ok == nil { return .clear }
        if s.isDown { return Color(nsColor: Palette.red).opacity(0.25) }
        if s.muted { return Color.gray.opacity(0.2) }
        if s.ok == false { return Color(nsColor: Palette.yellow).opacity(0.25) }
        return Color(nsColor: Palette.green).opacity(0.18)
    }
}

// Строка таблицы с колонками фиксированной ширины.
struct SiteRow: View {
    let cells: [String]
    let bold: Bool
    let color: Color

    var body: some View {
        HStack(spacing: 0) {
            ForEach(cells.indices, id: \.self) { i in
                Text(cells[i])
                    .fontWeight(bold ? .semibold : .regular)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(width: StatusView.widths[i], alignment: .leading)
                    .padding(.horizontal, 3)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 4)
        .background(color)
    }
}

// Вкладка «Настройки».
struct SettingsView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                row("Сайты (по одному в строке)") { editor($model.sitesText) }
                row("Кому писать") { editor($model.mailText) }
                row("Почта-отправитель (Gmail)") {
                    TextField("", text: $model.user)
                        .autocorrectionDisabled()
                        .frame(width: 260)
                }
                row("Пароль приложения") {
                    SecureField("", text: $model.password)
                        .frame(width: 260)
                }
                row("") {
                    Toggle("Не давать компьютеру засыпать", isOn: $model.keepAwake)
                        .toggleStyle(.checkbox)
                }
                row("") {
                    Toggle("Запускать при входе в систему", isOn: $model.autostart)
                        .toggleStyle(.checkbox)
                }
                row("") {
                    HStack {
                        Button("Сохранить") { model.save() }
                        Button("Тестовое письмо") { model.testMail() }
                            .disabled(model.sending)
                    }
                }
                row("") {
                    HStack {
                        Button("Сбросить настройки") { model.reset() }
                        Button("Выйти из программы") { model.app.quitAsk(nil) }
                    }
                }
                row("") {
                    Text("Проверка раз в \(Int(Const.intervalSeconds)) с; упал = \(Const.failThreshold) неудачи подряд; пока лежит — напоминание раз в \(Int(Const.reminderMinutes)) мин. Пароль хранится в «Связке ключей». Настройки: \(Config.folder.path)")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func row<Content: View>(_ label: String, @ViewBuilder _ content: () -> Content) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(label)
                .frame(width: 190, alignment: .leading)
                .padding(.top, 3)
            content()
            Spacer(minLength: 0)
        }
    }

    private func editor(_ text: Binding<String>) -> some View {
        TextEditor(text: text)
            .font(.body)
            .autocorrectionDisabled()
            .frame(width: 360, height: 90)
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.secondary.opacity(0.4)))
    }
}

// ===================================================================== приложение

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    var watcher: Watcher!
    var model: AppModel!
    var statusItem: NSStatusItem!
    var window: NSWindow!

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Одна копия: если SiteWatch уже запущен — просим его показать окно и выходим.
        let me = ProcessInfo.processInfo.processIdentifier
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? Const.bundleID)
            .filter { $0.processIdentifier != me }
        if !others.isEmpty {
            DistributedNotificationCenter.default().postNotificationName(
                Notification.Name(Const.showSignal), object: nil, userInfo: nil, deliverImmediately: true)
            NSApp.terminate(nil)
            return
        }
        DistributedNotificationCenter.default().addObserver(
            self, selector: #selector(showFromOtherCopy(_:)), name: Notification.Name(Const.showSignal), object: nil)

        let hidden = CommandLine.arguments.contains(Const.trayArg)
        Log.write("Запуск \(Const.version)" + (hidden ? " (автозапуск)" : ""))

        let cfg = Config.load()
        watcher = Watcher(cfg)
        model = AppModel(cfg: cfg, app: self)

        buildMainMenu()
        buildStatusItem()
        buildWindow()

        watcher.onChange = { [weak self] snap in self?.update(snap) }
        watcher.onNotify = { text, problem in Notifier.show(text, problem: problem) }
        UNUserNotificationCenter.current().delegate = self
        Notifier.setup()

        Power.keepAwake(cfg.keepAwake)
        watcher.start()
        if !hidden { openWindow(nil) }
    }

    // Повторный запуск из Finder или Launchpad, когда программа уже работает, — показать окно.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openWindow(nil)
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        Power.keepAwake(false)
        Log.write("Выход")
    }

    // Меню программы. Без меню «Правка» в полях окна не работают ⌘C / ⌘V / ⌘A.
    private func buildMainMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem()
        main.addItem(appItem)
        let appMenu = NSMenu()
        appItem.submenu = appMenu
        appMenu.addItem(withTitle: "Закрыть окно", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        let quit = appMenu.addItem(withTitle: "Выйти из SiteWatch", action: #selector(quitAsk(_:)), keyEquivalent: "q")
        quit.target = self

        let editItem = NSMenuItem()
        main.addItem(editItem)
        let edit = NSMenu(title: "Правка")
        editItem.submenu = edit
        edit.addItem(withTitle: "Отменить", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Повторить", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Вырезать", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Копировать", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Вставить", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Выделить всё", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")

        NSApp.mainMenu = main
    }

    // Значок в строке меню и его меню.
    private func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = Dot.image(Palette.blue)
        statusItem.button?.toolTip = "SiteWatch"
        let menu = NSMenu()
        menu.addItem(withTitle: "Открыть", action: #selector(openWindow(_:)), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Проверить сейчас", action: #selector(checkNow(_:)), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Выход", action: #selector(quitAsk(_:)), keyEquivalent: "").target = self
        statusItem.menu = menu
    }

    // Окно создаётся один раз; красная кнопка его только прячет — проверка продолжается.
    private func buildWindow() {
        let host = NSHostingController(rootView: MainView(model: model))
        host.sizingOptions = []
        window = NSWindow(contentViewController: host)
        window.title = "SiteWatch \(Const.version)"
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.isReleasedWhenClosed = false
        window.setContentSize(NSSize(width: 700, height: 700))
        window.minSize = NSSize(width: 660, height: 450)
        window.center()
    }

    private func update(_ snap: Snapshot) {
        model.snapshot = snap
        statusItem.button?.image = Dot.image(snap.headline.1)
        statusItem.button?.toolTip = snap.tooltip
    }

    @objc func openWindow(_ sender: Any?) {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    @objc func checkNow(_ sender: Any?) {
        watcher.checkNow()
    }

    @objc func showFromOtherCopy(_ note: Notification) {
        DispatchQueue.main.async { self.openWindow(nil) }
    }

    @objc func quitAsk(_ sender: Any?) {
        if askYesNo("Выйти из SiteWatch?", "Пока программа закрыта, о падении сайтов никто не узнает.") {
            NSApp.terminate(nil)
        }
    }

    // Уведомления показываем, даже когда окно программы на переднем плане.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }

    // Щелчок по уведомлению — открыть окно.
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        DispatchQueue.main.async { self.openWindow(nil) }
        completionHandler()
    }
}

// ===================================================================== запуск

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
// Без значка в Dock — только в строке меню (как значок в трее на Windows).
app.setActivationPolicy(.accessory)
app.run()
