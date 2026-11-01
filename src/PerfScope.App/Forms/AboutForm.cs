using System.Diagnostics;
using PerfScope.App.Infrastructure;
using PerfScope.Core;

namespace PerfScope.App.Forms;

/// <summary>关于窗口：产品定位、版本、数据目录与自证轻量的说明。</summary>
internal sealed class AboutForm : Form
{
    private readonly Theme _theme;
    private readonly float _scale;
    private bool _disposed;

    /// <summary>创建关于窗口。</summary>
    public AboutForm(AppHost host)
    {
        _scale = Theme.DetectDpiScale();
        _theme = Theme.Create(host.Settings.Theme, _scale);

        Text = "关于 PerfScope";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;
        ClientSize = new Size(Scaled(520), Scaled(360));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(Scaled(20)),
            BackColor = _theme.Background,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "PerfScope  " + AppPaths.Version,
            Font = _theme.BigNumberFont,
            ForeColor = _theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, Scaled(8)),
        };
        layout.Controls.Add(title, 0, 0);

        var description = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = _theme.TextSecondary,
            TextAlign = ContentAlignment.TopLeft,
            Text = "Windows 上真正能 7×24 常驻的 CPU / 内存监控小工具。" + Environment.NewLine + Environment.NewLine +
                   "• 后台线程 + 原生批量采样（NtQuerySystemInformation），自身开销极低" + Environment.NewLine +
                   "• 只展示「应用级」占用：按安装目录、版本信息、产品名把进程挂钩到同一个应用" + Environment.NewLine +
                   "• 零第三方依赖，纯 .NET + WinForms + P/Invoke" + Environment.NewLine +
                   "• 托盘常驻，窗口隐藏时自动降频采样" + Environment.NewLine + Environment.NewLine +
                   "数据目录：" + AppPaths.DataDirectory,
        };
        layout.Controls.Add(description, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = _theme.Background,
        };
        var closeButton = new Button { Text = "关闭", Width = Scaled(88), Height = Scaled(30), DialogResult = DialogResult.OK };
        var openDataButton = new Button { Text = "打开数据目录", Width = Scaled(120), Height = Scaled(30) };
        openDataButton.Click += OnOpenDataDirectory;
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(openDataButton);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        FormStyler.Apply(this, _theme);
    }

    private int Scaled(int value) => (int)Math.Round(value * _scale);

    private void OnOpenDataDirectory(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开数据目录失败：" + ex.Message, "PerfScope", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
