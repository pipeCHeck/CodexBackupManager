using System;
using System.Globalization;
using System.Windows.Data;

namespace CodexBackupManager.App.Converters;

/// <summary>
/// <c>bool</c>을 뒤집는다. Apply 진행 중(<see cref="ViewModels.MainViewModel.IsApplying"/>)에는
/// 비활성화돼야 하는데 <see cref="System.Windows.Controls.Control.IsEnabled"/>는 값 자체가
/// bool이라(Visibility가 아니다) <c>BooleanToVisibilityConverter</c>를 못 쓰는 자리에 쓴다.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
