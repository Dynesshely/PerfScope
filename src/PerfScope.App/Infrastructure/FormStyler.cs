using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 把主题配色递归套用到 WinForms 控件树。只处理颜色/字体，不改布局，
/// 保证高 DPI 下依旧由 TableLayoutPanel / Dock / Anchor 决定尺寸。
/// </summary>
internal static class FormStyler
{
    private static readonly ConditionalWeakTable<Form, Theme> FormThemes = new();
    private static readonly ConditionalWeakTable<Form, object> CaptionHooked = new();

    /// <summary>对整棵控件树套用主题（窗体额外套用系统标题栏配色）。</summary>
    public static void Apply(Control root, Theme theme)
    {
        if (root is Form form)
        {
            FormThemes.AddOrUpdate(form, theme);
            if (!CaptionHooked.TryGetValue(form, out _))
            {
                CaptionHooked.Add(form, new object());
                form.HandleCreated += OnFormHandleCreated;
            }

            if (form.IsHandleCreated)
            {
                ApplyCaptionTheme(form, theme);
            }
        }

        ApplyTo(root, theme);
    }

    private static void OnFormHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form form && FormThemes.TryGetValue(form, out var theme))
        {
            ApplyCaptionTheme(form, theme);
        }
    }

    /// <summary>
    /// 把系统标题栏切成深/浅色，和窗口内容一致（DWM）。
    /// 失败不影响功能：旧版 Windows 上直接忽略即可。
    /// </summary>
    public static void ApplyCaptionTheme(Form form, Theme theme)
    {
        if (!form.IsHandleCreated)
        {
            return;
        }

        try
        {
            int dark = theme.IsDark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win11/新版 Win10），19 = 旧版编号
            if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
            }

            // 34/35/36 = 边框色 / 标题栏色 / 标题文字色（Win11 22H1+）
            int border = ToColorRef(theme.Border);
            DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));

            int caption = ToColorRef(theme.HeaderBackground);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));

            int captionText = ToColorRef(theme.TextPrimary);
            DwmSetWindowAttribute(form.Handle, 36, ref captionText, sizeof(int));
        }
        catch
        {
            // 标题栏配色属于锦上添花，失败就保持系统默认
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void ApplyTo(Control control, Theme theme)
    {
        if (control is Form form)
        {
            form.BackColor = theme.Background;
            form.ForeColor = theme.TextPrimary;
            form.Font = theme.UiFont;
            AppIcon.Apply(form);
        }
        else if (control is TabPage page)
        {
            page.BackColor = theme.Background;
            page.ForeColor = theme.TextPrimary;
        }
        else if (control is TabControl tabs)
        {
            tabs.BackColor = theme.Background;
            tabs.ForeColor = theme.TextPrimary;
        }
        else if (control is TableLayoutPanel || control is Panel || control is FlowLayoutPanel || control is GroupBox)
        {
            control.BackColor = theme.Background;
            control.ForeColor = theme.TextPrimary;
        }
        else if (control is Label || control is CheckBox || control is RadioButton)
        {
            control.BackColor = Color.Transparent;
            control.ForeColor = theme.TextPrimary;
        }
        else if (control is TextBoxBase || control is ComboBox || control is NumericUpDown)
        {
            control.BackColor = theme.SurfaceAlt;
            control.ForeColor = theme.TextPrimary;
            if (control is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
            }
        }
        else if (control is ListView list)
        {
            list.BackColor = theme.Surface;
            list.ForeColor = theme.TextPrimary;
            list.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (control is Button button)
        {
            button.BackColor = theme.SurfaceAlt;
            button.ForeColor = theme.TextPrimary;
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderColor = theme.Border;
            button.FlatAppearance.MouseOverBackColor = theme.RowHover;
            button.FlatAppearance.MouseDownBackColor = theme.RowSelected;
            button.Font = theme.UiFont;
        }
        else if (control is ListBox listBox)
        {
            listBox.BackColor = theme.SurfaceAlt;
            listBox.ForeColor = theme.TextPrimary;
        }

        foreach (Control child in control.Controls)
        {
            ApplyTo(child, theme);
        }
    }
}
