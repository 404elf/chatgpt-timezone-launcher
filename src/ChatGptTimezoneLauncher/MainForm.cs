using System.Drawing;
using System.Diagnostics;

namespace ChatGptTimezoneLauncher;

public sealed class MainForm : Form
{
    private readonly ConfigStore _configStore;
    private readonly GeoIpService _geoIp;
    private readonly ChatGptDiscovery _discovery = new();
    private readonly ChatGptLauncher _launcher = new();
    private readonly UpdateService _updates = new();
    private readonly ToolTip _toolTip = new();
    private bool _busy;
    private bool _checkingUpdate;
    private CancellationTokenSource? _detectionCancellation;
    private readonly Stopwatch _detectionWatch = new();
    private readonly System.Windows.Forms.Timer _detectionTimer = new() { Interval = 1000 };
    private string _detectionLog = "";
    private string _detectionStep = "获取出口 IP";
    private LauncherConfig _config;
    private Panel _manualPanel = null!;
    private Panel _detailsPanel = null!;

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

    public MainForm(ConfigStore? configStore = null, bool checkUpdates = true, GeoIpService? geoIp = null)
    {
        _geoIp = geoIp ?? new GeoIpService();
        _configStore = configStore ?? new ConfigStore();
        var loaded = _configStore.Load();
        _config = loaded.Config;
        Text = "ChatGPT 时区启动器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 540);
        ClientSize = new Size(660, 540);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = UiArtwork.LoadIcon();

        BuildUi();
        LoadConfigIntoUi();
        ShowLastDetection();
        UpdateModeUi();

        _autoRadio.CheckedChanged += (_, _) => UpdateModeUi();
        _manualRadio.CheckedChanged += (_, _) => UpdateModeUi();
        _detectButton.Click += async (_, _) =>
        {
            if (_detectionCancellation is not null)
            {
                _detectButton.Enabled = false;
                _detectionState.Text = "正在取消检测…";
                _detectionCancellation.Cancel();
                return;
            }
            await DetectAsync();
        };
        _detectionTimer.Tick += (_, _) => UpdateDetectionProgress();
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
        BackColor = Color.FromArgb(244, 246, 252);
        ForeColor = Color.FromArgb(31, 39, 62);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7, AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 18) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new PictureBox { Image = UiArtwork.LoadImage(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0) }, 0, 0);
        var heading = new Panel { Dock = DockStyle.Fill, Height = 58, Margin = new Padding(0) };
        heading.Controls.Add(new Label { Text = "ChatGPT 时区启动器", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), Dock = DockStyle.Top, Height = 32, AutoEllipsis = true });
        heading.Controls.Add(new Label { Text = "跟随出口时区，轻松启动", ForeColor = Color.DimGray, Dock = DockStyle.Bottom, Height = 24 });
        header.Controls.Add(heading, 1, 0);
        _updateLink.Margin = new Padding(8, 12, 0, 0);
        header.Controls.Add(_updateLink, 2, 0);
        root.Controls.Add(header, 0, 0);

        var networkCard = new UiCard { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(18, 16, 18, 16), Margin = new Padding(0, 0, 0, 14) };
        var network = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        network.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        network.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _detectionState.AutoSize = true; _detectionState.Dock = DockStyle.Top;
        _detectionState.Margin = new Padding(0, 0, 0, 8);
        network.Controls.Add(_detectionState, 0, 0); network.SetColumnSpan(_detectionState, 2);
        _ipValue.AutoSize = false; _ipValue.Dock = DockStyle.Fill; _ipValue.Height = 38;
        _ipValue.Font = new Font(Font.FontFamily, 17, FontStyle.Bold); _ipValue.AutoEllipsis = true;
        _ipValue.Margin = new Padding(0, 0, 8, 3);
        network.Controls.Add(_ipValue, 0, 1);
        StyleButton(_detectButton, false); _detectButton.Text = "重新检测"; _detectButton.AutoSize = false;
        _detectButton.Size = new Size(104, 34); _detectButton.Margin = new Padding(0, 0, 0, 3);
        network.Controls.Add(_detectButton, 1, 1);
        var location = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0), WrapContents = true };
        _locationValue.Font = _zoneValue.Font = Font;
        _locationValue.ForeColor = _zoneValue.ForeColor = Color.FromArgb(90, 102, 127);
        _locationValue.Margin = new Padding(0, 3, 14, 0); _zoneValue.Margin = new Padding(0, 3, 0, 0);
        location.Controls.Add(_locationValue); location.Controls.Add(_zoneValue);
        network.Controls.Add(location, 0, 2); network.SetColumnSpan(location, 2);
        networkCard.Controls.Add(network); root.Controls.Add(networkCard, 0, 1);

        var modeCard = new UiCard { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(18, 14, 18, 14), Margin = new Padding(0, 0, 0, 14) };
        var modes = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _overrideState.AutoSize = true; _overrideState.Dock = DockStyle.Top; _overrideState.Margin = new Padding(0, 0, 0, 10);
        _overrideState.ForeColor = Color.DimGray;
        modes.Controls.Add(_overrideState);
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0), WrapContents = true };
        _autoRadio.Text = "自动跟随出口"; _manualRadio.Text = "手动选择时区";
        _autoRadio.Margin = new Padding(0, 0, 24, 0); _manualRadio.Margin = new Padding(0);
        choices.Controls.Add(_autoRadio); choices.Controls.Add(_manualRadio); modes.Controls.Add(choices);
        _manualPanel = new Panel { Dock = DockStyle.Top, Height = 46, Margin = new Padding(0, 10, 0, 0) };
        _timeZoneBox.Dock = DockStyle.Top; _timeZoneBox.DropDownStyle = ComboBoxStyle.DropDown;
        _timeZoneBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _timeZoneBox.AutoCompleteSource = AutoCompleteSource.ListItems;
        _timeZoneBox.MaxDropDownItems = 12; _timeZoneBox.Items.AddRange(TimeZoneCatalog.All.Cast<object>().ToArray());
        _manualPanel.Controls.Add(_timeZoneBox); modes.Controls.Add(_manualPanel);
        modeCard.Controls.Add(modes); root.Controls.Add(modeCard, 0, 2);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        buttons.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        StyleButton(_launchButton, true); StyleButton(_restoreButton, false);
        _launchButton.Dock = _restoreButton.Dock = DockStyle.Fill; _launchButton.AutoSize = _restoreButton.AutoSize = false;
        _launchButton.Margin = new Padding(0, 0, 8, 0); _restoreButton.Margin = new Padding(0);
        _restoreButton.Text = "恢复默认方式";
        buttons.Controls.Add(_launchButton); buttons.Controls.Add(_restoreButton); root.Controls.Add(buttons, 0, 3);

        var diagnostics = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 12) };
        diagnostics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var detailsLink = new LinkLabel { Text = "检测详情 ▾", AutoSize = true, LinkColor = Color.DimGray, Margin = new Padding(0, 0, 0, 5) };
        _details.Font = new Font("Consolas", 9); _details.BorderStyle = BorderStyle.None; _details.BackColor = Color.White;
        _detailsPanel = new UiCard { Dock = DockStyle.Top, Height = 134, Padding = new Padding(12), Visible = false, Margin = new Padding(0) };
        _detailsPanel.Controls.Add(_details);
        detailsLink.LinkClicked += (_, _) =>
        {
            _detailsPanel.Visible = !_detailsPanel.Visible;
            detailsLink.Text = _detailsPanel.Visible ? "收起详情 ▴" : "检测详情 ▾";
        };
        diagnostics.Controls.Add(detailsLink); diagnostics.Controls.Add(_detailsPanel); root.Controls.Add(diagnostics, 0, 4);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _closeAfterLaunch.Margin = new Padding(0, 2, 0, 10); footer.Controls.Add(_closeAfterLaunch);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        _shortcutButton.Text = "创建桌面入口"; _shortcutButton.FlatStyle = FlatStyle.Flat;
        _shortcutButton.FlatAppearance.BorderSize = 0; _shortcutButton.ForeColor = Color.DimGray; _shortcutButton.Margin = new Padding(0, 0, 14, 0);
        tools.Controls.Add(_shortcutButton);
        tools.Controls.Add(new Label { Text = "v" + typeof(MainForm).Assembly.GetName().Version!.ToString(3) + " · 系统时区不变", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 7, 0, 0) });
        footer.Controls.Add(tools); root.Controls.Add(footer, 0, 5);
        Controls.Add(root);

        root.SizeChanged += (_, _) =>
        {
            var width = Math.Max(180, root.ClientSize.Width - root.Padding.Horizontal - 40);
            _detectionState.MaximumSize = _overrideState.MaximumSize = new Size(width, 0);
            _locationValue.MaximumSize = _zoneValue.MaximumSize = new Size(width, 0);
        };
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(215, 222, 241);
        button.BackColor = primary ? Color.FromArgb(72, 93, 224) : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(72, 85, 121);
        button.Cursor = Cursors.Hand;
    }

    private async Task DetectAsync()
    {
        var watch = Stopwatch.StartNew();
        DetectionResult result;
        try { result = await DetectCurrentExitAsync(); }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) ShowDetectionCancelled();
            return;
        }
        finally { if (!IsDisposed) SetBusy(false); }
        if (IsDisposed) return;
        if (result.Success)
        {
            ShowLocation(result.Location!, $"检测成功 · {watch.Elapsed.TotalSeconds:0.0} 秒");
            var ipError = LaunchProtection.GetIpError(result);
            if (ipError is not null)
            {
                _detectionState.Text = "IP 错误（禁止启动）"; _detectionState.ForeColor = Color.Firebrick;
            }
            else
            {
                _config.LastSuccessfulAutoDetection = result.Location;
                _configStore.Save(_config);
            }
            _details.Text = (ipError ?? $"先通过 {result.Location!.DetectionMethod} 确定 ChatGPT 出口，再由 {result.Location.Provider} 查询该指定 IP。每次启动都会重新探测。")
                + "\r\n\r\n" + _detectionLog;
        }
        else
        {
            _detectionState.Text = "ChatGPT 出口检测失败（未猜测时区）"; _detectionState.ForeColor = Color.Firebrick;
            _details.Text = result.Message + "\r\n\r\n" + _detectionLog + LastSuccessText();
            _ipValue.Text = "无法确认出口"; _locationValue.Text = _zoneValue.Text = "—";
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

        try
        {
            var guarded = await LaunchProtection.RunAsync(DetectCurrentExitAsync, async location =>
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
                _details.Text = _detectionLog + Environment.NewLine + discovery.Diagnostics;
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
                if (guarded.Location is null)
                {
                    _ipValue.Text = "无法确认出口";
                    _locationValue.Text = _zoneValue.Text = "—";
                }
                _details.Text = guarded.IpError + "\r\n\r\n" + _detectionLog;
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
        catch (OperationCanceledException) { if (!IsDisposed) ShowDetectionCancelled(); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task<DetectionResult> DetectCurrentExitAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _detectionCancellation = cancellation;
        _detectionLog = "";
        _detectionStep = "获取出口 IP";
        _details.Clear();
        _detectionWatch.Restart();
        SetBusy(true, "正在检测当前出口…");
        _detectionTimer.Start();
        try
        {
            var result = await _geoIp.DetectAsync(cancellation.Token, message =>
            {
                if (IsDisposed) return;
                _detectionLog += message + Environment.NewLine;
                _details.Text = _detectionLog;
                if (message.StartsWith("2/2")) _detectionStep = "查询地区和时区";
            });
            cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _detectionWatch.Stop();
            _detectionCancellation = null;
            if (!IsDisposed)
            {
                _detectionTimer.Stop();
                _detectButton.Text = "重新检测";
                _detectButton.Enabled = !_busy;
            }
        }
    }

    private void UpdateDetectionProgress()
    {
        if (_detectionCancellation is null || _detectionCancellation.IsCancellationRequested) return;
        var seconds = (int)_detectionWatch.Elapsed.TotalSeconds;
        _detectionState.Text = seconds >= 7 ? $"网络较慢，继续检测 · 已等待 {seconds} 秒" : $"正在{_detectionStep} · {seconds} 秒";
        if (seconds >= 7) _locationValue.Text = $"正在{_detectionStep}，可取消检测";
    }

    private void ShowDetectionCancelled()
    {
        _detectionState.Text = "已取消检测（未启动）";
        _detectionState.ForeColor = Color.DimGray;
        _ipValue.Text = "检测已取消";
        _locationValue.Text = _zoneValue.Text = "—";
        _details.Text = "检测已取消，没有使用历史 IP，也没有请求启动或关闭 ChatGPT。可切换网络后重新检测。\r\n\r\n" + _detectionLog;
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
        _manualPanel.Visible = _manualRadio.Checked;
        _timeZoneBox.Enabled = !_busy && _manualRadio.Checked;
        _detectButton.Enabled = !_busy;
        _launchButton.Text = "启动 ChatGPT";
        UpdateOverrideState();
    }

    private void UpdateOverrideState()
    {
        if (!_autoRadio.Checked && !_manualRadio.Checked)
        {
            _overrideState.Text = "默认方式 · 不覆盖时区"; _overrideState.ForeColor = Color.FromArgb(72, 85, 121);
        }
        else
        {
            _overrideState.Text = _autoRadio.Checked ? "自动时区 · 启动前重新检测" : "手动时区 · 出口地区仍会检查";
            _overrideState.ForeColor = Color.FromArgb(72, 85, 121);
        }
    }

    private void ShowLastDetection()
    {
        if (_config.LastSuccessfulAutoDetection is null)
        {
            _detectionState.Text = "尚未检测";
            _ipValue.Text = "尚未检测";
            _proxyGroupValue.Text = _proxyNodeValue.Text = _locationValue.Text = _zoneValue.Text = _providerValue.Text = "—";
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
        _locationValue.Text = value.LocationText; _zoneValue.Text = string.IsNullOrWhiteSpace(value.TimeZone) ? "地区不支持" : value.TimeZone;
        _toolTip.SetToolTip(_ipValue, value.Ip);
        _providerValue.Text = $"{value.DetectionMethod} · {value.Provider} · {value.DetectedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private string LastSuccessText() => _config.LastSuccessfulAutoDetection is { } last
        ? $"\r\n\r\n保留的上次成功结果（未自动使用）：{last.Ip} / {last.LocationText} / {last.TimeZone}"
        : "\r\n\r\n没有可显示的历史成功结果。";

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        UseWaitCursor = busy && _detectionCancellation is null;
        _detectButton.Text = _detectionCancellation is null ? "重新检测" : "取消检测";
        _detectButton.Enabled = !busy || _detectionCancellation is { IsCancellationRequested: false };
        _launchButton.Enabled = !busy; _restoreButton.Enabled = !busy;
        _autoRadio.Enabled = _manualRadio.Enabled = _closeAfterLaunch.Enabled = !busy;
        _timeZoneBox.Enabled = !busy && _manualRadio.Checked;
        _updateLink.Enabled = !busy && !_checkingUpdate;
        if (busy && text is not null)
        {
            _detectionState.Text = text; _detectionState.ForeColor = Color.DarkOrange;
            if (text.Contains("出口") || text.Contains("当前网络"))
                _ipValue.Text = _locationValue.Text = _zoneValue.Text = "检测中…";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detectionCancellation?.Cancel();
            _detectionTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
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
