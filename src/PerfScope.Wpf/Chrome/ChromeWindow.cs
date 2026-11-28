using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PerfScope.App.Chrome
{
    /// <summary>
    /// 无边框窗口壳：自绘标题栏 + Win11 Snap Layouts + 系统原生最小化/最大化/还原动画。
    /// 来源：AwesomeTemplates/Wpf/WindowShell（见该模板 docs/design-notes.md 的实测结论）。
    ///
    /// 三条硬性前提（少一条就会退化）：
    ///  1. 保持 WindowStyle=SingleBorderWindow（保留 WS_CAPTION / WS_THICKFRAME），系统才播过渡动画；
    ///  2. 不要用 AllowsTransparency（会切到分层窗口，动画与 Snap 都会失效）；
    ///  3. 标题栏高度由 WindowChrome.CaptionHeight 决定，画的内容要和它对上。
    /// </summary>
    [TemplatePart(Name = PartMinButton, Type = typeof(Button))]
    [TemplatePart(Name = PartMaxButton, Type = typeof(Button))]
    [TemplatePart(Name = PartCloseButton, Type = typeof(Button))]
    [TemplatePart(Name = PartMaxGlyph, Type = typeof(TextBlock))]
    public class ChromeWindow : Window
    {
        public const string PartMinButton = "PART_MinButton";
        public const string PartMaxButton = "PART_MaxButton";
        public const string PartCloseButton = "PART_CloseButton";
        public const string PartMaxGlyph = "PART_MaxGlyph";

        // ---------- 可主题化的外观属性 ----------
        public static readonly DependencyProperty TitleBarHeightProperty =
            DependencyProperty.Register(nameof(TitleBarHeight), typeof(double), typeof(ChromeWindow),
                new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty TitleBarBackgroundProperty =
            DependencyProperty.Register(nameof(TitleBarBackground), typeof(Brush), typeof(ChromeWindow),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0xB3, 0x16, 0x18, 0x1D))));

        public static readonly DependencyProperty TitleBarForegroundProperty =
            DependencyProperty.Register(nameof(TitleBarForeground), typeof(Brush), typeof(ChromeWindow),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF))));

        public static readonly DependencyProperty WindowBorderBrushProperty =
            DependencyProperty.Register(nameof(WindowBorderBrush), typeof(Brush), typeof(ChromeWindow),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3B))));

        public static readonly DependencyProperty HeaderContentProperty =
            DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(ChromeWindow),
                new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty UseDarkTitleBarProperty =
            DependencyProperty.Register(nameof(UseDarkTitleBar), typeof(bool), typeof(ChromeWindow),
                new FrameworkPropertyMetadata(true, OnUseDarkTitleBarChanged));

        public double TitleBarHeight { get => (double)(GetValue(TitleBarHeightProperty) ?? 40.0); set => SetValue(TitleBarHeightProperty, value); }
        public Brush TitleBarBackground { get => (Brush)GetValue(TitleBarBackgroundProperty); set => SetValue(TitleBarBackgroundProperty, value); }
        public Brush TitleBarForeground { get => (Brush)GetValue(TitleBarForegroundProperty); set => SetValue(TitleBarForegroundProperty, value); }
        public Brush WindowBorderBrush { get => (Brush)GetValue(WindowBorderBrushProperty); set => SetValue(WindowBorderBrushProperty, value); }
        public object? HeaderContent { get => GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
        public bool UseDarkTitleBar { get => (bool)GetValue(UseDarkTitleBarProperty); set => SetValue(UseDarkTitleBarProperty, value); }

        // ---------- 模板部件 ----------
        private Button? _minBtn;
        private Button? _maxBtn;
        private Button? _closeBtn;
        private TextBlock? _maxGlyph;

        private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
        private bool _maxHover;

        static ChromeWindow()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ChromeWindow),
                new FrameworkPropertyMetadata(typeof(ChromeWindow)));
        }

        public ChromeWindow()
        {
            // 必须保留系统边框样式：WindowChrome 只是把非客户区「画没了」，底层窗口样式不能动
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 40,          // 和 TitleBarHeight 一致：这块由系统当作标题栏处理（拖动 / 双击最大化）
                ResizeBorderThickness = new Thickness(6),   // 边缘拖拽改尺寸的热区
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            StateChanged += (s, e) => { UpdateMaxGlyph(); UpdateRootState(); };
            _hoverTimer.Tick += (s, e) => UpdateMaxHoverByCursor();
            Loaded += OnLoadedCore;
            Closed += (s, e) => _hoverTimer.Stop();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            DetachParts();
            _minBtn = GetTemplateChild(PartMinButton) as Button;
            _maxBtn = GetTemplateChild(PartMaxButton) as Button;
            _closeBtn = GetTemplateChild(PartCloseButton) as Button;
            _maxGlyph = GetTemplateChild(PartMaxGlyph) as TextBlock;

            if (_minBtn != null)
            {
                _minBtn.Click += OnMinimize;
                _minBtn.ToolTip = Localization.Loc.T("最小化");
            }

            if (_maxBtn != null)
            {
                _maxBtn.Click += OnToggleMaximize;
                _maxBtn.ToolTip = Localization.Loc.T("最大化");
            }

            if (_closeBtn != null)
            {
                _closeBtn.Click += OnClose;
                _closeBtn.ToolTip = Localization.Loc.T("关闭");
            }

            UpdateMaxGlyph();
            UpdateRootState();
        }

        private void DetachParts()
        {
            if (_minBtn != null)
            {
                _minBtn.Click -= OnMinimize;
            }

            if (_maxBtn != null)
            {
                _maxBtn.Click -= OnToggleMaximize;
            }

            if (_closeBtn != null)
            {
                _closeBtn.Click -= OnClose;
            }

            _minBtn = _maxBtn = _closeBtn = null;
            _maxGlyph = null;
        }

        private void OnLoadedCore(object sender, RoutedEventArgs e)
        {
            if (IsWindows11)
            {
                _hoverTimer.Start();
            }
        }

        // ---------- 三大金刚键：全部交给系统命令，动画由 DWM 播 ----------
        private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void OnToggleMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        /// <summary>最大化 / 还原切换（走系统命令，保留 DWM 动画）。</summary>
        public void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                SystemCommands.RestoreWindow(this);
            }
            else
            {
                SystemCommands.MaximizeWindow(this);
            }
        }

        // E922 = 最大化，E923 = 还原
        private void UpdateMaxGlyph()
        {
            if (_maxGlyph == null)
            {
                return;
            }

            _maxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        // 最大化时去掉 1px 边框，避免工作区高度被吃掉
        private void UpdateRootState()
        {
            if (GetTemplateChild("PART_RootBorder") is Border border)
            {
                border.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            }
        }

        // ---------- 窗口消息：Snap Layouts / 工作区最大化 ----------

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTMAXBUTTON = 9;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCMOUSELEAVE = 0x02A2;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (UseDarkTitleBar)
            {
                TryDarkTitleBar();
            }

            HookChrome();
        }

        private void HookChrome()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(handle);
            src?.AddHook(ChromeWndProc);
        }

        private static void OnUseDarkTitleBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ChromeWindow window && window.IsLoaded)
            {
                window.TryDarkTitleBar();
            }
        }

        private IntPtr ChromeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 鼠标落在最大化键上时回 HTMAXBUTTON：Win11 才会弹 Snap Layouts 面板
            if (msg == WM_NCHITTEST && IsWindows11 && _maxBtn is { IsVisible: true })
            {
                int lp = lParam.ToInt32();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);
                if (PointInMaxButton(sx, sy))
                {
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
            }

            // HTMAXBUTTON 区域不会给 WPF 发 Click，按下/双击都由这里补
            if (msg == WM_NCLBUTTONDOWN && IsWindows11 && wParam.ToInt32() == HTMAXBUTTON)
            {
                ToggleMaximize();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_NCLBUTTONDBLCLK && IsWindows11 && wParam.ToInt32() == HTMAXBUTTON)
            {
                handled = true;
                return IntPtr.Zero;
            }

            // 同一批区域也不会有 WPF 的 IsMouseOver，hover 只能自己查光标
            if (msg == WM_MOUSEMOVE && _maxHover)
            {
                SetMaxHover(false);
            }

            if (msg == WM_NCMOUSEMOVE && IsWindows11)
            {
                int lp = lParam.ToInt32();
                SetMaxHover(PointInMaxButton((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            }
            else if (msg == WM_NCMOUSELEAVE)
            {
                SetMaxHover(false);
            }

            // 最大化铺满工作区（默认会把任务栏也盖住）
            if (msg == WM_GETMINMAXINFO)
            {
                if (Marshal.PtrToStructure(lParam, typeof(MINMAXINFO)) is MINMAXINFO mmi)
                {
                    IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (monitor != IntPtr.Zero)
                    {
                        var mi = new MONITORINFO();
                        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                        if (GetMonitorInfo(monitor, ref mi))
                        {
                            mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
                            mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
                            mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                            mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                            Marshal.StructureToPtr(mmi, lParam, true);
                        }
                    }
                }

                handled = true;
            }

            return IntPtr.Zero;
        }

        private void UpdateMaxHoverByCursor()
        {
            if (!IsWindows11 || _maxBtn == null)
            {
                return;
            }

            bool inside = false;
            try
            {
                if (GetCursorPos(out POINT pt))
                {
                    inside = IsActive && PointInMaxButton(pt.x, pt.y);
                }
            }
            catch
            {
                // 查询光标失败时按「不在按钮上」处理
            }

            SetMaxHover(inside);
        }

        private void SetMaxHover(bool on)
        {
            if (_maxHover == on)
            {
                return;
            }

            _maxHover = on;
            if (_maxBtn == null)
            {
                return;
            }

            // 用 Tag 让模板触发器接管，避免和样式里的 Trigger 打架
            _maxBtn.Tag = on ? "hover" : null;
        }

        private bool PointInMaxButton(int sx, int sy)
        {
            if (_maxBtn == null)
            {
                return false;
            }

            try
            {
                Point tl = _maxBtn.PointToScreen(new Point(0, 0));
                Point br = _maxBtn.PointToScreen(new Point(_maxBtn.ActualWidth, _maxBtn.ActualHeight));
                return sx >= tl.X && sx <= br.X && sy >= tl.Y && sy <= br.Y;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        // ---------- DWM：深色标题栏 ----------
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void TryDarkTitleBar()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                int dark = UseDarkTitleBar ? 1 : 0;
                DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));   // Win10 20H1+
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));   // Win10 1809+
                int caption = UseDarkTitleBar ? 0x00211B17 : 0x00F8F6F5;
                DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                int textColor = UseDarkTitleBar ? 0x00EFE9E6 : 0x00241F1B;
                DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
                int border = UseDarkTitleBar ? 0x003B312C : 0x00E6E1DD;
                DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }
            catch
            {
                // 旧版 Windows 不支持这些属性时忽略
            }
        }

        // ---------- P/Invoke 结构 ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }
}
