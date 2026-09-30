using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Threads;

namespace CodexBackupManager.App.ViewModels.Import;

/// <summary>미리보기 한 건의 결과(백그라운드에서 만든 표시용 메시지까지).</summary>
/// <param name="ThreadId">대화.</param>
/// <param name="Messages">메인 Viewer와 같은 표시용 메시지(<see cref="ConversationMessageViewModel.Body"/>는 아직 만들지 않았다).</param>
/// <param name="IsPartial">백업에 없는 조상 등으로 일부만 보이는지.</param>
/// <param name="WarningCount">transcript 경고 수(원문 없음).</param>
public sealed record ImportConversationPreviewResult(
    string ThreadId,
    IReadOnlyList<ConversationMessageViewModel> Messages,
    bool IsPartial,
    int WarningCount);

/// <summary>
/// 가져오기 화면의 대화 내용 미리보기(Phase 9_2b-03/04). 백업 파일 하나에 대해 <see cref="BackupRolloutContentSource"/>를
/// <b>한 번만</b> 열어 재사용하고(<see cref="Dispose"/> 때 닫는다), 백업 entry로 만든 체인으로 메인 Viewer와 같은 transcript를 만든다.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>읽기 전용이다. 임시 파일을 만들지 않고 Codex Home에는 아무것도 쓰지 않는다(백업 entry는 메모리로만 읽는다).</item>
///   <item>최근 3개 결과를 캐시한다(LRU). 작업은 한 번에 하나만 돈다(ZIP은 동시 읽기를 지원하지 않는다).</item>
///   <item>로그에는 개수와 짧은 해시만 남긴다(원문·제목 없음) — 로그는 호출자가 남긴다.</item>
/// </list>
/// </remarks>
public sealed class ImportConversationPreviewer : IDisposable
{
    /// <summary>캐시 크기(설계 §6 "9_2b").</summary>
    public const int CacheSize = 3;

    private readonly string _backupFilePath;
    private readonly int _cacheSize;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<ImportConversationPreviewResult> _cache = new();
    private BackupRolloutContentSource? _source;
    private IReadOnlyDictionary<string, ThreadChain>? _chains;
    private bool _disposed;

    /// <summary>생성자. 파일은 첫 미리보기 때 연다.</summary>
    public ImportConversationPreviewer(string backupFilePath)
        : this(backupFilePath, CacheSize)
    {
    }

    /// <summary>캐시 크기를 정하는 생성자(테스트가 적은 대화로 LRU 밀어내기를 확인할 때 쓴다).</summary>
    internal ImportConversationPreviewer(string backupFilePath, int cacheSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheSize, 1);
        _backupFilePath = backupFilePath;
        _cacheSize = cacheSize;
    }

    /// <summary>이 인스턴스가 백업 파일을 연 횟수(테스트 확인용 — 1이어야 한다).</summary>
    internal int OpenCount { get; private set; }

    /// <summary>transcript를 실제로 만든 횟수(캐시 적중이면 늘지 않는다, 테스트 확인용).</summary>
    internal int BuildCount { get; private set; }

    /// <summary>미리보기를 만든다. 취소되면 <see cref="OperationCanceledException"/>.</summary>
    public async Task<ImportConversationPreviewResult> LoadAsync(string threadId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryGetCached(threadId) is { } cached)
            {
                return cached;
            }

            ImportConversationPreviewResult result = await Task.Run(() => Build(threadId, cancellationToken), cancellationToken).ConfigureAwait(false);
            AddToCache(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ImportConversationPreviewResult Build(string threadId, CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            _source = BackupRolloutContentSource.Open(_backupFilePath);
            OpenCount++;
        }

        _chains ??= _source.ReadCatalog(cancellationToken).Chains;
        cancellationToken.ThrowIfCancellationRequested();

        ConversationTranscript transcript = ConversationTranscriptBuilder.Build(threadId, _chains, _source, cancellationToken);
        BuildCount++;
        List<ConversationMessageViewModel> messages = transcript.Messages.Select(ConversationMessageViewModel.FromDomain).ToList();
        return new ImportConversationPreviewResult(threadId, messages, transcript.Warnings.Count > 0, transcript.Warnings.Count);
    }

    private ImportConversationPreviewResult? TryGetCached(string threadId)
    {
        for (LinkedListNode<ImportConversationPreviewResult>? node = _cache.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value;
            }
        }

        return null;
    }

    private void AddToCache(ImportConversationPreviewResult result)
    {
        _cache.AddFirst(result);
        while (_cache.Count > _cacheSize)
        {
            _cache.RemoveLast();
        }
    }

    /// <summary>캐시에 있는 대화(최근 순, 테스트 확인용).</summary>
    internal IReadOnlyList<string> CachedThreadIds => _cache.Select(r => r.ThreadId).ToList();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();
        _source?.Dispose();
        _source = null;
    }
}
