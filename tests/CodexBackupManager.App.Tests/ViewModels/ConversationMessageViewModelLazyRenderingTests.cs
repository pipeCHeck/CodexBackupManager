using System.Linq;
using System.Windows.Documents;
using CodexBackupManager.App.Rendering;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Domain.Codex.Conversation;
using Xunit;
using FlowList = System.Windows.Documents.List;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 4 UI 성능 정합성 수정: <see cref="ConversationMessageViewModel.Body"/>(WPF
/// <see cref="FlowDocument"/>)는 생성 시점이 아니라 처음 <b>요청</b>했을 때만 만들어져야 한다.
/// 긴 대화(1,431개 메시지)를 열 때 UI 스레드에서 전부를 즉시 렌더링하면 초기 정지/메모리 증가가
/// 생기므로, virtualization이 실제로 "화면에 보이는 것만 렌더링 비용을 지불"하게 하려면 이 지연이
/// 반드시 필요하다.
/// </summary>
public sealed class ConversationMessageViewModelLazyRenderingTests
{
    private static ConversationMessage Message(string text, ConversationRole role = ConversationRole.User)
        => new() { Role = role, Text = text };

    private static ConversationMessageViewModel CreateViewModel(string text)
        => ConversationMessageViewModel.FromDomain(Message(text));

    [Fact]
    public void 생성_직후에는_Body가_아직_렌더링되지_않았다()
    {
        var vm = CreateViewModel("안녕하세요");

        Assert.False(vm.IsBodyRendered);
    }

    [Fact]
    public void Body를_처음_요청하면_그_시점에_렌더링된다()
    {
        var vm = CreateViewModel("안녕하세요");

        FlowDocument body = vm.Body;

        Assert.True(vm.IsBodyRendered);
        Assert.NotEmpty(body.Blocks.ToList());
    }

    [Fact]
    public void Body를_여러_번_요청해도_같은_인스턴스를_재사용한다()
    {
        var vm = CreateViewModel("안녕하세요");

        FlowDocument first = vm.Body;
        FlowDocument second = vm.Body;

        Assert.Same(first, second);
    }

    [Fact]
    public void 천개를_만들어도_Body는_즉시_렌더링되지_않는다()
    {
        var viewModels = Enumerable.Range(0, 1000)
            .Select(i => CreateViewModel($"메시지 본문 {i}"))
            .ToList();

        Assert.All(viewModels, vm => Assert.False(vm.IsBodyRendered));
    }

    [Fact]
    public void 요청한_것만_렌더링되고_나머지는_그대로_지연된다()
    {
        var viewModels = Enumerable.Range(0, 50)
            .Select(i => CreateViewModel($"메시지 본문 {i}"))
            .ToList();

        _ = viewModels[10].Body;
        _ = viewModels[20].Body;

        for (int i = 0; i < viewModels.Count; i++)
        {
            bool expectedRendered = i is 10 or 20;
            Assert.Equal(expectedRendered, viewModels[i].IsBodyRendered);
        }
    }

    [Fact]
    public void 지연_렌더링_결과는_즉시_렌더링했을_때와_동일한_구조를_준다()
    {
        const string markdown = "# 제목\n\n설명 문단입니다.\n\n1. 하나\n2. 둘\n\n- 불릿\n\n```\nvar x = 1;\n```";

        var vm = CreateViewModel(markdown);
        FlowDocument lazyBody = vm.Body;
        FlowDocument directBody = MarkdownLiteFlowDocumentRenderer.Render(MarkdownLiteParser.Parse(markdown));

        var lazyKinds = lazyBody.Blocks.Select(DescribeBlock).ToList();
        var directKinds = directBody.Blocks.Select(DescribeBlock).ToList();
        Assert.Equal(directKinds, lazyKinds);
    }

    private static string DescribeBlock(Block block) => block switch
    {
        Paragraph p => $"Paragraph:{p.Inlines.Count}",
        FlowList l => $"List:{l.MarkerStyle}:{l.ListItems.Count}",
        _ => block.GetType().Name,
    };
}
