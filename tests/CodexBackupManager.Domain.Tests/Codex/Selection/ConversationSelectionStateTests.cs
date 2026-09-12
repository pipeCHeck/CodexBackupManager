using System.Collections.Generic;
using CodexBackupManager.Domain.Codex.Selection;
using CodexBackupManager.Domain.Paths;
using Xunit;

namespace CodexBackupManager.Domain.Tests.Codex.Selection;

/// <summary>
/// <see cref="ConversationSelectionState"/> 테스트. Phase 4 백업 선택 상태의 단일 source of truth다.
/// UI(WPF)에 전혀 의존하지 않는 순수 모델이라 여기서 핵심 규칙을 전부 검증한다.
/// </summary>
public sealed class ConversationSelectionStateTests
{
    [Fact]
    public void 초기_선택_개수는_0이다()
    {
        var state = new ConversationSelectionState();

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public void 대화_하나를_선택하면_포함된다()
    {
        var state = new ConversationSelectionState();

        state.Select("thread-1");

        Assert.True(state.IsSelected("thread-1"));
        Assert.Equal(1, state.Count);
    }

    [Fact]
    public void 선택한_대화를_해제하면_제거된다()
    {
        var state = new ConversationSelectionState();
        state.Select("thread-1");

        state.Deselect("thread-1");

        Assert.False(state.IsSelected("thread-1"));
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void 같은_ThreadId를_중복_선택해도_한_번만_기록된다()
    {
        var state = new ConversationSelectionState();
        int changedCount = 0;
        state.Changed += () => changedCount++;

        state.Select("thread-1");
        state.Select("thread-1");
        state.Select("thread-1");

        Assert.Equal(1, state.Count);
        Assert.Equal(1, changedCount); // 두 번째/세 번째 Select는 변화가 없으므로 이벤트도 없어야 한다.
    }

    [Fact]
    public void SelectMany는_중복없이_반영되고_이벤트는_한_번만_발생한다()
    {
        var state = new ConversationSelectionState();
        int changedCount = 0;
        state.Changed += () => changedCount++;

        state.SelectMany(["thread-1", "thread-2", "thread-1", "thread-3"]);

        Assert.Equal(3, state.Count);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void DeselectMany도_이벤트가_한_번만_발생한다()
    {
        var state = new ConversationSelectionState();
        state.SelectMany(["thread-1", "thread-2", "thread-3"]);
        int changedCount = 0;
        state.Changed += () => changedCount++;

        state.DeselectMany(["thread-1", "thread-2"]);

        Assert.Equal(1, state.Count);
        Assert.True(state.IsSelected("thread-3"));
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void 변화가_없는_SelectMany_DeselectMany는_이벤트를_발생시키지_않는다()
    {
        var state = new ConversationSelectionState();
        state.Select("thread-1");
        int changedCount = 0;
        state.Changed += () => changedCount++;

        state.SelectMany(["thread-1"]); // 이미 선택됨
        state.DeselectMany(["thread-2"]); // 애초에 없던 것

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void Clear_하면_전체_선택이_초기화된다()
    {
        var state = new ConversationSelectionState();
        state.SelectMany(["thread-1", "thread-2"]);

        state.Clear();

        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Clear는_이미_비어있으면_이벤트를_발생시키지_않는다()
    {
        var state = new ConversationSelectionState();
        int changedCount = 0;
        state.Changed += () => changedCount++;

        state.Clear();

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void RetainOnly는_유효한_ID는_유지하고_사라진_ID는_제거한다()
    {
        var state = new ConversationSelectionState();
        state.SelectMany(["thread-1", "thread-2", "thread-3"]);

        state.RetainOnly(["thread-1", "thread-3", "thread-9-신규"]); // thread-2가 새 카탈로그에 없음

        Assert.True(state.IsSelected("thread-1"));
        Assert.False(state.IsSelected("thread-2"));
        Assert.True(state.IsSelected("thread-3"));
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public void RetainOnly는_제거할_것이_없으면_이벤트를_발생시키지_않는다()
    {
        var state = new ConversationSelectionState();
        state.SelectMany(["thread-1", "thread-2"]);
        int changedCount = 0;
        state.Changed += () => changedCount++;

        state.RetainOnly(["thread-1", "thread-2", "thread-3"]);

        Assert.Equal(0, changedCount);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public void ClearIfDifferentHome_같은_Home이면_선택을_유지한다()
    {
        var state = new ConversationSelectionState();
        state.Select("thread-1");
        CanonicalPath home = CanonicalPath.Create(@"C:\Users\User\.codex");

        bool cleared = state.ClearIfDifferentHome(home, CanonicalPath.Create(@"c:\USERS\user\.CODEX"));

        Assert.False(cleared);
        Assert.Equal(1, state.Count);
    }

    [Fact]
    public void ClearIfDifferentHome_다른_Home이면_선택을_초기화한다()
    {
        var state = new ConversationSelectionState();
        state.Select("thread-1");
        CanonicalPath oldHome = CanonicalPath.Create(@"C:\Users\User\.codex");
        CanonicalPath newHome = CanonicalPath.Create(@"D:\OtherCodexHome");

        bool cleared = state.ClearIfDifferentHome(oldHome, newHome);

        Assert.True(cleared);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void ClearIfDifferentHome_이전_Home이_없으면_초기화한다()
    {
        var state = new ConversationSelectionState();
        state.Select("thread-1"); // 정상적으로는 비어있을 상황이지만, 규칙 자체를 확인한다.

        bool cleared = state.ClearIfDifferentHome(previousHome: null, CanonicalPath.Create(@"C:\Users\User\.codex"));

        Assert.True(cleared);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Snapshot을_만든_뒤_상태가_바뀌어도_이전_snapshot은_변하지_않는다()
    {
        var state = new ConversationSelectionState();
        state.Select("thread-1");

        IReadOnlySet<string> snapshot = state.Snapshot();
        state.Select("thread-2");
        state.Deselect("thread-1");

        Assert.Single(snapshot);
        Assert.Contains("thread-1", snapshot);
        Assert.DoesNotContain("thread-2", snapshot);
    }
}
