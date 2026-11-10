using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Desktop;

public partial class MainWindow : Window
{
    private readonly PipeClient _client;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, FeatureCard> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _navigation = new(StringComparer.Ordinal);
    private readonly List<FeatureGroup> _groups = new();
    private PortableSettings _settings = new();
    private WireMessage? _actual;
    private bool _busy;
    private bool _polling;
    private bool _syncing;
    private bool _closing;
    private bool _allowClose;
    private string _lastConnectionError = "";
    private string _sessionId = "";
    private string _selectedGroup = "";
    private int _stoppedPid;

    public bool IsConnected { get; private set; }
    public string CurrentStatus => ConnectionTitle.Text;

    private sealed class FeatureCard
    {
        public required FeatureDefinition Definition;
        public required Border Container;
        public required CheckBox Toggle;
        public required TextBlock Status;
        public Slider? Slider;
        public TextBox? Editor;
        public Button? Apply;
        public bool Dirty;
        public bool Available;
        public bool ActualEnabled;
        public float ActualValue;
        public string Reason = "";
    }

    private sealed class FeatureGroup
    {
        public required string Name;
        public required StackPanel Container;
        public required WrapPanel Items;
        public required List<FeatureCard> Cards;
    }

    public MainWindow()
    {
        _client = new PipeClient(ResolveSelectedGameProcessId);
        InitializeComponent();
        BuildFeatureCards();
        LoadPreferences();
        ApplyFilter();
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        GamePathText.Text = _settings.GamePath.Length > 0 ? _settings.GamePath : "请在“游戏与插件”中选择";
        AppLog.Write("Desktop 0.3.2 starting; scene and same-window reconnect preserve choices; new windows start disabled.");
    }

    private void BuildFeatureCards()
    {
        AddNavigation("", "全部功能", FeatureCatalog.All.Length);
        foreach (var definitions in FeatureCatalog.All.GroupBy(feature => feature.Group))
        {
            AddNavigation(definitions.Key, definitions.Key, definitions.Count());
            var groupContainer = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var heading = new Grid { Margin = new Thickness(2, 2, 8, 11) };
            heading.Children.Add(new TextBlock { Text = definitions.Key, FontSize = 15, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = definitions.Count() + " 项", FontSize = 11, Foreground = Brush("#6F829D"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            groupContainer.Children.Add(heading);
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var group = new FeatureGroup { Name = definitions.Key, Container = groupContainer, Items = wrap, Cards = new List<FeatureCard>() };
            foreach (var definition in definitions)
            {
                FeatureCard card = CreateFeatureCard(definition);
                _cards.Add(definition.Id, card);
                group.Cards.Add(card);
                wrap.Children.Add(card.Container);
            }
            groupContainer.Children.Add(wrap);
            _groups.Add(group);
            FeatureGroupsHost.Children.Add(groupContainer);
        }
    }

    private void AddNavigation(string id, string name, int count)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var countLabel = new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), FontSize = 11, Foreground = Brush("#8FA3BD"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(countLabel, 1);
        layout.Children.Add(countLabel);
        var button = new Button { Content = layout, Padding = new Thickness(12, 12, 12, 12), Margin = new Thickness(0, 0, 0, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button, name + "分组");
        button.Click += (_, _) => NavigateToGroup(id);
        _navigation.Add(id, button);
        GroupNavigation.Children.Add(button);
    }

    private FeatureCard CreateFeatureCard(FeatureDefinition definition)
    {
        var container = new Border
        {
            Background = Brush("#191F2B"), BorderBrush = Brush("#2A3344"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11), Padding = new Thickness(17), Margin = new Thickness(0, 0, 12, 12), Width = 370
        };
        var body = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = definition.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        header.Children.Add(title);
        var toggle = new CheckBox { Style = (Style)FindResource("Toggle"), IsEnabled = false, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(toggle, 1);
        AutomationProperties.SetName(toggle, definition.Name + "开关");
        header.Children.Add(toggle);
        body.Children.Add(header);
        var description = new TextBlock { Text = definition.Description, Foreground = Brush("#8D9FB7"), FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 10, 0, 0), MinHeight = 38 };
        body.Children.Add(description);
        var status = new TextBlock { Text = "等待插件能力报告", Foreground = Brush("#7C91AC"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 11, 0, 0), MinHeight = 18 };
        body.Children.Add(status);
        var card = new FeatureCard { Definition = definition, Container = container, Toggle = toggle, Status = status, ActualValue = definition.Default };
        if (definition.HasValue)
        {
            var slider = new Slider { Minimum = definition.Min, Maximum = definition.Max, Value = definition.Default, TickFrequency = definition.Step, IsSnapToTickEnabled = true, IsEnabled = false, Margin = new Thickness(0, 13, 0, 10) };
            AutomationProperties.SetName(slider, definition.Name + "数值滑块");
            body.Children.Add(slider);
            var editorRow = new Grid();
            editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var editor = new TextBox { Text = FormatValue(definition.Default), Background = Brush("#101924"), BorderBrush = Brush("#3A4961"), BorderThickness = new Thickness(1), Foreground = Brush("#DDF5C7"), CaretBrush = Brush("#B6F383"), FontSize = 15, Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = false };
            AutomationProperties.SetName(editor, definition.Name + "数值输入");
            editorRow.Children.Add(editor);
            var unit = new TextBlock { Text = definition.Unit + "  /  " + FormatValue(definition.Min) + " – " + FormatValue(definition.Max), Foreground = Brush("#7187A4"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(unit, 1);
            editorRow.Children.Add(unit);
            var apply = new Button { Content = "应用", Padding = new Thickness(11, 6, 11, 6), IsEnabled = false };
            AutomationProperties.SetName(apply, "应用" + definition.Name);
            Grid.SetColumn(apply, 2);
            editorRow.Children.Add(apply);
            body.Children.Add(editorRow);
            card.Slider = slider;
            card.Editor = editor;
            card.Apply = apply;
            slider.ValueChanged += (_, args) =>
            {
                if (_syncing) return;
                _syncing = true;
                editor.Text = FormatValue((float)args.NewValue);
                _syncing = false;
                MarkEdited(card);
            };
            editor.TextChanged += (_, _) =>
            {
                if (_syncing) return;
                MarkEdited(card);
                if (TryReadValue(card, out float parsed))
                {
                    _syncing = true;
                    slider.Value = parsed;
                    _syncing = false;
                }
            };
            editor.KeyDown += async (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { args.Handled = true; await SendFeatureChangeAsync(card, card.ActualEnabled); } };
            apply.Click += async (_, _) => await SendFeatureChangeAsync(card, card.ActualEnabled);
        }
        toggle.Click += async (_, _) =>
        {
            if (_syncing) return;
            bool desired = toggle.IsChecked == true;
            RestoreToggleVisuals();
            await SendFeatureChangeAsync(card, desired);
        };
        container.Child = body;
        return card;
    }

    private void MarkEdited(FeatureCard card)
    {
        card.Dirty = true;
        card.Editor!.BorderBrush = Brush(TryReadValue(card, out _) ? "#668647" : "#C27C58");
        UpdateCardStatus(card);
        ShowNotice("“" + card.Definition.Name + "”参数已编辑，点击应用后等待插件确认；应用参数不会自动开启功能。", false);
    }

    private static bool TryReadValue(FeatureCard card, out float value)
    {
        value = card.Definition.Default;
        if (!card.Definition.HasValue) return true;
        string text = card.Editor?.Text.Trim() ?? "";
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return false;
        return float.IsFinite(value) && value >= card.Definition.Min && value <= card.Definition.Max;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ResizeFeatureCards();
        if (_settings.GamePath.Length == 0) await OpenSetupAsync();
        if (_closing) return;
        _pollTimer.Start();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy || _polling || _closing) return;
        if (_settings.GamePath.Length == 0)
        {
            SetDisconnected("请点击左下角“游戏与插件”，选择 PEAK 目录并检查插件。");
            return;
        }
        if (_stoppedPid != 0)
        {
            if (PipeClient.FindGameProcessId() == _stoppedPid) return;
            _stoppedPid = 0;
        }
        _polling = true;
        SetupButton.IsEnabled = false;
        try
        {
            var response = await _client.SendAsync(new WireMessage { kind = "state" });
            if (!_closing) ApplyActual(response);
            if (!response.ok && !_closing) ShowNotice(ResponseError(response), true);
        }
        catch (Exception ex) { if (!_closing) SetDisconnected(ex.Message); }
        finally { _polling = false; UpdateEnabled(); }
    }

    private void ApplyActual(WireMessage state)
    {
        bool wasConnected = IsConnected;
        bool newSession = _sessionId != state.sessionId;
        bool resetDrafts = _sessionId.Length > 0 && newSession;
        _sessionId = state.sessionId;
        _actual = state;
        IsConnected = true;
        _lastConnectionError = "";
        ConnectionDot.Fill = Brush("#B6F383");
        ConnectionTitle.Text = state.characterReady ? "已连接 · 角色就绪" : "已连接 · 等待角色";
        ConnectionDetail.Text = state.characterReady ? "插件状态已同步。功能开启记录不代表游戏效果已经测试通过。" : ReadinessDetail(state);
        SessionDetail.Text = $"PID {_client.ProcessId}  ·  插件 {ValueOrDash(state.pluginVersion)}  ·  场景 {ValueOrDash(state.scene)}";
        SessionDetail.ToolTip = "游戏版本：" + ValueOrDash(state.gameVersion) + "\n会话：" + state.sessionId;
        var features = (state.features ?? Array.Empty<FeatureValue>()).Where(value => value != null).GroupBy(value => value.id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _syncing = true;
        try
        {
            foreach (FeatureCard card in _cards.Values)
            {
                features.TryGetValue(card.Definition.Id, out FeatureValue? feature);
                card.Available = feature?.available == true;
                card.Reason = feature?.reason ?? "插件未报告此功能，请检查版本是否匹配。";
                card.ActualEnabled = feature?.enabled == true;
                card.ActualValue = feature != null && float.IsFinite(feature.value) ? feature.value : card.Definition.Default;
                card.Toggle.IsChecked = card.ActualEnabled;
                if (card.Definition.HasValue && (!card.Dirty || resetDrafts))
                {
                    SetCardValue(card, Math.Clamp(card.ActualValue, card.Definition.Min, card.Definition.Max));
                    card.Dirty = false;
                }
                UpdateCardStatus(card);
            }
        }
        finally { _syncing = false; }
        if (!wasConnected || newSession)
        {
            ShowNotice("连接已确认。请逐项启用并验证关闭恢复；未支持项目会显示具体原因。", false);
            AppLog.Write($"Connected PID {_client.ProcessId}; plugin {state.pluginVersion}; session {state.sessionId}; features={features.Count}.");
            TryUpdateGamePath();
        }
        UpdateEnabled();
        ApplyFilter();
    }

    private void SetCardValue(FeatureCard card, float value)
    {
        if (card.Slider != null) card.Slider.Value = value;
        if (card.Editor != null)
        {
            card.Editor.Text = FormatValue(value);
            card.Editor.BorderBrush = Brush("#3A4961");
        }
    }

    private void UpdateCardStatus(FeatureCard card)
    {
        if (!IsConnected)
        {
            card.Status.Text = _stoppedPid != 0 ? "插件已停用 · 重启游戏后重连" : card.ActualEnabled ? "开启配置已保留 · 等待重连" : "未连接 · 状态未知";
            card.Status.Foreground = Brush("#7C91AC");
        }
        else if (!card.Available)
        {
            card.Status.Text = (card.ActualEnabled ? "开启配置已保留 · 暂不可用 · " : "不可用 · ") + (string.IsNullOrWhiteSpace(card.Reason) ? "当前条件不支持" : card.Reason);
            card.Status.Foreground = Brush("#D8AA80");
        }
        else if (_actual?.characterReady != true)
        {
            card.Status.Text = ReadinessDetail(_actual) + (card.ActualEnabled ? " · 开启配置已保留，就绪后恢复" : " · 尚不能开启");
            card.Status.Foreground = Brush("#91A6BF");
        }
        else
        {
            card.Status.Text = card.ActualEnabled ? "已开启" + (card.Definition.HasValue ? " · " + FormatValue(card.ActualValue) + " " + card.Definition.Unit : "") + " · 插件已确认" : "已关闭 · 插件已确认";
            if (!string.IsNullOrWhiteSpace(card.Reason)) card.Status.Text += " · " + card.Reason;
            card.Status.Foreground = Brush(card.ActualEnabled ? "#B6E88F" : "#91A6BF");
        }
        if (card.Definition.HasValue && card.Dirty && IsConnected) card.Status.Text += " · 参数待应用";
        card.Toggle.ToolTip = card.Available ? "设置后等待插件确认" : card.Status.Text;
    }

    private async Task SendFeatureChangeAsync(FeatureCard target, bool enabled)
    {
        if (_busy || _closing || !IsConnected || _actual?.characterReady != true || !target.Available) { RestoreToggleVisuals(); return; }
        if (!TryReadValue(target, out float value))
        {
            RestoreToggleVisuals();
            ShowNotice("请输入“" + target.Definition.Name + "”的有效数值：" + FormatValue(target.Definition.Min) + " – " + FormatValue(target.Definition.Max) + "。", true);
            target.Editor?.Focus();
            return;
        }
        FeatureValue[] desired = FeatureCatalog.All.Select(definition =>
        {
            FeatureCard card = _cards[definition.Id];
            return new FeatureValue { id = definition.Id, enabled = card.ActualEnabled, value = card.ActualValue };
        }).ToArray();
        FeatureValue changed = desired.First(feature => feature.id == target.Definition.Id);
        changed.enabled = enabled;
        changed.value = value;
        int revision = _actual.stateRevision;
        SetBusy(true);
        ShowNotice("正在提交“" + target.Definition.Name + "”，等待插件确认…", false);
        try
        {
            var response = await _client.SendAsync(new WireMessage { kind = "set", sessionId = _sessionId, stateRevision = revision, features = desired });
            if (response.ok) target.Dirty = false;
            if (_closing) return;
            ApplyActual(response);
            ShowNotice(response.ok ? "插件已确认“" + target.Definition.Name + "”的设置。请在游戏内验证实际效果。" : ResponseError(response), !response.ok);
            AppLog.Write($"Set id={target.Definition.Id}; desired={enabled}/{value}; accepted={response.ok}; revision={response.stateRevision}; {response.error} {response.message}");
            SavePreferences();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            SetDisconnected(ex.Message);
            ShowNotice("未能确认设置结果：" + ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ShowNotice("正在重新检查游戏与插件连接…", false);
        await RefreshAsync();
        if (IsConnected && !_closing)
            ShowNotice(_actual?.characterReady == true ? "已连接，角色就绪。" : ReadinessDetail(_actual), false);
    }

    private async void ClearStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _stoppedPid != 0 || !IsConnected || _actual?.pluginStopped == true || _actual?.characterReady != true || !_actual.clearStatusAvailable) return;
        int revision = _actual.stateRevision;
        SetBusy(true);
        ShowNotice("正在清除当前状态，等待插件确认…", false);
        try
        {
            var reply = await _client.SendAsync(new WireMessage { kind = "clear_status", sessionId = _sessionId, stateRevision = revision });
            if (_closing) return;
            ApplyActual(reply);
            ShowNotice(reply.ok ? reply.message : ResponseError(reply), !reply.ok);
            AppLog.Write($"Clear current status accepted={reply.ok}; revision={reply.stateRevision}; {reply.error} {reply.message}");
        }
        catch (Exception ex)
        {
            if (_closing) return;
            SetDisconnected(ex.Message);
            ShowNotice("清除结果尚未确认，请检查角色当前状态；不会自动重复执行。" + ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !IsConnected || _closing) return;
        SetBusy(true);
        ShowNotice("正在恢复全部，等待插件确认…", false);
        try
        {
            var response = await _client.SendAsync(new WireMessage { kind = "reset", sessionId = _sessionId, stateRevision = _actual?.stateRevision ?? 0 });
            if (_closing) return;
            if (response.ok) foreach (FeatureCard card in _cards.Values) card.Dirty = false;
            ApplyActual(response);
            ShowNotice(response.ok ? "插件已确认关闭全部自制功能，请检查游戏内恢复结果。" : ResponseError(response), !response.ok);
            AppLog.Write($"Reset accepted={response.ok}; {response.error} {response.message}");
        }
        catch (Exception ex) { if (!_closing) { SetDisconnected(ex.Message); ShowNotice("恢复结果尚未确认：" + ex.Message, true); } }
        finally { SetBusy(false); }
    }

    private async void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || !IsConnected || _actual?.detachAvailable != true) return;
        int pid = _client.ProcessId;
        SetBusy(true);
        ShowNotice("正在关闭功能、撤销本插件补丁并停止通信…", false);
        try
        {
            var reply = await _client.SendAsync(new WireMessage { kind = "detach", sessionId = _sessionId, stateRevision = _actual.stateRevision });
            if (_closing) return;
            if (!reply.pluginStopped) { ApplyActual(reply); ShowNotice(ResponseError(reply), true); return; }
            _client.Disconnect();
            _stoppedPid = pid;
            IsConnected = false;
            _actual = null;
            foreach (FeatureCard card in _cards.Values)
            {
                card.ActualEnabled = false;
                card.Available = false;
                UpdateCardStatus(card);
            }
            RestoreToggleVisuals();
            ConnectionDot.Fill = Brush("#D4B878");
            ConnectionTitle.Text = reply.ok ? "插件已停用" : "功能已关闭 · 请重启游戏完成清理";
            ConnectionDetail.Text = reply.message;
            SessionDetail.Text = "窗口可以保持打开；重启 PEAK 后将自动连接新的游戏进程。";
            ShowNotice("DLL 随退出游戏释放。重启游戏后重新连接；磁盘插件文件需关闭游戏后卸载。", !reply.ok);
            AppLog.Write("Plugin deactivated; confirmed=" + reply.ok + "; " + reply.message);
            ApplyFilter();
        }
        catch (Exception ex) { if (!_closing) { SetDisconnected(ex.Message); ShowNotice("停用结果未确认，请退出游戏完成清理：" + ex.Message, true); } }
        finally { SetBusy(false); }
    }

    private void SetDisconnected(string detail)
    {
        bool wasConnected = IsConnected;
        IsConnected = false;
        _actual = null;
        ConnectionDot.Fill = Brush("#718096");
        ConnectionTitle.Text = detail.Contains("尚未检测到", StringComparison.Ordinal) ? "等待游戏启动" : "尚未连接游戏插件";
        ConnectionDetail.Text = detail;
        SessionDetail.Text = "没有可确认的游戏会话";
        foreach (FeatureCard card in _cards.Values)
        {
            card.Available = false;
            UpdateCardStatus(card);
        }
        RestoreToggleVisuals();
        if (_lastConnectionError != detail || wasConnected) { AppLog.Write("Disconnected: " + detail); _lastConnectionError = detail; }
        if (wasConnected) ShowNotice("连接暂时中断，已保留开关和倍率；同一窗口重新连接后会恢复应用。", true);
        UpdateEnabled();
        ApplyFilter();
    }

    private void RestoreToggleVisuals()
    {
        _syncing = true;
        try { foreach (FeatureCard card in _cards.Values) card.Toggle.IsChecked = card.ActualEnabled; }
        finally { _syncing = false; }
    }

    private void SetBusy(bool value) { _busy = value; UpdateEnabled(); }

    private void UpdateEnabled()
    {
        foreach (FeatureCard card in _cards.Values)
        {
            bool usable = !_busy && !_closing && IsConnected && _actual?.characterReady == true && card.Available;
            card.Toggle.IsEnabled = usable;
            if (card.Slider != null) card.Slider.IsEnabled = usable;
            if (card.Editor != null) card.Editor.IsEnabled = usable;
            if (card.Apply != null) card.Apply.IsEnabled = usable;
        }
        ResetButton.IsEnabled = !_busy && !_closing && IsConnected;
        DetachButton.IsEnabled = !_busy && !_closing && IsConnected && _actual?.detachAvailable == true;
        ConnectButton.IsEnabled = !_busy && !_closing && _stoppedPid == 0;
        SetupButton.IsEnabled = !_busy && !_closing && !_polling;
        ClearStatusButton.IsEnabled = !_busy && !_closing && _stoppedPid == 0 && IsConnected && _actual?.pluginStopped != true && _actual?.characterReady == true && _actual.clearStatusAvailable;
        string clearDetail = !IsConnected ? (_stoppedPid != 0 ? "插件已停用，重启游戏后再连接。" : "连接游戏并等待角色就绪后可使用。")
            : _actual?.characterReady != true ? ReadinessDetail(_actual)
            : _actual.clearStatusAvailable ? "清除可治疗状态并拔除荆棘，只执行一次；螃蟹、物品负重等来源需另行解除。"
            : string.IsNullOrWhiteSpace(_actual.clearStatusReason) ? "当前插件不支持清除状态，请确认版本。" : _actual.clearStatusReason;
        ClearStatusDetail.Text = clearDetail;
        ClearStatusButton.ToolTip = clearDetail;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (FeatureGroupsHost != null && FeatureCountText != null) ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (SearchBox == null) return;
        string search = SearchBox.Text.Trim();
        bool availableOnly = AvailableOnlyBox?.IsChecked == true;
        int visible = 0;
        foreach (FeatureGroup group in _groups)
        {
            int groupVisible = 0;
            foreach (FeatureCard card in group.Cards)
            {
                FeatureDefinition definition = card.Definition;
                bool matches = (!availableOnly || card.Available) &&
                    (search.Length == 0 || definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || definition.Description.Contains(search, StringComparison.OrdinalIgnoreCase) || definition.Group.Contains(search, StringComparison.OrdinalIgnoreCase) || definition.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
                card.Container.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                if (matches) { groupVisible++; visible++; }
            }
            group.Container.Visibility = groupVisible > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        FeatureCountText.Text = visible + " / " + FeatureCatalog.All.Length + " 项";
        EmptyFilterText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationFromScroll();
    }

    private void NavigateToGroup(string id)
    {
        // Sidebar entries are anchors in the same complete list. Clear filters
        // that could hide the requested anchor before locating it.
        SearchBox.Clear();
        AvailableOnlyBox.IsChecked = false;
        ApplyFilter();
        FeatureViewport.UpdateLayout();
        if (id.Length == 0)
        {
            FeatureViewport.ScrollToTop();
        }
        else
        {
            var group = _groups.FirstOrDefault(candidate => candidate.Name == id);
            if (group == null || FeatureViewport.Content is not UIElement content) return;
            double top = group.Container.TranslatePoint(new Point(), content).Y;
            FeatureViewport.ScrollToVerticalOffset(top);
        }
        FeatureViewport.UpdateLayout();
        UpdateNavigationFromScroll();
    }

    private void FeatureViewport_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, FeatureViewport)) UpdateNavigationFromScroll();
    }

    private void UpdateNavigationFromScroll()
    {
        if (FeatureViewport == null || FeatureViewport.ViewportHeight <= 0 || FeatureViewport.Content is not UIElement content) return;
        var visibleGroups = _groups.Where(group => group.Container.Visibility == Visibility.Visible).ToArray();
        string selected = "";
        if (visibleGroups.Length > 0)
        {
            selected = visibleGroups[0].Name;
            if (FeatureViewport.ScrollableHeight > 0 && FeatureViewport.VerticalOffset >= FeatureViewport.ScrollableHeight - 1)
            {
                selected = visibleGroups[^1].Name;
            }
            else
            {
                double marker = FeatureViewport.VerticalOffset + 8;
                foreach (var group in visibleGroups)
                {
                    if (group.Container.TranslatePoint(new Point(), content).Y > marker) break;
                    selected = group.Name;
                }
            }
        }
        _selectedGroup = selected;
        foreach (var navigation in _navigation)
        {
            bool highlighted = navigation.Key == _selectedGroup;
            navigation.Value.Background = Brush(highlighted ? "#27352C" : "#182130");
            navigation.Value.BorderBrush = Brush(highlighted ? "#455E3C" : "#273143");
        }
    }

    private void FeatureViewport_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeFeatureCards();

    private void ResizeFeatureCards()
    {
        if (FeatureViewport == null) return;
        double width = Math.Max(320, FeatureViewport.ActualWidth - 26);
        int columns = width >= 700 ? 2 : 1;
        double cardWidth = Math.Max(300, Math.Floor(width / columns) - 12);
        foreach (FeatureCard card in _cards.Values) card.Container.Width = cardWidth;
    }

    private void ShowNotice(string message, bool error)
    {
        NoticeText.Text = message;
        NoticeText.Foreground = Brush(error ? "#F1B896" : "#A4BACF");
    }

    private static string ResponseError(WireMessage message) => !string.IsNullOrWhiteSpace(message.message) ? message.message : !string.IsNullOrWhiteSpace(message.error) ? message.error : "插件未确认应用，请检查日志。";
    private static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string ReadinessDetail(WireMessage? state) => string.IsNullOrWhiteSpace(state?.characterReadinessMessage)
        ? "等待本地角色就绪" : state.characterReadinessMessage;
    private static string FormatValue(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void TryUpdateGamePath()
    {
        try
        {
            using var process = Process.GetProcessById(_client.ProcessId);
            string? executable = process.MainModule?.FileName;
            if (executable != null) GamePathText.Text = Path.GetDirectoryName(executable);
        }
        catch { }
    }

    private int ResolveSelectedGameProcessId()
    {
        if (string.IsNullOrWhiteSpace(_settings.GamePath)) return 0;
        string expected;
        try { expected = Path.GetFullPath(Path.Combine(_settings.GamePath, "PEAK.exe")); }
        catch { return 0; }
        Process[] candidates = Process.GetProcessesByName("PEAK");
        try
        {
            foreach (var candidate in candidates.OrderBy(process => process.Id))
                try
                {
                    string? executable = candidate.MainModule?.FileName;
                    if (executable != null && string.Equals(Path.GetFullPath(executable), expected, StringComparison.OrdinalIgnoreCase)) return candidate.Id;
                }
                catch { }
            return 0;
        }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }

    private async void Setup_Click(object sender, RoutedEventArgs e) => await OpenSetupAsync();

    private async Task OpenSetupAsync()
    {
        if (_busy || _polling || _closing) return;
        _pollTimer.Stop();
        SetBusy(true);
        try
        {
            if (IsConnected)
            {
                try { await _client.SendAsync(new WireMessage { kind = "reset", sessionId = _sessionId, stateRevision = _actual?.stateRevision ?? 0 }, 1500); }
                catch (Exception ex) { AppLog.Write("Reset before game setup unconfirmed: " + ex.Message); }
            }
            _client.Disconnect();
            SetDisconnected("正在检查游戏与插件，功能已关闭。");
            var setup = new SetupWindow(_settings.GamePath) { Owner = this };
            setup.ShowDialog();
            if (setup.SelectedGamePath.Length > 0)
            {
                _settings.GamePath = SteamDiscovery.ValidateGameDirectory(setup.SelectedGamePath);
                GamePathText.Text = _settings.GamePath;
                SavePreferences();
            }
        }
        catch (Exception ex) { ShowNotice("游戏设置未完成：" + ex.Message, true); AppLog.Write("Game setup: " + ex); }
        finally
        {
            SetBusy(false);
            if (IsLoaded && !_closing) _pollTimer.Start();
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            using var launched = Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + AppLog.DirectoryPath + "\"", UseShellExecute = true });
        }
        catch (Exception ex) { ShowNotice("无法打开日志目录：" + ex.Message, true); }
    }

    private void LoadPreferences()
    {
        try
        {
            _settings = PortableSettings.Load();
            if (_settings.GamePath.Length > 0)
                try { _settings.GamePath = SteamDiscovery.ValidateGameDirectory(_settings.GamePath); }
                catch { _settings.GamePath = ""; }
            _syncing = true;
            foreach (var preference in _settings.Values)
                if (_cards.TryGetValue(preference.Key, out FeatureCard? card) && card.Definition.HasValue)
                {
                    SetCardValue(card, Math.Clamp(preference.Value, card.Definition.Min, card.Definition.Max));
                    card.Dirty = true;
                }
        }
        catch (Exception ex) { _settings = new(); AppLog.Write("Settings read: " + ex.Message); ShowNotice("设置文件读取失败，已使用默认值：" + ex.Message, true); }
        finally { _syncing = false; }
    }

    private void SavePreferences()
    {
        try
        {
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (FeatureCard card in _cards.Values)
                if (card.Definition.HasValue && TryReadValue(card, out float value)) values[card.Definition.Id] = value;
            _settings.Values = values;
            _settings.Save();
        }
        catch (Exception ex) { AppLog.Write("Settings write: " + ex.Message); ShowNotice("设置未能保存：" + ex.Message, true); }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _pollTimer.Stop();
        SavePreferences();
        UpdateEnabled();
        ShowNotice("正在关闭，尝试恢复本次功能…", false);
        try
        {
            if (IsConnected)
            {
                var reply = await _client.SendAsync(new WireMessage { kind = "reset", sessionId = _sessionId, stateRevision = _actual?.stateRevision ?? 0 }, 1200);
                AppLog.Write("Close reset acknowledged=" + reply.ok);
            }
        }
        catch (Exception ex) { AppLog.Write("Close reset unconfirmed: " + ex.Message); }
        finally
        {
            _client.Dispose();
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
