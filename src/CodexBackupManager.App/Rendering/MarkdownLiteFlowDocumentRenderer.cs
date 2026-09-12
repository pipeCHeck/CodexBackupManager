using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using FlowList = System.Windows.Documents.List;

namespace CodexBackupManager.App.Rendering;

/// <summary>
/// <see cref="MarkdownLiteParser"/>가 만든 블록을 <see cref="FlowDocument"/>로 그린다.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RichTextBox"/>(읽기 전용)에 <see cref="FlowDocument"/>를 붙이면 일반 <c>TextBlock</c>과
/// 달리 텍스트 선택/복사가 그대로 유지되면서, 제목/목록/코드블록 같은 블록 서식과 인라인 코드 같은
/// 혼합 서식(굵기/폰트가 다른 <c>Run</c>이 한 문단 안에 섞이는 것)을 표준 WPF 타입만으로 표현할 수
/// 있다 — 그래서 <c>TextBlock.Inlines</c>를 직접 조립하는 커스텀 컨트롤을 새로 만들지 않았다.
/// </para>
/// <para>
/// 색상은 <c>App.xaml</c>의 리소스 키(<c>CodeBg</c>, <c>Fg</c>)와 같은 값으로 직접 얼린(freeze) 브러시를
/// 쓴다. <c>Application.Current.Resources</c>를 조회하지 않는 이유는, 이 클래스를 xunit처럼 WPF
/// <c>Application</c>이 실행되지 않는 프로세스(단위 테스트)에서도 그대로 쓸 수 있게 하기 위해서다.
/// </para>
/// </remarks>
public static class MarkdownLiteFlowDocumentRenderer
{
    private static readonly FontFamily CodeFontFamily = new("Consolas, D2Coding, Malgun Gothic");
    private static readonly FontFamily BodyFontFamily = new("Segoe UI, Malgun Gothic");
    private static readonly SolidColorBrush CodeBackgroundBrush = CreateFrozenBrush("#FF17181B");
    private static readonly SolidColorBrush ForegroundBrush = CreateFrozenBrush("#FFE6E6E6");

    /// <summary>파싱된 블록으로 <see cref="FlowDocument"/>를 만든다.</summary>
    public static FlowDocument Render(IReadOnlyList<MarkdownBlock> blocks)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = BodyFontFamily,
            FontSize = 13,
            Foreground = ForegroundBrush,
            TextAlignment = TextAlignment.Left,
        };

        int i = 0;
        while (i < blocks.Count)
        {
            switch (blocks[i])
            {
                case HeadingBlock heading:
                    document.Blocks.Add(RenderHeading(heading));
                    i++;
                    break;

                case ParagraphBlock paragraph:
                    document.Blocks.Add(RenderParagraph(paragraph));
                    i++;
                    break;

                case CodeBlock code:
                    document.Blocks.Add(RenderCode(code));
                    i++;
                    break;

                case NumberedListItemBlock:
                    i = AppendList(document, blocks, i, isNumbered: true);
                    break;

                case BulletListItemBlock:
                    i = AppendList(document, blocks, i, isNumbered: false);
                    break;

                default:
                    i++;
                    break;
            }
        }

        // 첫/마지막 블록의 위/아래 여백을 없애 메시지 말풍선 패딩과 이중으로 겹치지 않게 한다.
        if (document.Blocks.FirstBlock is { } firstBlock)
        {
            firstBlock.Margin = new Thickness(firstBlock.Margin.Left, 0, firstBlock.Margin.Right, firstBlock.Margin.Bottom);
        }

        if (document.Blocks.LastBlock is { } lastBlock)
        {
            lastBlock.Margin = new Thickness(lastBlock.Margin.Left, lastBlock.Margin.Top, lastBlock.Margin.Right, 0);
        }

        return document;
    }

    /// <summary>
    /// 연속된 같은 종류의 목록 항목 블록을 하나의 <see cref="FlowList"/>로 묶는다. 번호/불릿 목록은
    /// 파서에서 항목 단위 블록으로 나오므로, 렌더러가 여기서 그룹을 다시 만들어야 항목 사이 간격 없이
    /// 자연스러운 목록으로 보인다.
    /// </summary>
    private static int AppendList(FlowDocument document, IReadOnlyList<MarkdownBlock> blocks, int start, bool isNumbered)
    {
        var list = new FlowList
        {
            MarkerStyle = isNumbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(20, 0, 0, 0),
        };

        int i = start;
        bool first = true;
        while (i < blocks.Count)
        {
            IReadOnlyList<InlineSpan>? spans = (isNumbered, blocks[i]) switch
            {
                (true, NumberedListItemBlock numbered) => numbered.Spans,
                (false, BulletListItemBlock bullet) => bullet.Spans,
                _ => null,
            };

            if (spans is null)
            {
                break;
            }

            if (first && isNumbered && blocks[i] is NumberedListItemBlock firstNumbered)
            {
                list.StartIndex = firstNumbered.Number;
            }

            var itemParagraph = new Paragraph { Margin = new Thickness(0) };
            AppendSpans(itemParagraph.Inlines, spans);
            list.ListItems.Add(new ListItem(itemParagraph));

            first = false;
            i++;
        }

        document.Blocks.Add(list);
        return i;
    }

    private static Paragraph RenderHeading(HeadingBlock heading)
    {
        return new Paragraph(new Run(heading.Text))
        {
            FontWeight = FontWeights.Bold,
            FontSize = heading.Level switch
            {
                1 => 18,
                2 => 16,
                3 => 14.5,
                _ => 13.5,
            },
            Margin = new Thickness(0, 10, 0, 4),
        };
    }

    private static Paragraph RenderParagraph(ParagraphBlock block)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AppendSpans(paragraph.Inlines, block.Spans);
        return paragraph;
    }

    private static Paragraph RenderCode(CodeBlock block)
    {
        var paragraph = new Paragraph
        {
            FontFamily = CodeFontFamily,
            FontSize = 12,
            Background = CodeBackgroundBrush,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 8),
        };

        string[] lines = block.Code.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[i]));
        }

        return paragraph;
    }

    private static void AppendSpans(InlineCollection inlines, IReadOnlyList<InlineSpan> spans)
    {
        foreach (InlineSpan span in spans)
        {
            if (span.IsLineBreak)
            {
                inlines.Add(new LineBreak());
                continue;
            }

            if (span.IsCode)
            {
                inlines.Add(new Run(span.Text) { FontFamily = CodeFontFamily, Background = CodeBackgroundBrush });
                continue;
            }

            inlines.Add(new Run(span.Text));
        }
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
