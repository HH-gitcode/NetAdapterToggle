using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetAdapterToggle.Models;

public enum AdapterKind
{
    Ethernet,
    WiFi,
    Bluetooth,
    Virtual,
    Other
}

public sealed class AdapterInfo : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _isConnected;
    private bool _isPinned;
    private bool _isBusy;
    private string _ipAddresses = "-";
    private string _linkSpeedText = "-";

    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public int InterfaceIndex { get; init; }
    public string MacAddress { get; init; } = "-";
    public bool IsPhysical { get; init; }
    public AdapterKind Kind { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActionButtonText));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string IpAddresses
    {
        get => _ipAddresses;
        set
        {
            if (_ipAddresses == value) return;
            _ipAddresses = value;
            OnPropertyChanged();
        }
    }

    public string LinkSpeedText
    {
        get => _linkSpeedText;
        set
        {
            if (_linkSpeedText == value) return;
            _linkSpeedText = value;
            OnPropertyChanged();
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionButtonText));
        }
    }

    public string StatusText => !IsEnabled
        ? "已禁用"
        : (IsConnected ? "已连接" : "已启用 (未连接)");

    public string KindLabel => Kind switch
    {
        AdapterKind.WiFi => "无线 Wi-Fi",
        AdapterKind.Ethernet => "有线以太网",
        AdapterKind.Bluetooth => "蓝牙网络",
        AdapterKind.Virtual => "虚拟网卡",
        _ => IsPhysical ? "物理网卡" : "虚拟网卡"
    };

    public string KindGlyph => Kind switch
    {
        AdapterKind.WiFi => "\uE701",       // Segoe Fluent Icons: Wifi
        AdapterKind.Ethernet => "\uE839",   // Ethernet
        AdapterKind.Bluetooth => "\uE702",  // Bluetooth
        AdapterKind.Virtual => "\uE968",    // Network / VPN / Virtual
        _ => "\uE774"                       // Globe
    };

    public string ActionButtonText => IsBusy
        ? "处理中..."
        : (IsEnabled ? "禁用" : "启用");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
