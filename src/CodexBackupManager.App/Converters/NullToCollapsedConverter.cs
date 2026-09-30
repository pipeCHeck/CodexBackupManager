using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodexBackupManager.App.Converters;

/// <summary>
/// 값이 <c>null</c>이거나 빈 문자열이면 <see cref="Visibility.Collapsed"/>, 아니면 <see cref="Visibility.Visible"/>.
/// 선택적 안내 문구(있을 때만 보이는 한 줄)에 쓴다.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string text && text.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
