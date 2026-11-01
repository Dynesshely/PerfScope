using PerfScope.App.Infrastructure;
using PerfScope.Core.Settings;

namespace PerfScope.App.Forms;

/// <summary>单条自定义归并规则的编辑对话框（匹配方式 / 模式 / 动作 / 目标 / 启用）。</summary>
internal sealed class RuleEditDialog : Form
{
    private static readonly string[] MatchTexts = { "按映像名（exe 文件名）", "按可执行文件路径包含", "按产品名（版本信息）" };
    private static readonly string[] ActionTexts = { "合并到指定应用", "只改显示名", "排除（不显示、不统计）" };

    private readonly Theme _theme;
    private readonly ComboBox _matchBox;
    private readonly TextBox _patternBox;
    private readonly ComboBox _actionBox;
    private readonly TextBox _targetBox;
    private readonly CheckBox _enabledBox;
    private readonly Label _hintLabel;
    private readonly float _scale;

    /// <summary>打开编辑器；<paramref name="rule"/> 为待编辑规则的副本。</summary>
    public RuleEditDialog(Theme theme, AppRule rule, string title)
    {
        _theme = theme;
        _scale = Theme.DetectDpiScale();
        Rule = rule.Clone();

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = theme.Background;
        ForeColor = theme.TextPrimary;
        Font = theme.UiFont;
        ClientSize = new Size(Dp(460), Dp(300));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(Dp(16)),
            BackColor = theme.Background,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dp(92)));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 5; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Dp(38)));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _enabledBox = new CheckBox
        {
            Text = "启用这条规则",
            Checked = Rule.Enabled,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _matchBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
        };
        _matchBox.Items.AddRange(MatchTexts);
        _matchBox.SelectedIndex = (int)Rule.Match;
        _matchBox.SelectedIndexChanged += (_, _) => UpdateHint();

        _patternBox = new TextBox { Text = Rule.Pattern, Dock = DockStyle.Top };

        _actionBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Top,
        };
        _actionBox.Items.AddRange(ActionTexts);
        _actionBox.SelectedIndex = (int)Rule.Action;
        _actionBox.SelectedIndexChanged += (_, _) => UpdateTargetEnabled();

        _targetBox = new TextBox { Text = Rule.Target ?? string.Empty, Dock = DockStyle.Top };

        _hintLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = theme.TextMuted,
            Font = theme.SmallFont,
            TextAlign = ContentAlignment.TopLeft,
        };

        AddRow(layout, 0, "启用", _enabledBox);
        AddRow(layout, 1, "匹配方式", _matchBox);
        AddRow(layout, 2, "匹配模式", _patternBox);
        AddRow(layout, 3, "命中动作", _actionBox);
        AddRow(layout, 4, "目标名称", _targetBox);
        layout.Controls.Add(_hintLabel, 1, 5);
        layout.SetColumnSpan(_hintLabel, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = Dp(52),
            Padding = new Padding(Dp(16), Dp(6), Dp(16), Dp(6)),
            BackColor = theme.Background,
        };
        var cancelButton = new Button { Text = "取消", Width = Dp(88), Height = Dp(30), DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = "确定", Width = Dp(88), Height = Dp(30) };
        okButton.Click += OnConfirm;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = theme.Background };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(layout, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        FormStyler.Apply(this, theme);
        UpdateHint();
        UpdateTargetEnabled();
    }

    /// <summary>编辑后的规则（确定时才有意义）。</summary>
    public AppRule Rule { get; }

    private int Dp(int value) => (int)Math.Round(value * _scale);

    private void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, Dp(8), 0),
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private void UpdateHint()
    {
        _hintLabel.Text = _matchBox.SelectedIndex switch
        {
            1 => "示例：\\Program Files\\MyApp\\ —— 只要可执行文件路径包含该子串就命中。",
            2 => "示例：My Company MyApp —— 与版本信息 ProductName 比较（不区分大小写）。",
            _ => "示例：myapp.exe —— 只与该进程的映像文件名比较（不区分大小写）。",
        };
    }

    private void UpdateTargetEnabled()
    {
        bool needTarget = (AppRuleAction)Math.Max(0, _actionBox.SelectedIndex) != AppRuleAction.Exclude;
        _targetBox.Enabled = needTarget;
        _targetBox.BackColor = needTarget ? _theme.SurfaceAlt : _theme.Background;
    }

    private void OnConfirm(object? sender, EventArgs e)
    {
        string pattern = _patternBox.Text.Trim();
        if (pattern.Length == 0)
        {
            MessageBox.Show(this, "请填写匹配模式。", "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _patternBox.Focus();
            return;
        }

        var action = (AppRuleAction)Math.Max(0, _actionBox.SelectedIndex);
        string target = _targetBox.Text.Trim();
        if (action != AppRuleAction.Exclude && target.Length == 0)
        {
            MessageBox.Show(this, "该动作需要填写目标名称。", "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _targetBox.Focus();
            return;
        }

        Rule.Enabled = _enabledBox.Checked;
        Rule.Match = (AppRuleMatch)Math.Max(0, _matchBox.SelectedIndex);
        Rule.Pattern = pattern;
        Rule.Action = action;
        Rule.Target = action == AppRuleAction.Exclude ? null : target;
        DialogResult = DialogResult.OK;
        Close();
    }
}