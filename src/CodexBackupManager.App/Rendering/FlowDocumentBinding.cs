using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CodexBackupManager.App.Rendering;

/// <summary>
/// <see cref="RichTextBox.Document"/>는 일반 CLR 속성이라 XAML에서 <c>{Binding}</c>으로 직접 연결할 수
/// 없다. 이 첨부 속성이 값이 바뀔 때마다 <c>RichTextBox.Document</c>에 대신 대입해준다.
/// </summary>
public static class FlowDocumentBinding
{
    /// <summary>바인딩할 <see cref="FlowDocument"/>.</summary>
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.RegisterAttached(
        "Document",
        typeof(FlowDocument),
        typeof(FlowDocumentBinding),
        new PropertyMetadata(null, OnDocumentChanged));

    /// <summary>getter.</summary>
    public static FlowDocument? GetDocument(RichTextBox element) => (FlowDocument?)element.GetValue(DocumentProperty);

    /// <summary>setter.</summary>
    public static void SetDocument(RichTextBox element, FlowDocument? value) => element.SetValue(DocumentProperty, value);

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox richTextBox)
        {
            richTextBox.Document = e.NewValue as FlowDocument ?? new FlowDocument();
        }
    }
}
