using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
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

    private readonly string _supportDir;
    private readonly string _configPath;
    private readonly string _targetsPath;
    private readonly string _receiverSettingsPath;
    private readonly string _receiverSitesPath;
    private readonly ReceiverServer _receiverServer;
    private readonly DispatcherTimer _timer;

    private Guid? _selection;
    private Guid? _selectedTargetId;
    private ReceiverSettings _receiverSettings = ReceiverSettings.CreateDefault();
    private string _activity = "准备就绪";
    private string _command = "";
    private string _terminalOutput = "选择一个网页目录后，可以在这里执行维护命令。";
    private bool _isBusy;

    public AppState()
    {
        _supportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LANWebTerminalManager");
        Directory.CreateDirectory(_supportDir);
        _configPath = Path.Combine(_supportDir, "endpoints.json");
        _targetsPath = Path.Combine(_supportDir, "targets.json");
        _receiverSettingsPath = Path.Combine(_supportDir, "settings.json");
        _receiverSitesPath = Path.Combine(_supportDir, "receiver-sites.json");

        _receiverServer = new ReceiverServer(HandleReceiverRequest);

        Load();
        LoadTargets();
        LoadReceiverSettings();
        ReloadReceiverSites();

        if (Endpoints.Count == 0)
        {
            SeedDefaultEndpoints();
        }

        Selection = VisibleEndpoints.FirstOrDefault()?.Id;
        StartReceiverServer();
        RefreshAll();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();
    }

    public ObservableCollection<WebEndpoint> Endpoints { get; } = new();
    public ObservableCollection<WebEndpoint> ReceiverSites { get; } = new();
    public ObservableCollection<RemoteTarget> Targets { get; } = new();
    public Dictionary<Guid, EndpointStatus> Statuses { get; } = new();

    public ReceiverSettings ReceiverSettings
    {
        get => _receiverSettings;
        set { _receiverSettings = value; OnPropertyChanged(); OnPropertyChanged(nameof(ReceiverLocalUrl)); OnPropertyChanged(nameof(ReceiverLanUrls)); }
    }

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

    public Guid? SelectedTargetId
    {
        get => _selectedTargetId;
        set
        {
            if (_selectedTargetId == value) return;
            _selectedTargetId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTarget));
            OnPropertyChanged(nameof(IsLocalTargetSelected));
            OnPropertyChanged(nameof(SelectedTargetLabel));
        }
    }

    public WebEndpoint? SelectedEndpoint
    {
        get
        {
            if (Selection is not Guid id) return null;
            return Endpoints.FirstOrDefault(item => item.Id == id)
                ?? ReceiverSites.FirstOrDefault(item => item.Id == id);
        }
    }

    public RemoteTarget? SelectedTarget =>
        SelectedTargetId is null ? null : Targets.FirstOrDefault(item => item.Id == SelectedTargetId);

    public bool IsLocalTargetSelected => SelectedTargetId is null;

    public string SelectedTargetLabel => SelectedTarget?.Name ?? "local";

    public IEnumerable<WebEndpoint> VisibleEndpoints
    {
        get
        {
            var owned = Endpoints.Where(item => item.TargetId == SelectedTargetId).ToList();
            if (SelectedTargetId is not null) return owned;

            var ownedIds = owned.Select(item => item.Id).ToHashSet();
            var incoming = ReceiverSites.Where(item => !ownedIds.Contains(item.Id)).ToList();
            return owned.Concat(incoming);
        }
    }

    public bool IsReceiverManaged(WebEndpoint endpoint) =>
        ReceiverSites.Any(item => item.Id == endpoint.Id);

    public void ReloadReceiverSites()
    {
        ReceiverSites.Clear();
        foreach (var site in LoadReceiverSites()) ReceiverSites.Add(site);
        SyncSelectionToVisibleCategory();
        NotifyVisibleEndpointsChanged();
    }

    public void RefreshLocalView()
    {
        ReloadReceiverSites();
        RefreshAll();
        Activity = "已刷新本地与接收方站点";
    }

    public string ReceiverLocalUrl => $"http://127.0.0.1:{ReceiverSettings.Port}";

    public IEnumerable<string> ReceiverLanUrls =>
        NetworkHelper.LanIps().Select(ip => $"http://{ip}:{ReceiverSettings.Port}");

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

    public void SelectLocalTarget()
    {
        SelectedTargetId = null;
        ReloadReceiverSites();
        SyncSelectionToVisibleCategory();
        RefreshAll();
        Activity = "目标已切换：local";
        NotifyVisibleEndpointsChanged();
    }

    public void SelectTarget(RemoteTarget target)
    {
        SelectedTargetId = target.Id;
        SyncSelectionToVisibleCategory();
        RefreshAll();
        Activity = $"目标已切换：{target.Name}";
        NotifyVisibleEndpointsChanged();
    }

    public void SyncSelectionToVisibleCategory()
    {
        var visible = VisibleEndpoints.ToList();
        if (Selection is Guid id && visible.Any(item => item.Id == id))
        {
            return;
        }

        Selection = visible.FirstOrDefault()?.Id;
    }

    public string TargetName(Guid? targetId) =>
        targetId is null ? "local" : Targets.FirstOrDefault(item => item.Id == targetId)?.Name ?? "未知分类";

    public void TransferEndpoint(WebEndpoint endpoint, Guid? toTargetId)
    {
        var index = Endpoints.IndexOf(endpoint);
        if (index < 0 || Endpoints[index].TargetId == toTargetId) return;

        Endpoints[index].TargetId = toTargetId;
        Save();
        SyncSelectionToVisibleCategory();
        NotifyVisibleEndpointsChanged();
        Activity = $"已转移至 {TargetName(toTargetId)}：{endpoint.Name}";
    }

    public void CopyEndpoint(WebEndpoint endpoint, Guid? toTargetId)
    {
        if (Endpoints.FirstOrDefault(item => item.Id == endpoint.Id) is not { } source) return;

        var copy = new WebEndpoint
        {
            Id = Guid.NewGuid(),
            Name = source.Name,
            RootPath = source.RootPath,
            Port = NextAvailablePort(),
            Host = source.Host,
            UrlPath = source.UrlPath,
            AutoOpen = source.AutoOpen,
            TargetId = toTargetId
        };
        Endpoints.Add(copy);
        Save();
        NotifyVisibleEndpointsChanged();
        Activity = $"已复制至 {TargetName(toTargetId)}：{copy.Name}";
    }

    public void DeployTestHtml()
    {
        if (SelectedEndpoint is not { } endpoint) return;
        var index = Endpoints.IndexOf(endpoint);
        if (index < 0) return;

        if (!Directory.Exists(endpoint.RootPath))
        {
            Activity = $"目录不存在：{endpoint.RootPath}";
            return;
        }

        var display = SelectedTarget is null ? ReceiverTestDisplayText() : "等待接收方生成";
        var html = TestHtml(display);

        try
        {
            File.WriteAllText(Path.Combine(endpoint.RootPath, "remote-test.html"), html);
            Endpoints[index].UrlPath = "/remote-test.html";
            Save();
            Refresh(Endpoints[index]);
            Activity = SelectedTarget is null
                ? $"已部署测试页：{display}"
                : "已部署测试页模板，接收方会生成实际平台和令牌";
        }
        catch (Exception ex)
        {
            Activity = $"部署测试页失败：{ex.Message}";
        }
    }

    private int NextAvailablePort()
    {
        var usedPorts = Endpoints.Select(item => item.Port).ToHashSet();
        var port = 8088;
        while (usedPorts.Contains(port)) port++;
        return port;
    }

    private void NotifyVisibleEndpointsChanged() => OnPropertyChanged(nameof(VisibleEndpoints));

    public void AddEndpoint(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            Activity = $"目录不存在：{rootPath}";
            return;
        }

        var port = NextAvailablePort();

        var endpoint = new WebEndpoint
        {
            Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            RootPath = rootPath,
            Port = port,
            TargetId = SelectedTargetId
        };

        if (Directory.Exists(Path.Combine(rootPath, "web")))
        {
            endpoint.UrlPath = "/web/";
        }

        Endpoints.Add(endpoint);
        Selection = endpoint.Id;
        Save();
        Refresh(endpoint);
        NotifyVisibleEndpointsChanged();
        Activity = $"已添加：{endpoint.Name}";
    }

    public void RemoveSelected()
    {
        if (SelectedEndpoint is { } endpoint) Remove(endpoint);
    }

    public void Remove(WebEndpoint endpoint)
    {
        Stop(endpoint, forceLocal: IsReceiverManaged(endpoint));
        if (IsReceiverManaged(endpoint))
        {
            var sites = ReceiverSites.Where(item => item.Id != endpoint.Id).ToList();
            ReceiverSites.Clear();
            foreach (var site in sites) ReceiverSites.Add(site);
            SaveReceiverSites(sites);
        }
        else
        {
            Endpoints.Remove(endpoint);
            Save();
        }

        Statuses.Remove(endpoint.Id);
        SyncSelectionToVisibleCategory();
        NotifyVisibleEndpointsChanged();
        Activity = $"已移除：{endpoint.Name}";
        OnPropertyChanged(nameof(SelectedEndpoint));
        OnPropertyChanged(nameof(SelectedStatus));
    }

    public void SaveEndpoint(WebEndpoint endpoint)
    {
        if (IsReceiverManaged(endpoint))
        {
            SaveReceiverSites(ReceiverSites.ToList());
        }
        else
        {
            Save();
        }

        Refresh(endpoint);
    }

    public void RefreshAll()
    {
        var snapshot = VisibleEndpoints.ToList();
        var target = SelectedTarget;
        Task.Run(() =>
        {
            var results = snapshot.ToDictionary(
                item => item.Id,
                item => target is null ? StatusFor(item) : RemoteStatusFor(item, target));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var endpoint in snapshot)
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
        var target = SelectedTarget;
        Task.Run(() =>
        {
            var status = target is null ? StatusFor(endpoint) : RemoteStatusFor(endpoint, target);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Endpoints.Any(item => item.Id == endpoint.Id) || ReceiverSites.Any(item => item.Id == endpoint.Id))
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
            if (SelectedTarget is null)
            {
                StopSynchronously(endpoint);
            }
            else
            {
                StopRemoteSync(endpoint, SelectedTarget);
            }

            WaitForEndpointRelease(endpoint, TimeSpan.FromSeconds(5));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                Start(endpoint, "已刷新资产");
            });
        });
    }

    public void Start(WebEndpoint endpoint, string reason = "已启动", bool forceLocal = false)
    {
        if (!forceLocal && SelectedTarget is { } target)
        {
            StartRemote(endpoint, target);
            return;
        }

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
                var pid = StartLocalSynchronously(endpoint);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Refresh(endpoint);
                    Activity = $"{reason} {endpoint.Name}，PID {pid}";
                    if (endpoint.AutoOpen) OpenSelectedUrl();
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

    public void StartRemote(WebEndpoint endpoint, RemoteTarget target)
    {
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
                var payload = new ReceiverSyncPayload
                {
                    Token = target.Token,
                    Endpoint = new ReceiverEndpointPayload
                    {
                        Id = endpoint.Id,
                        Name = endpoint.Name,
                        Port = endpoint.Port,
                        Host = endpoint.Host,
                        UrlPath = endpoint.UrlPath
                    },
                    Files = CollectSiteFiles(endpoint.RootPath)
                };
                var url = $"{target.ServerURL.TrimmedSlash()}/api/receiver/sites/{endpoint.Id}/sync-start";
                var data = RemoteApiClient.Request(url, "POST", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
                var response = JsonSerializer.Deserialize<ReceiverEndpointResponse>(data, JsonOptions)
                    ?? throw new InvalidOperationException("目标服务器没有返回有效响应");
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    if (response.Status is not null)
                    {
                        Statuses[endpoint.Id] = response.Status.ToStatus(response.Status.Urls.FirstOrDefault() ?? "");
                    }

                    Activity = $"已在 {target.Name} 启动：{endpoint.Name}";
                    if (endpoint.AutoOpen && response.Status?.Urls.FirstOrDefault() is { } openUrl)
                    {
                        OpenUrl(openUrl);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Activity = $"远端启动失败：{ex.Message}";
                    Refresh(endpoint);
                });
            }
        });
    }

    public void Stop(WebEndpoint endpoint, bool forceLocal = false)
    {
        if (!forceLocal && SelectedTarget is { } target)
        {
            StopRemote(endpoint, target);
            return;
        }

        IsBusy = true;
        Task.Run(() =>
        {
            var messages = StopSynchronously(endpoint);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                if (Endpoints.Any(item => item.Id == endpoint.Id)) Refresh(endpoint);
                Activity = $"{endpoint.Name}：{string.Join("，", messages)}";
            });
        });
    }

    public void StopRemote(WebEndpoint endpoint, RemoteTarget target)
    {
        IsBusy = true;
        Task.Run(() =>
        {
            try
            {
                StopRemoteSync(endpoint, target);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Refresh(endpoint);
                    Activity = $"已停止 {target.Name}：{endpoint.Name}";
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Activity = $"远端停止失败：{ex.Message}";
                    Refresh(endpoint);
                });
            }
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

        var url = status.Urls.FirstOrDefault() ?? UrlsFor(endpoint).FirstOrDefault();
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

        string url;
        if (SelectedTarget is not null)
        {
            url = status.Urls.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(url))
            {
                Activity = "没有可用的访问地址";
                return;
            }
        }
        else
        {
            url = string.IsNullOrEmpty(status.LocalUrl) ? LocalUrlFor(endpoint) : status.LocalUrl;
        }

        OpenUrl(url);
        Activity = SelectedTarget is null ? $"已打开本机地址：{url}" : $"已打开：{url}";
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
        if (IsReceiverManaged(endpoint))
        {
            SaveReceiverSites(ReceiverSites.ToList());
        }
        else
        {
            Save();
        }
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

        var urlItems = status.Urls.Count > 0 ? status.Urls : UrlsFor(endpoint);
        if (urlItems.Count == 0)
        {
            Activity = "没有可复制的局域网地址";
            return;
        }

        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, urlItems));
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

        if (SelectedTarget is { } target)
        {
            Task.Run(() =>
            {
                try
                {
                    var payload = new ReceiverCommandPayload { Token = target.Token, Command = trimmed };
                    var url = $"{target.ServerURL.TrimmedSlash()}/api/receiver/sites/{endpoint.Id}/command";
                    var data = RemoteApiClient.Request(url, "POST", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
                    var result = JsonSerializer.Deserialize<RemoteCommandResult>(data, JsonOptions)
                        ?? throw new InvalidOperationException("远端命令没有返回有效结果");
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        IsBusy = false;
                        TerminalOutput += $"\n{(string.IsNullOrWhiteSpace(result.Output) ? "(无输出)" : result.Output)}";
                        Activity = result.Ok ? "命令完成" : $"命令失败，退出码 {result.Status}";
                        Refresh(endpoint);
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        IsBusy = false;
                        TerminalOutput += $"\n{ex.Message}";
                        Activity = "远端命令失败";
                        Refresh(endpoint);
                    });
                }
            });
            return;
        }

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

    public void SaveTargets()
    {
        try
        {
            File.WriteAllText(_targetsPath, JsonSerializer.Serialize(Targets.ToList(), JsonOptions));
        }
        catch (Exception ex)
        {
            Activity = $"保存目标服务器失败：{ex.Message}";
        }
    }

    public void SaveReceiverSettings()
    {
        try
        {
            var token = ReceiverSettings.Token.Trim();
            if (string.IsNullOrEmpty(token))
            {
                Activity = "接收令牌不能为空";
                return;
            }

            ReceiverSettings.Token = token;
            Directory.CreateDirectory(ReceiverSettings.RootPath);
            File.WriteAllText(_receiverSettingsPath, JsonSerializer.Serialize(ReceiverSettings, JsonOptions));
            StartReceiverServer();
            OnPropertyChanged(nameof(ReceiverLocalUrl));
            OnPropertyChanged(nameof(ReceiverLanUrls));
            Activity = "已保存接收方设置";
        }
        catch (Exception ex)
        {
            Activity = $"保存接收方设置失败：{ex.Message}";
        }
    }

    public void AddTarget()
    {
        var target = new RemoteTarget
        {
            Name = "新服务器",
            ServerURL = "http://192.168.1.20:4177",
            Token = ""
        };
        Targets.Add(target);
        SelectedTargetId = target.Id;
        SaveTargets();
        OnPropertyChanged(nameof(SelectedTargetLabel));
    }

    public void RemoveTarget(RemoteTarget target)
    {
        for (var index = 0; index < Endpoints.Count; index++)
        {
            if (Endpoints[index].TargetId == target.Id)
            {
                Endpoints[index].TargetId = null;
            }
        }

        Targets.Remove(target);
        if (SelectedTargetId == target.Id) SelectedTargetId = null;
        Save();
        SaveTargets();
        SyncSelectionToVisibleCategory();
        NotifyVisibleEndpointsChanged();
        RefreshAll();
    }

    public void ProbeTarget(RemoteTarget target)
    {
        var index = Targets.IndexOf(target);
        if (index < 0) return;

        IsBusy = true;
        Task.Run(() =>
        {
            try
            {
                var url = $"{target.ServerURL.TrimmedSlash()}/api/settings";
                var data = RemoteApiClient.Request(url, "GET");
                var payload = JsonSerializer.Deserialize<ReceiverSettingsPayload>(data, JsonOptions)
                    ?? throw new InvalidOperationException("无效响应");
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Targets[index].Platform = payload.Platform;
                    SaveTargets();
                    Activity = $"已连接：{Targets[index].Name}（{payload.Platform}）";
                });
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Activity = $"连接目标失败：{ex.Message}";
                });
            }
        });
    }

    private int StartLocalSynchronously(WebEndpoint endpoint)
    {
        StopSynchronously(endpoint);
        WaitForEndpointRelease(endpoint, TimeSpan.FromSeconds(5));

        var pid = PythonHelper.StartServer(endpoint);
        try { EndpointPaths.TryWritePid(endpoint, pid); } catch (UnauthorizedAccessException) { } catch (IOException) { }

        WaitForServerRunning(endpoint, TimeSpan.FromSeconds(5));
        var status = StatusFor(endpoint);
        if (!status.Running)
        {
            var log = Tail(EndpointPaths.LogFile(endpoint), 30);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(log) || log == "暂无日志"
                    ? "Python 服务启动后立即退出，请确认已安装 Python 3。"
                    : log);
        }

        return pid;
    }

    private void StopRemoteSync(WebEndpoint endpoint, RemoteTarget target)
    {
        var payload = new ReceiverCommandPayload { Token = target.Token };
        var url = $"{target.ServerURL.TrimmedSlash()}/api/receiver/sites/{endpoint.Id}/stop";
        var data = RemoteApiClient.Request(url, "POST", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        var response = JsonSerializer.Deserialize<ReceiverEndpointResponse>(data, JsonOptions);
        if (response?.Status is not null)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Statuses[endpoint.Id] = response.Status.ToStatus(response.Status.Urls.FirstOrDefault() ?? "");
            });
        }
    }

    private List<string> StopSynchronously(WebEndpoint endpoint)
    {
        var filePid = PidFromFile(EndpointPaths.PidFile(endpoint)) ?? PidFromFile(EndpointPaths.LegacyPidFile(endpoint));
        var pids = NetworkHelper.ListenerPids(endpoint.Port).ToHashSet();
        if (filePid is int pid) pids.Add(pid);

        var messages = new List<string>();
        var killedProcesses = new List<System.Diagnostics.Process>();
        if (pids.Count == 0)
        {
            messages.Add("服务未运行");
        }
        else
        {
            foreach (var target in pids.OrderBy(item => item))
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(target);
                    process.Kill(true);
                    killedProcesses.Add(process);
                    messages.Add($"已发送关闭信号：{target}");
                }
                catch (Exception ex)
                {
                    messages.Add($"关闭失败 {target}：{ex.Message}");
                }
            }
        }

        foreach (var process in killedProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.WaitForExit(2000);
                }
            }
            catch
            {
                // ignore wait failures for already-exited processes
            }
            finally
            {
                process.Dispose();
            }
        }

        EndpointPaths.TryDeletePid(endpoint);
        return messages;
    }

    private static void WaitForEndpointRelease(WebEndpoint endpoint, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var logPath = EndpointPaths.LogFile(endpoint);

        while (DateTime.UtcNow < deadline)
        {
            if (NetworkHelper.ListenerPids(endpoint.Port).Count == 0
                && EndpointPaths.IsLogFileAccessible(endpoint))
            {
                return;
            }

            Thread.Sleep(50);
        }

        var remainingPids = NetworkHelper.ListenerPids(endpoint.Port);
        if (remainingPids.Count > 0)
        {
            throw new InvalidOperationException(
                $"端口 {endpoint.Port} 仍被进程占用（PID: {string.Join(", ", remainingPids)}），请稍后重试。");
        }

        throw new InvalidOperationException($"日志文件仍被占用，无法启动服务：{logPath}");
    }

    private static void WaitForServerRunning(WebEndpoint endpoint, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (NetworkHelper.ListenerPids(endpoint.Port).Count > 0
                || NetworkHelper.IsPortOpen(endpoint.Host, endpoint.Port))
            {
                return;
            }

            Thread.Sleep(100);
        }
    }

    private EndpointStatus RemoteStatusFor(WebEndpoint endpoint, RemoteTarget target)
    {
        try
        {
            var url = $"{target.ServerURL.TrimmedSlash()}/api/receiver/sites/{endpoint.Id}/status?token={target.Token.UrlQueryEscaped()}";
            var data = RemoteApiClient.Request(url, "GET");
            var response = JsonSerializer.Deserialize<ReceiverEndpointResponse>(data, JsonOptions);
            return response?.Status?.ToStatus(response.Status.Urls.FirstOrDefault() ?? "")
                ?? new EndpointStatus { LogTail = "目标服务器没有返回状态" };
        }
        catch (Exception ex)
        {
            return new EndpointStatus { LogTail = $"无法连接目标服务器：{ex.Message}" };
        }
    }

    private (int Status, byte[] Body) HandleReceiverRequest(string method, string path, byte[] body)
    {
        if (method == "GET" && path == "/api/settings")
        {
            return JsonResponse(200, new ReceiverSettingsPayload
            {
                Platform = "win32",
                ProtocolVersion = 1
            });
        }

        var pathOnly = path.Split('?')[0];
        var parts = pathOnly.Trim('/').Split('/');
        if (parts.Length < 5 || parts[0] != "api" || parts[1] != "receiver" || parts[2] != "sites")
        {
            return JsonResponse(404, new Dictionary<string, string> { ["error"] = "未知 API" });
        }

        var siteId = parts[3];
        var action = parts[4];

        try
        {
            if (method == "POST" && action == "sync-start")
            {
                var payload = JsonSerializer.Deserialize<ReceiverSyncPayload>(body, JsonOptions)
                    ?? throw new InvalidOperationException("无效请求体");
                if (payload.Token != ReceiverSettings.Token)
                {
                    return JsonResponse(403, new Dictionary<string, string> { ["error"] = "接收令牌无效" });
                }

                WebEndpoint? existingSite = null;
                if (Guid.TryParse(siteId, out var existingId))
                {
                    existingSite = LoadReceiverSites().FirstOrDefault(item => item.Id == existingId);
                }

                if (existingSite is not null)
                {
                    StopSynchronously(existingSite);
                    WaitForEndpointRelease(existingSite, TimeSpan.FromSeconds(5));
                }

                var endpoint = ReceiveSite(payload);
                StartLocalSynchronously(endpoint);
                System.Windows.Application.Current?.Dispatcher.Invoke(ReloadReceiverSites);
                return EndpointResponse(endpoint);
            }

            var commandPayload = body.Length > 0
                ? JsonSerializer.Deserialize<ReceiverCommandPayload>(body, JsonOptions)
                : null;
            var token = commandPayload?.Token;
            if (string.IsNullOrEmpty(token))
            {
                token = ParseQueryToken(path);
            }

            if (token != ReceiverSettings.Token)
            {
                return JsonResponse(403, new Dictionary<string, string> { ["error"] = "接收令牌无效" });
            }

            if (!Guid.TryParse(siteId, out var id))
            {
                return JsonResponse(404, new Dictionary<string, string> { ["error"] = "接收方未找到该站点" });
            }

            var endpointFromSite = LoadReceiverSites().FirstOrDefault(item => item.Id == id);
            if (endpointFromSite is null)
            {
                return JsonResponse(404, new Dictionary<string, string> { ["error"] = "接收方未找到该站点" });
            }

            if (method == "GET" && action == "status")
            {
                return EndpointResponse(endpointFromSite);
            }

            if (method == "POST" && action == "stop")
            {
                StopSynchronously(endpointFromSite);
                WaitForEndpointRelease(endpointFromSite, TimeSpan.FromSeconds(5));
                return EndpointResponse(endpointFromSite);
            }

            if (method == "POST" && action == "command")
            {
                var command = commandPayload?.Command?.Trim();
                if (string.IsNullOrEmpty(command))
                {
                    return JsonResponse(400, new Dictionary<string, string> { ["error"] = "请输入命令" });
                }

                var result = ShellHelper.RunCommand(command, endpointFromSite.RootPath);
                var output = string.Join("\n", new[] { result.Stdout, result.Stderr }.Where(item => !string.IsNullOrWhiteSpace(item)));
                return JsonResponse(200, new RemoteCommandResult
                {
                    Ok = result.Ok,
                    Status = result.ExitCode,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    Output = string.IsNullOrWhiteSpace(output) ? "(无输出)" : output
                });
            }

            return JsonResponse(404, new Dictionary<string, string> { ["error"] = "未知 API" });
        }
        catch (Exception ex)
        {
            return JsonResponse(500, new Dictionary<string, string> { ["error"] = ex.Message });
        }
    }

    private WebEndpoint ReceiveSite(ReceiverSyncPayload payload)
    {
        var siteName = SanitizeSiteName(payload.Endpoint.Name);
        var root = Path.Combine(ReceiverSettings.RootPath, siteName);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        foreach (var file in payload.Files)
        {
            var target = SafeReceiverFile(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = Convert.FromBase64String(file.Content);
            File.WriteAllBytes(target, bytes);
        }

        if (payload.Files.Any(file => file.Path.Replace('\\', '/') == "remote-test.html"))
        {
            File.WriteAllText(
                Path.Combine(root, "remote-test.html"),
                TestHtml(ReceiverTestDisplayText()));
        }

        var endpoint = new WebEndpoint
        {
            Id = payload.Endpoint.Id,
            Name = payload.Endpoint.Name,
            RootPath = root,
            Port = payload.Endpoint.Port,
            Host = payload.Endpoint.Host,
            UrlPath = payload.Endpoint.UrlPath,
            AutoOpen = false
        };

        var sites = LoadReceiverSites();
        sites.RemoveAll(item => item.Id == endpoint.Id);
        sites.Add(endpoint);
        SaveReceiverSites(sites);
        return endpoint;
    }

    private List<WebEndpoint> LoadReceiverSites()
    {
        try
        {
            return JsonSerializer.Deserialize<List<WebEndpoint>>(File.ReadAllText(_receiverSitesPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveReceiverSites(List<WebEndpoint> sites)
    {
        File.WriteAllText(_receiverSitesPath, JsonSerializer.Serialize(sites, JsonOptions));
    }

    private static string SafeReceiverFile(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.Length == 0 || normalized.Any(part => part == "..") || relativePath.Contains(':') || relativePath.StartsWith('/'))
        {
            throw new InvalidOperationException($"非法文件路径：{relativePath}");
        }

        return normalized.Aggregate(root, Path.Combine);
    }

    private static string SanitizeSiteName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray();
        var name = new string(chars).Trim('_');
        return string.IsNullOrEmpty(name) ? "site" : name;
    }

    private string ReceiverTestDisplayText()
    {
        var token = ReceiverSettings.Token.Trim();
        return $"{CurrentPlatformDisplayName()} + {(string.IsNullOrEmpty(token) ? "未设置接收令牌" : token)}";
    }

    private static string CurrentPlatformDisplayName() => "Windows";

    private static string TestHtml(string display) =>
        $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>LWM Remote Test</title>
          <style>
            body {
              min-height: 100vh;
              margin: 0;
              display: grid;
              place-items: center;
              background: #f5f7fa;
              color: #111827;
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            }
            h1 {
              max-width: 90vw;
              margin: 0;
              font-size: clamp(36px, 9vw, 88px);
              line-height: 1.05;
              text-align: center;
              word-break: break-word;
            }
          </style>
        </head>
        <body>
          <h1>{{display.HtmlEscaped()}}</h1>
        </body>
        </html>
        """;

    private (int Status, byte[] Body) EndpointResponse(WebEndpoint endpoint)
    {
        var status = StatusFor(endpoint);
        var response = new ReceiverEndpointResponse
        {
            Endpoint = endpoint,
            Status = EndpointStatusDto.FromStatus(status)
        };
        return JsonResponse(200, response);
    }

    private static (int Status, byte[] Body) JsonResponse<T>(int status, T body) =>
        (status, JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions));

    private static List<SiteFilePayload> CollectSiteFiles(string rootPath)
    {
        var files = new List<SiteFilePayload>();
        foreach (var fullPath in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
            var lower = rel.ToLowerInvariant();
            if (lower.StartsWith(".git/") || lower.StartsWith("node_modules/") || lower.StartsWith("build/") || lower.StartsWith(".build/"))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(fullPath);
            files.Add(new SiteFilePayload
            {
                Path = rel,
                Content = Convert.ToBase64String(bytes)
            });
        }

        return files;
    }

    private List<string> UrlsFor(WebEndpoint endpoint)
    {
        var urlPath = NormalizeUrlPath(endpoint.UrlPath);
        return NetworkHelper.LanIps().Select(ip => BuildServiceUrl(ip, endpoint.Port, urlPath)).ToList();
    }

    private string LocalUrlFor(WebEndpoint endpoint) =>
        BuildServiceUrl("127.0.0.1", endpoint.Port, NormalizeUrlPath(endpoint.UrlPath));

    private EndpointStatus StatusFor(WebEndpoint endpoint)
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
            var items = JsonSerializer.Deserialize<List<WebEndpoint>>(File.ReadAllText(_configPath), JsonOptions) ?? [];
            foreach (var item in items) Endpoints.Add(item);
        }
        catch { }
    }

    private void LoadTargets()
    {
        Targets.Clear();
        try
        {
            var items = JsonSerializer.Deserialize<List<RemoteTarget>>(File.ReadAllText(_targetsPath), JsonOptions) ?? [];
            foreach (var item in items) Targets.Add(item);
        }
        catch { }
    }

    private void LoadReceiverSettings()
    {
        try
        {
            ReceiverSettings = JsonSerializer.Deserialize<ReceiverSettings>(File.ReadAllText(_receiverSettingsPath), JsonOptions)
                ?? ReceiverSettings.CreateDefault();
        }
        catch
        {
            ReceiverSettings = ReceiverSettings.CreateDefault();
            SaveReceiverSettings();
        }

        Directory.CreateDirectory(ReceiverSettings.RootPath);
    }

    private void StartReceiverServer()
    {
        try
        {
            _receiverServer.Start(ReceiverSettings.Port);
        }
        catch (Exception ex)
        {
            Activity = $"接收方服务启动失败：{ex.Message}";
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(Endpoints.ToList(), JsonOptions));
        }
        catch (Exception ex)
        {
            Activity = $"保存配置失败：{ex.Message}";
        }
    }

    private static void SeedDefaultEndpoints() { }

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
        return new UriBuilder("http", host, port, NormalizeUrlPath(urlPath)).Uri.AbsoluteUri;
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
        try { return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { return "--"; }
    }

    private static string Tail(string path, int lines)
    {
        try { return string.Join(Environment.NewLine, File.ReadAllLines(path).TakeLast(lines)); }
        catch { return "暂无日志"; }
    }

    private static void OpenUrl(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static string? ParseQueryToken(string path)
    {
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0) return null;
        foreach (var part in path[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "token") return Uri.UnescapeDataString(kv[1]);
        }

        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
