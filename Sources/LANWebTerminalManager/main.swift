import AppKit
import SwiftUI

struct ShellResult {
    let exitCode: Int32
    let stdout: String
    let stderr: String
    var ok: Bool { exitCode == 0 }
}

enum Shell {
    static func run(_ executable: String, args: [String] = [], cwd: String? = nil, env: [String: String]? = nil) -> ShellResult {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = args
        if let cwd {
            process.currentDirectoryURL = URL(fileURLWithPath: cwd)
        }
        if let env {
            process.environment = env
        }

        let outPipe = Pipe()
        let errPipe = Pipe()
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = outPipe
        process.standardError = errPipe

        do {
            try process.run()
        } catch {
            return ShellResult(exitCode: -1, stdout: "", stderr: error.localizedDescription)
        }

        process.waitUntilExit()
        let outData = outPipe.fileHandleForReading.readDataToEndOfFile()
        let errData = errPipe.fileHandleForReading.readDataToEndOfFile()
        return ShellResult(
            exitCode: process.terminationStatus,
            stdout: String(data: outData, encoding: .utf8) ?? "",
            stderr: String(data: errData, encoding: .utf8) ?? ""
        )
    }

    static func startDetached(_ executable: String, args: [String], cwd: String, logPath: String) throws -> Int32 {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = args
        process.currentDirectoryURL = URL(fileURLWithPath: cwd)
        process.standardInput = FileHandle.nullDevice

        FileManager.default.createFile(atPath: logPath, contents: nil)
        let logHandle = try FileHandle(forWritingTo: URL(fileURLWithPath: logPath))
        logHandle.seekToEndOfFile()
        process.standardOutput = logHandle
        process.standardError = logHandle

        try process.run()
        return process.processIdentifier
    }
}

struct WebEndpoint: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String
    var rootPath: String
    var port: Int
    var host: String = "0.0.0.0"
    var urlPath: String = "/"
    var autoOpen = false

    var pidFile: String { "\(rootPath)/.lan-web-terminal-\(port).pid" }
    var logFile: String { "\(rootPath)/.lan-web-terminal-\(port).log" }
}

struct EndpointStatus: Equatable {
    var running = false
    var pids: [Int32] = []
    var urls: [String] = []
    var pageCount = 0
    var indexMtime = "--"
    var logTail = ""
    var updatedAt = "--"
}

@MainActor
final class AppState: ObservableObject {
    @Published var endpoints: [WebEndpoint] = []
    @Published var selection: UUID?
    @Published var statuses: [UUID: EndpointStatus] = [:]
    @Published var activity = "准备就绪"
    @Published var command = ""
    @Published var terminalOutput = "选择一个网页目录后，可以在这里执行维护命令。"
    @Published var isBusy = false

    private let configURL: URL
    private var timer: Timer?

    init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            .appendingPathComponent("LANWebTerminalManager", isDirectory: true)
        try? FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        configURL = support.appendingPathComponent("endpoints.json")
        load()
        if endpoints.isEmpty {
            seedDefaultEndpoints()
        }
        selection = endpoints.first?.id
        refreshAll()
        timer = Timer.scheduledTimer(withTimeInterval: 2.0, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refreshAll() }
        }
    }

    var selectedEndpoint: WebEndpoint? {
        guard let selection else { return nil }
        return endpoints.first { $0.id == selection }
    }

    func endpointBinding(_ id: UUID) -> Binding<WebEndpoint>? {
        guard let index = endpoints.firstIndex(where: { $0.id == id }) else { return nil }
        return Binding(
            get: { self.endpoints[index] },
            set: { newValue in
                self.endpoints[index] = newValue
                self.save()
                self.refresh(newValue)
            }
        )
    }

    func addEndpoint() {
        let panel = NSOpenPanel()
        panel.title = "选择网页根目录"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let usedPorts = Set(endpoints.map(\.port))
        var port = 8088
        while usedPorts.contains(port) {
            port += 1
        }
        var endpoint = WebEndpoint(name: url.lastPathComponent, rootPath: url.path, port: port)
        if FileManager.default.fileExists(atPath: url.appendingPathComponent("web").path) {
            endpoint.urlPath = "/web/"
        }
        endpoints.append(endpoint)
        selection = endpoint.id
        save()
        refresh(endpoint)
        activity = "已添加：\(endpoint.name)"
    }

    func removeSelected() {
        guard let selected = selectedEndpoint else { return }
        stop(selected)
        endpoints.removeAll { $0.id == selected.id }
        statuses.removeValue(forKey: selected.id)
        selection = endpoints.first?.id
        save()
        activity = "已移除：\(selected.name)"
    }

    func refreshAll() {
        for endpoint in endpoints {
            refresh(endpoint)
        }
    }

    func refresh(_ endpoint: WebEndpoint) {
        let pids = listenerPids(port: endpoint.port)
        statuses[endpoint.id] = EndpointStatus(
            running: !pids.isEmpty || isPortOpen(host: endpoint.host, port: endpoint.port),
            pids: pids,
            urls: urls(for: endpoint),
            pageCount: pageCount(rootPath: endpoint.rootPath),
            indexMtime: mtime("\(endpoint.rootPath)/index.html"),
            logTail: tail(endpoint.logFile, lines: 80),
            updatedAt: DateFormatter.status.string(from: Date())
        )
    }

    func startSelected() {
        guard let endpoint = selectedEndpoint else { return }
        start(endpoint)
    }

    func stopSelected() {
        guard let endpoint = selectedEndpoint else { return }
        stop(endpoint)
    }

    func start(_ endpoint: WebEndpoint) {
        if statuses[endpoint.id]?.running == true {
            activity = "\(endpoint.name) 已在运行"
            return
        }
        guard FileManager.default.fileExists(atPath: endpoint.rootPath) else {
            activity = "目录不存在：\(endpoint.rootPath)"
            return
        }

        isBusy = true
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                let pid = try Shell.startDetached(
                    "/usr/bin/python3",
                    args: ["-m", "http.server", "\(endpoint.port)", "--bind", endpoint.host],
                    cwd: endpoint.rootPath,
                    logPath: endpoint.logFile
                )
                try "\(pid)".write(toFile: endpoint.pidFile, atomically: true, encoding: .utf8)
                Thread.sleep(forTimeInterval: 0.45)
                DispatchQueue.main.async {
                    self.isBusy = false
                    self.refresh(endpoint)
                    self.activity = "已启动 \(endpoint.name)，PID \(pid)"
                    if endpoint.autoOpen {
                        self.openSelectedURL()
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    self.isBusy = false
                    self.activity = "启动失败：\(error.localizedDescription)"
                    self.refresh(endpoint)
                }
            }
        }
    }

    func stop(_ endpoint: WebEndpoint) {
        isBusy = true
        DispatchQueue.global(qos: .userInitiated).async {
            let filePid = self.pidFromFile(path: endpoint.pidFile)
            let targets = Set(self.listenerPids(port: endpoint.port) + [filePid].compactMap { $0 })
            var messages: [String] = []
            if targets.isEmpty {
                messages.append("服务未运行")
            } else {
                for pid in targets.sorted() {
                    let result = Shell.run("/bin/kill", args: ["-TERM", "\(pid)"])
                    messages.append(result.ok ? "已发送关闭信号：\(pid)" : "关闭失败 \(pid)：\(result.stderr)")
                }
            }
            Thread.sleep(forTimeInterval: 0.5)
            try? FileManager.default.removeItem(atPath: endpoint.pidFile)
            DispatchQueue.main.async {
                self.isBusy = false
                self.refresh(endpoint)
                self.activity = "\(endpoint.name)：\(messages.joined(separator: "，"))"
            }
        }
    }

    func openSelectedURL() {
        guard let endpoint = selectedEndpoint else { return }
        let url = "http://127.0.0.1:\(endpoint.port)\(normalizedURLPath(endpoint.urlPath))"
        if let target = URL(string: url) {
            NSWorkspace.shared.open(target)
            activity = "已打开：\(url)"
        }
    }

    func revealSelectedFolder() {
        guard let endpoint = selectedEndpoint else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: endpoint.rootPath))
    }

    func copyURLs() {
        guard let endpoint = selectedEndpoint else { return }
        let text = urls(for: endpoint).joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        activity = "已复制访问地址"
    }

    func runCommand() {
        guard let endpoint = selectedEndpoint else { return }
        let trimmed = command.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        terminalOutput += "\n\n$ \(trimmed)"
        command = ""
        isBusy = true
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Shell.run("/bin/zsh", args: ["-lc", trimmed], cwd: endpoint.rootPath)
            let text = [result.stdout, result.stderr].filter { !$0.isEmpty }.joined(separator: "\n")
            DispatchQueue.main.async {
                self.isBusy = false
                self.terminalOutput += "\n\(text.isEmpty ? "(无输出)" : text)"
                self.activity = result.ok ? "命令完成" : "命令失败，退出码 \(result.exitCode)"
                self.refresh(endpoint)
            }
        }
    }

    func save() {
        do {
            let data = try JSONEncoder.pretty.encode(endpoints)
            try data.write(to: configURL, options: .atomic)
        } catch {
            activity = "保存配置失败：\(error.localizedDescription)"
        }
    }

    private func load() {
        do {
            let data = try Data(contentsOf: configURL)
            endpoints = try JSONDecoder().decode([WebEndpoint].self, from: data)
        } catch {
            endpoints = []
        }
    }

    private func seedDefaultEndpoints() {
        let candidates = [
            (name: "DestinyApp", path: "/Users/ryan/WorkSpace/MyProject/DestinyApp", port: 8089, urlPath: "/web/"),
            (name: "战斗设计", path: "/Users/ryan/Perforce/Tools&Docs/DesignDocs/战斗设计", port: 8088, urlPath: "/")
        ]
        endpoints = candidates.compactMap { item in
            guard FileManager.default.fileExists(atPath: item.path) else { return nil }
            return WebEndpoint(name: item.name, rootPath: item.path, port: item.port, urlPath: item.urlPath)
        }
        save()
    }

    nonisolated private func listenerPids(port: Int) -> [Int32] {
        let result = Shell.run("/usr/sbin/lsof", args: ["-nP", "-iTCP:\(port)", "-sTCP:LISTEN", "-t"])
        return result.stdout.split(whereSeparator: \.isNewline).compactMap { Int32($0.trimmingCharacters(in: .whitespaces)) }.sorted()
    }

    nonisolated private func pidFromFile(path: String) -> Int32? {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return nil }
        return Int32(text.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    nonisolated private func isPortOpen(host: String, port: Int) -> Bool {
        let target = host == "0.0.0.0" ? "127.0.0.1" : host
        let result = Shell.run("/bin/zsh", args: ["-lc", "nc -z \(target) \(port) >/dev/null 2>&1"])
        return result.ok
    }

    nonisolated private func urls(for endpoint: WebEndpoint) -> [String] {
        let path = normalizedURLPath(endpoint.urlPath)
        var items = ["http://127.0.0.1:\(endpoint.port)\(path)"]
        let ifconfig = Shell.run("/sbin/ifconfig").stdout
        let regex = try? NSRegularExpression(pattern: #"\binet (192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[0-1])\.\d+\.\d+)\b"#)
        let range = NSRange(ifconfig.startIndex..<ifconfig.endIndex, in: ifconfig)
        regex?.matches(in: ifconfig, range: range).forEach { match in
            if let ipRange = Range(match.range(at: 1), in: ifconfig) {
                items.append("http://\(ifconfig[ipRange]):\(endpoint.port)\(path)")
            }
        }
        return Array(dictUnique(items))
    }

    nonisolated private func normalizedURLPath(_ path: String) -> String {
        var value = path.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.isEmpty { value = "/" }
        if !value.hasPrefix("/") { value = "/" + value }
        return value
    }

    nonisolated private func pageCount(rootPath: String) -> Int {
        guard let enumerator = FileManager.default.enumerator(atPath: rootPath) else { return 0 }
        var count = 0
        for case let path as String in enumerator {
            let lower = path.lowercased()
            if lower.hasSuffix(".html") || lower.hasSuffix(".md") {
                if !lower.hasSuffix("/index.html") && lower != "index.html" {
                    count += 1
                }
            }
        }
        return count
    }

    nonisolated private func mtime(_ path: String) -> String {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let date = attrs[.modificationDate] as? Date else { return "--" }
        return DateFormatter.status.string(from: date)
    }

    nonisolated private func tail(_ path: String, lines: Int) -> String {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return "暂无日志" }
        return text.split(whereSeparator: \.isNewline).suffix(lines).joined(separator: "\n")
    }
}

struct ContentView: View {
    @StateObject private var state = AppState()

    var body: some View {
        NavigationSplitView {
            sidebar
                .navigationSplitViewColumnWidth(min: 240, ideal: 280)
        } detail: {
            detail
        }
        .frame(minWidth: 1120, minHeight: 720)
        .toolbar {
            ToolbarItemGroup {
                Button(action: state.addEndpoint) {
                    Label("添加", systemImage: "plus")
                }
                Button(action: state.removeSelected) {
                    Label("移除", systemImage: "minus")
                }
                .disabled(state.selectedEndpoint == nil)
                Button(action: state.refreshAll) {
                    Label("刷新", systemImage: "arrow.clockwise")
                }
            }
        }
    }

    private var sidebar: some View {
        VStack(spacing: 0) {
            List(selection: $state.selection) {
                ForEach(state.endpoints) { endpoint in
                    SidebarRow(endpoint: endpoint, status: state.statuses[endpoint.id])
                        .tag(endpoint.id)
                }
            }
            Divider()
            HStack {
                Text(state.activity)
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer()
                if state.isBusy {
                    ProgressView()
                        .controlSize(.small)
                }
            }
            .padding(10)
        }
    }

    @ViewBuilder
    private var detail: some View {
        if let endpoint = state.selectedEndpoint, let binding = state.endpointBinding(endpoint.id) {
            EndpointDetail(endpoint: binding, status: state.statuses[endpoint.id] ?? EndpointStatus())
                .environmentObject(state)
        } else {
            EmptyStateView()
        }
    }
}

struct SidebarRow: View {
    let endpoint: WebEndpoint
    let status: EndpointStatus?

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(status?.running == true ? Color.green : Color.gray.opacity(0.45))
                .frame(width: 9, height: 9)
            VStack(alignment: .leading, spacing: 3) {
                Text(endpoint.name)
                    .font(.headline)
                    .lineLimit(1)
                Text("\(endpoint.host):\(endpoint.port)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(.vertical, 4)
    }
}

struct EmptyStateView: View {
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "network")
                .font(.system(size: 42))
                .foregroundStyle(.secondary)
            Text("还没有网页终端")
                .font(.title2.weight(.semibold))
            Text("添加一个包含 HTML 或 Markdown 的目录来开始管理局域网网页服务。")
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct EndpointDetail: View {
    @EnvironmentObject private var state: AppState
    @Binding var endpoint: WebEndpoint
    let status: EndpointStatus

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    settings
                    stats
                    urlPanel
                    terminal
                    logs
                }
                .padding(18)
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var header: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(endpoint.name)
                    .font(.system(size: 24, weight: .semibold))
                Text(endpoint.rootPath)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer()
            Button(action: state.stopSelected) {
                Label("停止", systemImage: "stop.fill")
            }
            .disabled(!status.running || state.isBusy)
            Button(action: state.startSelected) {
                Label("启动", systemImage: "play.fill")
            }
            .keyboardShortcut(.return, modifiers: .command)
            .disabled(status.running || state.isBusy)
            Button(action: state.openSelectedURL) {
                Label("打开", systemImage: "safari")
            }
            .disabled(!status.running)
        }
        .padding(18)
    }

    private var settings: some View {
        GroupBox("服务设置") {
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 12) {
                GridRow {
                    Text("名称")
                    TextField("名称", text: $endpoint.name)
                        .textFieldStyle(.roundedBorder)
                }
                GridRow {
                    Text("目录")
                    HStack {
                        Text(endpoint.rootPath)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .foregroundStyle(.secondary)
                        Spacer()
                        Button(action: state.revealSelectedFolder) {
                            Label("目录", systemImage: "folder")
                        }
                    }
                }
                GridRow {
                    Text("监听")
                    HStack {
                        Picker("Host", selection: $endpoint.host) {
                            Text("局域网").tag("0.0.0.0")
                            Text("本机").tag("127.0.0.1")
                        }
                        .pickerStyle(.segmented)
                        .frame(width: 160)
                        Stepper(value: $endpoint.port, in: 1024...65535) {
                            TextField("端口", value: $endpoint.port, formatter: NumberFormatter.port)
                                .frame(width: 76)
                        }
                    }
                }
                GridRow {
                    Text("入口")
                    TextField("/", text: $endpoint.urlPath)
                        .textFieldStyle(.roundedBorder)
                }
                GridRow {
                    Text("")
                    Toggle("启动后自动打开", isOn: $endpoint.autoOpen)
                }
            }
        }
    }

    private var stats: some View {
        HStack(spacing: 12) {
            StatTile(title: "状态", value: status.running ? "运行中" : "已停止", systemImage: status.running ? "checkmark.circle.fill" : "pause.circle")
            StatTile(title: "页面", value: "\(status.pageCount)", systemImage: "doc.richtext")
            StatTile(title: "PID", value: status.pids.isEmpty ? "--" : status.pids.map(String.init).joined(separator: ", "), systemImage: "number")
            StatTile(title: "索引", value: status.indexMtime, systemImage: "clock")
        }
    }

    private var urlPanel: some View {
        GroupBox("局域网访问地址") {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(status.urls, id: \.self) { url in
                    HStack {
                        Image(systemName: "link")
                            .foregroundStyle(.secondary)
                        Text(url)
                            .font(.system(.body, design: .monospaced))
                            .textSelection(.enabled)
                        Spacer()
                    }
                }
                HStack {
                    Button(action: state.copyURLs) {
                        Label("复制", systemImage: "doc.on.doc")
                    }
                    Button(action: state.openSelectedURL) {
                        Label("打开本机地址", systemImage: "arrow.up.right.square")
                    }
                    .disabled(!status.running)
                }
            }
        }
    }

    private var terminal: some View {
        GroupBox("维护终端") {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    TextField("例如：python3 -m http.server --help", text: $state.command)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit(state.runCommand)
                    Button(action: state.runCommand) {
                        Label("执行", systemImage: "terminal")
                    }
                    .disabled(state.command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || state.isBusy)
                }
                ScrollView {
                    Text(state.terminalOutput)
                        .font(.system(.callout, design: .monospaced))
                        .foregroundStyle(.primary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding(12)
                }
                .frame(minHeight: 140)
                .background(Color(nsColor: .textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 8))
            }
        }
    }

    private var logs: some View {
        GroupBox("服务日志") {
            ScrollView {
                Text(status.logTail)
                    .font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
                    .padding(12)
            }
            .frame(minHeight: 120)
            .background(Color(nsColor: .textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 8))
        }
    }
}

struct StatTile: View {
    let title: String
    let value: String
    let systemImage: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: systemImage)
                Text(title)
                Spacer()
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            Text(value)
                .font(.system(.headline, design: .rounded))
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .padding(12)
        .frame(maxWidth: .infinity, minHeight: 82, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}

@main
struct LANWebTerminalManagerApp: App {
    var body: some Scene {
        WindowGroup("局域网网页终端管理器") {
            ContentView()
        }
        .commands {
            CommandGroup(replacing: .newItem) {}
        }
    }
}

extension DateFormatter {
    static let status: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return formatter
    }()
}

extension JSONEncoder {
    static let pretty: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }()
}

extension NumberFormatter {
    static let port: NumberFormatter = {
        let formatter = NumberFormatter()
        formatter.minimum = 1024
        formatter.maximum = 65535
        formatter.allowsFloats = false
        return formatter
    }()
}

func dictUnique<S: Sequence>(_ values: S) -> [S.Element] where S.Element: Hashable {
    var seen = Set<S.Element>()
    return values.filter { seen.insert($0).inserted }
}
