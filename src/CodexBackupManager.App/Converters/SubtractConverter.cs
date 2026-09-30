using System;
using System.Globalization;
using System.Windows.Data;

namespace CodexBackupManager.App.Converters;

/// <summary>
/// <c>double</c> 값에서 <c>ConverterParameter</c>만큼 뺀다(0 미만은 0). TreeViewItem 머리글은 기본 템플릿에서 폭 제한이 없어
/// 긴 문구가 줄바꿈되지 않으므로, TreeView 폭에서 들여쓰기만큼 뺀 값을 머리글 폭으로 준다(Phase 9_2-2 가져오기 트리).
/// </summary>
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double source = value is double d ? d : 0;
        double amount = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;
        return Math.Max(0, source - amount);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
