using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.App.Services;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Codex;
using Xunit;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 4 사후 정합성 수정: <see cref="MainViewModel.SelectAllConversationsCommand"/>/
/// <see cref="MainViewModel.ClearSelectionCommand"/>가 중앙 <c>ConversationSelectionState</c>를
/// 프로젝트 수만큼(N번) 바꾸는 게 아니라 카탈로그 전체에 대해 딱 한 번만 bulk mutation하는지,
/// <c>Changed</c> 이벤트 발생 횟수로 직접 검증한다. <c>InternalsVisibleTo</c>로 노출된
/// <see cref="MainViewModel.Selection"/>을 통해 WPF 없이 순수 ViewModel 수준에서 확인한다.
/// </summary>
public sealed class MainViewModelBulkSelectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-app-tests", Guid.NewGuid().ToString("N"));
    private readonly MainViewModel _viewModel;

    public MainViewModelBulkSelectionTests()
    {
        Directory.CreateDirectory(_directory);
        _viewModel = new MainViewModel(
            new CodexDetectionService(),
            new SettingsStore(Path.Combine(_directory, "settings.json")),
            new FileLogger(Path.Combine(_directory, "logs")),
            folderPicker: () => null);

        // 실제 탐지/카탈로그 빌드 파이프라인 없이, 여러 프로젝트에 걸친 대량 노드를 직접 구성한다.
        // 반드시 viewModel.Selection(내부 접근자)을 공유해야 MainViewModel이 실제로 쓰는 것과
        // 같은 저장소를 관찰할 수 있다.
        const int projectCount = 5;
        const int conversationsPerProject = 100; // 총 500개 — 요구사항의 "500개/여러 프로젝트" 시나리오.
        for (int p = 0; p < projectCount; p++)
        {
            var conversations = new List<ConversationNodeViewModel>();
            for (int c = 0; c < conversationsPerProject; c++)
            {
                conversations.Add(new ConversationNodeViewModel($"제목 {p}-{c}", $"thread-{p}-{c}", _viewModel.Selection));
            }

            _viewModel.ProjectNodes.Add(new ProjectNodeViewModel($"프로젝트 {p}", isUncategorized: false, conversations, _viewModel.Selection));
        }
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

    private int TotalConversations => _viewModel.ProjectNodes.Sum(p => p.Conversations.Count);

    [Fact]
    public void 여러_프로젝트_500개_전체_선택은_Changed가_한_번만_발생한다()
    {
        int changedCount = 0;
        _viewModel.Selection.Changed += () => changedCount++;

        _viewModel.SelectAllConversationsCommand.Execute(null);

        Assert.Equal(1, changedCount);
        Assert.Equal(TotalConversations, _viewModel.SelectedConversationCount);
        Assert.All(_viewModel.ProjectNodes, p => Assert.All(p.Conversations, c => Assert.True(c.IsSelected)));
    }

    [Fact]
    public void 전체_해제는_Changed가_한_번만_발생한다()
    {
        _viewModel.SelectAllConversationsCommand.Execute(null);
        int changedCount = 0;
        _viewModel.Selection.Changed += () => changedCount++;

        _viewModel.ClearSelectionCommand.Execute(null);

        Assert.Equal(1, changedCount);
        Assert.Equal(0, _viewModel.SelectedConversationCount);
        Assert.All(_viewModel.ProjectNodes, p => Assert.All(p.Conversations, c => Assert.False(c.IsSelected)));
    }

    [Fact]
    public void 이미_전체_선택_상태에서_다시_전체_선택하면_Changed가_발생하지_않는다()
    {
        _viewModel.SelectAllConversationsCommand.Execute(null);
        int changedCount = 0;
        _viewModel.Selection.Changed += () => changedCount++;

        _viewModel.SelectAllConversationsCommand.Execute(null);

        Assert.Equal(0, changedCount);
        Assert.Equal(TotalConversations, _viewModel.SelectedConversationCount);
    }

    [Fact]
    public void 이미_비어있을_때_전체_해제하면_Changed가_발생하지_않는다()
    {
        Assert.Equal(0, _viewModel.SelectedConversationCount); // 전제 확인
        int changedCount = 0;
        _viewModel.Selection.Changed += () => changedCount++;

        _viewModel.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, changedCount);
    }
}
