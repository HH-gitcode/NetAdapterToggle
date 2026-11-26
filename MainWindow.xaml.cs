using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetAdapterToggle.Models;
using NetAdapterToggle.Services;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace NetAdapterToggle;

public partial class MainWindow : Window
{
    private readonly NetworkAdapterService _adapterService = new();
    private readonly SettingsService _settingsService = new();
    private AppSettings _settings;
    private List<AdapterInfo> _allAdapters = [];
    private Forms.NotifyIcon? _notifyIcon;
    private bool _isExiting;
    private bool _isRefreshing;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;

        InitializeAdminBadge();
        InitializeTrayIcon();

        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += async (_, _) => await RefreshAdaptersAsync();
        if (startMinimized)
        {
            _ = Dispatcher.InvokeAsync(async () => await RefreshAdaptersAsync());
        }
    }

    private void InitializeAdminBadge()
    {
        bool isAdmin = NetworkAdapterService.IsAdministrator();
        if (isAdmin)
        {
            AdminBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 252, 231));
            AdminBadgeIcon.Text = "\uE73E";
            AdminBadgeIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61));
            AdminBadgeText.Text = "已获管理员权限";
            AdminBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 128, 61));
            ElevateButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBadge.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 243, 199));
            AdminBadgeIcon.Text = "\uE7BA";
            AdminBadgeIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9));
            AdminBadgeText.Text = "非管理员模式";
            AdminBadgeText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9));
            ElevateButton.Visibility = Visibility.Visible;
        }
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = App.CreateAppIcon(),
            Text = "网络适配器一键开关",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_notifyIcon is null) return;

        var menu = new Forms.ContextMenuStrip();

        var quickAdapters = _allAdapters
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.IsPhysical)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (quickAdapters.Count > 0)
        {
            foreach (var adapter in quickAdapters)
            {
                string stateIcon = adapter.IsEnabled ? (adapter.IsConnected ? "● " : "○ ") : "✕ ";
                string actionHint = adapter.IsEnabled ? "点击禁用" : "点击启用";
                var item = new Forms.ToolStripMenuItem($"{stateIcon}{adapter.Name} ({adapter.StatusText}) - {actionHint}");
                item.Click += async (_, _) => await ToggleAdapterAsync(adapter);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
        }

        var openItem = new Forms.ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(openItem);

        var refreshItem = new Forms.ToolStripMenuItem("刷新适配器状态");
        refreshItem.Click += async (_, _) => await RefreshAdaptersAsync();
        menu.Items.Add(refreshItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出程序");
        exitItem.Click += (_, _) =>
        {
            _isExiting = true;
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            System.Windows.Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExiting && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(1500, "网络适配器一键开关", "程序已最小化到系统托盘，右键托盘图标可一键切换网卡。", Forms.ToolTipIcon.Info);
            return;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        base.OnClosing(e);
        System.Windows.Application.Current.Shutdown();
    }

    private async void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            await RefreshAdaptersAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private async Task RefreshAdaptersAsync(string? overrideStatusMessage = null)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        RefreshButton.IsEnabled = false;

        try
        {
            var fetched = await _adapterService.GetAdaptersAsync();
            var pinnedSet = new HashSet<string>(_settings.PinnedAdapters, StringComparer.OrdinalIgnoreCase);

            foreach (var adapter in fetched)
            {
                adapter.IsPinned = pinnedSet.Contains(adapter.Name) ||
                                   (!string.IsNullOrEmpty(adapter.DeviceId) && pinnedSet.Contains(adapter.DeviceId));
            }

            _allAdapters = fetched
                .OrderByDescending(a => a.IsPinned)
                .ThenByDescending(a => a.IsPhysical)
                .ThenByDescending(a => a.IsConnected)
                .ThenByDescending(a => a.IsEnabled)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyFilter();
            RebuildTrayMenu();

            if (!string.IsNullOrWhiteSpace(overrideStatusMessage))
            {
                StatusBarText.Text = overrideStatusMessage;
            }
            else
            {
                int connected = _allAdapters.Count(a => a.IsConnected);
                int enabled = _allAdapters.Count(a => a.IsEnabled);
                int disabled = _allAdapters.Count - enabled;
                StatusBarText.Text = $"共 {_allAdapters.Count} 个网络适配器 · 已连接 {connected} · 已启用 {enabled} · 已禁用 {disabled}";
            }
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"刷新失败: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    private List<AdapterInfo> GetFilteredAdapters()
    {
        string keyword = SearchBox.Text.Trim();
        IEnumerable<AdapterInfo> query = _allAdapters;

        if (FilterPhysical.IsChecked == true)
            query = query.Where(a => a.IsPhysical);
        else if (FilterEnabled.IsChecked == true)
            query = query.Where(a => a.IsEnabled);
        else if (FilterDisabled.IsChecked == true)
            query = query.Where(a => !a.IsEnabled);

        if (!string.IsNullOrEmpty(keyword))
        {
            query = query.Where(a =>
                a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                a.IpAddresses.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                a.MacAddress.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToList();
    }

    private void ApplyFilter()
    {
        if (_allAdapters.Count == 0 && !_isRefreshing)
        {
            AdaptersItemsControl.ItemsSource = null;
            EmptyStateText.Text = "未检测到网络适配器，请尝试以管理员身份刷新。";
            EmptyStatePanel.Visibility = Visibility.Visible;
            return;
        }

        var filtered = GetFilteredAdapters();
        AdaptersItemsControl.ItemsSource = filtered;
        EmptyStateText.Text = "没有找到符合筛选条件的网络适配器";
        EmptyStatePanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ToggleAdapterAsync(AdapterInfo adapter)
    {
        if (adapter.IsBusy) return;
        adapter.IsBusy = true;
        bool targetEnable = !adapter.IsEnabled;
        StatusBarText.Text = targetEnable ? $"正在启用「{adapter.Name}」..." : $"正在禁用「{adapter.Name}」...";

        try
        {
            var result = await _adapterService.SetAdapterEnabledAsync(adapter, targetEnable);
            await RefreshAdaptersAsync(result.Message);
            if (!result.Success)
            {
                MessageBox.Show(this, result.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            adapter.IsBusy = false;
        }
    }

    private async void ToggleAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AdapterInfo adapter })
        {
            await ToggleAdapterAsync(adapter);
        }
    }

    private async void RestartAdapterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AdapterInfo adapter } || adapter.IsBusy)
            return;

        adapter.IsBusy = true;
        StatusBarText.Text = $"正在重启「{adapter.Name}」...";

        try
        {
            var result = await _adapterService.RestartAdapterAsync(adapter);
            await RefreshAdaptersAsync(result.Success ? $"已完成重启「{adapter.Name}」" : result.Message);
            if (!result.Success)
            {
                MessageBox.Show(this, result.Message, "重启失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            adapter.IsBusy = false;
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AdapterInfo adapter })
            return;

        adapter.IsPinned = !adapter.IsPinned;
        _settings.PinnedAdapters.RemoveAll(p =>
            string.Equals(p, adapter.Name, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(adapter.DeviceId) && string.Equals(p, adapter.DeviceId, StringComparison.OrdinalIgnoreCase)));

        if (adapter.IsPinned)
        {
            _settings.PinnedAdapters.Add(adapter.Name);
        }

        _settingsService.Save(_settings);

        _allAdapters = _allAdapters
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.IsPhysical)
            .ThenByDescending(a => a.IsConnected)
            .ThenByDescending(a => a.IsEnabled)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
        RebuildTrayMenu();
        StatusBarText.Text = adapter.IsPinned ? $"已置顶「{adapter.Name}」" : $"已取消置顶「{adapter.Name}」";
    }

    private void CreateShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AdapterInfo adapter })
            return;

        try
        {
            string lnkPath = ShortcutService.CreateDesktopToggleShortcut(adapter.Name);
            StatusBarText.Text = $"已在桌面创建快捷方式: {System.IO.Path.GetFileName(lnkPath)}";
            MessageBox.Show(this,
                $"已在桌面创建一键切换快捷方式：\n{lnkPath}\n\n以后双击该桌面图标即可一键启用或禁用「{adapter.Name}」。",
                "创建成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"创建桌面快捷方式失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EnableAllButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetFilteredAdapters().Where(a => !a.IsEnabled).ToList();
        if (targets.Count == 0)
        {
            StatusBarText.Text = "当前列表中没有需要启用的网络适配器。";
            return;
        }

        EnableAllButton.IsEnabled = false;
        DisableAllButton.IsEnabled = false;
        int ok = 0;

        try
        {
            foreach (var adapter in targets)
            {
                adapter.IsBusy = true;
                StatusBarText.Text = $"正在启用「{adapter.Name}」({ok + 1}/{targets.Count})...";
                var res = await _adapterService.SetAdapterEnabledAsync(adapter, true);
                if (res.Success) ok++;
                adapter.IsBusy = false;
            }
            await RefreshAdaptersAsync($"批量启用完成：成功 {ok}/{targets.Count} 个");
        }
        finally
        {
            EnableAllButton.IsEnabled = true;
            DisableAllButton.IsEnabled = true;
        }
    }

    private async void DisableAllButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = GetFilteredAdapters().Where(a => a.IsEnabled).ToList();
        if (targets.Count == 0)
        {
            StatusBarText.Text = "当前列表中没有已启用的网络适配器。";
            return;
        }

        var confirm = MessageBox.Show(this,
            $"确定要一键禁用当前列表中的 {targets.Count} 个已启用网络适配器吗？\n这将断开这些适配器对应的网络连接。",
            "确认批量禁用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        EnableAllButton.IsEnabled = false;
        DisableAllButton.IsEnabled = false;
        int ok = 0;

        try
        {
            foreach (var adapter in targets)
            {
                adapter.IsBusy = true;
                StatusBarText.Text = $"正在禁用「{adapter.Name}」({ok + 1}/{targets.Count})...";
                var res = await _adapterService.SetAdapterEnabledAsync(adapter, false);
                if (res.Success) ok++;
                adapter.IsBusy = false;
            }
            await RefreshAdaptersAsync($"批量禁用完成：成功 {ok}/{targets.Count} 个");
        }
        finally
        {
            EnableAllButton.IsEnabled = true;
            DisableAllButton.IsEnabled = true;
        }
    }

    private void OpenNcpaButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开网络连接面板: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAdaptersAsync();
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NetworkAdapterService.RestartAsAdministrator();
            _isExiting = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"未能以管理员身份重启：{ex.Message}", "提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void MinimizeToTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }
}
