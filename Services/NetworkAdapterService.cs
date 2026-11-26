using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using NetAdapterToggle.Models;

namespace NetAdapterToggle.Services;

public sealed record OperationResult(bool Success, string Message);

public sealed class NetworkAdapterService
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestartAsAdministrator(string? extraArgs = null)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = extraArgs ?? string.Empty,
            UseShellExecute = true,
            Verb = "runas"
        };

        Process.Start(startInfo);
    }

    public Task<List<AdapterInfo>> GetAdaptersAsync()
    {
        return Task.Run(GetAdaptersInternal);
    }

    public async Task<OperationResult> SetAdapterEnabledAsync(AdapterInfo adapter, bool enable)
    {
        if (!IsAdministrator())
        {
            return new OperationResult(false, "权限不足：请以管理员身份运行此程序后再试。");
        }

        var result = await Task.Run(() => TrySetStateViaMsftNetAdapter(adapter, enable));
        if (!result.Success)
        {
            result = await Task.Run(() => TrySetStateViaWin32NetAdapter(adapter, enable));
        }
        if (!result.Success)
        {
            result = await Task.Run(() => TrySetStateViaNetsh(adapter.Name, enable));
        }
        if (!result.Success)
        {
            result = await Task.Run(() => TrySetStateViaPowerShell(adapter.Name, enable));
        }

        if (!result.Success)
        {
            return result;
        }

        // Wait briefly for Windows network stack to settle and confirm state
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(250);
            var latest = await GetAdaptersAsync();
            var match = latest.FirstOrDefault(a =>
                (!string.IsNullOrEmpty(adapter.DeviceId) && string.Equals(a.DeviceId, adapter.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(a.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));

            if (match is not null && match.IsEnabled == enable)
            {
                return new OperationResult(true, enable ? $"已启用「{adapter.Name}」" : $"已禁用「{adapter.Name}」");
            }
        }

        return new OperationResult(true, enable ? $"已发送启用「{adapter.Name}」指令" : $"已发送禁用「{adapter.Name}」指令");
    }

    public async Task<OperationResult> RestartAdapterAsync(AdapterInfo adapter)
    {
        if (!IsAdministrator())
        {
            return new OperationResult(false, "权限不足：请以管理员身份运行此程序后再试。");
        }

        var cimRestart = await Task.Run(() => TryRestartViaMsftNetAdapter(adapter));
        if (cimRestart.Success)
        {
            await Task.Delay(1200);
            return new OperationResult(true, $"已完成重启「{adapter.Name}」");
        }

        var disableResult = await SetAdapterEnabledAsync(adapter, false);
        if (!disableResult.Success)
        {
            return disableResult;
        }

        await Task.Delay(800);
        return await SetAdapterEnabledAsync(adapter, true);
    }

    private List<AdapterInfo> GetAdaptersInternal()
    {
        var netInterfaces = SafeGetDotNetInterfaces();
        var byGuid = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in netInterfaces)
        {
            if (!string.IsNullOrWhiteSpace(ni.Id))
                byGuid[ni.Id.Trim()] = ni;
            if (!string.IsNullOrWhiteSpace(ni.Name))
                byName[ni.Name.Trim()] = ni;
        }

        var list = TryQueryMsftNetAdapters(byGuid, byName);
        if (list is { Count: > 0 })
        {
            return list;
        }

        return QueryWin32NetAdapters(byGuid, byName);
    }

    private static NetworkInterface[] SafeGetDotNetInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return [];
        }
    }

    private static List<AdapterInfo>? TryQueryMsftNetAdapters(
        Dictionary<string, NetworkInterface> byGuid,
        Dictionary<string, NetworkInterface> byName)
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return null;

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic services = locator.ConnectServer(".", @"root\StandardCimv2");
            dynamic results = services.ExecQuery("SELECT * FROM MSFT_NetAdapter");

            var adapters = new List<AdapterInfo>();
            foreach (dynamic obj in results)
            {
                bool hidden = GetBool(obj, "Hidden", false);
                if (hidden) continue;

                string name = GetString(obj, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                string desc = GetString(obj, "InterfaceDescription");
                string deviceId = GetString(obj, "InterfaceGuid");
                if (string.IsNullOrWhiteSpace(deviceId))
                    deviceId = GetString(obj, "DeviceID");

                int ifIndex = GetInt(obj, "InterfaceIndex", 0);
                string rawMac = GetFirstStringFromArray(obj, "NetworkAddresses");
                if (string.IsNullOrWhiteSpace(rawMac))
                    rawMac = GetString(obj, "PermanentAddress");
                string mac = FormatMac(rawMac);

                // Raw CIM properties on MSFT_NetAdapter are InterfaceAdminStatus (1=Up, 2=Down)
                // and InterfaceOperationalStatus (1=Up, 2=Down)
                int adminStatus = GetInt(obj, "InterfaceAdminStatus", 1);
                int opStatus = GetInt(obj, "InterfaceOperationalStatus", 2);
                int ndisMedium = GetInt(obj, "NdisMedium", 0);
                bool isHardware = GetBool(obj, "HardwareInterface", false);
                bool isVirtual = GetBool(obj, "Virtual", false);
                int physicalMedium = GetInt(obj, "NdisPhysicalMedium", 0);
                ulong speedBps = GetULong(obj, "TransmitLinkSpeed", 0);
                if (speedBps == 0) speedBps = GetULong(obj, "ReceiveLinkSpeed", 0);

                bool isEnabled = adminStatus == 1;
                bool isConnected = isEnabled && opStatus == 1;

                NetworkInterface? ni = FindMatchingInterface(deviceId, name, byGuid, byName);
                if (mac == "-" && ni is not null)
                {
                    var niMac = ni.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrWhiteSpace(niMac))
                        mac = FormatMac(niMac);
                }

                string ipText = isEnabled ? ExtractIpAddresses(ni) : "-";
                string speedText = isEnabled ? FormatSpeed(speedBps, ni?.Speed ?? 0) : "-";

                var kind = ClassifyKind(name, desc, physicalMedium, ndisMedium, isHardware, isVirtual, ni);

                adapters.Add(new AdapterInfo
                {
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(desc) ? name : desc,
                    DeviceId = deviceId,
                    InterfaceIndex = ifIndex,
                    MacAddress = mac,
                    IsPhysical = !isVirtual && (isHardware || kind == AdapterKind.Bluetooth),
                    Kind = kind,
                    IsEnabled = isEnabled,
                    IsConnected = isConnected,
                    IpAddresses = ipText,
                    LinkSpeedText = speedText
                });
            }

            return adapters;
        }
        catch
        {
            return null;
        }
    }

    private static List<AdapterInfo> QueryWin32NetAdapters(
        Dictionary<string, NetworkInterface> byGuid,
        Dictionary<string, NetworkInterface> byName)
    {
        var adapters = new List<AdapterInfo>();
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return adapters;

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic services = locator.ConnectServer(".", @"root\cimv2");
            dynamic results = services.ExecQuery("SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");

            foreach (dynamic obj in results)
            {
                string name = GetString(obj, "NetConnectionID");
                if (string.IsNullOrWhiteSpace(name)) continue;

                string desc = GetString(obj, "Name");
                string deviceId = GetString(obj, "GUID");
                int ifIndex = GetInt(obj, "InterfaceIndex", 0);
                string mac = FormatMac(GetString(obj, "MACAddress"));
                bool isPhysical = GetBool(obj, "PhysicalAdapter", false);
                bool? netEnabled = GetNullableBool(obj, "NetEnabled");
                int errCode = GetInt(obj, "ConfigManagerErrorCode", 0);
                int connStatus = GetInt(obj, "NetConnectionStatus", 0); // 2 = Connected
                ulong speedBps = GetULong(obj, "Speed", 0);

                bool isEnabled = netEnabled ?? (errCode != 22);
                bool isConnected = isEnabled && connStatus == 2;

                NetworkInterface? ni = FindMatchingInterface(deviceId, name, byGuid, byName);
                string ipText = isEnabled ? ExtractIpAddresses(ni) : "-";
                string speedText = isEnabled ? FormatSpeed(speedBps, ni?.Speed ?? 0) : "-";
                var kind = ClassifyKind(name, desc, 0, 0, isPhysical, !isPhysical, ni);

                adapters.Add(new AdapterInfo
                {
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(desc) ? name : desc,
                    DeviceId = deviceId,
                    InterfaceIndex = ifIndex,
                    MacAddress = mac,
                    IsPhysical = isPhysical,
                    Kind = kind,
                    IsEnabled = isEnabled,
                    IsConnected = isConnected,
                    IpAddresses = ipText,
                    LinkSpeedText = speedText
                });
            }
        }
        catch
        {
            // Ignore and return whatever was collected
        }

        return adapters;
    }

    private static OperationResult TrySetStateViaMsftNetAdapter(AdapterInfo adapter, bool enable)
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
                return new OperationResult(false, "WMI COM 组件不可用");

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic services = locator.ConnectServer(".", @"root\StandardCimv2");

            string query = !string.IsNullOrWhiteSpace(adapter.DeviceId)
                ? $"SELECT * FROM MSFT_NetAdapter WHERE InterfaceGuid='{EscapeWql(adapter.DeviceId)}' OR DeviceID='{EscapeWql(adapter.DeviceId)}'"
                : $"SELECT * FROM MSFT_NetAdapter WHERE Name='{EscapeWql(adapter.Name)}'";

            dynamic results = services.ExecQuery(query);
            foreach (dynamic obj in results)
            {
                string methodName = enable ? "Enable" : "Disable";
                dynamic outParams = obj.ExecMethod_(methodName);
                int returnVal = GetInt(outParams, "ReturnValue", 0);
                if (returnVal == 0)
                {
                    return new OperationResult(true, "OK");
                }
                return new OperationResult(false, $"MSFT_NetAdapter.{methodName} 返回错误码 {returnVal}");
            }

            return new OperationResult(false, "未找到对应的 MSFT_NetAdapter 实例");
        }
        catch (COMException ex)
        {
            return new OperationResult(false, $"WMI CIM 调用失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"CIM 调用失败: {ex.Message}");
        }
    }

    private static OperationResult TryRestartViaMsftNetAdapter(AdapterInfo adapter)
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
                return new OperationResult(false, "WMI COM 组件不可用");

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic services = locator.ConnectServer(".", @"root\StandardCimv2");

            string query = !string.IsNullOrWhiteSpace(adapter.DeviceId)
                ? $"SELECT * FROM MSFT_NetAdapter WHERE InterfaceGuid='{EscapeWql(adapter.DeviceId)}' OR DeviceID='{EscapeWql(adapter.DeviceId)}'"
                : $"SELECT * FROM MSFT_NetAdapter WHERE Name='{EscapeWql(adapter.Name)}'";

            dynamic results = services.ExecQuery(query);
            foreach (dynamic obj in results)
            {
                dynamic outParams = obj.ExecMethod_("Restart");
                int returnVal = GetInt(outParams, "ReturnValue", 0);
                return returnVal == 0
                    ? new OperationResult(true, "OK")
                    : new OperationResult(false, $"MSFT_NetAdapter.Restart 返回错误码 {returnVal}");
            }

            return new OperationResult(false, "未找到对应的 MSFT_NetAdapter 实例");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, ex.Message);
        }
    }

    private static OperationResult TrySetStateViaWin32NetAdapter(AdapterInfo adapter, bool enable)
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
                return new OperationResult(false, "WMI COM 组件不可用");

            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic services = locator.ConnectServer(".", @"root\cimv2");

            string query = !string.IsNullOrWhiteSpace(adapter.DeviceId)
                ? $"SELECT * FROM Win32_NetworkAdapter WHERE GUID='{EscapeWql(adapter.DeviceId)}'"
                : $"SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID='{EscapeWql(adapter.Name)}'";

            dynamic results = services.ExecQuery(query);
            foreach (dynamic obj in results)
            {
                string methodName = enable ? "Enable" : "Disable";
                dynamic outParams = obj.ExecMethod_(methodName);
                int returnVal = GetInt(outParams, "ReturnValue", -1);
                if (returnVal == 0)
                {
                    return new OperationResult(true, "OK");
                }
                return new OperationResult(false, $"Win32_NetworkAdapter.{methodName} 返回错误码 {returnVal}");
            }

            return new OperationResult(false, "未找到对应的 Win32_NetworkAdapter 实例");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Win32 WMI 调用失败: {ex.Message}");
        }
    }

    private static OperationResult TrySetStateViaNetsh(string adapterName, bool enable)
    {
        try
        {
            string state = enable ? "enabled" : "disabled";
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = $"interface set interface name=\"{adapterName.Replace("\"", "\\\"")}\" admin={state}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return new OperationResult(false, "无法启动 netsh.exe");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(8000);

            if (proc.ExitCode == 0)
                return new OperationResult(true, "OK");

            string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return new OperationResult(false, $"netsh 执行失败 ({proc.ExitCode}): {err.Trim()}");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"netsh 执行异常: {ex.Message}");
        }
    }

    private static OperationResult TrySetStateViaPowerShell(string adapterName, bool enable)
    {
        try
        {
            string cmdlet = enable ? "Enable-NetAdapter" : "Disable-NetAdapter";
            string safeName = adapterName.Replace("'", "''");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{cmdlet} -Name '{safeName}' -Confirm:$false\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return new OperationResult(false, "无法启动 PowerShell");

            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            return proc.ExitCode == 0
                ? new OperationResult(true, "OK")
                : new OperationResult(false, $"PowerShell 执行失败: {stderr.Trim()}");
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"PowerShell 异常: {ex.Message}");
        }
    }

    private static NetworkInterface? FindMatchingInterface(
        string deviceId,
        string name,
        Dictionary<string, NetworkInterface> byGuid,
        Dictionary<string, NetworkInterface> byName)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && byGuid.TryGetValue(deviceId.Trim(), out var niById))
            return niById;
        if (!string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name.Trim(), out var niByName))
            return niByName;
        return null;
    }

    private static string ExtractIpAddresses(NetworkInterface? ni)
    {
        if (ni is null) return "-";
        try
        {
            var props = ni.GetIPProperties();
            var ipv4 = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address.ToString())
                .ToList();

            if (ipv4.Count > 0)
                return string.Join(", ", ipv4);

            var ipv6 = props.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal)
                .Select(u => u.Address.ToString())
                .ToList();

            return ipv6.Count > 0 ? string.Join(", ", ipv6) : "-";
        }
        catch
        {
            return "-";
        }
    }

    private static string FormatSpeed(ulong wmiSpeedBps, long niSpeedBps)
    {
        ulong bps = wmiSpeedBps > 0 && wmiSpeedBps < ulong.MaxValue / 2
            ? wmiSpeedBps
            : (niSpeedBps > 0 ? (ulong)niSpeedBps : 0);

        if (bps == 0) return "-";
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000.0:0.##} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.##} Mbps";
        if (bps >= 1_000) return $"{bps / 1_000.0:0.##} Kbps";
        return $"{bps} bps";
    }

    private static string FormatMac(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "-";
        var clean = raw.Replace(":", "").Replace("-", "").Trim();
        if (clean.Length == 12)
        {
            return string.Join("-", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
        }
        return raw.Trim();
    }

    private static AdapterKind ClassifyKind(
        string name,
        string desc,
        int ndisPhysicalMedium,
        int ndisMedium,
        bool isHardware,
        bool isVirtual,
        NetworkInterface? ni)
    {
        // NdisPhysicalMedium: 1 = Wireless LAN, 9 = Native802_11 (Wi-Fi), 10 = Bluetooth, 14 = 802_3 (Ethernet)
        // NdisMedium: 15 = Tunnel, 16 = Native 802.11
        if (ndisPhysicalMedium is 1 or 9 || ndisMedium == 16 ||
            ni?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            return AdapterKind.WiFi;

        if (ndisPhysicalMedium == 10 || name.Contains("蓝牙", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return AdapterKind.Bluetooth;

        if (isVirtual || ndisMedium == 15 || !isHardware ||
            desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Sangfor", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("VNIC", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("VPN", StringComparison.OrdinalIgnoreCase))
            return AdapterKind.Virtual;

        if (name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
            return AdapterKind.WiFi;

        if (ndisPhysicalMedium == 14 ||
            ni?.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
            ni?.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
            name.Contains("以太网", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
            return AdapterKind.Ethernet;

        return AdapterKind.Other;
    }

    private static string EscapeWql(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string GetFirstStringFromArray(dynamic obj, string propName)
    {
        try
        {
            object? val = obj.Properties_.Item(propName).Value;
            if (val is Array arr && arr.Length > 0)
            {
                return arr.GetValue(0)?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetString(dynamic obj, string propName)
    {
        try
        {
            object? val = obj.Properties_.Item(propName).Value;
            return val?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetInt(dynamic obj, string propName, int fallback)
    {
        try
        {
            object? val = obj.Properties_.Item(propName).Value;
            return val is null ? fallback : Convert.ToInt32(val);
        }
        catch
        {
            return fallback;
        }
    }

    private static ulong GetULong(dynamic obj, string propName, ulong fallback)
    {
        try
        {
            object? val = obj.Properties_.Item(propName).Value;
            return val is null ? fallback : Convert.ToUInt64(val);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool GetBool(dynamic obj, string propName, bool fallback)
    {
        try
        {
            object? val = obj.Properties_.Item(propName).Value;
            return val is null ? fallback : Convert.ToBoolean(val);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool? GetNullableBool(dynamic obj, string propName)
    {
        try
        {
            object? val = obj.Properties_.Item(propName).Value;
            return val is null ? null : Convert.ToBoolean(val);
        }
        catch
        {
            return null;
        }
    }
}
