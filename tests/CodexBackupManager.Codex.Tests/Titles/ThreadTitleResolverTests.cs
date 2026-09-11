using System.Collections.Generic;
using CodexBackupManager.Codex.Titles;
using CodexBackupManager.Domain.Codex.Threads;
using CodexBackupManager.Domain.Codex.Titles;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Titles;

public sealed class ThreadTitleResolverTests
{
    private static ThreadRow Row(
        string? name = null, string? title = null, string? firstUserMessage = null, string? preview = null)
        => new()
        {
            Id = "01a00000-0000-7000-8000-000000000001",
            Name = name,
            Title = title,
            FirstUserMessage = firstUserMessage,
            Preview = preview,
        };

    [Fact]
    public void state_name이_있으면_1순위다()
    {
        ThreadTitle title = ThreadTitleResolver.Resolve(
            Row(name: "짧은 제목", title: "t", firstUserMessage: "f", preview: "p"),
            new Dictionary<string, string> { ["01a00000-0000-7000-8000-000000000001"] = "session-index" });

        Assert.Equal("짧은 제목", title.Text);
        Assert.Equal(ThreadTitleSource.StateName, title.Source);
    }

    [Fact]
    public void state_name이_없으면_session_index를_쓴다()
    {
        ThreadTitle title = ThreadTitleResolver.Resolve(
            Row(title: "t", firstUserMessage: "f", preview: "p"),
            new Dictionary<string, string> { ["01a00000-0000-7000-8000-000000000001"] = "session-index" });

        Assert.Equal("session-index", title.Text);
        Assert.Equal(ThreadTitleSource.SessionIndex, title.Source);
    }

    [Fact]
    public void 그다음은_state_title이다()
    {
        ThreadTitle title = ThreadTitleResolver.Resolve(
            Row(title: "t", firstUserMessage: "f", preview: "p"),
            new Dictionary<string, string>());

        Assert.Equal("t", title.Text);
        Assert.Equal(ThreadTitleSource.StateTitle, title.Source);
    }

    [Fact]
    public void 그다음은_first_user_message다()
    {
        ThreadTitle title = ThreadTitleResolver.Resolve(
            Row(firstUserMessage: "f", preview: "p"),
            new Dictionary<string, string>());

        Assert.Equal("f", title.Text);
        Assert.Equal(ThreadTitleSource.FirstUserMessage, title.Source);
    }

    [Fact]
    public void 그다음은_preview다()
    {
        ThreadTitle title = ThreadTitleResolver.Resolve(Row(preview: "p"), new Dictionary<string, string>());

        Assert.Equal("p", title.Text);
        Assert.Equal(ThreadTitleSource.Preview, title.Source);
    }

    [Fact]
    public void 아무것도_없으면_thread_id_기반_기본값이다()
    {
        ThreadTitle title = ThreadTitleResolver.Resolve(Row(), new Dictionary<string, string>());

        Assert.Equal(ThreadTitleSource.ThreadIdFallback, title.Source);
        Assert.Contains("01a00000", title.Text);
    }
}
