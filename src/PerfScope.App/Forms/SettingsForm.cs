using PerfScope.App.Localization;
using System.Diagnostics;
using PerfScope.App.Infrastructure;
using PerfScope.Core;
using PerfScope.Core.Settings;

namespace PerfScope.App.Forms;

/// <summary>
/// 设置页：覆盖 <see cref="AppSettings"/> 的全部字段，保存时先做中文校验，
/// 通过后交给 <see cref="AppHost.ApplySettings"/> 落盘并热生效（引擎、历史、开机启动同步）。
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly string[] CpuModeTexts = { Loc.T("整机百分比（0~100，同任务管理器）"), Loc.T("单核百分比（单线程跑满 = 100）") };
    private static readonly string[] MemoryModeTexts = { Loc.T("工作集（物理驻留）"), Loc.T("私有字节（更接近真实占用）") };
    private static readonly string[] CloseBehaviorTexts = { Loc.T("最小化到托盘"), Loc.T("直接退出程序") };
    private static readonly string[] ThemeTexts = { Loc.T("深色"), Loc.T("浅色") };
    private static readonly string[] LanguageTexts = { Loc.T("跟随系统"), "中文", "English" };
    private static readonly string[] LogLevelTexts = { Loc.T("错误"), Loc.T("警告"), Loc.T("信息"), Loc.T("调试") };

    private readonly AppHost _host;
    private readonly Theme _theme;
    private readonly float _scale;
    private readonly AppSettings _working;
    private readonly List<AppRule> _rules;

    private readonly NumericUpDown _sampleInterval;
    private readonly NumericUpDown _uiInterval;
    private readonly CheckBox _adaptive;
    private readonly NumericUpDown _hiddenInterval;
    private readonly NumericUpDown _smoothing;

    private readonly ComboBox _cpuMode;
    private readonly ComboBox _memoryMode;

    private readonly CheckBox _groupByProduct;
    private readonly CheckBox _groupByInstallRoot;
    private readonly CheckBox _inheritParent;
    private readonly CheckBox _mergeSystem;
    private readonly CheckBox _versionCache;
    private readonly TextBox _unknownGroup;
    private readonly TextBox _systemGroup;
    private readonly ThemedListView _ruleList;
    private bool _suppressRuleEvents;

    private readonly NumericUpDown _memoryRetention;
    private readonly NumericUpDown _diskRetention;
    private readonly NumericUpDown _trendSlots;
    private readonly CheckBox _diskHistory;
    private readonly CheckBox _insightEnabled;
    private readonly NumericUpDown _insightCpuPercent;
    private readonly NumericUpDown _insightCpuSeconds;
    private readonly NumericUpDown _insightGrowth;
    private readonly NumericUpDown _insightDigest;

    private readonly NumericUpDown _topN;
    private readonly CheckBox _startMinimized;
    private readonly CheckBox _runAtStartup;
    private readonly ComboBox _closeBehavior;
    private readonly CheckBox _showSelfUsage;
    private readonly CheckBox _showSystemGroups;
    private readonly ComboBox _themeBox;
    private readonly ComboBox _languageBox;

    private readonly CheckBox _enableAlerts;
    private readonly NumericUpDown _alertCpu;
    private readonly NumericUpDown _alertMemory;
    private readonly NumericUpDown _alertCooldown;

    private readonly ComboBox _logLevel;
    private readonly NumericUpDown _logMaxKb;
    private readonly Button _exportDiagnosticsButton;
    private readonly CheckBox _updateCheck;

    private bool _disposed;

    /// <summary>创建设置页。</summary>
    public SettingsForm(AppHost host)
    {
        _host = host;
        _scale = Theme.DetectDpiScale();
        _working = host.Settings.Clone();
        _rules = _working.Rules.Select(rule => rule.Clone()).ToList();
        _theme = Theme.Create(_working.Theme, _scale);

        Text = Loc.T("PerfScope 设置");
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;
        ClientSize = new Size(Scaled(760), Scaled(620));
        MinimumSize = new Size(Scaled(640), Scaled(480));

        // ---------- 控件实例化 ----------
        _sampleInterval = Num(500, 10000, 100, _working.SampleIntervalMs);
        _uiInterval = Num(250, 10000, 50, _working.UiRefreshIntervalMs);
        _adaptive = Check(Loc.T("主窗口隐藏时自动降频"), _working.AdaptiveThrottleWhenHidden);
        _hiddenInterval = Num(1000, 60000, 500, _working.HiddenSampleIntervalMs);
        _smoothing = Num(1, 15, 1, _working.SmoothingWindow);

        _cpuMode = Combo(CpuModeTexts, (int)_working.CpuMode);
        _memoryMode = Combo(MemoryModeTexts, (int)_working.MemoryMode);

        _groupByProduct = Check(Loc.T("按版本信息里的产品名归并"), _working.GroupByProductName);
        _groupByInstallRoot = Check(Loc.T("按安装根目录归并"), _working.GroupByInstallRoot);
        _inheritParent = Check(Loc.T("子进程继承父进程的应用归属"), _working.InheritParentGroup);
        _mergeSystem = Check(Loc.T("把系统进程合并为功能模块（WSL / 图形 / 安全 / 内核…）"), _working.MergeWindowsSystemProcesses);
        _versionCache = Check(Loc.T("缓存 exe 版本信息与目录解析结果"), _working.UseVersionInfoCache);
        _unknownGroup = Input(_working.UnknownGroupName);
        _systemGroup = Input(_working.SystemGroupName);
        _ruleList = BuildRuleList();

        _memoryRetention = Num(5, 1440, 5, _working.MemoryRetentionMinutes);
        _diskRetention = Num(0, 365, 1, _working.DiskRetentionDays);
        _trendSlots = Num(1, 64, 1, _working.TrendAppSlots);
        _diskHistory = Check(Loc.T("把历史写入磁盘（关闭后仅内存保留）"), _working.EnableDiskHistory);
        _insightEnabled = Check(Loc.T("持续产出分析历史（事件流 + 报告）"), _working.InsightEnabled);
        _insightCpuPercent = Num(1, 100, 1, _working.InsightAppCpuPercent);
        _insightCpuSeconds = Num(5, 600, 5, _working.InsightAppCpuSeconds);
        _insightGrowth = Num(5, 10000, 5, _working.InsightMemoryGrowthMbPerMinute);
        _insightDigest = Num(1, 60, 1, _working.InsightDigestMinutes);

        _topN = Num(5, 200, 1, _working.TopN);
        _startMinimized = Check(Loc.T("启动时最小化到托盘"), _working.StartMinimized);
        _runAtStartup = Check(Loc.T("开机自动启动（写入当前用户注册表）"), _working.RunAtStartup);
        _closeBehavior = Combo(CloseBehaviorTexts, (int)_working.CloseBehavior);
        _showSelfUsage = Check(Loc.T("在状态栏显示监控器自身开销"), _working.ShowSelfUsage);
        _showSystemGroups = Check(Loc.T("在列表中显示系统模块与系统进程组"), _working.ShowSystemProcesses);
        _themeBox = Combo(ThemeTexts, string.Equals(_working.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        _languageBox = Combo(LanguageTexts, LanguageIndex(_working.Language));

        _enableAlerts = Check(Loc.T("启用阈值气泡告警"), _working.EnableAlerts);
        _alertCpu = Num(10, 100, 1, _working.AlertCpuPercent);
        _alertMemory = Num(10, 100, 1, _working.AlertMemoryPercent);
        _alertCooldown = Num(30, 3600, 30, _working.AlertCooldownSeconds);

        _logLevel = Combo(LogLevelTexts, (int)_working.LogLevel);
        _logMaxKb = Num(64, 10240, 64, _working.LogMaxKilobytes);
        _updateCheck = Check(Loc.T("自动检查更新（唯一的网络访问）"), _working.UpdateCheckEnabled);
        _exportDiagnosticsButton = new Button { Text = Loc.T("导出诊断包…"), Width = Scaled(160), Height = Scaled(28) };
        _exportDiagnosticsButton.Click += (_, _) => ExportDiagnostics();

        // ---------- 布局 ----------
        var tabs = new ThemedTabControl(_theme)
        {
            Dock = DockStyle.Fill,
            Font = _theme.UiFont,
        };

        var samplingPage = new TabPage(Loc.T("采样")) { BackColor = _theme.Background };
        var sampling = CreateContent(samplingPage);
        AddSectionTitle(sampling, Loc.T("采样节奏"));
        AddRow(sampling, Loc.T("采样间隔（毫秒）"), _sampleInterval);
        AddRow(sampling, Loc.T("界面刷新间隔（毫秒）"), _uiInterval);
        AddSectionTitle(sampling, Loc.T("隐藏窗口时降频（托盘常驻省电省 CPU）"));
        AddRow(sampling, Loc.T("自适应降频"), _adaptive);
        AddRow(sampling, Loc.T("隐藏时采样间隔（毫秒）"), _hiddenInterval);
        AddSectionTitle(sampling, Loc.T("平滑"));
        AddRow(sampling, Loc.T("平滑窗口（采样点数）"), _smoothing);
        tabs.TabPages.Add(samplingPage);

        var caliberPage = new TabPage(Loc.T("口径")) { BackColor = _theme.Background };
        var caliber = CreateContent(caliberPage);
        AddSectionTitle(caliber, Loc.T("CPU 与内存的展示口径"));
        AddRow(caliber, Loc.T("CPU 口径"), _cpuMode);
        AddRow(caliber, Loc.T("内存口径"), _memoryMode);
        tabs.TabPages.Add(caliberPage);

        var groupingPage = new TabPage(Loc.T("归并")) { BackColor = _theme.Background };
        var grouping = CreateContent(groupingPage);
        AddSectionTitle(grouping, Loc.T("应用归并策略"));
        AddRow(grouping, Loc.T("产品名归并"), _groupByProduct);
        AddRow(grouping, Loc.T("安装目录归并"), _groupByInstallRoot);
        AddRow(grouping, Loc.T("父进程继承"), _inheritParent);
        AddRow(grouping, Loc.T("合并系统进程"), _mergeSystem);
        AddRow(grouping, Loc.T("版本信息缓存"), _versionCache);
        AddRow(grouping, Loc.T("兜底组名"), _unknownGroup);
        AddRow(grouping, Loc.T("系统组名"), _systemGroup);
        AddSectionTitle(grouping, Loc.T("自定义规则（按顺序优先匹配，双击可编辑）"));
        AddWideControl(grouping, BuildRuleEditorPanel(), Scaled(232));
        tabs.TabPages.Add(groupingPage);

        var retentionPage = new TabPage(Loc.T("留存")) { BackColor = _theme.Background };
        var retention = CreateContent(retentionPage);
        AddSectionTitle(retention, Loc.T("历史与趋势留存"));
        AddRow(retention, Loc.T("内存保留（分钟）"), _memoryRetention);
        AddRow(retention, Loc.T("磁盘保留（天）"), _diskRetention);
        AddRow(retention, Loc.T("趋势应用数"), _trendSlots);
        AddRow(retention, Loc.T("写入磁盘"), _diskHistory);
        AddSectionTitle(retention, Loc.T("智能分析（产出可被 AI 直接消费的历史）"));
        AddRow(retention, Loc.T("启用分析"), _insightEnabled);
        AddRow(retention, Loc.T("CPU 片段阈值（%）"), _insightCpuPercent);
        AddRow(retention, Loc.T("片段最短时长（秒）"), _insightCpuSeconds);
        AddRow(retention, Loc.T("内存增长阈值（MB/分）"), _insightGrowth);
        AddRow(retention, Loc.T("摘要窗口（分钟）"), _insightDigest);
        tabs.TabPages.Add(retentionPage);

        var uiPage = new TabPage(Loc.T("界面")) { BackColor = _theme.Background };
        var ui = CreateContent(uiPage);
        AddSectionTitle(ui, Loc.T("主窗口"));
        AddRow(ui, Loc.T("列表显示行数"), _topN);
        AddRow(ui, Loc.T("显示自身开销"), _showSelfUsage);
        AddRow(ui, Loc.T("显示系统模块组"), _showSystemGroups);
        AddRow(ui, Loc.T("主题"), _themeBox);
        AddRow(ui, Loc.T("语言"), _languageBox);
        var tipsButton = new Button { Text = Loc.T("重新查看新手引导"), Width = Scaled(180), Height = Scaled(28) };
        tipsButton.Click += (_, _) =>
        {
            using var tips = new FirstRunTipsForm(_working);
            tips.ShowDialog(this);
        };
        AddRow(ui, Loc.T("新手引导"), tipsButton);
        AddSectionTitle(ui, Loc.T("启动与关闭"));
        AddRow(ui, Loc.T("启动最小化"), _startMinimized);
        AddRow(ui, Loc.T("开机自动启动"), _runAtStartup);
        AddRow(ui, Loc.T("关闭按钮行为"), _closeBehavior);
        tabs.TabPages.Add(uiPage);

        var alertPage = new TabPage(Loc.T("告警")) { BackColor = _theme.Background };
        var alert = CreateContent(alertPage);
        AddSectionTitle(alert, Loc.T("阈值气泡告警（托盘气泡）"));
        AddRow(alert, Loc.T("启用告警"), _enableAlerts);
        AddRow(alert, Loc.T("CPU 阈值（%）"), _alertCpu);
        AddRow(alert, Loc.T("内存阈值（%）"), _alertMemory);
        AddRow(alert, Loc.T("冷却时间（秒）"), _alertCooldown);
        tabs.TabPages.Add(alertPage);

        var diagnosticsPage = new TabPage(Loc.T("诊断")) { BackColor = _theme.Background };
        var diagnostics = CreateContent(diagnosticsPage);
        AddSectionTitle(diagnostics, Loc.T("日志"));
        AddSectionTitle(diagnostics, Loc.T("更新"));
        AddRow(diagnostics, Loc.T("自动检查更新（唯一的网络访问）"), _updateCheck);
        AddRow(diagnostics, Loc.T("日志级别（重启后生效）"), _logLevel);
        AddRow(diagnostics, Loc.T("日志大小上限（KB，重启后生效）"), _logMaxKb);
        AddWideControl(diagnostics, new Label
        {
            AutoSize = true,
            ForeColor = _theme.TextMuted,
            Font = _theme.SmallFont,
            Text = Loc.T("说明：日志文件由 Core 的 FileLog 持有，没有热更新通道，改动会在下次启动时生效；其余设置保存后立即生效。"),
        }, Scaled(34));
        AddSectionTitle(diagnostics, Loc.T("数据目录"));
        var openFolder = new Button { Text = Loc.T("打开数据目录"), Width = Scaled(140), Height = Scaled(30) };
        openFolder.Click += OnOpenDataDirectory;
        AddRow(diagnostics, Loc.T("导出诊断包…"), _exportDiagnosticsButton);
        AddRow(diagnostics, Loc.T("数据位置"), openFolder);
        var pathHint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(Scaled(520), 0),
            ForeColor = _theme.TextMuted,
            Font = _theme.SmallFont,
            Text = Loc.T("设置：") + _host.SettingsStore.SettingsPath + Environment.NewLine + Loc.T("数据根：") + AppPaths.DataDirectory,
        };
        AddWideControl(diagnostics, pathHint, Scaled(52));
        tabs.TabPages.Add(diagnosticsPage);

        if (UiDebug.SettingsTabIndex >= 0 && UiDebug.SettingsTabIndex < tabs.TabPages.Count)
        {
            tabs.SelectedIndex = UiDebug.SettingsTabIndex;
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Height = Scaled(56),
            Padding = new Padding(Scaled(16), Scaled(10), Scaled(16), Scaled(10)),
            BackColor = _theme.Background,
        };

        var saveButton = new Button { Text = Loc.T("保存"), Width = Scaled(96), Height = Scaled(32) };
        saveButton.Click += OnSave;
        var cancelButton = new Button { Text = Loc.T("取消"), Width = Scaled(96), Height = Scaled(32), DialogResult = DialogResult.Cancel };
        var resetButton = new Button { Text = Loc.T("恢复默认"), Width = Scaled(110), Height = Scaled(32) };
        resetButton.Click += OnResetDefaults;
        actions.Controls.Add(saveButton);
        actions.Controls.Add(cancelButton);
        actions.Controls.Add(resetButton);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = _theme.Background };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        FormStyler.Apply(this, _theme);
        RefreshRuleList(0);
    }

    private int Scaled(int value) => (int)Math.Round(value * _scale);

    private Size Scaled(int width, int height) => new(Scaled(width), Scaled(height));

    private static int LanguageIndex(string? code) => code switch
    {
        "zh" => 1,
        "en" => 2,
        _ => 0,
    };

    private static string LanguageCode(int index) => index switch
    {
        1 => "zh",
        2 => "en",
        _ => "system",
    };

    /// <summary>导出脱敏诊断包并打开所在目录。</summary>
    private void ExportDiagnostics()
    {
        try
        {
            string? path = PerfScope.Core.Diagnostics.DiagnosticBundle.Create(PerfScope.Core.AppPaths.DataDirectory);
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this, Loc.T("导出失败，详情见日志。"), Loc.T("导出诊断包…"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("导出诊断包…"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private NumericUpDown Num(decimal minimum, decimal maximum, decimal step, decimal value) => new ThemedNumericUpDown(_theme)
    {
        Minimum = minimum,
        Maximum = maximum,
        Increment = step,
        Value = Math.Clamp(value, minimum, maximum),
        Width = Scaled(140),
        Height = Scaled(26),
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
    };

    private TextBox Input(string value) => new()
    {
        Text = value,
        Width = Scaled(300),
    };

    private ComboBox Combo(string[] items, int index)
    {
        var combo = new ThemedComboBox(_theme)
        {
            Width = Scaled(300),
        };
        combo.Items.AddRange(items);
        combo.SelectedIndex = Math.Clamp(index, 0, items.Length - 1);
        return combo;
    }

    private TableLayoutPanel CreateContent(TabPage page)
    {
        // 用原生 AutoScroll：自绘滚动容器的内容高度测量在 TableLayoutPanel + Dock.Top 组合下不稳定，
        // 先保证「能滚动、内容可达」，滚动条样式统一留作后续工作。
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = _theme.Background,
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = _theme.Background,
            Padding = new Padding(Scaled(18), Scaled(12), Scaled(18), Scaled(16)),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        scroll.Controls.Add(content);
        page.Controls.Add(scroll);
        return content;
    }

    private void AddSectionTitle(TableLayoutPanel panel, string title)
    {
        int row = panel.RowCount;
        panel.RowCount = row + 1;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = title,
            AutoSize = true,
            Font = _theme.UiBoldFont,
            ForeColor = _theme.TextSecondary,
            Margin = new Padding(0, Scaled(10), 0, Scaled(4)),
        };
        panel.Controls.Add(label, 0, row);
        panel.SetColumnSpan(label, 2);
    }

    private void AddRow(TableLayoutPanel panel, string label, Control control)
    {
        int row = panel.RowCount;
        panel.RowCount = row + 1;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, Scaled(8), Scaled(18), Scaled(4)),
        };
        control.Margin = new Padding(0, Scaled(4), 0, Scaled(4));

        panel.Controls.Add(caption, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private void AddWideControl(TableLayoutPanel panel, Control control, int height)
    {
        int row = panel.RowCount;
        panel.RowCount = row + 1;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, Scaled(4), 0, Scaled(8));
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private ThemedListView BuildRuleList()
    {
        var list = new ThemedListView(_theme)
        {
            CheckBoxes = true,
            Font = _theme.SmallFont,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        list.Columns.Add(Loc.T("启用"), Scaled(62));
        list.Columns.Add(Loc.T("匹配方式"), Scaled(100));
        list.Columns.Add(Loc.T("模式"), Scaled(200));
        list.Columns.Add(Loc.T("动作"), Scaled(100));
        list.Columns.Add(Loc.T("目标"), Scaled(150));
        list.ItemChecked += OnRuleItemChecked;
        list.DoubleClick += (_, _) => EditRule();
        return list;
    }

    private Control BuildRuleEditorPanel()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            BackColor = _theme.Background,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = _theme.Background,
            Padding = new Padding(Scaled(8), 0, 0, 0),
        };
        buttons.Controls.Add(RuleButton(Loc.T("新增"), (_, _) => AddRule()));
        buttons.Controls.Add(RuleButton(Loc.T("编辑"), (_, _) => EditRule()));
        buttons.Controls.Add(RuleButton(Loc.T("删除"), (_, _) => RemoveRule()));
        buttons.Controls.Add(RuleButton(Loc.T("上移"), (_, _) => MoveRule(-1)));
        buttons.Controls.Add(RuleButton(Loc.T("下移"), (_, _) => MoveRule(1)));

        _ruleList.Dock = DockStyle.Fill;
        _ruleList.Margin = Padding.Empty;
        layout.Controls.Add(_ruleList, 0, 0);
        layout.Controls.Add(buttons, 1, 0);
        return layout;
    }

    private Button RuleButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            Width = Scaled(76),
            Height = Scaled(28),
            Margin = new Padding(0, 0, 0, Scaled(6)),
        };
        button.Click += onClick;
        return button;
    }

    private void RefreshRuleList(int selectedIndex)
    {
        _suppressRuleEvents = true;
        try
        {
            _ruleList.BeginUpdate();
            _ruleList.Items.Clear();
            foreach (var rule in _rules)
            {
                var item = new ListViewItem(rule.Enabled ? Loc.T("是") : Loc.T("否")) { Checked = rule.Enabled };
                item.SubItems.Add(rule.Match switch
                {
                    AppRuleMatch.PathContains => Loc.T("路径包含"),
                    AppRuleMatch.ProductName => Loc.T("产品名"),
                    _ => Loc.T("映像名"),
                });
                item.SubItems.Add(rule.Pattern);
                item.SubItems.Add(rule.Action switch
                {
                    AppRuleAction.Rename => Loc.T("只改显示名"),
                    AppRuleAction.Exclude => Loc.T("排除"),
                    _ => Loc.T("合并到应用"),
                });
                item.SubItems.Add(rule.Target ?? string.Empty);
                _ruleList.Items.Add(item);
            }

            _ruleList.EndUpdate();
            _ruleList.FitColumnsToClient();
            if (_ruleList.Items.Count > 0)
            {
                int index = Math.Clamp(selectedIndex, 0, _ruleList.Items.Count - 1);
                _ruleList.Items[index].Selected = true;
                _ruleList.Items[index].Focused = true;
            }
        }
        finally
        {
            _suppressRuleEvents = false;
        }
    }

    private void OnRuleItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_suppressRuleEvents || e.Item is null || e.Item.Index < 0 || e.Item.Index >= _rules.Count)
        {
            return;
        }

        _rules[e.Item.Index].Enabled = e.Item.Checked;
        e.Item.Text = e.Item.Checked ? Loc.T("是") : Loc.T("否");
    }

    private void AddRule()
    {
        using var dialog = new RuleEditDialog(_theme, new AppRule(), Loc.T("新增归并规则"));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _rules.Add(dialog.Rule);
        RefreshRuleList(_rules.Count - 1);
    }

    private void EditRule()
    {
        int index = SelectedRuleIndex();
        if (index < 0)
        {
            MessageBox.Show(this, Loc.T("请先选中一条规则。"), "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new RuleEditDialog(_theme, _rules[index], Loc.T("编辑归并规则"));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _rules[index] = dialog.Rule;
        RefreshRuleList(index);
    }

    private void RemoveRule()
    {
        int index = SelectedRuleIndex();
        if (index < 0)
        {
            MessageBox.Show(this, Loc.T("请先选中一条规则。"), "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _rules.RemoveAt(index);
        RefreshRuleList(Math.Min(index, _rules.Count - 1));
    }

    private void MoveRule(int delta)
    {
        int index = SelectedRuleIndex();
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _rules.Count)
        {
            return;
        }

        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        RefreshRuleList(target);
    }

    private int SelectedRuleIndex()
    {
        if (_ruleList.SelectedIndices.Count == 0)
        {
            return -1;
        }

        int index = _ruleList.SelectedIndices[0];
        return index >= 0 && index < _rules.Count ? index : -1;
    }

    private void OnOpenDataDirectory(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("打开数据目录失败：") + ex.Message, "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnResetDefaults(object? sender, EventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            Loc.T("确定把所有设置恢复为出厂默认值吗？（不会立即保存，可点取消放弃）"),
            "PerfScope",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        _working.ResetToDefaults();
        _rules.Clear();
        _rules.AddRange(_working.Rules.Select(rule => rule.Clone()));
        LoadFromWorking();
    }

    private void LoadFromWorking()
    {
        SetNum(_sampleInterval, _working.SampleIntervalMs);
        SetNum(_uiInterval, _working.UiRefreshIntervalMs);
        _adaptive.Checked = _working.AdaptiveThrottleWhenHidden;
        SetNum(_hiddenInterval, _working.HiddenSampleIntervalMs);
        SetNum(_smoothing, _working.SmoothingWindow);

        _cpuMode.SelectedIndex = (int)_working.CpuMode;
        _memoryMode.SelectedIndex = (int)_working.MemoryMode;

        _groupByProduct.Checked = _working.GroupByProductName;
        _groupByInstallRoot.Checked = _working.GroupByInstallRoot;
        _inheritParent.Checked = _working.InheritParentGroup;
        _mergeSystem.Checked = _working.MergeWindowsSystemProcesses;
        _versionCache.Checked = _working.UseVersionInfoCache;
        _unknownGroup.Text = _working.UnknownGroupName;
        _systemGroup.Text = _working.SystemGroupName;

        SetNum(_memoryRetention, _working.MemoryRetentionMinutes);
        SetNum(_diskRetention, _working.DiskRetentionDays);
        SetNum(_trendSlots, _working.TrendAppSlots);
        _diskHistory.Checked = _working.EnableDiskHistory;
        _insightEnabled.Checked = _working.InsightEnabled;
        SetNum(_insightCpuPercent, _working.InsightAppCpuPercent);
        SetNum(_insightCpuSeconds, _working.InsightAppCpuSeconds);
        SetNum(_insightGrowth, _working.InsightMemoryGrowthMbPerMinute);
        SetNum(_insightDigest, _working.InsightDigestMinutes);

        SetNum(_topN, _working.TopN);
        _startMinimized.Checked = _working.StartMinimized;
        _runAtStartup.Checked = _working.RunAtStartup;
        _closeBehavior.SelectedIndex = (int)_working.CloseBehavior;
        _showSelfUsage.Checked = _working.ShowSelfUsage;
        _showSystemGroups.Checked = _working.ShowSystemProcesses;
        _themeBox.SelectedIndex = string.Equals(_working.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _languageBox.SelectedIndex = LanguageIndex(_working.Language);

        _enableAlerts.Checked = _working.EnableAlerts;
        SetNum(_alertCpu, _working.AlertCpuPercent);
        SetNum(_alertMemory, _working.AlertMemoryPercent);
        SetNum(_alertCooldown, _working.AlertCooldownSeconds);

        _logLevel.SelectedIndex = (int)_working.LogLevel;
        SetNum(_logMaxKb, _working.LogMaxKilobytes);
        _updateCheck.Checked = _working.UpdateCheckEnabled;

        RefreshRuleList(0);
    }

    private static void SetNum(NumericUpDown control, decimal value)
        => control.Value = Math.Clamp(value, control.Minimum, control.Maximum);

    private bool CollectAndValidate(out string? message, out Control? focusControl)
    {
        message = null;
        focusControl = null;

        if (_adaptive.Checked && _hiddenInterval.Value < _sampleInterval.Value)
        {
            message = Loc.T("隐藏时的采样间隔不能小于正常采样间隔，否则窗口隐藏后反而更耗资源。");
            focusControl = _hiddenInterval;
            return false;
        }

        string unknownGroup = _unknownGroup.Text.Trim();
        if (unknownGroup.Length == 0)
        {
            message = Loc.T("兜底组名不能为空。");
            focusControl = _unknownGroup;
            return false;
        }

        string systemGroup = _systemGroup.Text.Trim();
        if (systemGroup.Length == 0)
        {
            message = Loc.T("系统组名不能为空。");
            focusControl = _systemGroup;
            return false;
        }

        if (_mergeSystem.Checked && string.Equals(unknownGroup, systemGroup, StringComparison.OrdinalIgnoreCase))
        {
            message = Loc.T("兜底组名与系统组名不能相同，否则无法区分系统组。");
            focusControl = _systemGroup;
            return false;
        }

        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                message = Loc.T("第 ") + (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) + Loc.T(" 条自定义规则没有填写匹配模式。");
                focusControl = _ruleList;
                _ruleList.Items[i].Selected = true;
                return false;
            }

            if (rule.Action != AppRuleAction.Exclude && string.IsNullOrWhiteSpace(rule.Target))
            {
                message = Loc.T("第 ") + (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) + Loc.T(" 条自定义规则缺少目标名称。");
                focusControl = _ruleList;
                _ruleList.Items[i].Selected = true;
                return false;
            }
        }

        _working.SampleIntervalMs = (int)_sampleInterval.Value;
        _working.UiRefreshIntervalMs = (int)_uiInterval.Value;
        _working.AdaptiveThrottleWhenHidden = _adaptive.Checked;
        _working.HiddenSampleIntervalMs = (int)_hiddenInterval.Value;
        _working.SmoothingWindow = (int)_smoothing.Value;

        _working.CpuMode = (CpuNormalization)Math.Max(0, _cpuMode.SelectedIndex);
        _working.MemoryMode = (MemoryDisplay)Math.Max(0, _memoryMode.SelectedIndex);

        _working.GroupByProductName = _groupByProduct.Checked;
        _working.GroupByInstallRoot = _groupByInstallRoot.Checked;
        _working.InheritParentGroup = _inheritParent.Checked;
        _working.MergeWindowsSystemProcesses = _mergeSystem.Checked;
        _working.UseVersionInfoCache = _versionCache.Checked;
        _working.UnknownGroupName = unknownGroup;
        _working.SystemGroupName = systemGroup;
        _working.Rules = _rules.Select(rule => rule.Clone()).ToList();

        _working.MemoryRetentionMinutes = (int)_memoryRetention.Value;
        _working.DiskRetentionDays = (int)_diskRetention.Value;
        _working.TrendAppSlots = (int)_trendSlots.Value;
        _working.EnableDiskHistory = _diskHistory.Checked;
        _working.InsightEnabled = _insightEnabled.Checked;
        _working.InsightAppCpuPercent = (int)_insightCpuPercent.Value;
        _working.InsightAppCpuSeconds = (int)_insightCpuSeconds.Value;
        _working.InsightMemoryGrowthMbPerMinute = (int)_insightGrowth.Value;
        _working.InsightDigestMinutes = (int)_insightDigest.Value;

        _working.TopN = (int)_topN.Value;
        _working.StartMinimized = _startMinimized.Checked;
        _working.RunAtStartup = _runAtStartup.Checked;
        _working.CloseBehavior = (CloseBehavior)Math.Max(0, _closeBehavior.SelectedIndex);
        _working.ShowSelfUsage = _showSelfUsage.Checked;
        _working.ShowSystemProcesses = _showSystemGroups.Checked;
        _working.Theme = _themeBox.SelectedIndex == 1 ? "light" : "dark";
        _working.Language = LanguageCode(_languageBox.SelectedIndex);

        _working.EnableAlerts = _enableAlerts.Checked;
        _working.AlertCpuPercent = (int)_alertCpu.Value;
        _working.AlertMemoryPercent = (int)_alertMemory.Value;
        _working.AlertCooldownSeconds = (int)_alertCooldown.Value;

        _working.LogLevel = (LogLevel)Math.Max(0, _logLevel.SelectedIndex);
        _working.LogMaxKilobytes = (int)_logMaxKb.Value;
        _working.UpdateCheckEnabled = _updateCheck.Checked;

        return true;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!CollectAndValidate(out string? message, out Control? focusControl))
        {
            MessageBox.Show(this, message ?? Loc.T("设置校验未通过。"), "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            focusControl?.Focus();
            if (focusControl is NumericUpDown number)
            {
                number.Select(0, number.Text.Length);
            }

            return;
        }

        if (!_host.ApplySettings(_working))
        {
            MessageBox.Show(
                this,
                _host.LastError ?? Loc.T("保存设置失败，详情见日志。"),
                "PerfScope",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _theme.Dispose();
        }

        base.Dispose(disposing);
    }
}