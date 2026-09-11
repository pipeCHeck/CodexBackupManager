using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexBackupManager.App.ViewModels;

/// <summary>최소 MVVM 베이스. 외부 MVVM 프레임워크를 쓰지 않는다.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>속성 변경을 알린다.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>값이 바뀐 경우에만 대입하고 알린다.</summary>
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
