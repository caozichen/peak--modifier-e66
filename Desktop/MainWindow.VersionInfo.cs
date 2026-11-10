using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PeakWorkshop.Desktop;

public partial class MainWindow
{
    private DispatcherTimer? _versionInfoTimer;
    private CancellationTokenSource? _versionInfoLifetime;
    private bool _versionInfoBusy;

    private async void VersionInfo_Loaded(object sender, RoutedEventArgs e)
    {
        if (_versionInfoLifetime != null) return;
        _versionInfoLifetime = new CancellationTokenSource();
        SetTrainerVersionInfo();
        _versionInfoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _versionInfoTimer.Tick += VersionInfo_Tick;
        _versionInfoTimer.Start();
        await RefreshVersionInfoAsync();
    }

    private void SetTrainerVersionInfo()
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知";
        string baseline = InstallationPolicy.SupportedGameHash;
        TrainerVersionText.Text = $"修改器 {version} · 原支持 SHA256 {GameVersionInfo.ShortHash(baseline)}";
        TrainerVersionText.Tag = baseline;
        SetVersionToolTip(TrainerVersionText, $"修改器版本：{version}\n随包插件版本：{InstallationPolicy.Supported.PluginVersion}\n原支持的游戏程序集 SHA256：\n{baseline}\n此校验值仅作历史参照，不限制当前游戏版本。\n点击复制完整 SHA-256。");
    }

    private void ApplyGameVersionInfo(GameVersionInfo info)
    {
        GameBuildText.Text = $"游戏 Build {info.Build} · SHA256 {GameVersionInfo.ShortHash(info.AssemblySha256)}";
        GameBuildText.Tag = info.AssemblySha256;
        SetVersionToolTip(GameBuildText, $"游戏 Build：{info.Build}\n当前游戏程序集 SHA256：\n{(info.AssemblySha256.Length > 0 ? info.AssemblySha256 : "未读取")}\n{info.Detail}\n点击复制完整 SHA-256。");
    }

    private static void SetVersionToolTip(FrameworkElement owner, string text)
    {
        if (owner.ToolTip is ToolTip { Content: TextBlock content })
        {
            content.Text = text;
            return;
        }
        var foreground = new SolidColorBrush(Color.FromRgb(240, 244, 250));
        var background = new SolidColorBrush(Color.FromRgb(25, 31, 43));
        owner.ToolTip = new ToolTip
        {
            Background = background,
            Foreground = foreground,
            BorderBrush = new SolidColorBrush(Color.FromRgb(78, 99, 125)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            MaxWidth = 528,
            Placement = PlacementMode.Bottom,
            Content = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                Background = background,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 12,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500
            }
        };
        ToolTipService.SetShowDuration(owner, 60000);
    }

    private void CopyGameHash_Click(object sender, RoutedEventArgs e) => CopyVersionHash(GameBuildText.Tag as string);

    private void CopyBaselineHash_Click(object sender, RoutedEventArgs e) => CopyVersionHash(TrainerVersionText.Tag as string);

    private void CopyVersionHash(string? hash)
    {
        if (hash is not { Length: 64 })
        {
            ShowNotice("尚未读取到完整校验值，请稍后重试。", false);
            return;
        }
        try
        {
            Clipboard.SetText(hash);
            ShowNotice("已复制完整 SHA-256（64 个字符）。", false);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowNotice("剪贴板暂不可用，请稍后重试。", true);
        }
    }

    private void VersionInfo_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_versionInfoTimer != null)
        {
            _versionInfoTimer.Stop();
            _versionInfoTimer.Tick -= VersionInfo_Tick;
            _versionInfoTimer = null;
        }
        _versionInfoLifetime?.Cancel();
        _versionInfoLifetime?.Dispose();
        _versionInfoLifetime = null;
    }

    private async void VersionInfo_Tick(object? sender, EventArgs e) => await RefreshVersionInfoAsync();

    private async Task RefreshVersionInfoAsync()
    {
        if (_versionInfoBusy || _closing || _versionInfoLifetime == null) return;
        _versionInfoBusy = true;
        string gamePath = _settings.GamePath;
        CancellationToken token = _versionInfoLifetime.Token;
        try
        {
            var info = await Task.Run(() => GameVersionInfo.Read(gamePath, token), token);
            if (token.IsCancellationRequested || _closing || gamePath != _settings.GamePath) return;
            ApplyGameVersionInfo(info);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || _closing || gamePath != _settings.GamePath) return;
            GameBuildText.Text = "游戏 Build 未读取 · SHA256 未读取";
            GameBuildText.Tag = null;
            SetVersionToolTip(GameBuildText, ex.Message);
        }
        finally { _versionInfoBusy = false; }
    }
}
