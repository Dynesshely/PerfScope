using PerfScope.App.Infrastructure;
using PerfScope.App.Localization;
using PerfScope.Core.Settings;

namespace PerfScope.App.Forms;

/// <summary>
/// 首次启动的一次性引导：只讲三件「看懂了就会用」的事，不堆功能列表。
/// 之后可以从「设置 → 界面」重新打开。
/// </summary>
internal sealed class FirstRunTipsForm : Form
{
    private readonly Theme _theme;
    private readonly float _scale;
    private readonly string[] _tips;

    public FirstRunTipsForm(AppSettings settings)
    {
        _scale = Theme.DetectDpiScale();
        _theme = Theme.Create(settings.Theme, _scale);

        _tips = new[]
        {
            Loc.T("每一行、每一个方块都是一个「应用」，而不是单个进程——浏览器、Electron 应用的一堆子进程会被合并到同一个应用里。"),
            Loc.T("顶部指标卡悬停能看到坐标轴与该时刻的具体数值；列表里悬停进程行能看到完整可执行文件路径。"),
            Loc.T("历史会自动累积：工具栏「分析」生成日报，「接入 Agent」把结论连同数据路径一起交给你的 AI。"),
        };

        Text = Loc.T("欢迎使用 PerfScope");
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;

        int width = Scaled(620);
        int padding = Scaled(28);
        int contentWidth = width - padding * 2;

        var title = new Label
        {
            Text = Loc.T("欢迎使用 PerfScope"),
            Font = _theme.UiBoldFont,
            ForeColor = _theme.TextPrimary,
            AutoSize = false,
            Bounds = new Rectangle(padding, padding, contentWidth, Scaled(30)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var subtitle = new Label
        {
            Text = Loc.T("一个托盘常驻的「应用级」CPU / 内存监控工具。三件事看懂它："),
            ForeColor = _theme.TextSecondary,
            Font = _theme.SmallFont,
            AutoSize = false,
            Bounds = new Rectangle(padding, padding + Scaled(32), contentWidth, Scaled(24)),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(title);
        Controls.Add(subtitle);

        int top = padding + Scaled(66);
        for (int i = 0; i < _tips.Length; i++)
        {
            var index = new Label
            {
                Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                Font = _theme.UiBoldFont,
                ForeColor = _theme.Accent,
                AutoSize = false,
                Bounds = new Rectangle(padding, top, Scaled(24), Scaled(24)),
                TextAlign = ContentAlignment.TopLeft,
            };

            var text = new Label
            {
                Text = _tips[i],
                ForeColor = _theme.TextPrimary,
                AutoSize = false,
                Bounds = new Rectangle(padding + Scaled(32), top, contentWidth - Scaled(32), Scaled(52)),
                TextAlign = ContentAlignment.TopLeft,
            };

            // 按实际文字高度收缩/撑开，避免中英文换行后重叠
            var measured = TextRenderer.MeasureText(
                _tips[i],
                Font,
                new Size(contentWidth - Scaled(32), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            text.Height = Math.Max(Scaled(40), measured.Height + Scaled(6));

            Controls.Add(index);
            Controls.Add(text);
            top += text.Height + Scaled(10);
        }

        int footerTop = top + Scaled(6);
        int footerHeight = Scaled(22);
        var footer = new Label
        {
            Text = Loc.T("这些提示可以在「设置 → 界面」里重新查看。"),
            ForeColor = _theme.TextMuted,
            Font = _theme.SmallFont,
            AutoSize = false,
            Bounds = new Rectangle(padding, footerTop, contentWidth, footerHeight),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var startButton = new Button
        {
            Text = Loc.T("开始使用"),
            Width = Scaled(120),
            Height = Scaled(32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        startButton.Click += (_, _) => Close();

        // 底部预留：footer + 间距 + 按钮 + 四周留白（之前算少了，按钮会被表单边界裁掉）
        int buttonTop = footerTop + footerHeight + Scaled(14);
        int clientHeight = buttonTop + startButton.Height + Scaled(20);
        ClientSize = new Size(width, clientHeight);
        startButton.Location = new Point(width - padding - startButton.Width, buttonTop);

        Controls.Add(footer);
        Controls.Add(startButton);

        AcceptButton = startButton;
        CancelButton = startButton;

        FormStyler.Apply(this, _theme);
        AppIcon.Apply(this);
    }

    private int Scaled(int value) => (int)Math.Round(value * _scale);
}