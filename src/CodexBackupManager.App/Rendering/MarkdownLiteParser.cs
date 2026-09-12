using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexBackupManager.App.Rendering;

/// <summary>
/// Codex 대화 본문을 Codex UI에 좀 더 가깝게 보여주기 위한 최소("lite") Markdown 파서.
/// </summary>
/// <remarks>
/// <para>
/// 텍스트만 있던 Conversation Viewer가 "서식이 살아있지 않다"는 지적을 받아 도입했다. 완전한
/// CommonMark 구현이나 외부 markdown 패키지 대신, 실제로 필요한 범위만 직접 구현한다(문단 구분,
/// 줄바꿈 보존, 제목, 번호/불릿 목록, 인용문, 코드블록/인라인 코드, 굵게/기울임/링크) — 과설계를
/// 피하고 실용적인 방향을 우선한다는 지시를 따른다. Markdig 같은 검증된 라이브러리 대신 직접
/// 만든 이유: (1) FlowDocument 변환 계층은 라이브러리를 쓰더라도 결국 직접 짜야 해서 작업량이
/// 크게 줄지 않고, (2) 새 NuGet 의존성을 늘리지 않는다는 CLAUDE.md §2.1 원칙, (3) 요구된 범위가
/// 여전히 "subset"(굵게/기울임/링크/인용문 추가 정도)이라 완전한 CommonMark 엔진이 필요한
/// 수준이 아니다.
/// </para>
/// <para>
/// <b>단일 <c>*</c>/<c>_</c> 기울임의 한계.</b> Codex는 코딩 어시스턴트라 실제 대화에 코드/수식이
/// 자주 섞인다(<c>snake_case_name</c>, <c>a*b</c> 등). <c>_</c>/<c>__</c>는 CommonMark와 같은 방식
/// (delimiter 바로 바깥쪽이 영문/숫자/밑줄이면 emphasis로 인정하지 않음)으로 intraword 오탐을
/// 막는다. <c>*</c>/<c>**</c>는 "여는/닫는 기호 바로 안쪽이 공백이 아니어야 한다"는 최소한의
/// 안전장치만 두고 그 이상은 구현하지 않는다(과설계 방지) — 코드에서 <c>*</c>가 식별자 안에 그대로
/// 붙어 쓰이는 경우는 드물다.
/// </para>
/// <para>
/// WPF 타입을 전혀 참조하지 않는다(입력은 <c>string</c>, 출력은 이 프로젝트의 <c>MarkdownBlock</c>
/// 레코드들) — 그래서 WPF 렌더링 계층(<see cref="MarkdownLiteFlowDocumentRenderer"/>)과 분리해서
/// 단위 테스트할 수 있다.
/// </para>
/// </remarks>
public static class MarkdownLiteParser
{
    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedListPattern = new(@"^(\d{1,9})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletListPattern = new(@"^[-*]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BlockquotePattern = new(@"^>\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex FencePattern = new(@"^```\s*(\S*)\s*$", RegexOptions.Compiled);

    // 우선순위 순서가 중요하다 — 같은 위치에서 정규식 대체(alternation)는 앞쪽부터 시도한다.
    // link/code를 가장 먼저(가장 구체적), 그다음 굵게(**/__, 2글자 구분자)를 기울임(*/_, 1글자
    // 구분자)보다 먼저 둬야 "**굵게**"가 기울임으로 잘못 쪼개지지 않는다. 여는/닫는 기호 바로
    // 안쪽에 공백이 오는 경우는 제외해 "a * b" 같은 흔한 수식 표기의 오탐을 줄인다.
    //
    // "_"/"__" delimiter는 추가로 intraword 오탐 방지가 필요하다: 코딩 대화에는
    // snake_case_name, SOME_CONSTANT_NAME 같은 식별자가 자주 등장하는데, 이 안의 "_"는
    // emphasis delimiter가 아니다. CommonMark도 같은 이유로 "_"는 단어 경계에서만 emphasis로
    // 인정한다(밑줄 앞/뒤가 영문·숫자·밑줄이면 delimiter로 취급하지 않는다) — "*"는 코드에서
    // 그런 식으로 붙어 쓰이는 경우가 드물어 기존 정책(안쪽 공백만 배제)을 그대로 둔다.
    private static readonly Regex InlinePattern = new(
        @"\[(?<linktext>[^\]]+)\]\((?<linkurl>[^)\s]+)\)" +
        "|`(?<code>[^`]+)`" +
        @"|\*\*(?<boldstar>\S(?:[^*]*\S)?)\*\*" +
        @"|(?<![A-Za-z0-9_])__(?<boldunder>\S(?:[^_]*\S)?)__(?![A-Za-z0-9_])" +
        @"|\*(?<italicstar>\S(?:[^*]*\S)?)\*" +
        @"|(?<![A-Za-z0-9_])_(?<italicunder>\S(?:[^_]*\S)?)_(?![A-Za-z0-9_])",
        RegexOptions.Compiled);

    /// <summary>본문 전체를 블록 목록으로 파싱한다. 빈 문자열이면 빈 목록을 돌려준다.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraphLines = new List<string>();
        var blockquoteLines = new List<string>();

        List<InlineSpan> JoinLinesWithBreaks(List<string> textLines)
        {
            var spans = new List<InlineSpan>();
            for (int i = 0; i < textLines.Count; i++)
            {
                if (i > 0)
                {
                    spans.Add(InlineSpan.LineBreak());
                }

                spans.AddRange(ParseInline(textLines[i]));
            }

            return spans;
        }

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            blocks.Add(new ParagraphBlock(JoinLinesWithBreaks(paragraphLines)));
            paragraphLines.Clear();
        }

        void FlushBlockquote()
        {
            if (blockquoteLines.Count == 0)
            {
                return;
            }

            blocks.Add(new BlockquoteBlock(JoinLinesWithBreaks(blockquoteLines)));
            blockquoteLines.Clear();
        }

        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int i2 = 0;
        while (i2 < lines.Length)
        {
            string line = lines[i2];
            string trimmed = line.Trim();

            Match fenceMatch = FencePattern.Match(trimmed);
            if (fenceMatch.Success)
            {
                FlushParagraph();
                FlushBlockquote();
                string? language = fenceMatch.Groups[1].Value;
                if (string.IsNullOrEmpty(language))
                {
                    language = null;
                }

                var codeLines = new List<string>();
                i2++;
                while (i2 < lines.Length && !lines[i2].Trim().StartsWith("```"))
                {
                    codeLines.Add(lines[i2]);
                    i2++;
                }

                if (i2 < lines.Length)
                {
                    i2++; // 닫는 펜스 줄을 건너뛴다.
                }

                blocks.Add(new CodeBlock(string.Join("\n", codeLines), language));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushBlockquote();
                i2++;
                continue;
            }

            Match headingMatch = HeadingPattern.Match(trimmed);
            if (headingMatch.Success)
            {
                FlushParagraph();
                FlushBlockquote();
                blocks.Add(new HeadingBlock(headingMatch.Groups[1].Value.Length, headingMatch.Groups[2].Value.Trim()));
                i2++;
                continue;
            }

            Match numberedMatch = NumberedListPattern.Match(trimmed);
            if (numberedMatch.Success)
            {
                FlushParagraph();
                FlushBlockquote();
                int number = int.Parse(numberedMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                blocks.Add(new NumberedListItemBlock(number, ParseInline(numberedMatch.Groups[2].Value)));
                i2++;
                continue;
            }

            Match bulletMatch = BulletListPattern.Match(trimmed);
            if (bulletMatch.Success)
            {
                FlushParagraph();
                FlushBlockquote();
                blocks.Add(new BulletListItemBlock(ParseInline(bulletMatch.Groups[1].Value)));
                i2++;
                continue;
            }

            Match blockquoteMatch = BlockquotePattern.Match(trimmed);
            if (blockquoteMatch.Success)
            {
                FlushParagraph();
                blockquoteLines.Add(blockquoteMatch.Groups[1].Value);
                i2++;
                continue;
            }

            FlushBlockquote();
            paragraphLines.Add(line.TrimEnd());
            i2++;
        }

        FlushParagraph();
        FlushBlockquote();
        return blocks;
    }

    /// <summary>
    /// 한 줄 안의 인라인 코드/굵게/기울임/링크 조각을 분리한다. 나머지는 일반 텍스트 조각이다.
    /// </summary>
    private static List<InlineSpan> ParseInline(string line)
    {
        var spans = new List<InlineSpan>();
        int pos = 0;

        foreach (Match match in InlinePattern.Matches(line))
        {
            if (match.Index > pos)
            {
                spans.Add(new InlineSpan(line[pos..match.Index]));
            }

            if (match.Groups["linktext"].Success)
            {
                spans.Add(new InlineSpan(match.Groups["linktext"].Value, LinkUrl: match.Groups["linkurl"].Value));
            }
            else if (match.Groups["code"].Success)
            {
                spans.Add(new InlineSpan(match.Groups["code"].Value, IsCode: true));
            }
            else if (match.Groups["boldstar"].Success)
            {
                spans.Add(new InlineSpan(match.Groups["boldstar"].Value, IsBold: true));
            }
            else if (match.Groups["boldunder"].Success)
            {
                spans.Add(new InlineSpan(match.Groups["boldunder"].Value, IsBold: true));
            }
            else if (match.Groups["italicstar"].Success)
            {
                spans.Add(new InlineSpan(match.Groups["italicstar"].Value, IsItalic: true));
            }
            else if (match.Groups["italicunder"].Success)
            {
                spans.Add(new InlineSpan(match.Groups["italicunder"].Value, IsItalic: true));
            }

            pos = match.Index + match.Length;
        }

        if (pos < line.Length || spans.Count == 0)
        {
            spans.Add(new InlineSpan(line[pos..]));
        }

        return spans;
    }
}
