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
/// CommonMark 구현이나 외부 markdown 패키지 대신, 실제로 필요한 최소 범위만 직접 구현한다
/// (문단 구분, 줄바꿈 보존, 제목, 번호/불릿 목록, 코드블록/인라인 코드) — 과설계를 피하고
/// 실용적인 방향을 우선한다는 지시를 따른다.
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
    private static readonly Regex InlineCodePattern = new("`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex FencePattern = new(@"^```\s*(\S*)\s*$", RegexOptions.Compiled);

    /// <summary>본문 전체를 블록 목록으로 파싱한다. 빈 문자열이면 빈 목록을 돌려준다.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var paragraphLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraphLines.Count == 0)
            {
                return;
            }

            var spans = new List<InlineSpan>();
            for (int i = 0; i < paragraphLines.Count; i++)
            {
                if (i > 0)
                {
                    spans.Add(InlineSpan.LineBreak());
                }

                spans.AddRange(ParseInline(paragraphLines[i]));
            }

            blocks.Add(new ParagraphBlock(spans));
            paragraphLines.Clear();
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
                i2++;
                continue;
            }

            Match headingMatch = HeadingPattern.Match(trimmed);
            if (headingMatch.Success)
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(headingMatch.Groups[1].Value.Length, headingMatch.Groups[2].Value.Trim()));
                i2++;
                continue;
            }

            Match numberedMatch = NumberedListPattern.Match(trimmed);
            if (numberedMatch.Success)
            {
                FlushParagraph();
                int number = int.Parse(numberedMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                blocks.Add(new NumberedListItemBlock(number, ParseInline(numberedMatch.Groups[2].Value)));
                i2++;
                continue;
            }

            Match bulletMatch = BulletListPattern.Match(trimmed);
            if (bulletMatch.Success)
            {
                FlushParagraph();
                blocks.Add(new BulletListItemBlock(ParseInline(bulletMatch.Groups[1].Value)));
                i2++;
                continue;
            }

            paragraphLines.Add(line.TrimEnd());
            i2++;
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// 한 줄 안의 인라인 코드(<c>`code`</c>) 조각을 분리한다. 나머지는 일반 텍스트 조각이다.
    /// </summary>
    private static List<InlineSpan> ParseInline(string line)
    {
        var spans = new List<InlineSpan>();
        int pos = 0;

        foreach (Match match in InlineCodePattern.Matches(line))
        {
            if (match.Index > pos)
            {
                spans.Add(new InlineSpan(line[pos..match.Index], IsCode: false));
            }

            spans.Add(new InlineSpan(match.Groups[1].Value, IsCode: true));
            pos = match.Index + match.Length;
        }

        if (pos < line.Length || spans.Count == 0)
        {
            spans.Add(new InlineSpan(line[pos..], IsCode: false));
        }

        return spans;
    }
}
