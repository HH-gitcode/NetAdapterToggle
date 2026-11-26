# 网络适配器一键开关 (NetAdapterToggle)

基于 `.NET 10` + `WPF` 构建的 Windows 桌面工具，用于一键启用、禁用或重启网络适配器。

## 主要功能

- **一键启用 / 禁用**：每个网络适配器卡片右侧均提供醒目的一键切换按钮，并实时显示 IPv4/IPv6 地址、连接速率与 MAC 地址。
- **一键重启网卡**：先禁用再启用，快速解决偶发断网或 DHCP 异常。
- **批量操作与筛选**：支持按「全部 / 物理网卡 / 已启用 / 已禁用」筛选、关键词搜索，以及批量全部启用 / 全部禁用。
- **置顶与系统托盘快捷菜单**：支持将常用网卡置顶，在任务栏托盘右键菜单中一键切换，无需打开主窗口。
- **桌面一键快捷方式**：点击卡片上的快捷方式按钮，可在桌面生成 `切换 <网卡名>.lnk`，双击即可静默切换指定网卡并弹出系统通知。
- **命令行支持**：
  - `NetAdapterToggle.exe --toggle "WLAN"`
  - `NetAdapterToggle.exe --enable "以太网"`
  - `NetAdapterToggle.exe --disable "以太网"`
  - `NetAdapterToggle.exe --restart "WLAN"`
  - `NetAdapterToggle.exe --minimized`（启动并最小化到托盘）

## 编译与运行

```powershell
dotnet build -c Release
```
