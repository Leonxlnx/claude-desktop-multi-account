// Claude accounts: a menu bar item that lists every account window with the plan
// usage it last saved, and opens account windows. Built on your Mac with the Swift
// compiler from the Xcode Command Line Tools (see `ccm build`).
//
// It reads only what Claude itself saves in plain files: the usage history each
// desktop window writes, and the usage snapshot Claude Code caches. It never reads a
// login and never talks to Anthropic.

import AppKit
import SwiftUI

enum Paths {
    static let home = FileManager.default.homeDirectoryForCurrentUser
    // `ccm build` writes CCM_HOME into Info.plist, because apps started from Finder,
    // the Dock or Spotlight do not see the shell's environment. The path is used as
    // written, so the --user-data-dir it ends up in matches what `ccm` looks for.
    static let root: URL = {
        let custom = ProcessInfo.processInfo.environment["CCM_HOME"]
            ?? Bundle.main.object(forInfoDictionaryKey: "CCMHome") as? String
        if let custom = custom, !custom.isEmpty {
            return URL(fileURLWithPath: (custom as NSString).expandingTildeInPath)
        }
        return home.appendingPathComponent(".claude-accounts")
    }()
    static let mainData = home.appendingPathComponent("Library/Application Support/Claude")
    static let codeState = home.appendingPathComponent(".claude.json")
    static let bundleID = "com.anthropic.claudefordesktop"

    static var app: URL? {
        let fm = FileManager.default
        for candidate in ["/Applications/Claude.app", home.appendingPathComponent("Applications/Claude.app").path]
        where fm.fileExists(atPath: candidate) {
            return URL(fileURLWithPath: candidate)
        }
        return NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
    }
}

struct Failure: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

// One number the desktop app saved: when, for which organisation, and the two percentages.
struct Sample {
    var time: Date
    var org: String?
    var fiveHour: Double?
    var week: Double?
}

// One limit from the snapshot Claude Code caches, which does include the reset time.
struct CachedLimit {
    var kind: String?      // "session" is the 5-hour window
    var model: String?     // set for a per-model weekly limit, such as Fable
    var percent: Double
    var resetsAt: Date
}

struct Folder {
    var name: String?      // nil for the normal Claude app
    var dir: URL
    var samples: [Sample] = []
    var account: String?   // the account and organisation this window used last
    var org: String?
}

struct Bar {
    var text: String
    var percent: Double
}

// One card in the window.
struct AccountRow: Identifiable {
    var name: String?
    var title: String
    var label: String
    var tag: Color
    var isOpen: Bool
    var five: Bar?         // nil until the window has saved a number
    var week: Bar?
    var lines: [String] = []
    var id: String { name ?? "" }
}

enum Accounts {
    // Same order and colours as the launcher icons.
    static let colors = ["#2563EB", "#16A34A", "#7C3AED", "#DB2777", "#0D9488", "#475569", "#B45309", "#DC2626"]
    static let mainColor = "#D97757"
    static let fiveHours: TimeInterval = 5 * 3600
    static let week: TimeInterval = 7 * 24 * 3600

    // team1 -> T1, max20a -> M20a, and a name without digits gets its place in the
    // list: work -> W1. Short enough to read on an icon.
    static func label(for name: String, number: Int) -> String {
        let chars = Array(name)
        func isLetter(_ c: Character) -> Bool { c.isASCII && c.isLetter }
        if let first = chars.first, isLetter(first) {
            var i = 1
            while i < chars.count && isLetter(chars[i]) { i += 1 }
            if i < chars.count && chars[i].isASCII && chars[i].isNumber {
                return first.uppercased() + String(chars[i...])
            }
            return first.uppercased() + String(number)
        }
        return String(name.prefix(3))
    }

    static func iconFile(_ name: String) -> URL { dataDir(name).appendingPathComponent("ccm-icon.icns") }

    // Each account runs from its own copy of Claude.app, because the Dock takes a
    // running app's icon from the bundle it was started from. On APFS the copy shares
    // its data with the original and takes no extra space. The only change is a
    // custom icon, added the way Finder's Get Info does it; Claude's code and its
    // signature stay as they are.
    static func instanceApp(_ name: String) -> URL {
        Paths.root.appendingPathComponent(".apps").appendingPathComponent("Claude \(name).app")
    }

    static func version(_ app: URL) -> String? {
        NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist"))?["CFBundleVersion"] as? String
    }

    static func stampIcon(_ name: String) {
        let copy = instanceApp(name)
        guard FileManager.default.fileExists(atPath: copy.path), let icon = NSImage(contentsOf: iconFile(name)) else { return }
        NSWorkspace.shared.setIcon(icon, forFile: copy.path, options: [])
    }

    // Copies Claude again when the copy is missing or the installed app is newer. Only
    // called while the account's window is closed. Where an APFS clone is not possible
    // (another disk, another file system) it uses the installed app, untagged, rather
    // than a full copy of it.
    static func prepareInstance(_ name: String, from app: URL) -> URL {
        let copy = instanceApp(name)
        let fm = FileManager.default
        if let copied = version(copy) {
            guard let installed = version(app), installed.compare(copied, options: .numeric) == .orderedDescending else { return copy }
        }
        do {
            if fm.fileExists(atPath: copy.path) { try fm.removeItem(at: copy) }
            try fm.createDirectory(at: copy.deletingLastPathComponent(), withIntermediateDirectories: true)
        } catch { return app }
        guard clonefile(app.path, copy.path, UInt32(CLONE_NOFOLLOW)) == 0 else {
            try? fm.removeItem(at: copy)
            return app
        }
        stampIcon(name)
        return copy
    }

    static func dataDir(_ name: String?) -> URL {
        name.map { Paths.root.appendingPathComponent($0) } ?? Paths.mainData
    }

    static func isValidName(_ name: String) -> Bool {
        guard let first = name.unicodeScalars.first, first.isASCII, CharacterSet.alphanumerics.contains(first) else { return false }
        return name.unicodeScalars.allSatisfy { $0.isASCII && (CharacterSet.alphanumerics.contains($0) || "._-".unicodeScalars.contains($0)) }
    }

    static func names() -> [String] {
        let keys: [URLResourceKey] = [.isDirectoryKey, .creationDateKey]
        guard let entries = try? FileManager.default.contentsOfDirectory(at: Paths.root, includingPropertiesForKeys: keys, options: .skipsHiddenFiles)
        else { return [] }
        return entries
            .compactMap { url -> (String, Date)? in
                guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isDirectory == true,
                      isValidName(url.lastPathComponent) else { return nil }
                return (url.lastPathComponent, values.creationDate ?? .distantPast)
            }
            .sorted { $0.1 != $1.1 ? $0.1 < $1.1 : $0.0 < $1.0 }
            .map { $0.0 }
    }

    // MARK: Running windows

    // The command line of another process, from the kernel.
    static func arguments(of pid: pid_t) -> [String] {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 0 else { return [] }
        var buffer = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0, size > MemoryLayout<Int32>.size else { return [] }
        let argc = buffer.withUnsafeBytes { $0.load(as: Int32.self) }
        // argc, the executable path and its padding, then the arguments.
        var index = MemoryLayout<Int32>.size
        while index < size && buffer[index] != 0 { index += 1 }
        while index < size && buffer[index] == 0 { index += 1 }
        var result: [String] = []
        while result.count < argc && index < size {
            let start = index
            while index < size && buffer[index] != 0 { index += 1 }
            result.append(String(decoding: buffer[start..<index], as: UTF8.self))
            index += 1
        }
        return result
    }

    static func userDataDir(_ arguments: [String]) -> String? {
        for (i, argument) in arguments.enumerated() {
            if argument.hasPrefix("--user-data-dir=") {
                return String(argument.dropFirst("--user-data-dir=".count)).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            }
            if argument == "--user-data-dir" && i + 1 < arguments.count { return arguments[i + 1] }
        }
        return nil
    }

    static func key(_ path: String) -> String {
        URL(fileURLWithPath: path).resolvingSymlinksInPath().standardizedFileURL.path
    }

    // Every running Claude app, by the data folder it was started on. macOS lists each
    // instance separately, so this finds account windows by their --user-data-dir.
    static func runningWindows() -> [String: NSRunningApplication] {
        var result: [String: NSRunningApplication] = [:]
        for app in NSWorkspace.shared.runningApplications where app.bundleIdentifier == Paths.bundleID && !app.isTerminated {
            let dir = userDataDir(arguments(of: app.processIdentifier)) ?? Paths.mainData.path
            result[key(dir)] = app
        }
        return result
    }

    // MARK: Saved usage

    static func readJSON(_ url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    static func number(_ values: [String: Any]?, _ key: String) -> Double? {
        (values?[key] as? NSNumber)?.doubleValue
    }

    static func readFolder(_ name: String?) -> Folder {
        var folder = Folder(name: name, dir: dataDir(name))
        // The app may be rewriting the file; then this window shows no numbers until the next refresh.
        if let history = readJSON(folder.dir.appendingPathComponent("plan-usage-history.json")),
           let entries = history["samples"] as? [[String: Any]] {
            for entry in entries {
                guard let t = number(entry, "t") else { continue }
                let usage = entry["u"] as? [String: Any]
                folder.samples.append(Sample(time: Date(timeIntervalSince1970: t / 1000), org: entry["org"] as? String,
                                             fiveHour: number(usage, "fh"), week: number(usage, "sd")))
            }
        }
        folder.samples.sort { $0.time < $1.time }
        folder.org = folder.samples.last?.org

        // Sessions are stored under <account id>/<organisation id>; the folder for the
        // current organisation that was written last tells us the account.
        if let org = folder.org {
            let sessions = folder.dir.appendingPathComponent("claude-code-sessions")
            let accounts = (try? FileManager.default.contentsOfDirectory(at: sessions, includingPropertiesForKeys: nil)) ?? []
            folder.account = accounts
                .compactMap { account -> (String, Date)? in
                    let dir = account.appendingPathComponent(org)
                    guard let modified = (try? dir.resourceValues(forKeys: [.contentModificationDateKey, .isDirectoryKey]))
                        .flatMap({ $0.isDirectory == true ? $0.contentModificationDate : nil }) else { return nil }
                    return (account.lastPathComponent, modified)
                }
                .max { $0.1 < $1.1 }?.0
        }
        return folder
    }

    static func parseDate(_ text: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: text) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: text)
    }

    // Claude Code caches its latest usage answer, reset times included, for one account.
    static func readCachedLimits() -> (account: String?, limits: [CachedLimit]) {
        guard let cache = readJSON(Paths.codeState)?["cachedUsageUtilization"] as? [String: Any] else { return (nil, []) }
        let entries = (cache["utilization"] as? [String: Any])?["limits"] as? [[String: Any]] ?? []
        let limits = entries.compactMap { entry -> CachedLimit? in
            guard let resets = (entry["resets_at"] as? String).flatMap(parseDate) else { return nil }
            let model = ((entry["scope"] as? [String: Any])?["model"] as? [String: Any])?["display_name"] as? String
            return CachedLimit(kind: entry["kind"] as? String, model: model, percent: number(entry, "percent") ?? 0, resetsAt: resets)
        }
        return (cache["accountUuid"] as? String, limits)
    }

    static func when(_ time: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = Calendar.current.isDateInToday(time) ? "HH:mm" : "EEE d MMM, HH:mm"
        return formatter.string(from: time)
    }

    // A limit only falls when its window resets. Walking back from the newest sample
    // while the number never falls finds the oldest moment we know the current window
    // already existed; it cannot last longer than `length` past that. `before` is the
    // sample just ahead of a fall, which brackets the reset from the other side.
    static func windowKnownSince(_ samples: [Sample], _ pick: (Sample) -> Double?, _ length: TimeInterval) -> (since: Date?, before: Date?) {
        guard let last = samples.last, let lastValue = pick(last), lastValue > 0 else { return (nil, nil) }
        var since = last.time
        var i = samples.count - 2
        while i >= 0 {
            guard let value = pick(samples[i]), last.time.timeIntervalSince(samples[i].time) < length else { break }
            if value > (pick(samples[i + 1]) ?? 0) + 4 { return (since, samples[i].time) }
            if value > 0 { since = samples[i].time }
            i -= 1
        }
        return (since, nil)
    }

    static func percentText(_ value: Double) -> String { String(format: "%.0f%%", value) }

    static func row(_ folder: Folder, _ samples: [Sample], colorIndex: Int, cached: [CachedLimit], running: [String: NSRunningApplication]) -> AccountRow {
        var row = AccountRow(
            name: folder.name,
            title: folder.name ?? "Main app",
            label: folder.name.map { label(for: $0, number: colorIndex + 1) } ?? "C",
            tag: Color(hex: folder.name == nil ? mainColor : colors[colorIndex % colors.count]),
            isOpen: running[key(folder.dir.path)] != nil)
        guard let last = samples.last else {
            row.lines.append("Not signed in yet. Click to open and sign in.")
            return row
        }

        let now = Date()
        row.lines.append("Updated " + when(last.time))

        // 5-hour limit. A number saved more than five hours ago describes a window that is over.
        if now.timeIntervalSince(last.time) >= fiveHours {
            row.five = Bar(text: "reset", percent: 0)
        } else {
            row.five = Bar(text: last.fiveHour.map(percentText) ?? "n/a", percent: last.fiveHour ?? 0)
            let session = cached.first { $0.kind == "session" && $0.resetsAt > now }
            let since = windowKnownSince(samples, { $0.fiveHour }, fiveHours).since
            if let session = session { row.lines.append("5-hour limit resets at " + when(session.resetsAt)) }
            else if let since = since { row.lines.append("5-hour limit resets by " + when(since + fiveHours) + " at the latest") }
        }

        // Weekly limit.
        if let weekValue = last.week {
            row.week = Bar(text: percentText(weekValue), percent: weekValue)
            let known = windowKnownSince(samples, { $0.week }, week)
            if let weekly = cached.first(where: { $0.kind != "session" && $0.model == nil && $0.resetsAt > now }) {
                row.lines.append("Weekly limit resets " + when(weekly.resetsAt))
            } else if let since = known.since {
                let latest = since + week
                if latest <= now {
                    row.week = Bar(text: "reset", percent: 0)
                } else if let before = known.before, before + week > now, latest.timeIntervalSince(before + week) <= 24 * 3600 {
                    row.lines.append("Weekly limit resets between " + when(before + week) + " and " + when(latest))
                } else {
                    row.lines.append("Weekly limit resets by " + when(latest) + " at the latest")
                }
            }
        }

        // Per-model weekly limits, such as Fable, exist only in Claude Code's snapshot.
        for limit in cached where limit.model != nil && limit.resetsAt > now {
            row.lines.append(limit.model! + " weekly limit: " + String(format: "%.0f", limit.percent) + "% used, resets " + when(limit.resetsAt))
        }
        return row
    }

    static func load() -> [AccountRow] {
        let names = names()
        let running = runningWindows()
        let main = readFolder(nil)
        let folders = names.map { readFolder($0) }
        let (cachedAccount, cached) = readCachedLimits()
        func limits(_ folder: Folder) -> [CachedLimit] {
            folder.account != nil && folder.account == cachedAccount ? cached : []
        }

        var rows: [AccountRow] = []
        for (i, folder) in folders.enumerated() {
            var samples = folder.samples.filter { $0.org == folder.org }
            // Numbers this account saved while it was signed in to the normal app count
            // too, unless several of your windows share the organisation (team seats).
            if let org = folder.org, folders.filter({ $0.org == org }).count == 1 {
                samples = (samples + main.samples.filter { $0.org == org }).sorted { $0.time < $1.time }
            }
            rows.append(row(folder, samples, colorIndex: i, cached: limits(folder), running: running))
        }
        rows.sort { ($0.name ?? "").localizedCaseInsensitiveCompare($1.name ?? "") == .orderedAscending }
        rows.insert(row(main, main.samples.filter { $0.org == main.org }, colorIndex: 0, cached: limits(main), running: running), at: 0)
        return rows
    }

    // MARK: Opening windows

    static func bringForward(_ app: NSRunningApplication) {
        app.unhide()
        app.activate(options: [.activateAllWindows])
        // Activating does not bring back a closed window. The reopen event is what a
        // Dock click sends, and makes Claude show its window again.
        let event = NSAppleEventDescriptor(
            eventClass: AEEventClass(kCoreEventClass), eventID: AEEventID(kAEReopenApplication),
            targetDescriptor: NSAppleEventDescriptor(processIdentifier: app.processIdentifier),
            returnID: AEReturnID(kAutoGenerateReturnID),
            transactionID: AETransactionID(kAnyTransactionID))
        _ = try? event.sendEvent(options: [.noReply], timeout: 5)
    }

    // Claude does not stop a second copy from opening the same data folder, so an open
    // window is brought forward instead of started again.
    static func open(_ name: String?, wait: Bool) throws {
        let dir = dataDir(name)
        if let name = name, !FileManager.default.fileExists(atPath: dir.path) {
            throw Failure("No account '\(name)'. Create it with: ccm add \(name)")
        }
        if let running = runningWindows()[key(dir.path)] {
            bringForward(running)
            return
        }
        guard let app = Paths.app else { throw Failure("The Claude desktop app is not installed (Claude.app not found).") }
        let configuration = NSWorkspace.OpenConfiguration()
        // Another account's window may already be running; without this macOS would just switch to it.
        configuration.createsNewApplicationInstance = true
        var start = app
        if let name = name {
            configuration.arguments = ["--user-data-dir=" + dir.path]
            start = prepareInstance(name, from: app)
        }
        let done = DispatchSemaphore(value: 0)
        var failure: Error?
        NSWorkspace.shared.openApplication(at: start, configuration: configuration) { _, error in
            failure = error
            done.signal()
        }
        if wait {
            done.wait()
            if let failure = failure { throw failure }
        }
    }

    // MARK: `ccm list`

    static func printList() {
        let running = runningWindows()
        let saved = DateFormatter()
        saved.dateStyle = .short
        saved.timeStyle = .short
        func number(_ value: Double?) -> String {
            guard let value = value else { return "" }
            return value == value.rounded() ? String(Int(value)) : String(format: "%.1f", value)
        }
        var table = [["Account", "Status", "5h %", "Week %", "Saved"]]
        for (label, name) in [("(normal app)", nil as String?)] + names().map({ ($0, $0 as String?) }) {
            let folder = readFolder(name)
            let last = folder.samples.last
            table.append([label, running[key(folder.dir.path)] != nil ? "open" : "closed",
                          number(last?.fiveHour), number(last?.week), last.map { saved.string(from: $0.time) } ?? "never signed in"])
        }
        let widths = (0..<table[0].count).map { column in table.map { $0[column].count }.max() ?? 0 }
        func line(_ cells: [String]) -> String {
            zip(cells, widths).map { $0.padding(toLength: $1, withPad: " ", startingAt: 0) }.joined(separator: " ")
                .trimmingCharacters(in: .whitespaces)
        }
        print("")
        print(line(table[0]))
        print(line(widths.map { String(repeating: "-", count: $0) }))
        table.dropFirst().forEach { print(line($0)) }
        print("")
    }
}

extension Color {
    init(hex: String) { self.init(nsColor: NSColor(hex: hex)) }
}

extension NSColor {
    convenience init(hex: String) {
        let value = UInt32(hex.trimmingCharacters(in: CharacterSet(charactersIn: "#")), radix: 16) ?? 0
        self.init(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                  blue: CGFloat(value & 0xFF) / 255, alpha: 1)
    }
}

// Draws the Claude icon with a coloured tag and saves it as an .icns. The icon is
// copied out of the installed app, so the file keeps working after app updates.
enum Icons {
    static func write(label: String, color: NSColor, to output: URL) throws {
        guard let app = Paths.app else { throw Failure("The Claude desktop app is not installed (Claude.app not found).") }
        let base = bundledIcon(of: app) ?? NSWorkspace.shared.icon(forFile: app.path)
        let fm = FileManager.default
        let iconset = fm.temporaryDirectory.appendingPathComponent("ccm-\(UUID().uuidString).iconset")
        try fm.createDirectory(at: iconset, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: iconset) }
        for points in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let file = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
                try render(base, label: label, color: color, pixels: points * scale).write(to: iconset.appendingPathComponent(file))
            }
        }
        let iconutil = Process()
        iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
        iconutil.arguments = ["-c", "icns", iconset.path, "-o", output.path]
        try iconutil.run()
        iconutil.waitUntilExit()
        if iconutil.terminationStatus != 0 { throw Failure("iconutil could not write \(output.path).") }
    }

    // The .icns inside the app. NSWorkspace can hand back a blank placeholder while it
    // is still loading an icon, so it is only the fallback.
    static func bundledIcon(of app: URL) -> NSImage? {
        guard var name = Bundle(url: app)?.object(forInfoDictionaryKey: "CFBundleIconFile") as? String else { return nil }
        if !name.hasSuffix(".icns") { name += ".icns" }
        return NSImage(contentsOf: app.appendingPathComponent("Contents/Resources").appendingPathComponent(name))
    }

    static func render(_ base: NSImage,label: String, color: NSColor, pixels: Int) throws -> Data {
        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                                            samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                            bytesPerRow: 0, bitsPerPixel: 0)
        else { throw Failure("Could not draw the icon.") }
        // Draw in 1024-point units whatever the pixel size; macOS icons sit on that grid.
        bitmap.size = NSSize(width: 1024, height: 1024)
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        let context = NSGraphicsContext(bitmapImageRep: bitmap)
        context?.imageInterpolation = .high
        NSGraphicsContext.current = context
        base.draw(in: NSRect(x: 0, y: 0, width: 1024, height: 1024))

        let tag = NSRect(x: 60, y: 70, width: 904, height: 420)
        color.setFill()
        NSBezierPath(roundedRect: tag, xRadius: 150, yRadius: 150).fill()

        var size: CGFloat = 400
        var font = NSFont.systemFont(ofSize: size, weight: .bold)
        while size > 96 && (label as NSString).size(withAttributes: [.font: font]).width > tag.width - 80 {
            size -= 16
            font = NSFont.systemFont(ofSize: size, weight: .bold)
        }
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white]
        let width = (label as NSString).size(withAttributes: attributes).width
        let baseline = tag.midY - font.capHeight / 2
        (label as NSString).draw(at: NSPoint(x: tag.midX - width / 2, y: baseline + font.descender), withAttributes: attributes)
        context?.flushGraphics()

        guard let png = bitmap.representation(using: .png, properties: [:]) else { throw Failure("Could not draw the icon.") }
        return png
    }
}

// MARK: Menu bar window

final class UsageModel: ObservableObject {
    @Published var rows: [AccountRow] = []

    func reload() { rows = Accounts.load() }

    func refresh() {
        DispatchQueue.global(qos: .userInitiated).async {
            let rows = Accounts.load()
            DispatchQueue.main.async { self.rows = rows }
        }
    }
}

struct BarView: View {
    let title: String
    let bar: Bar

    var fill: Color {
        bar.percent >= 90 ? Color(hex: "#F2555A") : bar.percent >= 70 ? Color(hex: "#F0B429") : Color.primary.opacity(0.7)
    }

    var body: some View {
        HStack(spacing: 0) {
            Text(title).font(.system(size: 12)).foregroundStyle(.secondary).frame(width: 58, alignment: .leading)
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.12))
                    Capsule().fill(fill).frame(width: geometry.size.width * CGFloat(min(max(bar.percent, 0), 100)) / 100)
                }
            }
            .frame(height: 4)
            Text(bar.text).font(.system(size: 12).monospacedDigit()).frame(width: 44, alignment: .trailing)
        }
    }
}

struct CardView: View {
    let row: AccountRow
    let open: (String?) -> Void
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        Button { open(row.name) } label: {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 0) {
                    Text(row.label).font(.system(size: 10.5, weight: .bold)).foregroundStyle(.white)
                        .padding(.horizontal, 6).padding(.vertical, 1.5)
                        .background(row.tag, in: RoundedRectangle(cornerRadius: 5, style: .continuous))
                    Text(row.title).font(.system(size: 14, weight: .semibold)).lineLimit(1).padding(.leading, 9)
                    Spacer(minLength: 8)
                    Circle().fill(row.isOpen ? Color.green : Color.secondary).frame(width: 6, height: 6).padding(.trailing, 6)
                    Text(row.isOpen ? "Open" : "Closed").font(.system(size: 12))
                        .foregroundStyle(row.isOpen ? Color.green : Color.secondary)
                }
                if let five = row.five { BarView(title: "5-hour", bar: five).padding(.top, 12) }
                if let week = row.week { BarView(title: "Weekly", bar: week).padding(.top, 7) }
                VStack(alignment: .leading, spacing: 1) {
                    ForEach(Array(row.lines.enumerated()), id: \.offset) { _, line in
                        Text(line).font(.system(size: 11.5)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(.top, 9)
            }
            .padding(.horizontal, 14).padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(shape.fill(Color.primary.opacity(hovering ? 0.08 : 0.045)))
            .overlay(shape.strokeBorder(Color.primary.opacity(hovering ? 0.14 : 0.08)))
            .contentShape(shape)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

struct ContentHeight: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

struct UsageList: View {
    @ObservedObject var model: UsageModel
    let open: (String?) -> Void
    let quit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(model.rows) { CardView(row: $0, open: open) }
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("Numbers are what each Claude window last saved. Click a card to open that account.")
                    .font(.system(size: 11)).foregroundStyle(.tertiary).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
                Button("Quit", action: quit).buttonStyle(.borderless).font(.system(size: 11)).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 2).padding(.top, 2)
        }
        .padding(12)
        .frame(width: 400)
    }
}

// As tall as the cards, and scrolls once they no longer fit on the screen.
struct UsageView: View {
    @ObservedObject var model: UsageModel
    let maxHeight: CGFloat
    let open: (String?) -> Void
    let quit: () -> Void
    @State private var height: CGFloat = 320

    var body: some View {
        ScrollView {
            UsageList(model: model, open: open, quit: quit)
                .background(GeometryReader { Color.clear.preference(key: ContentHeight.self, value: $0.size.height) })
        }
        .onPreferenceChange(ContentHeight.self) { height = $0 }
        .frame(width: 400, height: min(height, maxHeight))
    }
}

@MainActor
final class MenuBar: NSObject, NSApplicationDelegate {
    let showOnLaunch: Bool
    let model = UsageModel()
    let popover = NSPopover()
    var item: NSStatusItem!
    var timer: Timer?
    var launchedAsLoginItem = false

    init(showOnLaunch: Bool) { self.showOnLaunch = showOnLaunch }

    func applicationWillFinishLaunching(_ notification: Notification) {
        // Started from Login Items: stay in the menu bar instead of popping up.
        let event = NSAppleEventManager.shared().currentAppleEvent
        launchedAsLoginItem = event?.eventID == AEEventID(kAEOpenApplication)
            && event?.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?.enumCodeValue == OSType(keyAELaunchedAsLogInItem)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button {
            button.image = NSImage(systemSymbolName: "gauge.with.dots.needle.33percent", accessibilityDescription: "Claude accounts")
            button.toolTip = "Claude accounts"
            button.target = self
            button.action = #selector(clicked)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        let height = (NSScreen.main?.visibleFrame.height ?? 800) - 60
        let content = NSHostingController(rootView: UsageView(
            model: model, maxHeight: height,
            open: { [weak self] name in
                self?.popover.performClose(nil)
                do { try Accounts.open(name, wait: false) } catch { Program.alert(error.localizedDescription) }
            },
            quit: { NSApp.terminate(nil) }))
        content.sizingOptions = .preferredContentSize
        popover.contentViewController = content
        popover.behavior = .transient
        popover.animates = true

        timer = Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { if self?.popover.isShown == true { self?.model.refresh() } }
        }
        if showOnLaunch && !launchedAsLoginItem { DispatchQueue.main.async { self.show() } }
    }

    // Opening the app again (ccm usage, Spotlight, Finder) shows the window.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        show()
        return false
    }

    func show() {
        guard let button = item.button else { return }
        model.reload()
        if #available(macOS 14, *) { NSApp.activate() } else { NSApp.activate(ignoringOtherApps: true) }
        if !popover.isShown { popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY) }
        popover.contentViewController?.view.window?.makeKey()
    }

    @objc func clicked() {
        if NSApp.currentEvent?.type == .rightMouseUp {
            let menu = NSMenu()
            menu.addItem(withTitle: "Show usage", action: #selector(showFromMenu), keyEquivalent: "").target = self
            menu.addItem(.separator())
            menu.addItem(withTitle: "Quit Claude accounts", action: #selector(quit), keyEquivalent: "q").target = self
            item.menu = menu
            item.button?.performClick(nil)
            item.menu = nil
        } else if popover.isShown {
            popover.performClose(nil)
        } else {
            show()
        }
    }

    @objc func showFromMenu() { show() }
    @objc func quit() { NSApp.terminate(nil) }
}

@main
enum Program {
    // `ClaudeAccounts`                       menu bar item, with the usage window shown
    // `ClaudeAccounts tray`                  menu bar item only
    // `ClaudeAccounts open <name>`           open that account's Claude window, or bring it forward
    // `ClaudeAccounts list`                  the saved numbers as a table
    // `ClaudeAccounts icon <out.icns> [name]` draw an account's icon (no name: this app's)
    // `ClaudeAccounts app-path`              where the Claude desktop app is installed
    @MainActor
    static func main() {
        let args = Array(CommandLine.arguments.dropFirst())
        do {
            switch args.first ?? "" {
            case "open" where args.count > 1:
                try Accounts.open(args[1], wait: true)
            case "list":
                Accounts.printList()
            case "icon" where args.count > 1:
                let output = URL(fileURLWithPath: args[1])
                if args.count > 2 {
                    let name = args[2]
                    let index = Accounts.names().firstIndex(of: name) ?? 0
                    try Icons.write(label: Accounts.label(for: name, number: index + 1),
                                    color: NSColor(hex: Accounts.colors[index % Accounts.colors.count]), to: output)
                    // Its copy of Claude shows the new icon from the next time the window opens.
                    if output.standardizedFileURL == Accounts.iconFile(name).standardizedFileURL { Accounts.stampIcon(name) }
                } else {
                    try Icons.write(label: "%", color: NSColor(hex: "#334155"), to: output)
                }
            case "app-path":
                guard let app = Paths.app else { throw Failure("The Claude desktop app is not installed (Claude.app not found).") }
                print(app.path)
            default:
                runMenuBar(showing: args.first != "tray")
            }
        } catch {
            fail(error.localizedDescription)
        }
    }

    @MainActor
    static func runMenuBar(showing: Bool) {
        // One menu bar item at a time; a second start asks the running one to show its window.
        if let id = Bundle.main.bundleIdentifier,
           let other = NSRunningApplication.runningApplications(withBundleIdentifier: id).first(where: { $0 != .current }) {
            if showing { Accounts.bringForward(other) }
            return
        }
        let app = NSApplication.shared
        let delegate = MenuBar(showOnLaunch: showing)
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { app.run() }
    }

    @MainActor
    static func alert(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Claude accounts"
        alert.informativeText = message
        if #available(macOS 14, *) { NSApp.activate() } else { NSApp.activate(ignoringOtherApps: true) }
        alert.runModal()
    }

    // Launchers have no terminal to print to, so they show the problem in a dialog.
    @MainActor
    static func fail(_ message: String) -> Never {
        FileHandle.standardError.write(Data(("ccm: " + message + "\n").utf8))
        if isatty(STDERR_FILENO) == 0 {
            NSApplication.shared.setActivationPolicy(.accessory)
            alert(message)
        }
        exit(1)
    }
}
