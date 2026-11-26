using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using NetAdapterToggle.Services;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace NetAdapterToggle;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2)
        {
            var mode = e.Args[0].ToLowerInvariant();
            var targetName = e.Args[1];

            if (mode is "--toggle" or "--enable" or "--disable" or "--restart")
            {
                await RunCliActionAsync(mode, targetName);
                Shutdown();
                return;
            }
        }

        bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;
        if (!startMinimized)
        {
            mainWindow.Show();
        }
    }

    private static async Task RunCliActionAsync(string mode, string targetName)
    {
        var service = new NetworkAdapterService();
        if (!NetworkAdapterService.IsAdministrator())
        {
            try
            {
                NetworkAdapterService.RestartAsAdministrator($"{mode} \"{targetName}\"");
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"需要管理员权限才能操作网络适配器。\n{ex.Message}", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var adapters = await service.GetAdaptersAsync();
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Name, targetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.DeviceId, targetName, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            MessageBox.Show($"未找到名称为「{targetName}」的网络适配器。", "网络适配器一键开关", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        OperationResult result = mode switch
        {
            "--enable" => await service.SetAdapterEnabledAsync(adapter, true),
            "--disable" => await service.SetAdapterEnabledAsync(adapter, false),
            "--toggle" => await service.SetAdapterEnabledAsync(adapter, !adapter.IsEnabled),
            "--restart" => await service.RestartAdapterAsync(adapter),
            _ => new OperationResult(false, "未知命令参数")
        };

        if (!result.Success)
        {
            MessageBox.Show(result.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Briefly show a Windows tray balloon notification so shortcut clicks give clear feedback
        using var notifyIcon = new Forms.NotifyIcon
        {
            Icon = CreateAppIcon(),
            Visible = true,
            BalloonTipTitle = "网络适配器一键开关",
            BalloonTipText = result.Message,
            BalloonTipIcon = Forms.ToolTipIcon.Info
        };
        notifyIcon.ShowBalloonTip(2500);
        await Task.Delay(2500);
        notifyIcon.Visible = false;
    }

    public static Icon CreateAppIcon()
    {
        try
        {
            var streamInfo = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (streamInfo?.Stream is not null)
            {
                using var stream = streamInfo.Stream;
                return new Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            // Fall back to runtime-drawn icon if resource lookup fails
        }

        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgBrush = new SolidBrush(Color.FromArgb(37, 99, 235));
        using var path = new GraphicsPath();
        int r = 7;
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(31 - r * 2, 0, r * 2, r * 2, 270, 90);
        path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(0, 31 - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(bgBrush, path);

        using var pen = new Pen(Color.White, 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Draw stylized Wi-Fi / network arcs + power dot
        g.DrawArc(pen, 6, 8, 20, 20, 225, 90);
        g.DrawArc(pen, 10, 12, 12, 12, 225, 90);
        using var dotBrush = new SolidBrush(Color.White);
        g.FillEllipse(dotBrush, 13.5f, 20.5f, 5f, 5f);

        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
