using PerfScope.App.Localization;
using PerfScope.App.Infrastructure;
using PerfScope.Core.Settings;

namespace PerfScope.App.Forms;

/// <summary>
/// 「接入 Agent」窗口：把一段已经填好真实路径、数据口径与结论的 Prompt 摆出来，一键复制给任意模型/Agent。
/// </summary>
internal sealed class AgentHandoffForm : Form
{
    private readonly AppHost _host;
    private readonly Theme _theme;
    private readonly float _scale;
    private readonly TextBox _promptBox;
    private readonly Label _statusLabel;
    private string _prompt;

    /// <summary>创建窗口。</summary>
    public AgentHandoffForm(AppHost host)
    {
        _host = host;
        _scale = Theme.DetectDpiScale();
        _theme = Theme.Create(host.Settings.Theme, _scale);
        _prompt = string.Empty;

        Text = Loc.T("接入 Agent — 把分析任务交给你的 AI");
        AutoScaleMode = AutoScaleMode.None;
        // 用 CenterScreen：主窗口可能是Loc.T("贴满屏幕")的宽窗口，按父窗口居中会把对话框挤出屏幕
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = _theme.Background;
        ForeColor = _theme.TextPrimary;
        Font = _theme.UiFont;
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int width = Math.Min(Scaled(900), (int)(workArea.Width * 0.92));
        int height = Math.Min(Scaled(620), (int)(workArea.Height * 0.88));
        ClientSize = new Size(width, height);
        MinimumSize = new Size(Math.Min(Scaled(640), width), Math.Min(Scaled(420), height));

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = Scaled(78),
            BackColor = _theme.Background,
            Padding = new Padding(Scaled(16), Scaled(12), Scaled(16), Scaled(6)),
        };

        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = Scaled(26),
            Text = Loc.T("把下面的 Prompt 复制给你的 Agent"),
            Font = _theme.UiBoldFont,
            ForeColor = _theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var hintLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = Loc.T("Prompt 里已经写入本机真实的报告路径、数据口径与自动结论；")
                   + Loc.T("Agent 只要能读本地文件（Claude Code / Cursor / Codex 等）就能直接分析，无需你再补充上下文。"),
            Font = _theme.SmallFont,
            ForeColor = _theme.TextMuted,
            TextAlign = ContentAlignment.TopLeft,
        };

        header.Controls.Add(hintLabel);
        header.Controls.Add(titleLabel);

        _promptBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _theme.SurfaceAlt,
            ForeColor = _theme.TextPrimary,
            Font = CreateMonospaceFont(Scaled(9)),
        };

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Scaled(16), Scaled(4), Scaled(16), Scaled(8)),
            BackColor = _theme.Background,
        };
        body.Controls.Add(_promptBox);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = _theme.TextMuted,
            Font = _theme.SmallFont,
            Padding = new Padding(Scaled(16), 0, 0, 0),
        };

        var copyButton = new Button
        {
            Text = Loc.T("复制 Prompt"),
            Width = Scaled(150),
            Height = Scaled(30),
            Margin = new Padding(Scaled(8), 0, 0, 0),
        };
        copyButton.Click += (_, _) => CopyPrompt();

        var saveButton = new Button
        {
            Text = Loc.T("另存为…"),
            Width = Scaled(96),
            Height = Scaled(30),
            Margin = new Padding(Scaled(8), 0, 0, 0),
        };
        saveButton.Click += (_, _) => SavePrompt();

        var folderButton = new Button
        {
            Text = Loc.T("打开数据目录"),
            Width = Scaled(150),
            Height = Scaled(30),
            Margin = new Padding(Scaled(8), 0, 0, 0),
        };
        folderButton.Click += (_, _) => OpenDataDirectory();

        var closeButton = new Button
        {
            Text = Loc.T("关闭"),
            Width = Scaled(88),
            Height = Scaled(30),
            Margin = new Padding(Scaled(8), 0, 0, 0),
        };
        closeButton.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, Scaled(7), Scaled(16), 0),
            BackColor = _theme.Background,
        };
        buttons.Controls.Add(copyButton);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(folderButton);
        buttons.Controls.Add(closeButton);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Scaled(46),
            BackColor = _theme.Background,
        };
        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(buttons);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        AcceptButton = copyButton;
        CancelButton = closeButton;

        FormStyler.Apply(this, _theme);
        Shown += (_, _) => LoadPrompt();
    }

    private int Scaled(int value) => (int)Math.Round(value * _scale);

    private static Font CreateMonospaceFont(float size)
    {
        string[] candidates = { "Cascadia Mono", "Consolas", "Courier New" };
        foreach (var candidate in candidates)
        {
            try
            {
                var font = new Font(candidate, size);
                if (string.Equals(font.FontFamily.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }

                font.Dispose();
            }
            catch
            {
                // 换下一个候选字体
            }
        }

        return new Font(FontFamily.GenericMonospace, size);
    }

    private void LoadPrompt()
    {
        try
        {
            _prompt = _host.BuildAgentPrompt();
            _promptBox.Text = _prompt;
            _promptBox.SelectionStart = 0;
            _promptBox.SelectionLength = 0;
            _statusLabel.Text = Loc.T("数据目录：") + _host.AnalysisDirectory;
        }
        catch (Exception ex)
        {
            _prompt = string.Empty;
            _promptBox.Text = Loc.T("生成 Prompt 失败：") + ex.Message;
            _statusLabel.Text = Loc.T("生成失败，可查看日志");
        }
    }

    private void CopyPrompt()
    {
        if (string.IsNullOrEmpty(_prompt))
        {
            LoadPrompt();
        }

        if (string.IsNullOrEmpty(_prompt))
        {
            return;
        }

        try
        {
            // 剪贴板偶尔被其它程序占用，重试几次即可
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(_prompt);
                    _statusLabel.Text = Loc.T("已复制 ") + _prompt.Length + Loc.T(" 个字符到剪贴板，粘贴给你的 Agent 即可");
                    return;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    Thread.Sleep(60);
                }
            }

            _statusLabel.Text = Loc.T("剪贴板被占用，请稍后重试或手动全选（Ctrl+A）复制");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = Loc.T("复制失败：") + ex.Message;
        }
    }

    private void SavePrompt()
    {
        if (string.IsNullOrEmpty(_prompt))
        {
            LoadPrompt();
        }

        using var dialog = new SaveFileDialog
        {
            Title = Loc.T("保存 Agent Prompt"),
            FileName = "perfscope-agent-prompt.md",
            Filter = Loc.T("Markdown (*.md)|*.md|文本文件 (*.txt)|*.txt"),
            InitialDirectory = _host.AnalysisDirectory,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _prompt, new System.Text.UTF8Encoding(false));
            _statusLabel.Text = Loc.T("已保存：") + dialog.FileName;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = Loc.T("保存失败：") + ex.Message;
        }
    }

    private void OpenDataDirectory()
    {
        try
        {
            var directory = _host.AnalysisDirectory;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _statusLabel.Text = Loc.T("打开目录失败：") + ex.Message;
        }
    }
}
