using System.Linq;
using System.Windows;
using System.Windows.Documents;
using CodexBackupManager.App.Rendering;
using Xunit;
using FlowList = System.Windows.Documents.List;

namespace CodexBackupManager.App.Tests.Rendering;

/// <summary>
/// <see cref="MarkdownLiteFlowDocumentRenderer"/> 스모크 테스트. 파싱 로직 자체는
/// <see cref="MarkdownLiteParserTests"/>에서 이미 꼼꼼히 검증했으므로, 여기서는 블록 → WPF
/// <see cref="FlowDocument"/> 매핑이 기대한 구조로 나오는지만 확인한다(제목 폰트 크기, 목록 묶임,
/// 코드블록 서식). WPF <c>Application</c> 없이(순수 xunit 프로세스) 이 타입들을 만들 수 있는지도
/// 함께 확인한다.
/// </summary>
public sealed class MarkdownLiteFlowDocumentRendererTests
{
    private static FlowDocument RenderText(string text)
        => MarkdownLiteFlowDocumentRenderer.Render(MarkdownLiteParser.Parse(text));

    [Fact]
    public void 평문_문단은_Paragraph_하나가_된다()
    {
        FlowDocument document = RenderText("안녕하세요");

        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        var run = Assert.IsType<Run>(paragraph.Inlines.Single());
        Assert.Equal("안녕하세요", run.Text);
    }

    [Fact]
    public void 제목_레벨이_클수록_폰트가_커진다()
    {
        FlowDocument h1 = RenderText("# 제목1");
        FlowDocument h3 = RenderText("### 제목3");

        var p1 = Assert.IsType<Paragraph>(Assert.Single(h1.Blocks));
        var p3 = Assert.IsType<Paragraph>(Assert.Single(h3.Blocks));

        Assert.True(p1.FontSize > p3.FontSize);
        Assert.Equal(FontWeights.Bold, p1.FontWeight);
    }

    [Fact]
    public void 연속된_번호_목록_항목은_하나의_List로_묶인다()
    {
        FlowDocument document = RenderText("1. 하나\n2. 둘\n3. 셋");

        var list = Assert.IsType<FlowList>(Assert.Single(document.Blocks));
        Assert.Equal(TextMarkerStyle.Decimal, list.MarkerStyle);
        Assert.Equal(3, list.ListItems.Count);
        Assert.Equal(1, list.StartIndex);
    }

    [Fact]
    public void 번호_목록이_1이_아닌_수로_시작하면_StartIndex에_반영된다()
    {
        FlowDocument document = RenderText("5. 다섯\n6. 여섯");

        var list = Assert.IsType<FlowList>(Assert.Single(document.Blocks));
        Assert.Equal(5, list.StartIndex);
    }

    [Fact]
    public void 불릿_목록은_Disc_마커로_묶인다()
    {
        FlowDocument document = RenderText("- 하나\n- 둘");

        var list = Assert.IsType<FlowList>(Assert.Single(document.Blocks));
        Assert.Equal(TextMarkerStyle.Disc, list.MarkerStyle);
        Assert.Equal(2, list.ListItems.Count);
    }

    [Fact]
    public void 목록_뒤에_일반_문단이_오면_List가_끊기고_별개_Paragraph가_된다()
    {
        FlowDocument document = RenderText("1. 하나\n2. 둘\n\n그 다음 설명");

        Assert.Equal(2, document.Blocks.Count);
        Assert.IsType<FlowList>(document.Blocks.First());
        Assert.IsType<Paragraph>(document.Blocks.Last());
    }

    [Fact]
    public void 코드블록은_다른_배경의_문단이_되고_줄바꿈이_LineBreak로_보존된다()
    {
        FlowDocument document = RenderText("```\nline1\nline2\n```");

        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        Assert.NotNull(paragraph.Background);
        Assert.Contains(paragraph.Inlines, inline => inline is LineBreak);
        var runs = paragraph.Inlines.OfType<Run>().Select(r => r.Text).ToList();
        Assert.Equal(["line1", "line2"], runs);
    }

    [Fact]
    public void 인라인_코드_span은_코드용_배경과_폰트를_가진_Run이_된다()
    {
        FlowDocument document = RenderText("앞 `code` 뒤");

        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        var codeRun = paragraph.Inlines.OfType<Run>().Single(r => r.Text == "code");
        Assert.NotNull(codeRun.Background);
    }

    [Fact]
    public void 문단_안의_줄바꿈은_LineBreak_Inline으로_보존된다()
    {
        FlowDocument document = RenderText("첫줄\n둘째줄");

        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        Assert.Contains(paragraph.Inlines, inline => inline is LineBreak);
    }

    [Fact]
    public void 빈_텍스트는_빈_FlowDocument가_된다()
        => Assert.Empty(MarkdownLiteFlowDocumentRenderer.Render(MarkdownLiteParser.Parse(string.Empty)).Blocks.ToList());
}
