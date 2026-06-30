using System.Windows;
using System.Windows.Controls;
using LANWebTerminalManager.Models;
using LANWebTerminalManager.Services;
using Microsoft.Win32;

namespace LANWebTerminalManager;

public partial class SettingsWindow : Window
{
    private readonly AppState _state;

    public SettingsWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        LoadForm();
    }

    private void LoadForm()
    {
        ReceiverRootInput.Text = _state.ReceiverSettings.RootPath;
        ReceiverPortInput.Text = _state.ReceiverSettings.Port.ToString();
        ReceiverTokenInput.Text = _state.ReceiverSettings.Token;

        ReceiverUrlList.Children.Clear();
        ReceiverUrlList.Children.Add(CreateUrlText(_state.ReceiverLocalUrl));
        foreach (var url in _state.ReceiverLanUrls)
        {
            ReceiverUrlList.Children.Add(CreateUrlText(url));
        }

        RenderTargets();
    }

    private static TextBlock CreateUrlText(string url) =>
        new() { Text = url, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 4) };

    private void RenderTargets()
    {
        TargetsList.Items.Clear();
        foreach (var target in _state.Targets)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Tag = target
            };

            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = new TextBox { Text = target.Name, Margin = new Thickness(0, 0, 8, 0) };
            nameBox.TextChanged += (_, _) => target.Name = nameBox.Text;
            Grid.SetColumn(nameBox, 0);

            var probeButton = new Button { Content = "测试", Margin = new Thickness(0, 0, 8, 0) };
            probeButton.Click += (_, _) => _state.ProbeTarget(target);
            Grid.SetColumn(probeButton, 1);

            var deleteButton = new Button { Content = "删除" };
            deleteButton.Click += (_, _) =>
            {
                _state.RemoveTarget(target);
                RenderTargets();
            };
            Grid.SetColumn(deleteButton, 2);

            header.Children.Add(nameBox);
            header.Children.Add(probeButton);
            header.Children.Add(deleteButton);

            var urlBox = new TextBox
            {
                Text = target.ServerURL,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "LWM 客户端地址，例如 http://192.168.1.20:4177"
            };
            urlBox.TextChanged += (_, _) => target.ServerURL = urlBox.Text;

            var tokenBox = new TextBox
            {
                Text = target.Token,
                ToolTip = "接收方设置中的字符串，例如 office-server"
            };
            tokenBox.TextChanged += (_, _) => target.Token = tokenBox.Text;

            panel.Children.Add(header);
            panel.Children.Add(urlBox);
            panel.Children.Add(tokenBox);

            if (!string.IsNullOrWhiteSpace(target.Platform))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"平台：{target.Platform}",
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                    FontSize = 12,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            var border = new Border
            {
                Padding = new Thickness(12),
                Background = (System.Windows.Media.Brush)FindResource("ControlBgBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = panel
            };

            TargetsList.Items.Add(border);
        }
    }

    private void ChooseReceiverRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择接收方文件根目录" };
        if (dialog.ShowDialog() == true)
        {
            ReceiverRootInput.Text = dialog.FolderName;
        }
    }

    private void ResetToken_Click(object sender, RoutedEventArgs e) => ReceiverTokenInput.Text = "lwm-server";

    private void SaveReceiver_Click(object sender, RoutedEventArgs e)
    {
        ApplyReceiverForm();
        _state.SaveReceiverSettings();
    }

    private void SaveTargets_Click(object sender, RoutedEventArgs e) => _state.SaveTargets();

    private void AddTarget_Click(object sender, RoutedEventArgs e)
    {
        _state.AddTarget();
        RenderTargets();
    }

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        ApplyReceiverForm();
        _state.SaveReceiverSettings();
        _state.SaveTargets();
        Close();
    }

    private void ApplyReceiverForm()
    {
        _state.ReceiverSettings.RootPath = ReceiverRootInput.Text.Trim();
        if (int.TryParse(ReceiverPortInput.Text, out var port))
        {
            _state.ReceiverSettings.Port = Math.Clamp(port, 1024, 65535);
        }

        _state.ReceiverSettings.Token = ReceiverTokenInput.Text.Trim();
    }
}
