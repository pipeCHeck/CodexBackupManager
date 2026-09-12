using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Domain.Codex.Selection;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 4 Selection의 ViewModel 계층(<see cref="ProjectNodeViewModel"/>/<see cref="ConversationNodeViewModel"/>)
/// 테스트. 전부 <see cref="ConversationSelectionState"/> 하나를 공유해 구성하고, WPF 없이(트리/체크박스
/// 렌더링 없이) 순수 ViewModel 수준에서 검증한다.
/// </summary>
public sealed class SelectionViewModelTests
{
    private static ConversationNodeViewModel Conversation(ConversationSelectionState selection, string threadId, string title = "제목")
        => new(title, threadId, selection);

    private static ProjectNodeViewModel Project(
        ConversationSelectionState selection, string displayName, IReadOnlyList<ConversationNodeViewModel> conversations, bool isUncategorized = false)
        => new(displayName, isUncategorized, conversations, selection);

    [Fact]
    public void 새_ConversationNode는_기본적으로_선택되지_않는다()
    {
        var selection = new ConversationSelectionState();
        ConversationNodeViewModel node = Conversation(selection, "thread-1");

        Assert.False(node.IsSelected);
    }

    [Fact]
    public void 대화_체크박스를_선택하면_중앙_상태에_반영된다()
    {
        var selection = new ConversationSelectionState();
        ConversationNodeViewModel node = Conversation(selection, "thread-1");

        node.IsSelected = true;

        Assert.True(selection.IsSelected("thread-1"));
    }

    [Fact]
    public void 대화_체크박스를_해제하면_중앙_상태에서_제거된다()
    {
        var selection = new ConversationSelectionState();
        ConversationNodeViewModel node = Conversation(selection, "thread-1");
        node.IsSelected = true;

        node.IsSelected = false;

        Assert.False(selection.IsSelected("thread-1"));
    }

    [Fact]
    public void 프로젝트_체크박스를_선택하면_모든_자식이_선택된다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);

        project.IsSelected = true;

        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.Equal(2, selection.Count);
    }

    [Fact]
    public void 프로젝트_체크박스를_해제하면_모든_자식이_해제된다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);
        project.IsSelected = true;

        project.IsSelected = false;

        Assert.False(a.IsSelected);
        Assert.False(b.IsSelected);
        Assert.Equal(0, selection.Count);
    }

    [Fact]
    public void 자식_하나만_선택하면_프로젝트는_indeterminate다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);

        a.IsSelected = true;

        Assert.Null(project.IsSelected);
    }

    [Fact]
    public void indeterminate_상태에서_프로젝트를_클릭하면_전체_선택된다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);
        a.IsSelected = true;
        Assert.Null(project.IsSelected); // 전제 확인

        // WPF tri-state 체크박스가 클릭 시 어떤 원시값(true/false/null)을 전달하든, 클릭 "의도"는
        // 항상 같다 — 현재 indeterminate/미선택이면 전체 선택. 설정값 자체는 무시하고
        // 클릭 직전의 계산된 상태만으로 목표를 정하므로 아무 값이나 대입해도 결과는 같아야 한다.
        project.IsSelected = true;

        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.True(project.IsSelected);
    }

    [Fact]
    public void 전체_선택_상태에서_프로젝트를_클릭하면_전체_해제된다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);
        project.IsSelected = true;
        Assert.True(project.IsSelected); // 전제 확인

        project.IsSelected = true; // 원시값과 무관하게 "전체 선택된 상태에서의 클릭"은 항상 전체 해제다.

        Assert.False(a.IsSelected);
        Assert.False(b.IsSelected);
        Assert.False(project.IsSelected);
    }

    [Fact]
    public void 모든_child를_개별_선택하면_project는_true가_된다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);

        a.IsSelected = true;
        b.IsSelected = true;

        Assert.True(project.IsSelected);
    }

    [Fact]
    public void 모든_child를_개별_해제하면_project는_false가_된다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel project = Project(selection, "P", [a, b]);
        a.IsSelected = true;
        b.IsSelected = true;

        a.IsSelected = false;
        b.IsSelected = false;

        Assert.False(project.IsSelected);
    }

    [Fact]
    public void 서로_다른_프로젝트의_선택은_독립적이다()
    {
        var selection = new ConversationSelectionState();
        var a1 = Conversation(selection, "thread-a1");
        var a2 = Conversation(selection, "thread-a2");
        var b1 = Conversation(selection, "thread-b1");
        ProjectNodeViewModel projectA = Project(selection, "A", [a1, a2]);
        ProjectNodeViewModel projectB = Project(selection, "B", [b1]);

        projectA.IsSelected = true; // A 전체 선택

        Assert.True(projectA.IsSelected);
        Assert.False(projectB.IsSelected); // B는 영향받지 않는다
        Assert.False(b1.IsSelected);
        Assert.Equal(2, selection.Count);
    }

    [Fact]
    public void 기타_대화_그룹도_일반_프로젝트와_동일하게_동작한다()
    {
        var selection = new ConversationSelectionState();
        var a = Conversation(selection, "thread-1");
        var b = Conversation(selection, "thread-2");
        ProjectNodeViewModel uncategorized = Project(selection, "기타 대화", [a, b], isUncategorized: true);

        uncategorized.IsSelected = true;

        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.True(uncategorized.IsSelected);
    }

    [Fact]
    public void 같은_ThreadId가_여러_ProjectNode에_노출돼도_최종_선택에는_중복이_없다()
    {
        var selection = new ConversationSelectionState();
        // 실제로는 카탈로그가 같은 ThreadId를 두 그룹에 동시에 넣을 일은 없지만, 방어적으로
        // "ThreadId 기준" 규칙 자체를 확인한다 — 서로 다른 노드 인스턴스라도 같은 ThreadId면 하나다.
        var nodeInProjectA = Conversation(selection, "thread-dup");
        var nodeInProjectB = Conversation(selection, "thread-dup");
        Project(selection, "A", [nodeInProjectA]);
        Project(selection, "B", [nodeInProjectB]);

        nodeInProjectA.IsSelected = true;
        nodeInProjectB.IsSelected = true;

        Assert.Equal(1, selection.Count);
        Assert.Single(selection.Snapshot());
    }

    [Fact]
    public void 대량_선택_시_자식_수만큼만_PropertyChanged가_발생한다()
    {
        var selection = new ConversationSelectionState();
        var conversations = Enumerable.Range(0, 500)
            .Select(i => Conversation(selection, $"thread-{i}"))
            .ToList();
        ProjectNodeViewModel project = Project(selection, "P", conversations);

        int notifyCount = 0;
        foreach (ConversationNodeViewModel c in conversations)
        {
            c.PropertyChanged += (_, _) => notifyCount++;
        }

        int projectNotifyCount = 0;
        project.PropertyChanged += (_, _) => projectNotifyCount++;

        project.IsSelected = true;

        Assert.Equal(conversations.Count, notifyCount); // 자식마다 정확히 한 번씩만(연쇄/재귀 없음)
        Assert.Equal(1, projectNotifyCount); // 프로젝트 자신도 한 번만
        Assert.True(project.IsSelected);
    }
}
