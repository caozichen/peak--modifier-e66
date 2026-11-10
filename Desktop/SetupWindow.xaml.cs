using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PeakWorkshop.Desktop;

public partial class SetupWindow : Window
{
    private readonly InstallationService _installation = new(PortableEnvironment.Root);
    private readonly string _initialPath;
    private bool _working, _selecting, _noticeFromAction;
    private int _inspection;
    private InstallationStatus? _status;
    public string SelectedGamePath => (GameSelector.SelectedItem as GameCandidate)?.Path ?? "";

    public SetupWindow(string selectedPath)
    {
        InitializeComponent();
        _initialPath = selectedPath;
    }

    private async void Setup_Loaded(object sender, RoutedEventArgs e) => await FindGamesAsync();
    private async void Find_Click(object sender, RoutedEventArgs e) => await FindGamesAsync();

    private async Task FindGamesAsync()
    {
        if (_working) return;
        string preferred = SelectedGamePath.Length > 0 ? SelectedGamePath : _initialPath;
        _noticeFromAction = false;
        SetWorking(true);
        SetupTitle.Text = "正在查找游戏…";
        try
        {
            var games = await Task.Run(() => SteamDiscovery.FindInstalledGames(preferred));
            _selecting = true;
            GameSelector.ItemsSource = games;
            GameSelector.SelectedItem = games.FirstOrDefault(g => string.Equals(g.Path, preferred, StringComparison.OrdinalIgnoreCase))
                ?? (games.Count == 1 ? games[0] : null);
            SetupTitle.Text = games.Count == 0 ? "尚未找到 PEAK" : games.Count > 1 && GameSelector.SelectedItem == null ? "找到多个游戏目录" : "已找到游戏目录";
            SetupDetail.Text = games.Count == 0 ? "请使用“手动选择”，选择包含 PEAK.exe 和 PEAK_Data 的游戏文件夹。"
                : games.Count > 1 && GameSelector.SelectedItem == null ? "请在上方选择这次要使用的 PEAK 安装目录。" : "正在检查游戏版本与插件…";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _selecting = false; SetWorking(false); }
        await InspectSelectedAsync();
    }

    private async void Game_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_selecting && !_working) await InspectSelectedAsync();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var picker = new OpenFolderDialog { Title = "选择包含 PEAK.exe 的游戏文件夹", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            string path = SteamDiscovery.ValidateGameDirectory(picker.FolderName);
            var games = (GameSelector.ItemsSource as IEnumerable<GameCandidate>)?.ToList() ?? new();
            var selected = games.FirstOrDefault(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase));
            if (selected == null) { selected = new GameCandidate(path, "手动选择"); games.Add(selected); }
            _selecting = true;
            GameSelector.ItemsSource = games;
            GameSelector.SelectedItem = selected;
            _noticeFromAction = false;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _selecting = false; }
        await InspectSelectedAsync();
    }

    private async Task InspectSelectedAsync()
    {
        string path = SelectedGamePath;
        int revision = ++_inspection;
        _status = null;
        UpdateActions();
        if (path.Length == 0) return;
        SetupTitle.Text = "正在检查…";
        try
        {
            var status = await Task.Run(() => _installation.Inspect(path));
            if (revision != _inspection || path != SelectedGamePath || !IsLoaded) return;
            _status = status;
            SetupTitle.Text = status.Installed ? "插件已就绪" : status.CanInstall ? "可以安装游戏插件" : "请先处理以下情况";
            SetupDetail.Text = status.Message;
            GameSelector.ToolTip = path;
            if (!_noticeFromAction)
                SetupNotice.Text = status.Installed ? "插件已就绪。请从 Steam 启动游戏，点击“完成，返回修改器”后自动连接。"
                    : status.CanInstall ? "确认游戏目录后，点击下方按钮。兼容的已有加载器和配置会保留。" : "请按上方提示处理，再点击“重新查找”检查。";
            InstallButton.Content = string.IsNullOrWhiteSpace(status.InstallLabel) ? "安装游戏插件" : status.InstallLabel;
        }
        catch (Exception ex) { if (revision == _inspection) ShowError(ex); }
        UpdateActions();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _status?.CanInstall != true) return;
        string path = SelectedGamePath;
        bool recovery = _status.InstallLabel.Contains("恢复", StringComparison.Ordinal);
        await ExecuteAsync(() => _installation.Install(path), recovery ? "正在恢复上次操作…" : "正在安装游戏插件…",
            recovery ? "上次操作已恢复，请检查结果；如需安装，请再次点击安装按钮。" : "安装完成。请从 Steam 启动游戏，返回修改器后会自动连接。");
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _status?.CanUninstall != true) return;
        if (MessageBox.Show(this, "将移除本程序管理且未被修改的文件，并按安装记录恢复备份。其他 Mod 和生成的配置、日志会保留。\n\n确定卸载？", "卸载游戏插件", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        string path = SelectedGamePath;
        await ExecuteAsync(() => _installation.Uninstall(path), "正在卸载…", "卸载完成。安装备份与诊断记录保留在本程序文件夹。");
    }

    private async Task ExecuteAsync(Action action, string pending, string completed)
    {
        SetWorking(true);
        _noticeFromAction = true;
        SetupTitle.Text = pending;
        try
        {
            // Capture all UI values before moving file operations to a worker.
            await Task.Run(action);
            SetupNotice.Text = completed;
            AppLog.Write(completed);
        }
        catch (UnauthorizedAccessException)
        {
            SetupNotice.Text = "目录没有写入权限。请关闭游戏和本窗口，再右键 PeakWorkshop.exe 选择“以管理员身份运行”后重试。";
            AppLog.Write("Plugin management denied write access.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetWorking(false); }
        await InspectSelectedAsync();
    }

    private async void Collect_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        string path = SelectedGamePath;
        _noticeFromAction = true;
        SetWorking(true);
        try { SetupNotice.Text = "诊断已保存到：" + await Task.Run(() => DiagnosticsService.Collect(path)); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetWorking(false); }
    }

    private void SetWorking(bool value) { _working = value; UpdateActions(); }
    private void UpdateActions()
    {
        FindButton.IsEnabled = BrowseButton.IsEnabled = GameSelector.IsEnabled = DoneButton.IsEnabled = CollectButton.IsEnabled = !_working;
        InstallButton.IsEnabled = !_working && _status?.CanInstall == true;
        UninstallButton.IsEnabled = !_working && _status?.CanUninstall == true;
    }
    private void ShowError(Exception ex)
    {
        _noticeFromAction = true;
        SetupTitle.Text = "操作未完成";
        SetupNotice.Text = ex.Message;
        AppLog.Write("Game setup: " + ex);
    }
    private void Done_Click(object sender, RoutedEventArgs e) { if (!_working) Close(); }
    private void Setup_Closing(object? sender, CancelEventArgs e) { if (_working) e.Cancel = true; ++_inspection; }
}
