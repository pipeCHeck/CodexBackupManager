using System.Collections.Generic;

namespace CodexBackupManager.App.Rendering;

/// <summary>
/// 메시지 본문 안의 인라인 조각 하나. 일반 텍스트, 인라인 코드(<c>`code`</c>), 굵게, 기울임,
/// 링크, 줄바꿈 중 하나다.
/// </summary>
/// <param name="Text">조각의 텍스트. 줄바꿈 조각이면 빈 문자열.</param>
/// <param name="IsCode">인라인 코드(<c>`code`</c>) 조각인지.</param>
/// <param name="IsBold">굵게(<c>**text**</c>/<c>__text__</c>) 조각인지.</param>
/// <param name="IsItalic">기울임(<c>*text*</c>/<c>_text_</c>) 조각인지.</param>
/// <param name="LinkUrl">Markdown 링크(<c>[text](url)</c>)였다면 그 URL. 아니면 <c>null</c>.</param>
/// <param name="IsLineBreak">
/// 같은 문단/목록 항목 안에서 원본 줄바꿈을 표현하는 조각인지(요구사항: 줄바꿈 유지).
/// 참이면 다른 필드는 의미가 없다.
/// </param>
public sealed record InlineSpan(
    string Text,
    bool IsCode = false,
    bool IsBold = false,
    bool IsItalic = false,
    string? LinkUrl = null,
    bool IsLineBreak = false)
{
    /// <summary>줄바꿈 조각을 만든다.</summary>
    public static InlineSpan LineBreak() => new(string.Empty, IsLineBreak: true);
}

/// <summary>Markdown-lite 파싱 결과 블록의 공통 기반.</summary>
public abstract record MarkdownBlock;

/// <summary>제목/소제목. <c>#</c>~<c>######</c>.</summary>
/// <param name="Level">1~6.</param>
/// <param name="Text">제목 텍스트(인라인 서식은 적용하지 않는다 — subset 범위를 넘지 않는다).</param>
public sealed record HeadingBlock(int Level, string Text) : MarkdownBlock;

/// <summary>일반 문단. 원본의 여러 줄이 한 문단으로 묶이면 <see cref="InlineSpan.LineBreak"/>로 이어진다.</summary>
public sealed record ParagraphBlock(IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

/// <summary>번호 목록 항목(<c>1. </c>, <c>2) </c> 등).</summary>
public sealed record NumberedListItemBlock(int Number, IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

/// <summary>불릿 목록 항목(<c>- </c>, <c>* </c>).</summary>
public sealed record BulletListItemBlock(IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

/// <summary>인용문(<c>&gt; </c>). 연속된 인용 줄은 하나로 묶인다.</summary>
public sealed record BlockquoteBlock(IReadOnlyList<InlineSpan> Spans) : MarkdownBlock;

/// <summary>펜스로 감싼 코드 블록(<c>```</c>). 안쪽 줄은 인라인 파싱 없이 그대로 보존한다.</summary>
/// <param name="Code">코드 내용(줄바꿈 포함).</param>
/// <param name="Language">여는 펜스 뒤에 언어 표기가 있으면 그 값. 없으면 <c>null</c>.</param>
public sealed record CodeBlock(string Code, string? Language) : MarkdownBlock;
