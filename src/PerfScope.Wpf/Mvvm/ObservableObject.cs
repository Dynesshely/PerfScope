using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PerfScope.App.Mvvm;

/// <summary>
/// 所有 ViewModel 的基类：实现 INotifyPropertyChanged，并提供带相等性判断的 SetProperty。
/// 项目红线是「零第三方 NuGet」，所以 MVVM 基础设施自己写，不引 CommunityToolkit。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>属性变化通知。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变化通知。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>批量通知若干属性（例如整行数据刷新时一次通知多个列）。</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>赋值并在值确实变化时触发通知；返回是否发生了变化。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
