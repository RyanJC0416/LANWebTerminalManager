using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using LANWebTerminalManager.Models;

namespace LANWebTerminalManager.Services;

public sealed class AppState : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configPath;
    private readonly DispatcherTimer _timer;
    private Guid? _selection;
    private string _activity = "准备就绪";
    private string _command = "";
    private string _terminalOutput = "选择一个网页目录后，可以在这里执行维护命令。";
    private bool _isBusy;

    public AppState()
    {
        var support = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LANWebTerminalManager");
        Directory.CreateDirectory(support);
        _configPath = Path.Combine(support, "endpoints.json");

        Load();

        if (Endpoints.Count == 0)
        {
            SeedDefaultEndpoints();
        }

        Selection = Endpoints.FirstOrDefault()?.Id;
        RefreshAll();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();
    }

    public ObservableCollection<WebEndpoint> Endpoints { get; } = new();
    public Dictionary<Guid, EndpointStatus> Statuses { get; } = new();

    public Guid? Selection
    {
        get => _selection;
        set
        {
            if (_selection == value) return;
            _selection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedEndpoint));
            OnPropertyChanged(nameof(SelectedStatus));
        }
    }

    public WebEndpoint? SelectedEndpoint =>
        Selection is null ? null : Endpoints.FirstOrDefault(item => item.Id == Selection);

    public EndpointStatus SelectedStatus =>
        Selection is Guid id && Statuses.TryGetValue(id, out var status) ? status : new EndpointStatus();

    public string Activity
    {
        get => _activity;
        set { _activity = value; OnPropertyChanged(); }
    }

    public string Command
    {
        get => _command;
        set { _command = value; OnPropertyChanged(); }
    }

    public string TerminalOutput
    {
        get => _terminalOutput;
        set { _terminalOutput = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StatusesChanged;

    public void AddEndpoint(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            Activity = $"目录不存在：{rootPath}";
            return;
        }

        var usedPorts = Endpoints.Select(item => item.Port).ToHashSet();
        var port = 8088;
        while (usedPorts.Contains(port)) port++;

        var endpoint = new WebEndpoint
        {
            Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            RootPath = rootPath,
            Port = port
        };

        if (Directory.Exists(Path.Combine(rootPath, "web")))
        {
            endpoint.UrlPath = "/web/";
        }

        Endpoints.Add(endpoint);
        Selection = endpoint.Id;
        Save();
        Refresh(endpoint);
        Activity = $"已添加：{endpoint.Name}";
    }

    public void RemoveSelected()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        Remove(endpoint);
    }

    public void Remove(WebEndpoint endpoint)
    {
        Stop(endpoint);
        Endpoints.Remove(endpoint);
        Statuses.Remove(endpoint.Id);
        Selection = Endpoints.FirstOrDefault()?.Id;
        Save();
        Activity = $"已移除：{endpoint.Name}";
        OnPropertyChanged(nameof(SelectedEndpoint));
        OnPropertyChanged(nameof(SelectedStatus));
    }

    public void SaveEndpoint(WebEndpoint endpoint)
    {
        Save();
        Refresh(endpoint);
    }

    public void RefreshAll()
    {
        var snapshot = Endpoints.ToList();
        Task.Run(() =>
        {
            var results = snapshot.ToDictionary(item => item.Id, StatusFor);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var endpoint in Endpoints)
                {
                    if (results.TryGetValue(endpoint.Id, out var status))
                    {
                        Statuses[endpoint.Id] = status;
                    }
                }

                OnPropertyChanged(nameof(SelectedStatus));
                StatusesChanged?.Invoke();
            });
        });
    }

    public void Refresh(WebEndpoint endpoint)
    {
        Task.Run(() =>
        {
            var status = StatusFor(endpoint);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Endpoints.Any(item => item.Id == endpoint.Id))
                {
                    Statuses[endpoint.Id] = status;
                    OnPropertyChanged(nameof(SelectedStatus));
                }
            });
        });
    }

    public void StartSelected()
    {
        if (SelectedEndpoint is { } endpoint) Start(endpoint);
    }

    public void StopSelected()
    {
        if (SelectedEndpoint is { } endpoint) Stop(endpoint);
    }

    public void RefreshSelectedAssets()
    {
        if (SelectedEndpoint is not { } endpoint) return;

        if (Statuses.TryGetValue(endpoint.Id, out var status) && !status.Running)
        {
            Refresh(endpoint);
            Activity = $"已刷新资产状态：{endpoint.Name}";
            return;
        }

        IsBusy = true;
        Task.Run(() =>
        {
            StopSynchronously(endpoint);
            Thread.Sleep(350);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                Start(endpoint, "已刷新资产");
            });
        });
    }

    public void Start(WebEndpoint endpoint, string reason = "已启动")
    {
        if (Statuses.TryGetValue(endpoint.Id, out var status) && status.Running)
        {
            Activity = $"{endpoint.Name} 已在运行";
            return;
        }

        if (!Directory.Exists(endpoint.RootPath))
        {
            Activity = $"目录不存在：{endpoint.RootPath}";
            return;
        }

        IsBusy = true;
        Task.Run(() =>
        {
            try
            {
                // 启动前先清理同端口的旧进程，避免 Windows 上出现多个 listener 导致 ERR_EMPTY_RESPONSE。
                StopSynchronously(endpoint);
                Thread.Sleep(350);

                var pid = PythonHelper.StartServer(endpoint);
                try
                {
                    EndpointPaths.TryWritePid(endpoint, pid);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // PID 文件只是辅助信息，写入失败不应阻断已启动的服务。
                }
                Thread.Sleep(450);

                var status = StatusFor(endpoint);
                if (!status.Running)
                {
                    var log = Tail(EndpointPaths.LogFile(endpoint), 30);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(log) || log == "暂无日志"
                            ? "Python 服务启动后立即退出，请确认已安装 Python 3。"
                            : log);
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Refresh(endpoint);
                    Activity = $"{reason} {endpoint.Name}，PID {pid}";
                    if (endpoint.AutoOpen)
                    {
                        OpenSelectedUrl();
                    }
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Activity = $"启动失败：{ex.Message}";
                    Refresh(endpoint);
                });
            }
        });
    }

    public void Stop(WebEndpoint endpoint)
    {
        IsBusy = true;
        Task.Run(() =>
        {
            var messages = StopSynchronously(endpoint);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                if (Endpoints.Any(item => item.Id == endpoint.Id))
                {
                    Refresh(endpoint);
                }

                Activity = $"{endpoint.Name}：{string.Join("，", messages)}";
            });
        });
    }

    public void OpenSelectedUrl()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        if (!Statuses.TryGetValue(endpoint.Id, out var status) || !status.Running)
        {
            Activity = "服务已停止，请先启动后再打开";
            return;
        }

        var url = status.Urls.FirstOrDefault();
        if (url is null)
        {
            Activity = "没有可用的局域网地址";
            return;
        }

        OpenUrl(url);
        Activity = $"已打开：{url}";
    }

    public void OpenSelectedLocalUrl()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        if (!Statuses.TryGetValue(endpoint.Id, out var status) || !status.Running)
        {
            Activity = "服务已停止，请先启动后再打开";
            return;
        }

        OpenUrl(status.LocalUrl);
        Activity = $"已打开本机地址：{status.LocalUrl}";
    }

    public void RevealSelectedFolder()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = endpoint.RootPath,
            UseShellExecute = true
        });
    }

    public void ChooseHomepage(string selectedPath)
    {
        if (SelectedEndpoint is not { } endpoint) return;

        var rootPath = Path.GetFullPath(endpoint.RootPath);
        var fullPath = Path.GetFullPath(selectedPath);
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            Activity = "主页必须位于当前网页目录中";
            return;
        }

        var relative = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative)) return;

        endpoint.UrlPath = "/" + relative;
        Save();
        Refresh(endpoint);
        OnPropertyChanged(nameof(SelectedEndpoint));
        Activity = $"已选择主页：{relative}";
    }

    public void CopyUrls()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        if (!Statuses.TryGetValue(endpoint.Id, out var status) || !status.Running)
        {
            Activity = "服务已停止，访问地址暂不可用";
            return;
        }

        if (status.Urls.Count == 0)
        {
            Activity = "没有可复制的局域网地址";
            return;
        }

        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, status.Urls));
        Activity = "已复制访问地址";
    }

    public void RunCommand()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        var trimmed = Command.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        TerminalOutput += $"\n\n$ {trimmed}";
        Command = "";
        IsBusy = true;

        Task.Run(() =>
        {
            var result = ShellHelper.RunCommand(trimmed, endpoint.RootPath);
            var text = string.Join("\n", new[] { result.Stdout, result.Stderr }.Where(item => !string.IsNullOrWhiteSpace(item)));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                TerminalOutput += $"\n{(string.IsNullOrWhiteSpace(text) ? "(无输出)" : text)}";
                Activity = result.Ok ? "命令完成" : $"命令失败，退出码 {result.ExitCode}";
                Refresh(endpoint);
            });
        });
    }

    private List<string> StopSynchronously(WebEndpoint endpoint)
    {
        var filePid = PidFromFile(EndpointPaths.PidFile(endpoint));
        if (filePid is null)
        {
            filePid = PidFromFile(EndpointPaths.LegacyPidFile(endpoint));
        }
        var targets = NetworkHelper.ListenerPids(endpoint.Port).ToHashSet();
        if (filePid is int pid) targets.Add(pid);

        var messages = new List<string>();
        if (targets.Count == 0)
        {
            messages.Add("服务未运行");
        }
        else
        {
            foreach (var target in targets.OrderBy(item => item))
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(target);
                    process.Kill(true);
                    messages.Add($"已发送关闭信号：{target}");
                }
                catch (Exception ex)
                {
                    messages.Add($"关闭失败 {target}：{ex.Message}");
                }
            }
        }

        Thread.Sleep(500);
        EndpointPaths.TryDeletePid(endpoint);
        return messages;
    }

    private static EndpointStatus StatusFor(WebEndpoint endpoint)
    {
        var pids = NetworkHelper.ListenerPids(endpoint.Port);
        var urlPath = NormalizeUrlPath(endpoint.UrlPath);
        return new EndpointStatus
        {
            Running = pids.Count > 0 || NetworkHelper.IsPortOpen(endpoint.Host, endpoint.Port),
            Pids = pids,
            Urls = NetworkHelper.LanIps().Select(ip => BuildServiceUrl(ip, endpoint.Port, urlPath)).ToList(),
            LocalUrl = BuildServiceUrl("127.0.0.1", endpoint.Port, urlPath),
            PageCount = PageCount(endpoint.RootPath),
            IndexMtime = Mtime(Path.Combine(endpoint.RootPath, "index.html")),
            LogTail = Tail(EndpointPaths.LogFile(endpoint), 80),
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    private void Load()
    {
        Endpoints.Clear();
        try
        {
            var data = File.ReadAllText(_configPath);
            var items = JsonSerializer.Deserialize<List<WebEndpoint>>(data, JsonOptions) ?? [];
            foreach (var item in items)
            {
                Endpoints.Add(item);
            }
        }
        catch
        {
            // use empty list
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Endpoints.ToList(), JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Activity = $"保存配置失败：{ex.Message}";
        }
    }

    private void SeedDefaultEndpoints()
    {
        // Windows 版不预置 macOS 专用路径
    }

    private static int? PidFromFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildServiceUrl(string host, int port, string urlPath)
    {
        var path = NormalizeUrlPath(urlPath);
        return new UriBuilder("http", host, port, path).Uri.AbsoluteUri;
    }

    private static string NormalizeUrlPath(string path)
    {
        var value = path.Trim();
        if (string.IsNullOrEmpty(value)) value = "/";
        if (!value.StartsWith('/')) value = "/" + value;
        return value;
    }

    private static int PageCount(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/').ToLowerInvariant();
            if ((rel.EndsWith(".html") || rel.EndsWith(".md")) && rel != "index.html" && !rel.EndsWith("/index.html"))
            {
                count++;
            }
        }

        return count;
    }

    private static string Mtime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return "--";
        }
    }

    private static string Tail(string path, int lines)
    {
        try
        {
            var text = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, text.TakeLast(lines));
        }
        catch
        {
            return "暂无日志";
        }
    }

    private static void OpenUrl(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
