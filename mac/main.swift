// SiteWatch для macOS — паук-звонарь: следит за нашими сайтами, при падении пишет на почту,
// а через GSM-модем шлёт SMS и звонит. Та же логика, что у Windows-версии (main.cs), но родными средствами Мака:
//   • значок в строке меню вместо значка в трее;
//   • пароль почты — в «Связке ключей» вместо DPAPI;
//   • уведомления — в Центре уведомлений;
//   • запрет сна — через IOKit (то же, что делает caffeinate);
//   • автозапуск — LaunchAgent в ~/Library/LaunchAgents вместо реестра;
//   • письма уходят через системный curl (SMTP + STARTTLS), своих библиотек не нужно;
//   • модем — через последовательный порт /dev/cu.* (POSIX termios), AT-команды те же, что в Windows-версии.
// Один файл Swift (AppKit + SwiftUI), собирается build.sh без Xcode-проекта. Нужна macOS 13 Ventura или новее.

import AppKit
import SwiftUI
import Security
import IOKit.pwr_mgt
import UserNotifications

// ===================================================================== константы

enum Const {
    // Версия: заголовок окна, журнал, User-Agent. Держать в согласии с Info.plist.
    static let version = "0.5.0"
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

// Настройки в ~/Library/Application Support/SiteWatch/config.txt — тот же формат, что у Windows-версии;
// порт модема — путь вида /dev/cu.usbserial-1420 (ключ modem_port). Пароль — в «Связке ключей».
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
    // Телефоны для SMS и звонков и порт модема; пусто — модемом не пользуемся.
    var phones: [String] = []
    var modemPort = ""
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
            var phones: [String] = []
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
                case "phones":
                    phones.append(line)
                case "options":
                    guard let eq = line.firstIndex(of: "="), eq != line.startIndex else { continue }
                    let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
                    let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
                    switch key {
                    case "smtp_host": cfg.smtpHost = value
                    case "smtp_port": if let p = Int(value) { cfg.smtpPort = p }
                    case "smtp_user": cfg.smtpUser = value
                    case "modem_port": cfg.modemPort = value
                    case "keep_awake": cfg.keepAwake = value != "0"
                    default: break
                    }
                default:
                    break
                }
            }
            cfg.sites = sites
            cfg.mailTo = mail
            cfg.phones = phones
        }
        cfg.smtpHost = hostFor(cfg.smtpUser, fallback: cfg.smtpHost)
        cfg.smtpPassword = Keychain.get(cfg.smtpUser)
        return cfg
    }

    // Записать настройки в файл (целиком) и пароль — в «Связку ключей».
    func save() throws {
        try FileManager.default.createDirectory(at: Config.folder, withIntermediateDirectories: true)
        var s = "# SiteWatch. Сайты, почта и телефоны — по одному в строке. Пароль хранится в «Связке ключей», не здесь.\n"
        s += "[sites]\n" + sites.map { $0 + "\n" }.joined()
        s += "[mail]\n" + mailTo.map { $0 + "\n" }.joined()
        s += "[phones]\n" + phones.map { $0 + "\n" }.joined()
        s += "[options]\n"
        s += "smtp_host=\(smtpHost)\n"
        s += "smtp_port=\(smtpPort)\n"
        s += "smtp_user=\(smtpUser)\n"
        s += "modem_port=\(modemPort)\n"
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
    // Сколько кругов подряд у этого компьютера нет интернета и ушло ли об этом SMS через модем.
    private var offlineRounds = 0
    private var offlineSmsSent = false

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
            // Интернета нет failThreshold кругов подряд — сайты никто не проверяет, а письмо не отправить.
            // Модем ходит через сотовую сеть, интернет компьютера ему не нужен — одно SMS об этом.
            offlineRounds += 1
            if offlineRounds == Const.failThreshold {
                let cfg = self.cfg
                DispatchQueue.global().async {
                    let sent = self.modem(cfg, sms: "SiteWatch (\(Machine.name)): у компьютера нет интернета, сайты не проверяются. " + Fmt.hm.string(from: Date()), call: false)
                    self.q.async {
                        self.offlineSmsSent = sent
                        self.roundDone()
                    }
                }
            } else {
                roundDone()
            }
            return
        }
        if !internetOk {
            internetOk = true
            Log.write("Интернет снова есть")
            say("Интернет снова есть", false)
        }
        // Если о пропаже интернета уходило SMS — после круга сообщим, что проверка снова идёт.
        offlineRounds = 0
        let backOnline = offlineSmsSent
        offlineSmsSent = false

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

        // Оповещения — вне очереди сторожа: письма, SMS и звонки идут долго. Следующий круг — после них.
        let cfg = self.cfg
        let downList = down, stillList = still, upList = up
        DispatchQueue.global().async {
            if backOnline {
                self.modem(cfg, sms: "SiteWatch (\(Machine.name)): интернет снова есть, сайты проверяются. " + Fmt.hm.string(from: Date()), call: false)
            }
            if !downList.isEmpty {
                // Упали: письмо, SMS с причиной и звонок.
                self.say("Не работает: " + Watcher.names(downList), true)
                self.alert(cfg, "НЕ РАБОТАЕТ: " + Watcher.names(downList), Watcher.body("Перестали отвечать:", downList, now),
                           sms: Watcher.smsText("НЕ РАБОТАЕТ", downList, now, reasons: true), call: true)
            }
            if !stillList.isEmpty {
                // Всё ещё лежат: письмо и звонок (причина уже была в первом SMS, лишние SMS не шлём).
                self.alert(cfg, "ВСЁ ЕЩЁ НЕ РАБОТАЕТ: " + Watcher.names(stillList), Watcher.body("По-прежнему не отвечают:", stillList, now),
                           sms: nil, call: true)
            }
            if !upList.isEmpty {
                // Поднялись: письмо и SMS, без звонка.
                self.say("Снова работает: " + Watcher.names(upList), false)
                self.alert(cfg, "Снова работает: " + Watcher.names(upList), Watcher.body("Снова работают:", upList, now),
                           sms: Watcher.smsText("Снова работает", upList, now, reasons: false), call: false)
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

    // Оповещение: письмо и модем (SMS, звонок) — одновременно. Если почта «висит» без связи с сервером,
    // SMS и звонок не ждут её. sms = nil — без SMS; call — звонить ли. Сбой одного не отменяет другое.
    private func alert(_ cfg: Config, _ subject: String, _ body: String, sms: String?, call: Bool) {
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async {
            do {
                try Mailer.send(cfg, subject: subject, body: body)
                Log.write("Письмо: " + subject)
            } catch {
                Log.write("ERROR письмо не ушло: " + error.localizedDescription)
                self.say("Письмо не отправлено: " + error.localizedDescription, true)
            }
            group.leave()
        }
        group.enter()
        DispatchQueue.global().async {
            self.modem(cfg, sms: sms, call: call)
            group.leave()
        }
        group.wait()
    }

    // SMS и/или звонок на все номера через модем — если указаны номера и порт. Вернёт true, если SMS ушло.
    @discardableResult
    private func modem(_ cfg: Config, sms: String?, call: Bool) -> Bool {
        if cfg.phones.isEmpty || cfg.modemPort.isEmpty { return false }
        var sent = false
        if let text = sms {
            do {
                try Modem.sms(port: cfg.modemPort, phones: cfg.phones, text: text)
                sent = true
            } catch {
                Log.write("ERROR SMS не ушло: " + error.localizedDescription)
                say("SMS не отправлено: " + error.localizedDescription, true)
            }
        }
        if call {
            do {
                try Modem.call(port: cfg.modemPort, phones: cfg.phones)
            } catch {
                Log.write("ERROR звонок не прошёл: " + error.localizedDescription)
                say("Звонок не прошёл: " + error.localizedDescription, true)
            }
        }
        return sent
    }

    // Короткий текст SMS: «НЕ РАБОТАЕТ: litewms.ru (502 Bad Gateway), lite-box.ru (нет ответа за 15 с). 14:05».
    // Причины — только для упавших. Длинный текст модем отправит частями, телефон склеит их в одно сообщение.
    static func smsText(_ title: String, _ list: [SiteState], _ now: Date, reasons: Bool) -> String {
        title + ": " + list.map { reasons ? "\($0.name) (\($0.details))" : $0.name }.joined(separator: ", ") + ". " + Fmt.hm.string(from: now)
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

// ===================================================================== GSM-модем

// Последовательный порт модема (/dev/cu.*): 115200 бод, 8 бит, без чётности и без аппаратного управления потоком.
// Открывается без ожидания линии (O_NONBLOCK), чтение — с ожиданием через poll(). Закрывается, когда объект освобождается.
final class Serial {
    private let fd: Int32

    init(_ path: String) throws {
        fd = Darwin.open(path, O_RDWR | O_NOCTTY | O_NONBLOCK)
        if fd < 0 { throw AppError("порт \(path) не открылся: " + String(cString: strerror(errno))) }
        var t = termios()
        if tcgetattr(fd, &t) != 0 { throw AppError("порт \(path): не удалось прочитать настройки") }
        cfmakeraw(&t)
        cfsetspeed(&t, speed_t(B115200))
        t.c_cflag |= tcflag_t(CLOCAL | CREAD | CS8)
        t.c_cflag &= ~tcflag_t(PARENB | CSTOPB | CCTS_OFLOW | CRTS_IFLOW)
        if tcsetattr(fd, TCSANOW, &t) != 0 { throw AppError("порт \(path): не удалось задать скорость 115200") }
        tcflush(fd, TCIOFLUSH)
    }

    deinit {
        if fd >= 0 { Darwin.close(fd) }
    }

    // Выбросить всё, что модем прислал раньше (остатки ответов, RING и т. п.).
    func discard() {
        tcflush(fd, TCIFLUSH)
    }

    // Записать строку целиком; модем не принимает данные 3 с — ошибка.
    func send(_ text: String) throws {
        let bytes = Array(text.utf8)
        var offset = 0
        let deadline = Date().addingTimeInterval(3)
        while offset < bytes.count {
            let n = bytes.withUnsafeBufferPointer { Darwin.write(fd, $0.baseAddress! + offset, $0.count - offset) }
            if n > 0 { offset += n; continue }
            if n < 0 && errno != EAGAIN { throw AppError("порт: ошибка записи (" + String(cString: strerror(errno)) + ")") }
            if Date() > deadline { throw AppError("порт: модем не принимает данные") }
            usleep(20_000)
        }
    }

    // Прочитать то, что пришло, ожидая до seconds секунд. Пустая строка — ничего не пришло.
    func receive(wait seconds: Double) -> String {
        var pfd = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
        if poll(&pfd, 1, Int32(max(0, seconds) * 1000)) <= 0 { return "" }
        var buf = [UInt8](repeating: 0, count: 1024)
        let n = Darwin.read(fd, &buf, buf.count)
        if n <= 0 { return "" }
        return String(decoding: buf[0..<n], as: UTF8.self)
    }

    // AT-команда; вернуть ответ модема одной строкой (без эха и OK). mustOk — без OK бросить ошибку.
    // Ждём не дольше timeout секунд; ERROR, +CME/+CMS ERROR, NO CARRIER, BUSY — ждать дальше нечего.
    @discardableResult
    func at(_ cmd: String, mustOk: Bool = false, timeout: Double = 5) throws -> String {
        discard()
        try send(cmd + "\r")
        var got = ""
        var answer: [String] = []
        var ok = false
        var done = false
        let deadline = Date().addingTimeInterval(timeout)
        while !done && Date() < deadline {
            let chunk = receive(wait: min(0.5, deadline.timeIntervalSinceNow))
            if chunk.isEmpty { continue }
            got += chunk
            // Разбираем только законченные строки; хвост без перевода строки ждёт продолжения.
            while let r = got.rangeOfCharacter(from: CharacterSet(charactersIn: "\r\n")) {
                let line = got[..<r.lowerBound].trimmingCharacters(in: .whitespaces)
                got.removeSubrange(got.startIndex..<r.upperBound)
                if line.isEmpty || line == cmd { continue }
                if line == "OK" { ok = true; done = true; break }
                answer.append(line)
                if line.hasPrefix("ERROR") || line.hasPrefix("+CME") || line.hasPrefix("+CMS") || line == "NO CARRIER" || line == "BUSY" {
                    done = true
                    break
                }
            }
        }
        let text = answer.joined(separator: " ")
        if mustOk && !ok { throw AppError("модем не ответил OK на \(cmd) (\(text))") }
        return text
    }

    // Ждать в ответе модема token (например, «>» или «OK») до seconds секунд. ERROR — ошибка с текстом модема.
    func waitFor(_ token: String, seconds: Double) throws -> String {
        var got = ""
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            got += receive(wait: min(0.5, deadline.timeIntervalSinceNow))
            if got.contains("ERROR") { throw AppError("модем: " + Serial.clean(got)) }
            if got.contains(token) { return got }
        }
        throw AppError("модем не ответил вовремя (" + Serial.clean(got) + ")")
    }

    // Ждать конца вызова до seconds секунд: сбросили, занято, не дозвонились — вернуть это слово; иначе пустую строку.
    func waitCallEnd(seconds: Double) -> String {
        var got = ""
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            got += receive(wait: min(0.5, deadline.timeIntervalSinceNow))
            for word in ["NO CARRIER", "BUSY", "NO ANSWER", "NO DIALTONE"] where got.contains(word) { return word }
        }
        return ""
    }

    // Ответ модема одной строкой: без пустых строк и переводов строки.
    static func clean(_ s: String) -> String {
        s.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }.joined(separator: " ")
    }
}

// SMS и звонки через GSM-модем (AT-команды). Модем один — тревоги и тестовые кнопки работают с ним строго по очереди.
enum Modem {
    private static let lock = NSLock()

    // Последовательные порты Мака. Bluetooth и отладочные порты пропускаем: модема там не бывает.
    static func listPorts() -> [String] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: "/dev")) ?? []
        return names.filter { $0.hasPrefix("cu.") && !$0.contains("Bluetooth") && !$0.contains("debug-console") && !$0.contains("wlan") }
            .sorted()
            .map { "/dev/" + $0 }
    }

    // SMS на все номера (PDU-режим, UCS-2 — кириллица; длинный текст — частями, телефон склеит).
    // Ошибка на одном номере не мешает остальным; в конце — ошибка со списком номеров, куда не ушло.
    static func sms(port: String, phones: [String], text: String) throws {
        lock.lock()
        defer { lock.unlock() }
        let p = try Serial(port)
        try prepare(p)
        // Модем мог только что включиться и ещё ищет сеть — ждём регистрацию до 30 с.
        if !waitNetwork(p, seconds: 30) { Log.write("Модем: нет регистрации в сети, всё равно пробую отправить SMS") }
        try p.at("AT+CMGF=0", mustOk: true)
        var errors: [String] = []
        for phone in phones {
            do {
                let parts = try SmsPdu.encode(phone: phone, text: text, reference: UInt8.random(in: 0...255))
                for pdu in parts { try sendPdu(p, pdu) }
                Log.write("SMS → " + phone + (parts.count > 1 ? " (\(parts.count) части)" : ""))
            } catch {
                errors.append(phone + ": " + error.localizedDescription)
                Log.write("ERROR SMS → " + phone + ": " + error.localizedDescription)
            }
        }
        if !errors.isEmpty { throw AppError(errors.joined(separator: "; ")) }
    }

    // Одна часть SMS: AT+CMGS=<длина> → приглашение «>» → PDU и Ctrl+Z → «+CMGS: n» и OK.
    private static func sendPdu(_ p: Serial, _ pdu: String) throws {
        // Длина — в байтах и без первого байта PDU (адрес SMS-центра «00» = взять из SIM-карты).
        let length = pdu.count / 2 - 1
        p.discard()
        try p.send("AT+CMGS=\(length)\r")
        do {
            _ = try p.waitFor(">", seconds: 10)
        } catch {
            // Приглашения не дождались — Esc отменяет ввод, чтобы модем не ждал текст вечно.
            try? p.send("\u{1B}")
            throw error
        }
        try p.send(pdu + "\u{1A}")
        // Сеть может подтверждать SMS до минуты.
        let answer = try p.waitFor("OK", seconds: 60)
        if !answer.contains("+CMGS") { throw AppError("модем не подтвердил отправку (" + Serial.clean(answer) + ")") }
    }

    // Звонок на все номера: ATD номер; — вызов, до 30 с ожидания, ATH — сброс. Трубку брать не нужно,
    // звонок и есть сигнал; сбросили или занято — сразу следующий номер.
    static func call(port: String, phones: [String]) throws {
        lock.lock()
        defer { lock.unlock() }
        let p = try Serial(port)
        try prepare(p)
        _ = waitNetwork(p, seconds: 30)
        // OK на ATD — сразу, не дожидаясь, пока снимут трубку.
        try p.at("AT+COLP=0")
        var errors: [String] = []
        for raw in phones {
            let phone = SmsPdu.clean(raw)
            Log.write("Звоню " + phone)
            do {
                // ATD<номер>; — голосовой вызов (точка с запятой = голос).
                try p.at("ATD\(phone);", mustOk: true)
                let end = p.waitCallEnd(seconds: 30)
                if !end.isEmpty { Log.write("Звонок \(phone): \(end)") }
            } catch {
                errors.append(phone + ": " + error.localizedDescription)
            }
            // В любом случае кладём трубку и даём модему 2 с передышки перед следующим номером.
            _ = try? p.at("ATH")
            Thread.sleep(forTimeInterval: 2)
        }
        if !errors.isEmpty { throw AppError(errors.joined(separator: "; ")) }
    }

    // «Найти модем»: опросить порты; модем — где ответили на AT (тогда описание начинается со слова «модем»).
    static func probe(_ ports: [String]) -> [(String, String)] {
        ports.map { ($0, probeOne($0)) }
    }

    // Один порт: AT → модель (AT+CGMM), SIM-карта (AT+CPIN?), регистрация в сети. Без общего замка:
    // если модем сейчас звонит, он просто не ответит. Ошибки не выпускает.
    private static func probeOne(_ path: String) -> String {
        do {
            let p = try Serial(path)
            do {
                try p.at("AT", mustOk: true, timeout: 2)
            } catch {
                usleep(300_000)
                do { try p.at("AT", mustOk: true, timeout: 2) } catch { return "не модем (нет ответа на AT)" }
            }
            try p.at("ATE0")
            let model = try p.at("AT+CGMM")
            let pin = try p.at("AT+CPIN?")
            let sim = pin.uppercased().contains("READY") ? "SIM готова" : "SIM: " + (pin.isEmpty ? "нет ответа" : pin)
            return "модем " + (model.isEmpty ? "(модель не назвал)" : model) + ", " + sim + ", " + (registered(p) ? "сеть есть" : "сети нет")
        } catch {
            return "ошибка: " + error.localizedDescription
        }
    }

    // Подготовка: модем на связи, эхо выключено, ошибки — словами (AT+CMEE=2), SIM-карта готова.
    private static func prepare(_ p: Serial) throws {
        // Первый AT после открытия порта иногда теряется (модем подстраивает скорость) — даём вторую попытку.
        do {
            try p.at("AT", mustOk: true)
        } catch {
            usleep(500_000)
            try p.at("AT", mustOk: true)
        }
        try p.at("ATE0")
        try p.at("AT+CMEE=2")
        let pin = try p.at("AT+CPIN?")
        if !pin.uppercased().contains("READY") {
            throw AppError("SIM-карта не готова: " + (pin.isEmpty ? "нет ответа" : pin) + " — вставлена ли SIM, не стоит ли на ней PIN-код")
        }
    }

    // Ждать регистрации в сети до seconds секунд (опрос раз в 2 с).
    private static func waitNetwork(_ p: Serial, seconds: Double) -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while true {
            if registered(p) { return true }
            if Date() >= deadline { return false }
            Thread.sleep(forTimeInterval: 2)
        }
    }

    // Зарегистрирован ли модем: «+CREG: 0,1» — домашняя сеть, «0,5» — роуминг. 4G-модемы могут сообщать это только в +CEREG.
    private static func registered(_ p: Serial) -> Bool {
        regStat((try? p.at("AT+CREG?")) ?? "", "+CREG:") || regStat((try? p.at("AT+CEREG?")) ?? "", "+CEREG:")
    }

    private static func regStat(_ answer: String, _ prefix: String) -> Bool {
        guard let r = answer.range(of: prefix) else { return false }
        let parts = answer[r.upperBound...].split(separator: ",")
        guard parts.count >= 2 else { return false }
        let stat = parts[1].trimmingCharacters(in: CharacterSet(charactersIn: " \""))
        return stat == "1" || stat == "5"
    }
}

// SMS в формате PDU (3GPP TS 23.040, SMS-SUBMIT) в кодировке UCS-2. Одна SMS — до 70 символов;
// длиннее — части по 67 с заголовком склейки. Больше 4 частей (268 символов) не шлём — остальное обрезается многоточием.
enum SmsPdu {
    private static let maxParts = 4

    // Номер без пробелов, скобок и дефисов; «+» в начале сохраняется: «+7 (989) 700-48-20» → «+79897004820».
    static func clean(_ phone: String) -> String {
        let s = phone.trimmingCharacters(in: .whitespaces)
        return (s.hasPrefix("+") ? "+" : "") + s.filter { $0.isASCII && $0.isNumber }
    }

    // Готовые PDU (шестнадцатеричные строки) — по одной на каждую часть SMS.
    static func encode(phone: String, text: String, reference: UInt8) throws -> [String] {
        let number = clean(phone)
        let digits = number.hasPrefix("+") ? String(number.dropFirst()) : number
        if digits.isEmpty { throw AppError("пустой номер телефона") }

        // Режем текст (в UTF-16 — как и считает UCS-2) на части, не разрывая суррогатные пары.
        let units = Array(text.utf16)
        var chunks: [[UInt16]] = []
        if units.count <= 70 {
            chunks = [units]
        } else {
            var pos = 0
            while pos < units.count && chunks.count < maxParts {
                var len = min(67, units.count - pos)
                if len < units.count - pos && UTF16.isLeadSurrogate(units[pos + len - 1]) { len -= 1 }
                chunks.append(Array(units[pos..<(pos + len)]))
                pos += len
            }
            if pos < units.count {
                chunks[chunks.count - 1][chunks[chunks.count - 1].count - 1] = 0x2026  // «…»
            }
        }

        let multi = chunks.count > 1
        return chunks.enumerated().map { (i, chunk) -> String in
            // Данные: [заголовок склейки 05 00 03 <номер склейки> <всего частей> <эта часть>] + текст в UCS-2.
            var ud: [UInt8] = multi ? [0x05, 0x00, 0x03, reference, UInt8(chunks.count), UInt8(i + 1)] : []
            for u in chunk { ud.append(UInt8(u >> 8)); ud.append(UInt8(u & 0xFF)) }
            var pdu = "00"                                         // адрес SMS-центра — из SIM-карты
            pdu += multi ? "41" : "01"                             // SMS-SUBMIT; +0x40 — есть заголовок склейки
            pdu += "00"                                            // номер сообщения назначит модем
            pdu += String(format: "%02lX", digits.count)           // длина номера в цифрах
            pdu += number.hasPrefix("+") ? "91" : "81"             // 91 — международный (+7…), 81 — как набран (8…)
            pdu += swap(digits)                                    // цифры парами наоборот, нечётная — с F
            pdu += "00"                                            // протокол — обычная SMS
            pdu += "08"                                            // кодировка UCS-2
            pdu += String(format: "%02lX", ud.count)               // длина данных в байтах
            pdu += ud.map { String(format: "%02X", $0) }.joined()
            return pdu
        }
    }

    // «79897004820» → «9798074028F0»: в PDU цифры номера идут парами, переставленными местами.
    private static func swap(_ digits: String) -> String {
        var d = Array(digits)
        if d.count % 2 == 1 { d.append("F") }
        var out = ""
        for i in stride(from: 0, to: d.count, by: 2) { out.append(d[i + 1]); out.append(d[i]) }
        return out
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
    @Published var phonesText = ""
    @Published var modemPort = ""
    @Published var ports: [String] = []
    @Published var keepAwake = true
    @Published var autostart = false
    // Идёт долгая операция (письмо, SMS, звонок, поиск модема) — кнопки этих операций неактивны.
    @Published var busy = false

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
        phonesText = cfg.phones.joined(separator: "\n")
        modemPort = cfg.modemPort
        ports = Modem.listPorts()
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
        c.phones = AppModel.lines(phonesText)
        c.modemPort = modemPort.trimmingCharacters(in: .whitespaces)
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
        busy = true
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
                self.busy = false
                if let f = failure {
                    showInfo("Не отправилось: " + f + Mailer.hint(c.smtpHost))
                } else {
                    showInfo("Письмо отправлено. Если не пришло — проверьте «Спам».")
                }
            }
        }
    }

    // «Тестовое SMS» и «Тестовый звонок» — на первый номер из списка, через выбранный порт (даже без сохранения).
    func testSms() {
        let c = collect()
        guard let first = c.phones.first, !c.modemPort.isEmpty else {
            showInfo("Укажите номер телефона и порт модема (кнопка «Найти модем»).")
            return
        }
        runModem({ try Modem.sms(port: c.modemPort, phones: [first], text: "SiteWatch: тестовое SMS с компьютера \(Machine.name) (macOS). Оповещения работают.") },
                 ok: "SMS отправлено на \(first).", fail: "SMS не ушло: ")
    }

    func testCall() {
        let c = collect()
        guard let first = c.phones.first, !c.modemPort.isEmpty else {
            showInfo("Укажите номер телефона и порт модема (кнопка «Найти модем»).")
            return
        }
        runModem({ try Modem.call(port: c.modemPort, phones: [first]) },
                 ok: "Модем позвонил на \(first) — телефон должен был звонить до 30 секунд.", fail: "Звонок не прошёл: ")
    }

    // Выполнить работу с модемом в фоне и показать итог.
    private func runModem(_ work: @escaping () throws -> Void, ok: String, fail: String) {
        busy = true
        DispatchQueue.global().async {
            var failure: String?
            do { try work() } catch { failure = error.localizedDescription }
            DispatchQueue.main.async {
                self.busy = false
                showInfo(failure.map { fail + $0 } ?? ok)
            }
        }
    }

    // «Найти модем»: опросить последовательные порты и выбрать тот, где ответил модем.
    func findModem() {
        let list = Modem.listPorts()
        ports = list
        if list.isEmpty {
            showInfo("Последовательных портов нет: модем не подключён или для него не установлен драйвер (для SIM800C — драйвер CH340).")
            return
        }
        busy = true
        DispatchQueue.global().async {
            let found = Modem.probe(list)
            DispatchQueue.main.async {
                self.busy = false
                let text = found.map { "\($0.0) — \($0.1)" }.joined(separator: "\n")
                if let best = found.first(where: { $0.1.hasPrefix("модем") })?.0 {
                    self.modemPort = best
                    showInfo(text + "\n\nВыбран \(best). Нажмите «Сохранить», затем «Тестовое SMS».")
                } else {
                    showInfo(text + "\n\nМодем не найден: ни один порт не ответил на AT. Проверьте драйвер модема и что его порт не занят другой программой.")
                }
            }
        }
    }

    func reset() {
        guard askYesNo("Сбросить все настройки к значениям по умолчанию?",
                       "Список сайтов, адреса, телефоны и порт модема вернутся к исходным, пароль почты будет удалён из «Связки ключей» — его нужно будет ввести заново, иначе письма уходить не будут.") else { return }
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
                row("Телефоны: SMS и звонок (+79…)") { editor($model.phonesText, height: 60) }
                row("Порт модема") {
                    HStack {
                        TextField("/dev/cu.…", text: $model.modemPort)
                            .autocorrectionDisabled()
                            .frame(width: 210)
                        // Выбор из найденных последовательных портов; список можно обновить.
                        Menu("Порты") {
                            if model.ports.isEmpty { Text("Портов нет") }
                            ForEach(model.ports, id: \.self) { port in
                                Button(port) { model.modemPort = port }
                            }
                            Divider()
                            Button("Обновить список") { model.ports = Modem.listPorts() }
                        }
                        .fixedSize()
                        // Опросит порты и выберет тот, где ответил модем.
                        Button("Найти модем") { model.findModem() }
                            .disabled(model.busy)
                    }
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
                            .disabled(model.busy)
                        Button("Тестовое SMS") { model.testSms() }
                            .disabled(model.busy)
                        Button("Тестовый звонок") { model.testCall() }
                            .disabled(model.busy)
                    }
                }
                row("") {
                    HStack {
                        Button("Сбросить настройки") { model.reset() }
                        Button("Выйти из программы") { model.app.quitAsk(nil) }
                    }
                }
                row("") {
                    Text("Проверка раз в \(Int(Const.intervalSeconds)) с; упал = \(Const.failThreshold) неудачи подряд; пока лежит — напоминание раз в \(Int(Const.reminderMinutes)) мин. Модем (если указаны порт и номера): сайт упал — SMS с причиной и звонок, напоминания — звонком, сайт поднялся — SMS; у компьютера пропал интернет — SMS. Пароль хранится в «Связке ключей». Настройки: \(Config.folder.path)")
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

    private func editor(_ text: Binding<String>, height: CGFloat = 90) -> some View {
        TextEditor(text: text)
            .font(.body)
            .autocorrectionDisabled()
            .frame(width: 360, height: height)
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
        window.setContentSize(NSSize(width: 700, height: 760))
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
