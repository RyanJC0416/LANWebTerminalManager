using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANWebTerminalManager.Services;

public sealed class UpdateManager : INotifyPropertyChanged
{
    public const string CurrentVersion = "2.0.1";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private bool _isChecking;
    private bool _isDownloading;
    private bool _isInstalling;
    private string? _statusText;
    private double? _downloadProgress;
    private string _message = "";
    private bool _isPresentingMessage;
    private bool _canRetry;
    private GitHubRelease? _pendingRelease;
    private GitHubReleaseAsset? _pendingAsset;
    private UpdateAction? _retryAction;

    public UpdateManager()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LANWebTerminalManager");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set { _isChecking = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(PrimaryButtonTitle)); }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set { _isDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(PrimaryButtonTitle)); }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set { _isInstalling = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(PrimaryButtonTitle)); }
    }

    public string? StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public double? DownloadProgress
    {
        get => _downloadProgress;
        private set { _downloadProgress = value; OnPropertyChanged(); }
    }

    public string Message
    {
        get => _message;
        private set { _message = value; OnPropertyChanged(); }
    }

    public bool IsPresentingMessage
    {
        get => _isPresentingMessage;
        set { _isPresentingMessage = value; OnPropertyChanged(); }
    }

    public bool CanRetry
    {
        get => _canRetry;
        private set { _canRetry = value; OnPropertyChanged(); }
    }

    public bool IsBusy => IsChecking || IsDownloading || IsInstalling;

    public string PrimaryButtonTitle
    {
        get
        {
            if (IsChecking) return "检查中...";
            if (IsDownloading) return "下载中...";
            if (IsInstalling) return "安装中...";
            if (_pendingAsset is not null) return "立即更新";
            return "检查更新";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? UpdateReadyToInstall;

    public async Task PerformPrimaryUpdateActionAsync()
    {
        if (IsBusy) return;
        if (_pendingAsset is not null)
        {
            await InstallPendingUpdateAsync();
        }
        else
        {
            await CheckForUpdatesAsync();
        }
    }

    public async Task RetryLastFailedActionAsync()
    {
        var action = _retryAction;
        CanRetry = false;
        _retryAction = null;
        IsPresentingMessage = false;

        if (action == UpdateAction.Install)
        {
            await InstallPendingUpdateAsync();
        }
        else
        {
            await CheckForUpdatesAsync();
        }
    }

    public void OpenReleasePage()
    {
        var url = _pendingRelease?.TagName is { } tag
            ? $"https://github.com/RyanJC0416/LANWebTerminalManager/releases/tag/{tag}"
            : "https://github.com/RyanJC0416/LANWebTerminalManager/releases/latest";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task CheckForUpdatesAsync()
    {
        IsChecking = true;
        StatusText = "正在检查更新...";
        DownloadProgress = null;
        _pendingRelease = null;
        _pendingAsset = null;

        try
        {
            var release = await FetchLatestReleaseAsync();
            var latestVersion = release.TagName.TrimStart('v', 'V');

            if (IsVersion(CurrentVersion, latestVersion))
            {
                StatusText = $"当前版本 {CurrentVersion} 高于最新版本 {latestVersion}";
                return;
            }

            if (!IsVersion(latestVersion, CurrentVersion))
            {
                StatusText = $"当前已是最新版本 {latestVersion}";
                return;
            }

            var asset = release.Assets.FirstOrDefault(IsWindowsAsset);
            if (asset is null)
            {
                ShowFailure(new InvalidOperationException("更新包里没有找到 Windows 版本。"), UpdateAction.Check);
                return;
            }

            _pendingRelease = release;
            _pendingAsset = asset;
            StatusText = $"发现新版本 {latestVersion}";
            OnPropertyChanged(nameof(PrimaryButtonTitle));
        }
        catch (Exception ex)
        {
            StatusText = "检查更新失败";
            ShowFailure(ex, UpdateAction.Check);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task InstallPendingUpdateAsync()
    {
        if (_pendingAsset is not { } asset)
        {
            await CheckForUpdatesAsync();
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = $"正在下载更新 {_pendingRelease?.TagName}";

        try
        {
            var updatesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LANWebTerminalManager", "updates");
            Directory.CreateDirectory(updatesDir);

            var tempDir = Path.Combine(updatesDir, $"LANWebTerminalManagerUpdate-{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "LANWebTerminalManager.zip");

            using (var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(archivePath);
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    readTotal += read;
                    if (total > 0)
                    {
                        DownloadProgress = Math.Clamp((double)readTotal / total, 0, 1);
                    }
                }
            }

            DownloadProgress = 1;
            IsDownloading = false;
            IsInstalling = true;
            StatusText = "正在安装更新...";

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(archivePath, extractDir, true);

            var newExe = Directory.EnumerateFiles(extractDir, "LANWebTerminalManager.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (newExe is null)
            {
                throw new InvalidOperationException("更新包里没有找到 LANWebTerminalManager.exe。");
            }

            var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe is null)
            {
                throw new InvalidOperationException("无法确定当前程序路径。");
            }

            var scriptPath = Path.Combine(tempDir, "install-update.ps1");
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $currentExe = '{{currentExe.Replace("'", "''")}}'
                $newExe = '{{newExe.Replace("'", "''")}}'
                $pid = {{Environment.ProcessId}}

                while (Get-Process -Id $pid -ErrorAction SilentlyContinue) {
                    Start-Sleep -Milliseconds 200
                }

                Copy-Item -Path $newExe -Destination $currentExe -Force
                Start-Process -FilePath $currentExe
                Remove-Item -Path '{{tempDir.Replace("'", "''")}}' -Recurse -Force -ErrorAction SilentlyContinue
                """;
            await File.WriteAllTextAsync(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            UpdateReadyToInstall?.Invoke();
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            IsInstalling = false;
            DownloadProgress = null;
            StatusText = "更新失败";
            ShowFailure(ex, UpdateAction.Install);
        }
    }

    private async Task<GitHubRelease> FetchLatestReleaseAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var response = await _http.GetAsync("https://api.github.com/repos/RyanJC0416/LANWebTerminalManager/releases/latest");
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<GitHubRelease>(json) ?? throw new InvalidOperationException("无法读取最新 release。");
            }
            catch when (attempt == 0)
            {
                await Task.Delay(700);
            }
        }

        throw new InvalidOperationException("无法读取最新 release。");
    }

    private static bool IsWindowsAsset(GitHubReleaseAsset asset)
    {
        var name = asset.Name.ToLowerInvariant();
        return name.Contains("-windows") && name.EndsWith(".zip");
    }

    private static bool IsVersion(string lhs, string rhs)
    {
        var left = lhs.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        var right = rhs.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        var count = Math.Max(left.Length, right.Length);
        for (var index = 0; index < count; index++)
        {
            var leftPart = index < left.Length ? left[index] : 0;
            var rightPart = index < right.Length ? right[index] : 0;
            if (leftPart != rightPart) return leftPart > rightPart;
        }

        return false;
    }

    private void ShowFailure(Exception error, UpdateAction action)
    {
        _retryAction = action;
        CanRetry = true;
        Message = $"更新失败，请重试或手动下载更新。\n\n{error.Message}";
        IsPresentingMessage = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private enum UpdateAction
    {
        Check,
        Install
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
