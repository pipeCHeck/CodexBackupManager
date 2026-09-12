using System.Linq;
using CodexBackupManager.App.Rendering;
using Xunit;

namespace CodexBackupManager.App.Tests.Rendering;

/// <summary>
/// <see cref="MarkdownLiteParser"/> 테스트. WPF 타입을 전혀 쓰지 않는 순수 파서라 여기서 전체
/// subset(문단/줄바꿈/제목/번호·불릿 목록/코드블록/인라인 코드)을 꼼꼼히 검증한다.
/// </summary>
public sealed class MarkdownLiteParserTests
{
    [Fact]
    public void 빈_문자열은_빈_블록_목록을_준다()
        => Assert.Empty(MarkdownLiteParser.Parse(string.Empty));

    [Fact]
    public void 한_줄짜리_평문은_문단_하나가_된다()
    {
        var blocks = MarkdownLiteParser.Parse("안녕하세요");

        ParagraphBlock paragraph = Assert.Single(blocks.Cast<ParagraphBlock>());
        InlineSpan span = Assert.Single(paragraph.Spans);
        Assert.Equal("안녕하세요", span.Text);
        Assert.False(span.IsCode);
    }

    [Fact]
    public void 빈_줄_없는_여러_줄은_한_문단_안에서_줄바꿈으로_이어진다()
    {
        var blocks = MarkdownLiteParser.Parse("첫째 줄\n둘째 줄\n셋째 줄");

        ParagraphBlock paragraph = Assert.Single(blocks.Cast<ParagraphBlock>());
        // "첫째 줄", LineBreak, "둘째 줄", LineBreak, "셋째 줄" 순서로 5개 span.
        Assert.Equal(5, paragraph.Spans.Count);
        Assert.Equal("첫째 줄", paragraph.Spans[0].Text);
        Assert.True(paragraph.Spans[1].IsLineBreak);
        Assert.Equal("둘째 줄", paragraph.Spans[2].Text);
        Assert.True(paragraph.Spans[3].IsLineBreak);
        Assert.Equal("셋째 줄", paragraph.Spans[4].Text);
    }

    [Fact]
    public void 빈_줄로_구분된_두_문단은_별개_블록이_된다()
    {
        var blocks = MarkdownLiteParser.Parse("첫 문단\n\n둘째 문단");

        Assert.Equal(2, blocks.Count);
        var first = Assert.IsType<ParagraphBlock>(blocks[0]);
        var second = Assert.IsType<ParagraphBlock>(blocks[1]);
        Assert.Equal("첫 문단", Assert.Single(first.Spans).Text);
        Assert.Equal("둘째 문단", Assert.Single(second.Spans).Text);
    }

    [Theory]
    [InlineData("# 제목", 1, "제목")]
    [InlineData("## 소제목", 2, "소제목")]
    [InlineData("### 소소제목", 3, "소소제목")]
    public void 샵으로_시작하고_공백이_있으면_제목이다(string line, int expectedLevel, string expectedText)
    {
        var blocks = MarkdownLiteParser.Parse(line);

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(blocks));
        Assert.Equal(expectedLevel, heading.Level);
        Assert.Equal(expectedText, heading.Text);
    }

    [Fact]
    public void 공백_없이_샵으로_시작하면_제목이_아니라_평문이다()
    {
        var blocks = MarkdownLiteParser.Parse("#해시태그같은문장");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
        Assert.Equal("#해시태그같은문장", Assert.Single(paragraph.Spans).Text);
    }

    [Fact]
    public void 번호_목록_1_2_3은_순서대로_번호목록_블록이_된다()
    {
        var blocks = MarkdownLiteParser.Parse("1. 첫 항목\n2. 둘째 항목\n3. 셋째 항목");

        Assert.Equal(3, blocks.Count);
        var items = blocks.Cast<NumberedListItemBlock>().ToList();
        Assert.Equal([1, 2, 3], items.Select(i => i.Number));
        Assert.Equal("첫 항목", Assert.Single(items[0].Spans).Text);
        Assert.Equal("셋째 항목", Assert.Single(items[2].Spans).Text);
    }

    [Fact]
    public void 번호_목록은_괄호_구분자도_인식한다()
    {
        var blocks = MarkdownLiteParser.Parse("5) 다섯 번째");

        var item = Assert.IsType<NumberedListItemBlock>(Assert.Single(blocks));
        Assert.Equal(5, item.Number);
        Assert.Equal("다섯 번째", Assert.Single(item.Spans).Text);
    }

    [Fact]
    public void 불릿_목록은_대시와_별표_둘_다_인식한다()
    {
        var blocks = MarkdownLiteParser.Parse("- 대시 항목\n* 별표 항목");

        Assert.Equal(2, blocks.Count);
        var items = blocks.Cast<BulletListItemBlock>().ToList();
        Assert.Equal("대시 항목", Assert.Single(items[0].Spans).Text);
        Assert.Equal("별표 항목", Assert.Single(items[1].Spans).Text);
    }

    [Fact]
    public void 펜스_코드블록은_내용을_그대로_보존하고_인라인_파싱하지_않는다()
    {
        var blocks = MarkdownLiteParser.Parse("```\nvar x = 1;\n# 이건 코드 안이라 제목이 아니다\n```");

        var code = Assert.IsType<CodeBlock>(Assert.Single(blocks));
        Assert.Equal("var x = 1;\n# 이건 코드 안이라 제목이 아니다", code.Code);
        Assert.Null(code.Language);
    }

    [Fact]
    public void 코드블록_여는_펜스의_언어_표기를_읽는다()
    {
        var blocks = MarkdownLiteParser.Parse("```csharp\nConsole.WriteLine(1);\n```");

        var code = Assert.IsType<CodeBlock>(Assert.Single(blocks));
        Assert.Equal("csharp", code.Language);
        Assert.Equal("Console.WriteLine(1);", code.Code);
    }

    [Fact]
    public void 인라인_코드는_앞뒤_일반_텍스트와_분리된_span이_된다()
    {
        var blocks = MarkdownLiteParser.Parse("실행하려면 `dotnet build`를 입력하세요.");

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(blocks));
        Assert.Equal(3, paragraph.Spans.Count);
        Assert.Equal("실행하려면 ", paragraph.Spans[0].Text);
        Assert.False(paragraph.Spans[0].IsCode);
        Assert.Equal("dotnet build", paragraph.Spans[1].Text);
        Assert.True(paragraph.Spans[1].IsCode);
        Assert.Equal("를 입력하세요.", paragraph.Spans[2].Text);
        Assert.False(paragraph.Spans[2].IsCode);
    }

    [Fact]
    public void 문단_목록_코드블록이_섞이면_원래_순서를_유지한다()
    {
        var blocks = MarkdownLiteParser.Parse("설명 문단\n\n1. 목록 항목\n\n```\ncode\n```");

        Assert.Equal(3, blocks.Count);
        Assert.IsType<ParagraphBlock>(blocks[0]);
        Assert.IsType<NumberedListItemBlock>(blocks[1]);
        Assert.IsType<CodeBlock>(blocks[2]);
    }
}
