using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels.Import;
using Xunit;
using static CodexBackupManager.App.Tests.TestSupport.ImportWorkspaceHarness;

namespace CodexBackupManager.App.Tests.ViewModels;

/// <summary>
/// Phase 9_2-3 — 가져오기 화면의 대화 내용 미리보기(9_2b)와 화면 다듬기(9_2-26~28). 커밋된 fixture 복사본만 쓴다.
/// </summary>
public sealed class ImportWorkspacePreviewTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static ImportConversationNodeViewModel Node(ImportWorkspaceViewModel ws, string threadId)
        => ws.Projects.SelectMany(p => p.Conversations).First(c => c.ThreadId == threadId);

    private async Task<string> ExportWithMarkersAsync()
    {
        // 9_2-31: harness가 복사본의 rollout_path를 절대 경로로 보정하므로 Thread1(대상 PC에 그대로 있음 → 이어받기)에도 붙인다.
        _h.AppendToSourceRollout(Thread1, 2, "마커-하나");
        _h.AppendToSourceRollout(Thread2, 2, "마커-둘");
        _h.AppendToSourceRollout(Thread3, 2, "마커-셋");
        return await _h.ExportAsync();
    }

    // ── 9_2-27 자동 선택 / 9_2b 내용 표시 ──────────────────────────────────────

    [Fact]
    public async Task 편집에_들어가면_첫_대화가_선택되고_내용이_보인다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await ExportWithMarkersAsync();

        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        Assert.NotNull(ws.SelectedConversation);
        Assert.True(ws.SelectedConversation!.IsTreeSelected);
        Assert.False(ws.HasNoSelection);
        Assert.NotEmpty(ws.ContentMessages);
        Assert.False(ws.IsContentLoading);
        Assert.Contains(ws.ContentMessages, m => m.IsUser);
        Assert.Contains(ws.ContentMessages, m => !m.IsUser);
        Assert.Equal(Thread1, ws.SelectedConversation.ThreadId); // 첫 프로젝트(Alpha)의 첫 대화
        Assert.All(ws.ContentMessages, m => Assert.False(m.IsBodyRendered)); // FlowDocument는 화면에 보일 때만 만든다
        Assert.Contains(ws.ContentMessages, m => m.Text == "fixture user text");
        Assert.Contains(ws.ContentMessages, m => m.Text.Contains("마커-하나", StringComparison.Ordinal)); // 백업 쪽(더 긴) 기록
    }

    [Fact]
    public async Task 다시_분석해도_이전_선택을_유지한다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await ExportWithMarkersAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ws.SelectNode(Node(ws, Thread3));
        await ContentLoadedAsync(ws);
        ImportConversationPreviewer previewer = ws.Previewer!;

        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing && ws.AnalysisStartCount == 2, "다시 분석");
        await ContentLoadedAsync(ws);

        Assert.Equal(Thread3, ws.SelectedConversation!.ThreadId);
        Assert.Contains(ws.ContentMessages, m => m.Text.Contains("마커-셋", StringComparison.Ordinal));
        Assert.Same(previewer, ws.Previewer); // 같은 백업이면 미리보기 reader를 다시 열지 않는다
        Assert.Equal(1, previewer.OpenCount);
    }

    // ── 9_2b-03/04 reader 한 번, 캐시, 수명 ─────────────────────────────────────

    [Fact]
    public async Task 미리보기_reader는_한_번만_열고_최근_대화는_캐시하며_닫으면_Dispose한다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await ExportWithMarkersAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ImportConversationPreviewer previewer = ws.Previewer!;

        foreach (string id in new[] { Thread1, Thread2, Thread3, Thread1, Thread2 })
        {
            ws.SelectNode(Node(ws, id));
            await ContentLoadedAsync(ws);
        }

        Assert.Equal(1, previewer.OpenCount);
        Assert.Equal(3, previewer.BuildCount); // 세 대화 한 번씩만 만들고 나머지는 캐시
        Assert.Equal([Thread2, Thread1, Thread3], previewer.CachedThreadIds);

        // 미리보기 reader가 열려 있어도 적용(Plan 생성의 SHA-256 재확인과 Apply)이 막히지 않는다.
        await ws.ImportAsync();
        Assert.Equal(CodexBackupManager.Restore.RestoreOutcome.Succeeded, ws.Result!.Outcome);

        ws.CloseCommand.Execute(null);
        Assert.Null(ws.Previewer);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => previewer.LoadAsync(Thread1, CancellationToken.None));
    }

    [Fact]
    public async Task 캐시는_최근_N개만_남긴다()
    {
        string backup = await ExportWithMarkersAsync();
        using var previewer = new ImportConversationPreviewer(backup, cacheSize: 2);

        await previewer.LoadAsync(Thread1, CancellationToken.None);
        await previewer.LoadAsync(Thread2, CancellationToken.None);
        await previewer.LoadAsync(Thread3, CancellationToken.None);
        await previewer.LoadAsync(Thread1, CancellationToken.None); // 밀려났으므로 다시 만든다

        Assert.Equal(4, previewer.BuildCount);
        Assert.Equal([Thread1, Thread3], previewer.CachedThreadIds);
    }

    [Fact]
    public async Task 다른_파일을_열면_이전_미리보기를_닫는다()
    {
        string first = await ExportWithMarkersAsync();
        string second = await _h.ExportAsync(Thread2);
        string pick = first;
        ImportWorkspaceViewModel ws = _h.CreateWorkspace(() => pick);
        await ws.OpenAsync();
        await ContentLoadedAsync(ws);
        ImportConversationPreviewer old = ws.Previewer!;

        ws.CloseCommand.Execute(null);
        pick = second;
        await ws.OpenAsync();
        await ContentLoadedAsync(ws);

        Assert.NotSame(old, ws.Previewer);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => old.LoadAsync(Thread1, CancellationToken.None));
    }

    // ── 9_2b-T3 빠른 선택 전환 ────────────────────────────────────────────────

    [Fact]
    public async Task 빠르게_선택을_바꾸면_마지막_대화만_보인다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await ExportWithMarkersAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        var tasks = new List<Task>();
        foreach (string id in new[] { Thread2, Thread3, Thread1, Thread2, Thread3 })
        {
            ws.SelectNode(Node(ws, id));
            tasks.Add(ws.ContentLoadTask!);
        }

        await Task.WhenAll(tasks);

        Assert.Equal(Thread3, ws.SelectedConversation!.ThreadId);
        Assert.Contains(ws.ContentMessages, m => m.Text.Contains("마커-셋", StringComparison.Ordinal));
        Assert.DoesNotContain(ws.ContentMessages, m => m.Text.Contains("마커-둘", StringComparison.Ordinal));
        Assert.False(ws.IsContentLoading);

        // 프로젝트를 고르면 대화 내용은 비운다.
        ws.SelectNode(ws.Projects[0]);
        Assert.Empty(ws.ContentMessages);
    }

    // ── 9_2b-T2 쓰기 0건, temp 파일 0건 ─────────────────────────────────────────

    [Fact]
    public async Task 미리보기_동안_Codex_Home에_쓰지_않고_temp에_기록_파일을_만들지_않는다()
    {
        _h.RemoveFromTarget(Thread2);
        string backup = await ExportWithMarkersAsync();
        Dictionary<string, string> homeBefore = HashTree(_h.TargetHome);
        HashSet<string> tempBefore = TempFiles();

        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        foreach (ImportConversationNodeViewModel node in ws.Projects.SelectMany(p => p.Conversations).ToList())
        {
            ws.SelectNode(node);
            await ContentLoadedAsync(ws);
        }

        List<string> created = TempFiles().Except(tempBefore).ToList();
        Assert.Equal(homeBefore, HashTree(_h.TargetHome));
        // 다른 테스트/프로세스도 temp를 쓰므로 "대화 기록으로 보이는 새 파일"이 0건인지 본다(rollout, jsonl, zst, 백업).
        Assert.DoesNotContain(created, f =>
            f.Contains("rollout", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".codexbackup", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> TempFiles()
    {
        string temp = Path.GetTempPath();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in Directory.EnumerateFileSystemEntries(temp))
        {
            string name = Path.GetFileName(entry);
            if (name.StartsWith("cbm-", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 이 테스트 스위트의 fixture 복사본 폴더(쓰기 대상이 정해져 있다)
            }

            try
            {
                if (File.Exists(entry))
                {
                    files.Add(entry);
                }
                else
                {
                    foreach (string file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files;
    }

    // ── 9_2-26 행 위치 / 9_2-28 버전 표기 ──────────────────────────────────────

    [Fact]
    public async Task 이미_있는_대화는_행_안에_이_PC_위치가_보이고_새_대화는_없다()
    {
        _h.RemoveFromTarget(Thread3);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        Assert.Equal("이 PC 위치: 기타 대화", Node(ws, Thread2).RowLocationText);
        Assert.Equal("이 PC 위치: Alpha 프로젝트", Node(ws, Thread1).RowLocationText);
        Assert.Null(Node(ws, Thread3).RowLocationText);
        Assert.Equal($"{Node(ws, Thread2).Title}, 이미 있음, 이 PC 위치: 기타 대화", Node(ws, Thread2).AutomationName);
        Assert.Equal($"{Node(ws, Thread3).Title}, 새 대화", Node(ws, Thread3).AutomationName);
    }

    [Fact]
    public async Task 머리_정보는_백업을_만든_앱_버전을_그렇게_표시한다()
    {
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        string expected = ImportTexts.BackupAppVersion(ws.CurrentPreview!.Manifest!.AppVersion);
        Assert.StartsWith("만든 앱 v", expected);
        Assert.Contains(expected, ws.BackupHeaderText);
        Assert.DoesNotContain(" · 앱 ", ws.BackupHeaderText);
    }

    // ── 9_2-31 이어받기(IncomingAhead) App E2E ─────────────────────────────────

    [Fact]
    public async Task 이어받기를_포함한_가져오기가_성공하고_결과와_rollout_바이트가_백업과_같다()
    {
        _h.RemoveFromTarget(Thread3);
        _h.AppendToSourceRollout(Thread1, 4, "이어받을-줄");
        string backup = await _h.ExportAsync(Thread1, Thread3);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);

        ImportConversationNodeViewModel thread1 = Node(ws, Thread1);
        Assert.Equal(CodexBackupManager.Domain.Codex.Import.RevisionRelation.IncomingAhead, thread1.Result.Preview.Relation);
        Assert.True(thread1.IsChecked);
        Assert.Equal("이 PC 위치: Alpha 프로젝트", thread1.PresenceText);
        Assert.StartsWith("이 PC에 있지만 백업이 더 깁니다", thread1.StatusSentence);
        Assert.True(ws.CanImport);

        await ws.ImportAsync();

        Assert.Equal(CodexBackupManager.Restore.RestoreOutcome.Succeeded, ws.Result!.Outcome);
        List<ImportResultItem> items = ws.Result.Groups.SelectMany(g => g.Items).ToList();
        Assert.Contains(items, i => i.Title == thread1.Title && i.Text == "이어받음");
        Assert.Contains(items, i => i.Text == "새로 가져옴");
        Assert.Contains(Thread1, ws.Result.ImportedThreadIds);

        // 대상 rollout이 원본 PC(=백업에 들어간) rollout과 바이트까지 같다.
        byte[] source = File.ReadAllBytes(RolloutFileOf(_h.SourceHome, Thread1));
        byte[] target = File.ReadAllBytes(RolloutFileOf(_h.TargetHome, Thread1));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)),
                     Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(target)));
    }

    // ── 9_2-32 다시 분석 뒤 이전 선택이 사라진 경우 ─────────────────────────────

    [Fact]
    public async Task 다시_분석했을_때_이전_선택_대화가_없어졌으면_첫_대화로_돌아간다()
    {
        _h.RemoveFromTarget(Thread2, Thread3);
        string backup = await ExportWithMarkersAsync();
        string smaller = await _h.ExportAsync(Thread1, Thread2); // Thread3 없음
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ws.SelectNode(Node(ws, Thread3));
        await ContentLoadedAsync(ws);
        ImportConversationPreviewer first = ws.Previewer!;

        // 같은 경로의 백업 내용이 바뀐다(미리보기 reader가 열려 있어도 교체할 수 있다 — FileShare.ReadWrite|Delete).
        File.Copy(smaller, backup, overwrite: true);
        ws.ReanalyzeCommand.Execute(null);
        await WaitUntil(() => ws.State == ImportWorkspaceState.Editing && ws.AnalysisStartCount == 2, "다시 분석");
        await ContentLoadedAsync(ws);

        Assert.DoesNotContain(ws.Projects.SelectMany(p => p.Conversations), c => c.ThreadId == Thread3);
        Assert.Equal(Thread1, ws.SelectedConversation!.ThreadId);
        Assert.True(ws.SelectedConversation.IsTreeSelected);
        Assert.DoesNotContain(ws.ContentMessages, m => m.Text.Contains("마커-셋", StringComparison.Ordinal));
        Assert.NotSame(first, ws.Previewer); // 내용이 바뀐 백업이면 미리보기를 새로 연다(이전 캐시를 쓰지 않는다)
    }

    // ── 9_2-30 상세 문구 ───────────────────────────────────────────────────────

    [Fact]
    public async Task 상세의_위치_줄은_상태_문장을_되풀이하지_않는다()
    {
        string backup = await _h.ExportAsync(Thread2);
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ImportConversationNodeViewModel node = Node(ws, Thread2);

        Assert.Equal("이미 이 PC에 있습니다. 내용이 같습니다.", node.StatusSentence);
        Assert.Equal("이 PC 위치: 기타 대화", node.PresenceText);
        Assert.DoesNotContain("있음", node.PresenceText);
    }
}
