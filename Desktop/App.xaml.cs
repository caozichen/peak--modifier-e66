using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PeakWorkshop.Desktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, "Local\\PeakWorkshop.Desktop.v1", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("PEAK Workshop 已经打开，请使用现有窗口。", "PEAK Workshop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;
        try { PortableEnvironment.EnsureWritable(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "请先完整解压到可写文件夹", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length >= 2 && e.Args[0] == "--smoke-test")
            _ = SmokeTestAsync(window, e.Args[1]);
    }

    private static async Task SmokeTestAsync(MainWindow window, string reportPath)
    {
        await Task.Delay(1700);
        try
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string screenshotPath = Path.ChangeExtension(reportPath, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!);
            using (var screenshot = File.Create(screenshotPath)) encoder.Save(screenshot);
            var report = new
            {
                rendered = window.IsLoaded && window.IsVisible,
                title = window.Title,
                width = window.ActualWidth,
                height = window.ActualHeight,
                connected = window.IsConnected,
                status = window.CurrentStatus,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                createdUtc = DateTime.UtcNow
            };
            string? dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (dir != null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Write("Smoke report: " + ex.Message); }
        window.Close();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Unhandled: " + e.Exception);
        MessageBox.Show("窗口遇到错误，请将日志提供给开发端。\n" + e.Exception.Message,
            "PEAK Workshop", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
