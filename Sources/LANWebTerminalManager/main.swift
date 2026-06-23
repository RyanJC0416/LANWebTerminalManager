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
        remove(selected)
    }

    func remove(_ endpoint: WebEndpoint) {
        stop(endpoint)
        endpoints.removeAll { $0.id == endpoint.id }
        statuses.removeValue(forKey: endpoint.id)
        selection = endpoints.first?.id
        save()
        activity = "已移除：\(endpoint.name)"
    }

    func removeEndpoints(at offsets: IndexSet) {
        for index in offsets.sorted(by: >) {
            guard endpoints.indices.contains(index) else { continue }
            remove(endpoints[index])
        }
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
                if self.endpoints.contains(where: { $0.id == endpoint.id }) {
                    self.refresh(endpoint)
                }
                self.activity = "\(endpoint.name)：\(messages.joined(separator: "，"))"
            }
        }
    }

    func openSelectedURL() {
        guard let endpoint = selectedEndpoint else { return }
        guard statuses[endpoint.id]?.running == true else {
            activity = "服务已停止，请先启动后再打开"
            return
        }
        guard let url = urls(for: endpoint).first else {
            activity = "没有可用的局域网地址"
            return
        }
        if let target = URL(string: url) {
            NSWorkspace.shared.open(target)
            activity = "已打开：\(url)"
        }
    }

    func openSelectedLocalURL() {
        guard let endpoint = selectedEndpoint else { return }
        guard statuses[endpoint.id]?.running == true else {
            activity = "服务已停止，请先启动后再打开"
            return
        }
        let url = localURL(for: endpoint)
        if let target = URL(string: url) {
            NSWorkspace.shared.open(target)
            activity = "已打开本机地址：\(url)"
        }
    }

    func revealSelectedFolder() {
        guard let endpoint = selectedEndpoint else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: endpoint.rootPath))
    }

    func chooseHomepage() {
        guard let endpoint = selectedEndpoint,
              let endpointIndex = endpoints.firstIndex(where: { $0.id == endpoint.id }) else { return }

        let rootURL = URL(fileURLWithPath: endpoint.rootPath, isDirectory: true)
        let panel = NSOpenPanel()
        panel.title = "选择主页"
        panel.message = "从已选择的网页目录中选择要打开的主页文件。"
        panel.directoryURL = rootURL
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false

        guard panel.runModal() == .OK, let selectedURL = panel.url else { return }
        let rootPath = rootURL.standardizedFileURL.path
        let selectedPath = selectedURL.standardizedFileURL.path
        guard selectedPath == rootPath || selectedPath.hasPrefix(rootPath + "/") else {
            activity = "主页必须位于当前网页目录中"
            return
        }

        let relativePath = selectedPath
            .dropFirst(rootPath.count)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !relativePath.isEmpty else { return }

        endpoints[endpointIndex].urlPath = "/" + relativePath
        save()
        refresh(endpoints[endpointIndex])
        activity = "已选择主页：\(relativePath)"
    }

    func copyURLs() {
        guard let endpoint = selectedEndpoint else { return }
        guard statuses[endpoint.id]?.running == true else {
            activity = "服务已停止，访问地址暂不可用"
            return
        }
        let urlItems = urls(for: endpoint)
        guard !urlItems.isEmpty else {
            activity = "没有可复制的局域网地址"
            return
        }
        let text = urlItems.joined(separator: "\n")
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
        return lanIPs().map { "http://\($0):\(endpoint.port)\(path)" }
    }

    nonisolated private func localURL(for endpoint: WebEndpoint) -> String {
        "http://127.0.0.1:\(endpoint.port)\(normalizedURLPath(endpoint.urlPath))"
    }

    nonisolated private func lanIPs() -> [String] {
        if let primaryIP = primaryLANIP() {
            return [primaryIP]
        }

        let ifconfig = Shell.run("/sbin/ifconfig").stdout
        let regex = try? NSRegularExpression(pattern: #"\binet (192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[0-1])\.\d+\.\d+)\b"#)
        let range = NSRange(ifconfig.startIndex..<ifconfig.endIndex, in: ifconfig)
        var items: [String] = []
        regex?.matches(in: ifconfig, range: range).forEach { match in
            if let ipRange = Range(match.range(at: 1), in: ifconfig) {
                items.append(String(ifconfig[ipRange]))
            }
        }
        return Array(dictUnique(items))
    }

    nonisolated private func primaryLANIP() -> String? {
        let route = Shell.run("/sbin/route", args: ["-n", "get", "default"]).stdout
        guard let interfaceLine = route.split(whereSeparator: \.isNewline)
            .first(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("interface:") }) else {
            return nil
        }

        let interface = interfaceLine
            .split(separator: ":", maxSplits: 1)
            .last?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !interface.isEmpty else { return nil }

        let ip = Shell.run("/usr/sbin/ipconfig", args: ["getifaddr", interface])
            .stdout
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return isPrivateIPv4(ip) ? ip : nil
    }

    nonisolated private func isPrivateIPv4(_ value: String) -> Bool {
        let pattern = #"^(192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[0-1])\.\d+\.\d+)$"#
        return value.range(of: pattern, options: .regularExpression) != nil
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
    @StateObject private var updateManager = UpdateManager()

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
        .onDeleteCommand(perform: state.removeSelected)
        .alert("应用更新", isPresented: $updateManager.isPresentingMessage) {
            if updateManager.canRetry {
                Button("重试") { updateManager.retryLastFailedAction() }
                Button("打开 release 页面") { updateManager.openReleasePage() }
                Button("取消", role: .cancel) {}
            } else {
                Button("好", role: .cancel) {}
            }
        } message: {
            Text(updateManager.message)
        }
    }

    private var sidebar: some View {
        VStack(spacing: 0) {
            HStack {
                Text("网页终端")
                    .font(.headline)
                Spacer()
                Button(action: state.addEndpoint) {
                    Image(systemName: "plus")
                }
                .buttonStyle(.borderless)
                .help("添加网页终端")
                Button(action: state.removeSelected) {
                    Image(systemName: "trash")
                }
                .buttonStyle(.borderless)
                .help("删除选中的网页终端")
                .disabled(state.selectedEndpoint == nil)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            List(selection: $state.selection) {
                ForEach(state.endpoints) { endpoint in
                    SidebarRow(endpoint: endpoint, status: state.statuses[endpoint.id])
                        .tag(endpoint.id)
                        .contextMenu {
                            Button(role: .destructive) {
                                state.remove(endpoint)
                            } label: {
                                Label("删除", systemImage: "trash")
                            }
                        }
                }
                .onDelete(perform: state.removeEndpoints)
            }
            Divider()
            VStack(alignment: .leading, spacing: 8) {
                if let statusText = updateManager.statusText {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(statusText)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if let downloadProgress = updateManager.downloadProgress {
                            ProgressView(value: downloadProgress)
                            Text("\(Int((downloadProgress * 100).rounded()))%")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                    }
                }

                Button(action: updateManager.performPrimaryUpdateAction) {
                    Label(updateManager.primaryButtonTitle, systemImage: "arrow.down.circle")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .disabled(updateManager.isBusy)

                Text("当前版本 \(UpdateManager.currentVersion)")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(10)
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
                    HStack {
                        Button(action: state.chooseHomepage) {
                            HStack {
                                Image(systemName: "house")
                                    .foregroundStyle(.secondary)
                                Text(endpoint.urlPath)
                                    .font(.system(.body, design: .monospaced))
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                Spacer()
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        Spacer()
                        Button(action: state.chooseHomepage) {
                            Label("选择主页", systemImage: "folder")
                        }
                    }
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
                if status.running {
                    if status.urls.isEmpty {
                        HStack {
                            Image(systemName: "wifi.exclamationmark")
                                .foregroundStyle(.secondary)
                            Text("没有检测到可用的局域网地址。")
                                .foregroundStyle(.secondary)
                            Spacer()
                        }
                    } else {
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
                    }
                } else {
                    HStack {
                        Image(systemName: "link.badge.plus")
                            .foregroundStyle(.secondary)
                        Text("服务停止后地址不可访问，启动后可复制或打开。")
                            .foregroundStyle(.secondary)
                        Spacer()
                    }
                }
                HStack {
                    Button(action: state.copyURLs) {
                        Label("复制", systemImage: "doc.on.doc")
                    }
                    .disabled(!status.running || status.urls.isEmpty)
                    Button(action: state.openSelectedLocalURL) {
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

@MainActor
final class UpdateManager: ObservableObject {
    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    @Published var isChecking = false
    @Published var isDownloading = false
    @Published var isInstalling = false
    @Published var statusText: String?
    @Published var downloadProgress: Double?
    @Published var message = ""
    @Published var isPresentingMessage = false
    @Published var canRetry = false

    var isBusy: Bool {
        isChecking || isDownloading || isInstalling
    }

    var primaryButtonTitle: String {
        if isChecking { return "检查中..." }
        if isDownloading { return "下载中..." }
        if isInstalling { return "安装中..." }
        if pendingAsset != nil { return "立即更新" }
        return "检查更新"
    }

    private let appName = "局域网网页终端管理器"
    private let latestReleaseURL = URL(string: "https://api.github.com/repos/RyanJC0416/LANWebTerminalManager/releases/latest")!
    private let latestReleasePageURL = URL(string: "https://github.com/RyanJC0416/LANWebTerminalManager/releases/latest")!
    private let updatesDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/LANWebTerminalManager/updates", isDirectory: true)
    private let requestTimeout: TimeInterval = 20
    private let downloadTimeout: TimeInterval = 90
    private var pendingRelease: GitHubRelease?
    private var pendingAsset: GitHubReleaseAsset?
    private var retryAction: UpdateAction?

    func performPrimaryUpdateAction() {
        guard !isBusy else { return }
        if pendingAsset != nil {
            installPendingUpdate()
        } else {
            checkForUpdates()
        }
    }

    func retryLastFailedAction() {
        let action = retryAction
        canRetry = false
        retryAction = nil
        isPresentingMessage = false

        switch action {
        case .install:
            installPendingUpdate()
        default:
            checkForUpdates()
        }
    }

    func openReleasePage() {
        NSWorkspace.shared.open(releasePageURL())
    }

    private func checkForUpdates() {
        guard !isBusy else { return }
        Task { await checkLatestRelease() }
    }

    private func checkLatestRelease() async {
        isChecking = true
        statusText = "正在检查更新..."
        downloadProgress = nil
        pendingRelease = nil
        pendingAsset = nil
        defer { isChecking = false }

        do {
            let release = try await fetchLatestRelease()
            let latestVersion = release.tagName.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))

            if Self.isVersion(Self.currentVersion, newerThan: latestVersion) {
                statusText = "当前版本 \(Self.currentVersion) 高于最新版本 \(latestVersion)"
                return
            }

            guard Self.isVersion(latestVersion, newerThan: Self.currentVersion) else {
                statusText = "当前已是最新版本 \(latestVersion)"
                return
            }

            guard !Self.isAppTranslocated() else {
                showMessage("""
                当前 app 正在 macOS 隔离/转移位置运行，无法原地更新。

                请先把 \(appName).app 移到 /Applications 后重新打开，再检查更新。
                """)
                return
            }

            guard let asset = release.assets.first(where: { $0.name.lowercased().hasSuffix(".zip") }) else {
                showFailure(UpdateError.appBundleMissing(appName), retryAction: .check)
                return
            }

            pendingRelease = release
            pendingAsset = asset
            statusText = "发现新版本 \(latestVersion)"
        } catch {
            statusText = "检查更新失败"
            showFailure(error, retryAction: .check)
        }
    }

    private func installPendingUpdate() {
        guard !isBusy else { return }
        guard let asset = pendingAsset else {
            checkForUpdates()
            return
        }

        Task { await installLatestRelease(asset: asset) }
    }

    private func installLatestRelease(asset: GitHubReleaseAsset) async {
        isDownloading = true
        downloadProgress = 0
        statusText = "正在下载更新 \(pendingRelease?.tagName ?? "")"

        do {
            try await downloadAndInstall(assetURL: asset.browserDownloadURL)
        } catch {
            isDownloading = false
            isInstalling = false
            downloadProgress = nil
            statusText = "更新失败"
            showFailure(error, retryAction: .install)
        }
    }

    private func fetchLatestRelease() async throws -> GitHubRelease {
        var lastError: Error?

        for attempt in 0..<2 {
            do {
                return try await fetchLatestReleaseFromAPI()
            } catch {
                lastError = error
                guard Self.shouldRetryReleaseLookup(error), attempt == 0 else { break }
                try? await Task.sleep(nanoseconds: 700_000_000)
            }
        }

        do {
            return try await fetchLatestReleaseFromRedirectPage()
        } catch {
            throw lastError ?? error
        }
    }

    private func fetchLatestReleaseFromAPI() async throws -> GitHubRelease {
        var request = URLRequest(url: latestReleaseURL, timeoutInterval: requestTimeout)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("LANWebTerminalManager", forHTTPHeaderField: "User-Agent")

        let (data, response) = try await URLSession.shared.data(for: request)
        try validateHTTPResponse(response, context: "读取最新 release")

        do {
            return try JSONDecoder().decode(GitHubRelease.self, from: data)
        } catch {
            throw UpdateError.releaseLookupFailed
        }
    }

    private func fetchLatestReleaseFromRedirectPage() async throws -> GitHubRelease {
        var request = URLRequest(url: latestReleasePageURL, timeoutInterval: requestTimeout)
        request.setValue("LANWebTerminalManager", forHTTPHeaderField: "User-Agent")

        let (_, response) = try await URLSession.shared.data(for: request)
        guard let finalURL = response.url,
              let tag = Self.releaseTag(from: finalURL),
              let assetURL = URL(string: "https://github.com/RyanJC0416/LANWebTerminalManager/releases/download/\(tag)/app.zip") else {
            throw UpdateError.releaseLookupFailed
        }

        return GitHubRelease(
            tagName: tag,
            assets: [GitHubReleaseAsset(name: "app.zip", browserDownloadURL: assetURL)]
        )
    }

    private func downloadAndInstall(assetURL: URL) async throws {
        let fileManager = FileManager.default
        let tempDirectory = updatesDirectory
            .appendingPathComponent("LANWebTerminalManagerUpdate-\(UUID().uuidString)", isDirectory: true)
        let archiveURL = tempDirectory.appendingPathComponent("LANWebTerminalManager.zip")
        let extractURL = tempDirectory.appendingPathComponent("extracted", isDirectory: true)
        var shouldCleanUp = true
        defer {
            if shouldCleanUp {
                try? fileManager.removeItem(at: tempDirectory)
            }
        }

        try fileManager.createDirectory(at: updatesDirectory, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: tempDirectory, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: extractURL, withIntermediateDirectories: true)

        var request = URLRequest(url: assetURL, timeoutInterval: downloadTimeout)
        request.setValue("LANWebTerminalManager", forHTTPHeaderField: "User-Agent")
        let (downloadedURL, response) = try await downloadWithProgress(request)
        try validateHTTPResponse(response, context: "下载更新包")
        if fileManager.fileExists(atPath: archiveURL.path) {
            try fileManager.removeItem(at: archiveURL)
        }
        try fileManager.moveItem(at: downloadedURL, to: archiveURL)
        downloadProgress = 1
        isDownloading = false
        isInstalling = true
        statusText = "正在安装更新..."

        try run("/usr/bin/unzip", arguments: ["-q", archiveURL.path, "-d", extractURL.path], timeout: 30)

        let newAppURL = extractURL.appendingPathComponent("\(appName).app", isDirectory: true)
        guard fileManager.fileExists(atPath: newAppURL.path) else {
            throw UpdateError.appBundleMissing(appName)
        }

        try launchInstallerScript(newAppURL: newAppURL, tempDirectory: tempDirectory)
        shouldCleanUp = false
        NSApp.terminate(nil)
    }

    private func downloadWithProgress(_ request: URLRequest) async throws -> (URL, URLResponse) {
        let downloader = UpdateDownloadDelegate { [weak self] progress in
            Task { @MainActor in
                self?.downloadProgress = progress
            }
        }

        return try await downloader.download(request)
    }

    private func validateHTTPResponse(_ response: URLResponse, context: String) throws {
        guard let httpResponse = response as? HTTPURLResponse else { return }
        guard (200..<300).contains(httpResponse.statusCode) else {
            throw UpdateError.httpRequestFailed(context, httpResponse.statusCode)
        }
    }

    private func run(_ executablePath: String, arguments: [String], timeout: TimeInterval = 30) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executablePath)
        process.arguments = arguments
        let errorPipe = Pipe()
        process.standardError = errorPipe
        try process.run()

        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.05)
        }

        if process.isRunning {
            process.terminate()
            throw UpdateError.commandTimedOut(executablePath)
        }

        guard process.terminationStatus == 0 else {
            let errorData = errorPipe.fileHandleForReading.readDataToEndOfFile()
            let errorText = String(data: errorData, encoding: .utf8)?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            throw UpdateError.commandFailed(executablePath, errorText)
        }
    }

    private func launchInstallerScript(newAppURL: URL, tempDirectory: URL) throws {
        let currentAppPath = Bundle.main.bundleURL.path
        let currentAppDirectory = (currentAppPath as NSString).deletingLastPathComponent
        let logPath = updatesDirectory.appendingPathComponent("install.log").path
        let scriptURL = tempDirectory.appendingPathComponent("install-update.zsh")
        let pid = ProcessInfo.processInfo.processIdentifier
        let script = """
        #!/bin/zsh
        set -e

        LOG=\(Self.shellQuote(logPath))
        exec > "$LOG" 2>&1
        echo "[$(date)] LANWebTerminalManager updater started"

        APP_PATH=\(Self.shellQuote(currentAppPath))
        APP_DIR=\(Self.shellQuote(currentAppDirectory))
        NEW_APP=\(Self.shellQuote(newAppURL.path))
        TEMP_DIR=\(Self.shellQuote(tempDirectory.path))
        APP_PID=\(pid)

        while kill -0 "$APP_PID" 2>/dev/null; do
            sleep 0.2
        done

        rm -rf "$APP_PATH.old"

        if ! mv "$APP_PATH" "$APP_PATH.old"; then
            echo "ERROR: failed to move current app out of the way"
            exit 1
        fi

        if ! cp -R "$NEW_APP" "$APP_DIR/"; then
            echo "ERROR: failed to copy new app"
            mv "$APP_PATH.old" "$APP_PATH" || true
            exit 1
        fi

        xattr -dr com.apple.quarantine "$APP_PATH" 2>/dev/null || true
        if ! open "$APP_PATH"; then
            echo "ERROR: failed to open updated app"
            exit 1
        fi

        rm -rf "$APP_PATH.old" || true
        rm -rf "$TEMP_DIR"
        rm -f "$0"
        echo "[$(date)] LANWebTerminalManager updater finished"
        """

        try script.write(to: scriptURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: scriptURL.path)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = [scriptURL.path]
        try process.run()
    }

    private func showMessage(_ text: String) {
        canRetry = false
        retryAction = nil
        message = text
        isPresentingMessage = true
    }

    private func showFailure(_ error: Error, retryAction: UpdateAction) {
        self.retryAction = retryAction
        canRetry = true
        message = """
        更新失败，请重试或手动下载更新。

        \(error.localizedDescription)
        """
        isPresentingMessage = true
    }

    private func releasePageURL() -> URL {
        if let tag = pendingRelease?.tagName {
            return URL(string: "https://github.com/RyanJC0416/LANWebTerminalManager/releases/tag/\(tag)")!
        }

        return latestReleasePageURL
    }

    private static func isVersion(_ lhs: String, newerThan rhs: String) -> Bool {
        let left = lhs.split(separator: ".").map { Int($0) ?? 0 }
        let right = rhs.split(separator: ".").map { Int($0) ?? 0 }
        let count = max(left.count, right.count)

        for index in 0..<count {
            let leftPart = index < left.count ? left[index] : 0
            let rightPart = index < right.count ? right[index] : 0
            if leftPart != rightPart {
                return leftPart > rightPart
            }
        }

        return false
    }

    private static func shouldRetryReleaseLookup(_ error: Error) -> Bool {
        if case UpdateError.httpRequestFailed(_, let statusCode) = error {
            return statusCode >= 500
        }

        if case UpdateError.releaseLookupFailed = error {
            return true
        }

        let urlError = error as? URLError
        return urlError?.code == .timedOut
            || urlError?.code == .cannotConnectToHost
            || urlError?.code == .networkConnectionLost
    }

    private static func releaseTag(from url: URL) -> String? {
        guard let range = url.path.range(of: "/releases/tag/") else {
            return nil
        }

        let tag = String(url.path[range.upperBound...])
            .split(separator: "/")
            .first
            .map(String.init)

        return tag?.isEmpty == false ? tag : nil
    }

    private static func shellQuote(_ value: String) -> String {
        "'\(value.replacingOccurrences(of: "'", with: "'\\''"))'"
    }

    private static func isAppTranslocated() -> Bool {
        let appPath = Bundle.main.bundleURL.path
        return appPath.contains("/AppTranslocation/")
            || (appPath.contains("/private/var/folders/") && appPath.contains("/T/"))
    }
}

private enum UpdateAction {
    case check
    case install
}

private final class UpdateDownloadDelegate: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let progressHandler: @Sendable (Double) -> Void
    private var continuation: CheckedContinuation<(URL, URLResponse), Error>?
    private var session: URLSession?
    private let lock = NSLock()

    init(progressHandler: @escaping @Sendable (Double) -> Void) {
        self.progressHandler = progressHandler
    }

    func download(_ request: URLRequest) async throws -> (URL, URLResponse) {
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1

        return try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let session = URLSession(configuration: .default, delegate: self, delegateQueue: queue)
            self.session = session
            session.downloadTask(with: request).resume()
        }
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        guard totalBytesExpectedToWrite > 0 else { return }
        let progress = min(max(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite), 0), 1)
        progressHandler(progress)
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        guard let response = downloadTask.response else {
            finish(.failure(UpdateError.releaseLookupFailed))
            return
        }

        let tempURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("LANWebTerminalManagerDownload-\(UUID().uuidString).zip")

        do {
            if FileManager.default.fileExists(atPath: tempURL.path) {
                try FileManager.default.removeItem(at: tempURL)
            }
            try FileManager.default.moveItem(at: location, to: tempURL)
            finish(.success((tempURL, response)))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        if let error {
            finish(.failure(error))
        }
    }

    private func finish(_ result: Result<(URL, URLResponse), Error>) {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        let session = self.session
        self.session = nil
        lock.unlock()

        guard let continuation else { return }
        session?.finishTasksAndInvalidate()

        switch result {
        case .success(let value):
            continuation.resume(returning: value)
        case .failure(let error):
            continuation.resume(throwing: error)
        }
    }
}

private struct GitHubRelease: Decodable {
    let tagName: String
    let assets: [GitHubReleaseAsset]

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case assets
    }
}

private struct GitHubReleaseAsset: Decodable {
    let name: String
    let browserDownloadURL: URL

    enum CodingKeys: String, CodingKey {
        case name
        case browserDownloadURL = "browser_download_url"
    }
}

private enum UpdateError: LocalizedError {
    case releaseLookupFailed
    case appBundleMissing(String)
    case commandFailed(String, String?)
    case commandTimedOut(String)
    case httpRequestFailed(String, Int)

    var errorDescription: String? {
        switch self {
        case .releaseLookupFailed:
            return "无法读取最新 release。"
        case .appBundleMissing(let appName):
            return "更新包里没有找到 \(appName).app。"
        case .commandFailed(let command, let detail):
            if let detail, !detail.isEmpty {
                return "\(command) 执行失败：\(detail)"
            }
            return "\(command) 执行失败。"
        case .commandTimedOut(let command):
            return "\(command) 执行超时，请稍后重试。"
        case .httpRequestFailed(let context, let statusCode):
            return "\(context)失败：HTTP \(statusCode)。"
        }
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
