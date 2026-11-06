using System.Drawing;

namespace PerfScope.App.Infrastructure;

/// <summary>
/// 应用图标的唯一来源：优先用内嵌的 app.ico（窗口 / 任务栏 / 托盘 / 对话框共用一份），
/// 取不到时退回到 exe 资源，最后退回现画的托盘图标，保证任何情况下都有图标。
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "PerfScope.App.app.ico";

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Icon> Cache = new();
    private static readonly Lazy<Icon?> Source = new(LoadSource);

    /// <summary>取指定尺寸的图标（16 / 24 / 32 / 48 等），已缓存，调用方不要 Dispose。</summary>
    public static Icon? Get(int size)
    {
        if (size < 16)
        {
            size = 16;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(size, out var cached))
            {
                return cached;
            }
        }

        Icon? icon = null;
        var source = Source.Value;
        if (source is not null)
        {
            try
            {
                icon = new Icon(source, new Size(size, size));
            }
            catch
            {
                icon = null;
            }
        }

        icon ??= TrayIconFactory.Create(size);

        lock (Gate)
        {
            Cache[size] = icon;
        }

        return icon;
    }

    /// <summary>
    /// 给窗体设置图标。
    /// 注意：WinForms 窗体的 <see cref="Form.Icon"/> 默认就返回应用程序图标（非 null），
    /// 所以不能靠「null 才设置」判断，否则永远设不上——必须直接赋值。
    /// </summary>
    public static void Apply(Form form)
    {
        var icon = Get(32);
        if (icon is null)
        {
            return;
        }

        if (!ReferenceEquals(form.Icon, icon))
        {
            form.Icon = icon;
        }
    }

    private static Icon? LoadSource()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                return new Icon(stream);
            }
        }
        catch
        {
            // 内嵌资源读取失败就继续走后面的兜底
        }

        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return Icon.ExtractAssociatedIcon(path);
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }
}
