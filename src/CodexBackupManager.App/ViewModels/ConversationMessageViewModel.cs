using System;
using System.Collections.Generic;
using System.Windows.Documents;
using CodexBackupManager.App.Rendering;
using CodexBackupManager.Domain.Codex.Conversation;

namespace CodexBackupManager.App.ViewModels;

/// <summary>Conversation Viewer에 표시할 메시지 한 줄.</summary>
/// <remarks>
/// <para>
/// <b><see cref="Body"/>(WPF <see cref="FlowDocument"/>)는 지연 생성한다.</b> 긴 대화(실측 1,431개
/// 메시지)를 열 때 <see cref="MainViewModel.LoadConversationAsync"/>가 전체 메시지에 대해 이 타입을
/// 한꺼번에 만드는데, 예전에는 <c>FromDomain</c> 안에서 <c>Body</c>까지 즉시 렌더링했다. 그러면
/// ListBox가 UI 컨테이너는 가상화하더라도 <c>FlowDocument</c> 생성 자체는 전부 미리 끝나 있어
/// 초기 UI 정지와 메모리 증가로 이어진다. 그래서 이 클래스는 아래처럼 나눈다.
/// </para>
/// <list type="bullet">
///   <item><see cref="Blocks"/>(Markdown-lite 파싱 결과) — WPF에 의존하지 않는 순수 데이터라
///     <c>FromDomain</c> 시점(백그라운드 스레드에서 호출돼도 안전)에 미리 만들어 둔다.</item>
///   <item><see cref="Body"/> — WPF <see cref="FlowDocument"/>는 이 프로퍼티를 처음 읽을 때만
///     만든다. 실제로 화면에 이 아이템이 virtualize되어 바인딩되는 순간(= UI 스레드에서 WPF
///     바인딩 엔진이 이 getter를 호출하는 순간)에만 생성되고, 그 뒤로는 캐시된 같은 인스턴스를
///     돌려준다. Worker 스레드가 이 프로퍼티를 미리 건드리지 않는 한 WPF 객체는 항상 UI 스레드에서
///     만들어진다.</item>
/// </list>
/// </remarks>
public sealed class ConversationMessageViewModel
{
    private readonly Lazy<FlowDocument> _body;

    /// <summary>
    /// 생성자. <c>Blocks!</c>: <c>required init</c> 속성이라 이 시점엔 아직 대입되지 않았지만,
    /// 지연 팩토리는 <see cref="Body"/>를 실제로 읽을 때(항상 객체 초기화가 끝난 뒤) 실행되므로
    /// 안전하다 — null 허용 분석기가 이 순서를 알 수 없어 붙인 null-forgiving이다.
    /// </summary>
    public ConversationMessageViewModel() => _body = new Lazy<FlowDocument>(() => MarkdownLiteFlowDocumentRenderer.Render(Blocks!));

    /// <summary>"User" 또는 "Assistant".</summary>
    public required string RoleLabel { get; init; }

    /// <summary>메시지 본문. 원래 줄바꿈을 그대로 보존한다.</summary>
    public required string Text { get; init; }

    /// <summary>Assistant의 phase 표시("commentary"/"final"). 없으면 <c>null</c>.</summary>
    public string? PhaseLabel { get; init; }

    /// <summary>User 메시지인지(정렬/색상 구분용).</summary>
    public bool IsUser { get; init; }

    /// <summary>
    /// <see cref="Text"/>를 Markdown-lite로 미리 파싱해둔 결과(WPF 비의존 순수 데이터). <see cref="Body"/>가
    /// 실제로 필요해지는 시점에 이 값을 그대로 써서 <see cref="FlowDocument"/>를 만든다.
    /// </summary>
    public required IReadOnlyList<MarkdownBlock> Blocks { get; init; }

    /// <summary>
    /// <see cref="Blocks"/>를 그린 <see cref="FlowDocument"/>(제목/문단/목록/코드블록 서식 포함).
    /// 처음 읽을 때 생성되고 이후로는 캐시된다(클래스 remarks 참고). Viewer는 이 문서를 읽기 전용
    /// <c>RichTextBox</c>에 붙여서 보여준다 — 그래야 서식이 섞여도 텍스트 선택/복사가 유지된다.
    /// </summary>
    public FlowDocument Body => _body.Value;

    /// <summary><see cref="Body"/>가 이미 생성되었는지(테스트/진단용). 값 자체를 강제로 만들지 않는다.</summary>
    public bool IsBodyRendered => _body.IsValueCreated;

    /// <summary>생성. <see cref="Blocks"/>만 미리 파싱하고 <see cref="Body"/>는 만들지 않는다.</summary>
    public static ConversationMessageViewModel FromDomain(ConversationMessage message)
        => new()
        {
            RoleLabel = message.Role == ConversationRole.User ? "User" : "Assistant",
            Text = message.Text,
            Blocks = MarkdownLiteParser.Parse(message.Text),
            PhaseLabel = message.Phase switch
            {
                AssistantPhase.Commentary => "commentary",
                AssistantPhase.Final => "final",
                _ => null,
            },
            IsUser = message.Role == ConversationRole.User,
        };
}
