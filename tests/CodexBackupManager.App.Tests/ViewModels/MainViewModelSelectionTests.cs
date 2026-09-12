using System;
using System.IO;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex;
using CodexBackupManager.Domain.Codex.Selection;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/>에서 "Viewer 포커스"(<see cref="MainViewModel.SelectConversation"/>)와
/// "백업 선택"(<see cref="ConversationNodeViewModel.IsSelected"/>)이 실제로 서로 영향을 주지 않는지
/// 확인한다. 이 요구사항이 Phase 4의 핵심이므로 가장 바깥쪽(ViewModel) 수준에서 직접 검증한다.
/// </summary>
/// <remarks>
/// 실제 <c>.codex</c> 탐지/카탈로그 빌드는 거치지 않는다 — <see cref="MainViewModel.SelectConversation"/>은
/// 카탈로그가 없어도(아직 준비되지 않았다는 오류만 내고) <c>SelectedConversationTitle</c>은 정상적으로
/// 갱신하므로, 이 테스트의 목적(선택 상태와 Viewer 포커스의 독립성)에는 실제 카탈로그가 필요 없다.
/// </remarks>
public sealed class MainViewModelSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
    private readonly MainViewModel _viewModel;
    private readonly ConversationSelectionState _selection = new();

    public MainViewModelSelectionTests()
    {
        Directory.CreateDirectory(_directory);
        _viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_directory, "settings.json")),
            new FileLogger(Path.Combine(_directory, "logs")),
            folderPicker: () => null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Viewer에서_대화를_클릭해도_백업_선택_변화가_없다()
    {
        var node = new ConversationNodeViewModel("제목", "thread-1", _selection);

        _viewModel.SelectConversation(node);

        Assert.False(_selection.IsSelected("thread-1"));
        Assert.Equal(0, _selection.Count);
        Assert.Equal("제목", _viewModel.SelectedConversationTitle); // Viewer 포커스는 정상적으로 바뀌었다.
    }

    [Fact]
    public void 체크_상태를_바꿔도_현재_Viewer는_바뀌지_않는다()
    {
        var viewedNode = new ConversationNodeViewModel("보고 있는 대화", "thread-viewed", _selection);
        var otherNode = new ConversationNodeViewModel("체크만 하는 대화", "thread-other", _selection);
        _viewModel.SelectConversation(viewedNode);

        otherNode.IsSelected = true;

        Assert.Equal("보고 있는 대화", _viewModel.SelectedConversationTitle);
        Assert.True(otherNode.IsSelected);
        Assert.False(_selection.IsSelected("thread-viewed"));
    }

    [Fact]
    public void 지금_보고_있는_대화를_체크해도_Viewer_포커스는_그대로다()
    {
        var node = new ConversationNodeViewModel("보고 있는 대화", "thread-1", _selection);
        _viewModel.SelectConversation(node);

        node.IsSelected = true; // 지금 보고 있는 바로 그 대화를 백업 대상으로도 체크

        Assert.Equal("보고 있는 대화", _viewModel.SelectedConversationTitle); // Viewer는 여전히 이 대화를 보여준다.
        Assert.True(node.IsSelected);
    }
}
