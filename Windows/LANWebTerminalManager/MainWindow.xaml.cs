using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LANWebTerminalManager.Models;
using LANWebTerminalManager.Services;
using Microsoft.Win32;

namespace LANWebTerminalManager;

public partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly UpdateManager _updateManager = new();
    private bool _suppressFieldChanges;

    public MainWindow()
    {
        InitializeComponent();
        _state = new AppState();
        VersionText.Text = $"当前版本 {UpdateManager.CurrentVersion}";
        EndpointList.ItemsSource = _state.Endpoints;
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(AppState.Activity))
            {
                ActivityText.Text = _state.Activity;
            }
            else if (args.PropertyName is nameof(AppState.IsBusy))
            {
                BusyIndicator.Visibility = _state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                RefreshActionButtons();
            }
            else if (args.PropertyName is nameof(AppState.SelectedEndpoint) or nameof(AppState.SelectedStatus))
            {
                RenderDetail();
                RefreshEndpointListVisuals();
            }
            else if (args.PropertyName is nameof(AppState.TerminalOutput))
            {
                TerminalOutputText.Text = _state.TerminalOutput;
            }
        };

        _updateManager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(UpdateManager.StatusText))
            {
                UpdateStatusText.Text = _updateManager.StatusText ?? "";
            }
            else if (args.PropertyName is nameof(UpdateManager.DownloadProgress))
            {
                if (_updateManager.DownloadProgress is double progress)
                {
                    UpdateProgress.Visibility = Visibility.Visible;
                    UpdateProgress.IsIndeterminate = false;
                    UpdateProgress.Value = progress * 100;
                }
                else
                {
                    UpdateProgress.Visibility = Visibility.Collapsed;
                }
            }
            else if (args.PropertyName is nameof(UpdateManager.PrimaryButtonTitle))
            {
                UpdateButton.Content = _updateManager.PrimaryButtonTitle;
            }
            else if (args.PropertyName is nameof(UpdateManager.IsBusy))
            {
                UpdateButton.IsEnabled = !_updateManager.IsBusy;
            }
            else if (args.PropertyName is nameof(UpdateManager.IsPresentingMessage) && _updateManager.IsPresentingMessage)
            {
                ShowUpdateMessage();
            }
        };

        _state.StatusesChanged += () => Dispatcher.Invoke(() =>
        {
            RefreshEndpointListVisuals();
            RenderDetail();
        });
        _updateManager.UpdateReadyToInstall += () => System.Windows.Application.Current.Shutdown();
        ActivityText.Text = _state.Activity;
        RefreshTargetPicker();
        RenderDetail();
        RefreshEndpointListVisuals();
    }

    private sealed class TargetPickerItem
    {
        public required string Label { get; init; }
        public Guid? TargetId { get; init; }
        public override string ToString() => Label;
    }

    private void RefreshTargetPicker()
    {
        var items = new List<TargetPickerItem> { new() { Label = "local", TargetId = null } };
        items.AddRange(_state.Targets.Select(target => new TargetPickerItem { Label = target.Name, TargetId = target.Id }));

        _suppressFieldChanges = true;
        TargetPicker.ItemsSource = items;
        TargetPicker.SelectedItem = items.FirstOrDefault(item => item.TargetId == _state.SelectedTargetId)
            ?? items[0];
        _suppressFieldChanges = false;
    }

    private void TargetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFieldChanges || TargetPicker.SelectedItem is not TargetPickerItem item) return;
        if (item.TargetId is null) _state.SelectLocalTarget();
        else if (_state.Targets.FirstOrDefault(target => target.Id == item.TargetId) is { } target) _state.SelectTarget(target);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_state) { Owner = this };
        window.ShowDialog();
        RefreshTargetPicker();
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择网页根目录",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _state.AddEndpoint(dialog.FolderName);
            EndpointList.SelectedItem = _state.SelectedEndpoint;
            RefreshEndpointListVisuals();
            RenderDetail();
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_state.SelectedEndpoint is null) return;
        var result = MessageBox.Show(
            $"删除 {_state.SelectedEndpoint.Name}？",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _state.RemoveSelected();
        RefreshEndpointListVisuals();
        RenderDetail();
    }

    private void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointList.SelectedItem is WebEndpoint endpoint)
        {
            _state.Selection = endpoint.Id;
        }

        RenderDetail();
    }

    private void EndpointField_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFieldChanges || _state.SelectedEndpoint is not { } endpoint) return;

        endpoint.Name = NameInput.Text;
        endpoint.Port = int.TryParse(PortInput.Text, out var port) ? Math.Clamp(port, 1024, 65535) : endpoint.Port;
        endpoint.Host = HostInput.SelectedItem is ComboBoxItem item && item.Tag is string host ? host : endpoint.Host;
        endpoint.AutoOpen = AutoOpenInput.IsChecked == true;
        _state.SaveEndpoint(endpoint);
        RefreshEndpointListVisuals();
    }

    private void StartSelected_Click(object sender, RoutedEventArgs e) => _state.StartSelected();

    private void StopSelected_Click(object sender, RoutedEventArgs e) => _state.StopSelected();

    private void RefreshAssets_Click(object sender, RoutedEventArgs e) => _state.RefreshSelectedAssets();

    private void OpenUrl_Click(object sender, RoutedEventArgs e) => _state.OpenSelectedUrl();

    private void OpenLocalUrl_Click(object sender, RoutedEventArgs e) => _state.OpenSelectedLocalUrl();

    private void CopyUrls_Click(object sender, RoutedEventArgs e) => _state.CopyUrls();

    private void RevealFolder_Click(object sender, RoutedEventArgs e) => _state.RevealSelectedFolder();

    private void ChooseHomepage_Click(object sender, RoutedEventArgs e)
    {
        if (_state.SelectedEndpoint is not { } endpoint) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择主页",
            InitialDirectory = endpoint.RootPath,
            Filter = "网页文件|*.html;*.htm;*.md|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            _state.ChooseHomepage(dialog.FileName);
            RenderDetail();
        }
    }

    private void RunCommand_Click(object sender, RoutedEventArgs e) => _state.RunCommand();

    private void CommandInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _state.Command = CommandInput.Text;
            _state.RunCommand();
            CommandInput.Clear();
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await _updateManager.PerformPrimaryUpdateActionAsync();
    }

    private void ShowUpdateMessage()
    {
        if (_updateManager.CanRetry)
        {
            var result = MessageBox.Show(
                _updateManager.Message + "\n\n是否重试？",
                "应用更新",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _ = _updateManager.RetryLastFailedActionAsync();
            }
            else if (result == MessageBoxResult.Cancel)
            {
                _updateManager.OpenReleasePage();
            }
        }
        else
        {
            MessageBox.Show(_updateManager.Message, "应用更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _updateManager.IsPresentingMessage = false;
    }

    private void RenderDetail()
    {
        var endpoint = _state.SelectedEndpoint;
        var hasSelection = endpoint is not null;
        EmptyState.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSelection) return;

        var status = _state.SelectedStatus;
        _suppressFieldChanges = true;

        DetailTitle.Text = endpoint!.Name;
        DetailRootPath.Text = endpoint.RootPath;
        RootPathText.Text = endpoint.RootPath;
        NameInput.Text = endpoint.Name;
        PortInput.Text = endpoint.Port.ToString();
        HostInput.SelectedIndex = endpoint.Host == "127.0.0.1" ? 1 : 0;
        UrlPathText.Text = endpoint.UrlPath;
        AutoOpenInput.IsChecked = endpoint.AutoOpen;

        StatusValue.Text = status.Running ? "运行中" : "已停止";
        PageCountValue.Text = status.PageCount.ToString();
        PidValue.Text = status.Pids.Count == 0 ? "--" : string.Join(", ", status.Pids);
        IndexMtimeValue.Text = status.IndexMtime;
        LogOutputText.Text = status.LogTail;
        TerminalOutputText.Text = _state.TerminalOutput;

        UrlList.Children.Clear();
        if (status.Running && status.Urls.Count > 0)
        {
            foreach (var url in status.Urls)
            {
                UrlList.Children.Add(new TextBlock
                {
                    Text = url,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
        }
        else
        {
            UrlList.Children.Add(new TextBlock
            {
                Text = status.Running ? "没有检测到可用的局域网地址。" : "服务停止后地址不可访问，启动后可复制或打开。",
                Foreground = (Brush)FindResource("MutedBrush")
            });
        }

        RefreshActionButtons(status);
        _suppressFieldChanges = false;
    }

    private void RefreshActionButtons(EndpointStatus? status = null)
    {
        status ??= _state.SelectedStatus;
        StartButton.IsEnabled = !status.Running && !_state.IsBusy;
        StopButton.IsEnabled = status.Running && !_state.IsBusy;
        RefreshAssetsButton.IsEnabled = !_state.IsBusy;
        OpenButton.IsEnabled = status.Running;
        CopyUrlsButton.IsEnabled = status.Running && status.Urls.Count > 0;
        OpenLocalButton.IsEnabled = status.Running;
    }

      private void RefreshEndpointListVisuals()
    {
        EndpointList.Items.Refresh();
        if (_state.SelectedEndpoint is not null)
        {
            EndpointList.SelectedItem = _state.SelectedEndpoint;
        }

        UpdateEndpointStatusDots();
    }

    private void UpdateEndpointStatusDots()
    {
        for (var index = 0; index < EndpointList.Items.Count; index++)
        {
            if (EndpointList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container) continue;
            if (container.Content is not WebEndpoint endpoint) continue;
            if (FindVisualChild<System.Windows.Shapes.Ellipse>(container) is not { } dot) continue;
            var running = _state.Statuses.TryGetValue(endpoint.Id, out var status) && status.Running;
            dot.Fill = running
                ? (Brush)FindResource("OkBrush")
                : new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xB9));
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
