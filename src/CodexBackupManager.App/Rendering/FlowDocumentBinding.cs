using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace CodexBackupManager.App.Rendering;

/// <summary>
/// <see cref="RichTextBox.Document"/>는 일반 CLR 속성이라 XAML에서 <c>{Binding}</c>으로 직접 연결할 수
/// 없다. 이 첨부 속성이 값이 바뀔 때마다 <c>RichTextBox.Document</c>에 대신 대입해준다.
/// </summary>
/// <remarks>
/// <para>
/// <b>실측 크래시 root cause 수정.</b> 긴 대화를 빠르게 스크롤하면(특히 방향을 자주 바꾸면)
/// <c>ArgumentException: 문서가 이미 다른 RichTextBox에 속합니다</c>가 <c>RichTextBox.set_Document</c>에서
/// 발생했다. <c>ConversationMessageViewModel</c>은 메시지 하나당 <see cref="FlowDocument"/> 인스턴스를
/// (즉시 생성이든 <c>Lazy&lt;T&gt;</c>든) 계속 재사용하는데, <c>VirtualizationMode="Recycling"</c>은
/// 스크롤에 따라 같은 항목을 서로 다른 <see cref="RichTextBox"/> 컨테이너로 여러 번 바꿔 보여줄 수
/// 있다. 정상적인 경우라면 이전 컨테이너가 다른 항목으로 재활용되면서(그 컨테이너의 <c>Document</c>가
/// 다른 문서로 교체되면서) 자동으로 이전 소유권이 풀리지만, 빠른 스크롤/방향 전환으로 컨테이너
/// 재활용 순서가 겹치면 이 문서가 "아직 다른 살아있는 RichTextBox의 Document로 남아있는" 상태에서
/// 새 컨테이너에 다시 대입되는 경우가 실제로 생긴다(<c>FlowDocument</c>는 <c>FrameworkContentElement</c>라
/// 논리 부모가 하나만 허용된다). WPF UI는 단일 스레드이므로, 대입 직전에 "이 문서가 지금 이 컨트롤이
/// 아닌 다른 컨트롤의 자식인가"를 확인해 그 다른 컨트롤에서 먼저 강제로 떼어내면(빈 문서로 교체)
/// 경쟁 없이 항상 안전하게 재대입할 수 있다.
/// </para>
/// </remarks>
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
        if (d is not RichTextBox richTextBox)
        {
            return;
        }

        FlowDocument newDocument = e.NewValue as FlowDocument ?? new FlowDocument();

        // 클래스 remarks 참고: 재사용되는 FlowDocument가 아직 다른(살아있는) RichTextBox에 붙어 있으면
        // 그쪽에서 먼저 강제로 떼어낸다. UI 스레드 단일 스레드 모델이라 이 확인과 대입 사이에 경쟁은
        // 없다 — 여기서 읽은 Parent는 항상 지금 이 순간의 실제 상태다.
        if (newDocument.Parent is RichTextBox previousOwner && !ReferenceEquals(previousOwner, richTextBox))
        {
            previousOwner.Document = new FlowDocument();
        }

        richTextBox.Document = newDocument;
    }
}
