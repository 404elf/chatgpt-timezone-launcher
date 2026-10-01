using System.Drawing;
using System.Diagnostics;

namespace ChatGptTimezoneLauncher;

public sealed class MainForm : Form
{
    private readonly ConfigStore _configStore;
    private readonly GeoIpService _geoIp = new();
    private readonly ChatGptDiscovery _discovery = new();
    private readonly ChatGptLauncher _launcher = new();
    private readonly UpdateService _updates = new();
    private readonly ToolTip _toolTip = new();
    private bool _busy;
    private bool _checkingUpdate;
    private LauncherConfig _config;

    private readonly RadioButton _autoRadio = new() { Text = "自动跟随 ChatGPT 实际出口", AutoSize = true };
    private readonly RadioButton _manualRadio = new() { Text = "手动选择时区", AutoSize = true };
    private readonly ComboBox _timeZoneBox = new();
    private readonly Label _overrideState = new();
    private readonly Label _detectionState = new();
    private readonly Label _ipValue = ValueLabel();
    private readonly Label _locationValue = ValueLabel();
    private readonly Label _zoneValue = ValueLabel();
    private readonly Label _proxyGroupValue = ValueLabel();
    private readonly Label _proxyNodeValue = ValueLabel();
    private readonly Label _providerValue = ValueLabel();
    private readonly Button _detectButton = new() { Text = "检测 ChatGPT 出口", AutoSize = true };
    private readonly Button _launchButton = new() { Text = "保存并启动 ChatGPT", Height = 42, AutoSize = true };
    private readonly Button _restoreButton = new() { Text = "恢复 ChatGPT 默认启动方式", Height = 42, AutoSize = true };
    private readonly Button _shortcutButton = new() { Text = "创建桌面快捷方式", AutoSize = true };
    private readonly CheckBox _closeAfterLaunch = new() { Text = "启动后自动关闭启动器", AutoSize = true };
    private readonly LinkLabel _updateLink = new() { Text = "↻ 更新", AutoSize = true, LinkColor = Color.DimGray, Margin = new Padding(12, 8, 3, 3) };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 84, Dock = DockStyle.Fill };

    public MainForm(ConfigStore? configStore = null, bool checkUpdates = true)
    {
        _configStore = configStore ?? new ConfigStore();
        var loaded = _configStore.Load();
        _config = loaded.Config;
        Text = "ChatGPT 时区启动器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 720);
        ClientSize = new Size(800, 920);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildUi();
        LoadConfigIntoUi();
        ShowLastDetection();
        UpdateModeUi();

        _autoRadio.CheckedChanged += (_, _) => UpdateModeUi();
        _manualRadio.CheckedChanged += (_, _) => UpdateModeUi();
        _detectButton.Click += async (_, _) => await DetectAsync();
        _launchButton.Click += async (_, _) => await SaveAndLaunchAsync();
        _restoreButton.Click += (_, _) => RestoreDefault();
        _shortcutButton.Click += (_, _) => CreateShortcut();
        _closeAfterLaunch.CheckedChanged += (_, _) =>
        {
            _config.CloseLauncherAfterLaunch = _closeAfterLaunch.Checked;
            _configStore.Save(_config);
        };
        _updateLink.LinkClicked += async (_, _) => await CheckUpdateAsync(true);
        _toolTip.SetToolTip(_updateLink, "检查并下载新版启动器");
        _toolTip.SetToolTip(_closeAfterLaunch, "仅在 ChatGPT 启动成功后关闭启动器；出错时保留窗口。");
        if (checkUpdates) Shown += async (_, _) => await CheckUpdateAsync(false);
        _timeZoneBox.SelectedIndexChanged += (_, _) => { if (_timeZoneBox.Focused) _manualRadio.Checked = true; };

        if (loaded.Warning is not null)
            _details.Text = loaded.Warning;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 20),
            ColumnCount = 1,
            RowCount = 8,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label { Text = "ChatGPT 时区启动器", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        var subtitle = new Label { Text = "只为本次启动的 ChatGPT 进程设置时区，不修改 Windows 系统时区。", ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
        var titlePanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false };
        titlePanel.Controls.Add(title); titlePanel.Controls.Add(subtitle);
        root.Controls.Add(titlePanel);

        var statePanel = new Panel { Height = 48, Dock = DockStyle.Fill, BackColor = Color.FromArgb(239, 246, 255), Margin = new Padding(0, 0, 0, 14) };
        _overrideState.AutoSize = true; _overrideState.Location = new Point(14, 14); _overrideState.Font = new Font(Font, FontStyle.Bold);
        statePanel.Controls.Add(_overrideState); root.Controls.Add(statePanel);

        var modeGroup = new GroupBox { Text = "模式", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(14, 10, 14, 12), Margin = new Padding(0, 0, 0, 12) };
        var modes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _autoRadio.Margin = new Padding(3, 5, 3, 6); _manualRadio.Margin = new Padding(3, 2, 3, 3);
        modes.Controls.Add(_autoRadio); modes.Controls.Add(_manualRadio); modeGroup.Controls.Add(modes); root.Controls.Add(modeGroup);

        var statusGroup = new GroupBox { Text = "ChatGPT 出口检测", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(14, 10, 14, 12), Margin = new Padding(0, 0, 0, 12) };
        var status = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 8 };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detectionState.AutoSize = false; _detectionState.Dock = DockStyle.Fill; _detectionState.Height = 24;
        AddRow(status, 0, "检测状态", _detectionState); AddRow(status, 1, "ChatGPT 出口 IP", _ipValue);
        AddRow(status, 2, "实际命中代理组", _proxyGroupValue); AddRow(status, 3, "当前节点", _proxyNodeValue);
        AddRow(status, 4, "位置", _locationValue); AddRow(status, 5, "IANA 时区", _zoneValue);
        AddRow(status, 6, "检测方式", _providerValue);
        _detectButton.Margin = new Padding(0, 9, 0, 0); status.Controls.Add(_detectButton, 1, 7);
        statusGroup.Controls.Add(status); root.Controls.Add(statusGroup);

        var manualGroup = new GroupBox { Text = "手动时区", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(14, 10, 14, 12), Margin = new Padding(0, 0, 0, 12) };
        var manual = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        manual.Controls.Add(new Label { Text = "输入城市或时区片段搜索，或从列表选择标准 IANA 时区：", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 7) });
        _timeZoneBox.Dock = DockStyle.Top; _timeZoneBox.DropDownStyle = ComboBoxStyle.DropDown; _timeZoneBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _timeZoneBox.AutoCompleteSource = AutoCompleteSource.ListItems; _timeZoneBox.MaxDropDownItems = 14;
        _timeZoneBox.Items.AddRange(TimeZoneCatalog.All.Cast<object>().ToArray()); manual.Controls.Add(_timeZoneBox);
        manualGroup.Controls.Add(manual); root.Controls.Add(manualGroup);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
        buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _launchButton.AutoSize = false; _launchButton.Dock = DockStyle.Fill; _launchButton.BackColor = Color.FromArgb(37, 99, 235); _launchButton.ForeColor = Color.White; _launchButton.FlatStyle = FlatStyle.Flat;
        _restoreButton.AutoSize = false; _restoreButton.Dock = DockStyle.Fill; _restoreButton.BackColor = Color.FromArgb(255, 247, 237); _restoreButton.ForeColor = Color.FromArgb(154, 52, 18); _restoreButton.FlatStyle = FlatStyle.Flat;
        _launchButton.Margin = new Padding(0, 0, 6, 0); _restoreButton.Margin = new Padding(6, 0, 0, 0);
        buttons.Controls.Add(_launchButton); buttons.Controls.Add(_restoreButton); root.Controls.Add(buttons);

        var detailsGroup = new GroupBox { Text = "状态与诊断", Dock = DockStyle.Fill, MinimumSize = new Size(0, 85), Padding = new Padding(10), Margin = new Padding(0, 0, 0, 12) };
        detailsGroup.Controls.Add(_details); root.Controls.Add(detailsGroup);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _closeAfterLaunch.Margin = new Padding(3, 0, 3, 8);
        footer.Controls.Add(_closeAfterLaunch);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        tools.Controls.Add(_shortcutButton); tools.Controls.Add(_updateLink);
        tools.Controls.Add(new Label { Text = "  配置：%LocalAppData%\\ChatGPTTimezoneLauncher", AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 6, 0, 0) });
        footer.Controls.Add(tools);
        root.Controls.Add(footer); Controls.Add(root);
    }

    private async Task DetectAsync()
    {
        SetBusy(true, "正在检测当前网络出口…");
        var result = await _geoIp.DetectAsync();
        SetBusy(false);
        if (result.Success)
        {
            _config.LastSuccessfulAutoDetection = result.Location;
            _configStore.Save(_config);
            ShowLocation(result.Location!, "检测成功");
            var ipError = LaunchProtection.GetIpError(result);
            if (ipError is not null)
            {
                _detectionState.Text = "IP 错误（禁止启动）"; _detectionState.ForeColor = Color.Firebrick;
            }
            _details.Text = ipError ?? $"先通过 {result.Location!.DetectionMethod} 确定 ChatGPT 出口，再由 {result.Location.Provider} 查询该指定 IP。每次启动都会重新探测。";
        }
        else
        {
            _detectionState.Text = "ChatGPT 出口检测失败（未猜测时区）"; _detectionState.ForeColor = Color.Firebrick;
            _details.Text = result.Message + LastSuccessText();
            if (_config.LastSuccessfulAutoDetection is not null) ShowLocationValues(_config.LastSuccessfulAutoDetection);
        }
    }

    private async Task SaveAndLaunchAsync()
    {
        var auto = _autoRadio.Checked;
        var manual = _manualRadio.Checked;
        var manualZone = _timeZoneBox.Text.Trim();
        if (manual && !TimeZoneCatalog.IsValid(manualZone))
        {
            MessageBox.Show(this, "请选择有效的标准 IANA 时区，例如 Asia/Shanghai。", "时区无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true, "启动前检查 ChatGPT 出口 IP 和地区…");
        try
        {
            var guarded = await LaunchProtection.RunAsync(() => _geoIp.DetectAsync(), async location =>
            {
                if (IsDisposed) return null;
                _config.LastSuccessfulAutoDetection = location;
                _config.TimeZoneOverrideEnabled = auto || manual;
                if (auto) _config.Mode = TimeZoneMode.Auto;
                if (manual) { _config.Mode = TimeZoneMode.Manual; _config.ManualTimeZone = manualZone; }
                _configStore.Save(_config); UpdateOverrideState();
                ShowLocation(location, "启动前检测成功");
                var zone = auto ? location.TimeZone : manual ? manualZone : null;
                SetBusy(true, "正在定位 Windows 版 ChatGPT…");
                var discovery = await _discovery.DiscoverAsync();
                if (IsDisposed) return null;
                _details.Text = discovery.Diagnostics;
                if (!discovery.Found)
                    return new LaunchResult(false, false, "未找到 Windows 版 ChatGPT。\r\n\r\n" + discovery.Diagnostics);

                var installation = discovery.Installation!;
                if (_config.TimeZoneOverrideEnabled && _launcher.IsRunning(installation))
                {
                    var answer = MessageBox.Show(this, "ChatGPT 已经在运行。新的时区将在 ChatGPT 重启后生效。\r\n\r\n是否让启动器请求 ChatGPT 正常关闭并重新启动？不会强制结束进程。",
                        "需要重启 ChatGPT", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (answer != DialogResult.Yes) return null;
                    SetBusy(true, "正在等待 ChatGPT 正常退出…");
                    var closed = await _launcher.CloseGracefullyAsync(installation);
                    if (!closed.Success) return new LaunchResult(false, true, closed.Message);
                }

                if (IsDisposed) return null;
                SetBusy(true, "正在启动并验证 ChatGPT…");
                var launch = await _launcher.LaunchAsync(installation, zone);
                // AppsFolder activation only acknowledges the request. Confirm the client before auto-closing.
                if (_config.CloseLauncherAfterLaunch && zone is null && launch.Success &&
                    !await _launcher.WaitForRunningAsync(installation))
                    return new LaunchResult(false, false, "已请求默认启动，但未检测到 ChatGPT 进程。启动器保持打开，请重试。");
                return launch;
            });
            if (IsDisposed) return;
            if (guarded.Location is not null) ShowLocation(guarded.Location, "启动前检测成功");
            if (guarded.IpError is not null)
            {
                _detectionState.Text = "IP 错误（未启动）"; _detectionState.ForeColor = Color.Firebrick;
                _details.Text = guarded.IpError;
                MessageBox.Show(this, guarded.IpError, "IP 错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (guarded.Launch is { } launch)
            {
                _details.Text = launch.Message + Environment.NewLine + _details.Text;
                if (!launch.Success) MessageBox.Show(this, launch.Message, "启动结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (LaunchProtection.ShouldCloseLauncher(_config.CloseLauncherAfterLaunch, launch)) Close();
            }
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void RestoreDefault()
    {
        _config.TimeZoneOverrideEnabled = false;
        _configStore.Save(_config);
        _autoRadio.Checked = false; _manualRadio.Checked = false;
        UpdateOverrideState(); UpdateModeUi();
        _details.Text = "已恢复默认：启动器未修改系统时区、注册表或全局环境变量。之后点击“启动 ChatGPT（默认方式）”将使用标准 AppX 激活且不注入 TZ。保存的手动选择和上次检测结果仍保留，便于以后重新启用。";
        MessageBox.Show(this, "已恢复 ChatGPT 默认启动方式。\r\n\r\n没有修改 Windows 系统时区，也没有删除 ChatGPT 数据。",
            "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadConfigIntoUi()
    {
        _timeZoneBox.Text = _config.ManualTimeZone;
        _closeAfterLaunch.Checked = _config.CloseLauncherAfterLaunch;
        if (_config.TimeZoneOverrideEnabled)
        {
            _autoRadio.Checked = _config.Mode == TimeZoneMode.Auto;
            _manualRadio.Checked = _config.Mode == TimeZoneMode.Manual;
        }
    }

    private void UpdateModeUi()
    {
        _timeZoneBox.Enabled = !_busy && _manualRadio.Checked;
        _detectButton.Enabled = !_busy;
        _launchButton.Text = _autoRadio.Checked || _manualRadio.Checked ? "保存并启动 ChatGPT" : "启动 ChatGPT（默认方式）";
        UpdateOverrideState();
    }

    private void UpdateOverrideState()
    {
        if (!_config.TimeZoneOverrideEnabled)
        {
            _overrideState.Text = "当前：ChatGPT 默认启动方式（不注入 TZ）"; _overrideState.ForeColor = Color.FromArgb(30, 64, 175);
        }
        else
        {
            var zone = _config.Mode == TimeZoneMode.Manual ? _config.ManualTimeZone : _config.LastSuccessfulAutoDetection?.TimeZone ?? "等待检测";
            _overrideState.Text = $"当前：{(_config.Mode == TimeZoneMode.Auto ? "自动时区" : "手动时区")} · {zone}"; _overrideState.ForeColor = Color.FromArgb(21, 128, 61);
        }
    }

    private void ShowLastDetection()
    {
        if (_config.LastSuccessfulAutoDetection is null)
        {
            _detectionState.Text = "尚未检测";
            _ipValue.Text = _proxyGroupValue.Text = _proxyNodeValue.Text = _locationValue.Text = _zoneValue.Text = _providerValue.Text = "—";
        }
        else { ShowLocation(_config.LastSuccessfulAutoDetection, "上次成功结果（仅供参考）"); }
    }

    private void ShowLocation(GeoLocation value, string state)
    {
        _detectionState.Text = state; _detectionState.ForeColor = state.Contains("成功") ? Color.ForestGreen : Color.DimGray;
        ShowLocationValues(value);
    }

    private void ShowLocationValues(GeoLocation value)
    {
        _ipValue.Text = value.Ip;
        _proxyGroupValue.Text = value.ProxyGroup ?? "未读取（检测不依赖 Clash API）";
        _proxyNodeValue.Text = value.ProxyNode ?? "未读取（检测不依赖 Clash API）";
        _locationValue.Text = value.LocationText; _zoneValue.Text = value.TimeZone;
        _providerValue.Text = $"{value.DetectionMethod} · {value.Provider} · {value.DetectedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private string LastSuccessText() => _config.LastSuccessfulAutoDetection is { } last
        ? $"\r\n\r\n保留的上次成功结果（未自动使用）：{last.Ip} / {last.LocationText} / {last.TimeZone}"
        : "\r\n\r\n没有可显示的历史成功结果。";

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        UseWaitCursor = busy; _detectButton.Enabled = !busy; _launchButton.Enabled = !busy; _restoreButton.Enabled = !busy;
        _autoRadio.Enabled = _manualRadio.Enabled = _closeAfterLaunch.Enabled = !busy;
        _timeZoneBox.Enabled = !busy && _manualRadio.Checked;
        _updateLink.Enabled = !busy && !_checkingUpdate;
        if (busy && text is not null) { _detectionState.Text = text; _detectionState.ForeColor = Color.DarkOrange; }
    }

    private void CreateShortcut()
    {
        try { var path = ShortcutService.CreateDesktopShortcut(); _details.Text = $"已创建桌面快捷方式：{path}"; }
        catch (Exception ex) { MessageBox.Show(this, $"创建快捷方式失败：{ex.Message}", "创建失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task CheckUpdateAsync(bool manual)
    {
        if (_checkingUpdate || _busy) return;
        _checkingUpdate = true; _updateLink.Enabled = false;
        try
        {
            var current = typeof(MainForm).Assembly.GetName().Version!;
            var update = await _updates.CheckAsync(current);
            if (IsDisposed) return;
            _updateLink.Text = update is null ? "↻ 更新" : "↻ 有更新";
            _updateLink.LinkColor = update is null ? Color.DimGray : Color.FromArgb(37, 99, 235);
            _toolTip.SetToolTip(_updateLink, update is null ? "检查并下载新版启动器" : $"可下载 v{update.Version}");
            if (!manual) return;
            if (update is null)
            {
                MessageBox.Show(this, $"当前已是最新版本（v{current.ToString(3)}）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, $"发现新版 v{update.Version}，现在下载吗？", "启动器更新",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            _updateLink.Text = "↻ 下载中";
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGPTTimezoneLauncher", "Updates");
            var path = await _updates.DownloadAsync(update, directory);
            if (IsDisposed) return;
            _details.Text = $"新版已下载并通过校验：{path}\r\n关闭启动器后，用这个文件替换旧程序即可，设置会保留。";
            MessageBox.Show(this, "新版已下载，关闭启动器后替换旧程序即可。设置会保留。", "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.Arguments = $"/select,\"{path}\"";
            Process.Start(info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed)
            {
                _toolTip.SetToolTip(_updateLink, "检查更新失败，点击重试");
                if (manual) MessageBox.Show(this, $"更新失败：{ex.Message}\r\n稍后点击更新重试。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _checkingUpdate = false;
            if (!IsDisposed) { if (_updateLink.Text == "↻ 下载中") _updateLink.Text = "↻ 有更新"; _updateLink.Enabled = !_busy; }
        }
    }

    private static Label ValueLabel() => new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
    private static void AddRow(TableLayoutPanel panel, int row, string key, Control value)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = key + "：", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 3, 0, 5) }, 0, row);
        value.Margin = new Padding(0, 3, 0, 5); panel.Controls.Add(value, 1, row);
    }
}
