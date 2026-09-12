using System;
using System.Collections.Generic;
using System.Linq;
using CodexBackupManager.Domain.Paths;

namespace CodexBackupManager.Domain.Codex.Selection;

/// <summary>
/// 백업 대상으로 선택된 대화(thread)의 단일 source of truth.
/// </summary>
/// <remarks>
/// <para>
/// <b>Viewer 포커스와는 완전히 분리된 상태다.</b> "오른쪽에서 어떤 대화를 보고 있는가"(Viewer 포커스)와
/// "Phase 5에서 어떤 대화를 백업할 것인가"(이 클래스)는 서로 다른 질문이다. 이 클래스는 오직 후자만
/// 담당하며, TreeView의 선택/포커스 상태를 전혀 참조하지 않는다.
/// </para>
/// <para>
/// <b>ThreadId 기준으로만 관리한다.</b> UI 객체(ViewModel 인스턴스)나 제목, index가 아니라 안정적인
/// <c>ThreadId</c> 문자열로 선택 여부를 판정한다 — 같은 ThreadId가 여러 트리 노드(예: 서로 다른
/// ProjectNode)에 중복 노출되어도 최종 선택 집합에는 중복이 생기지 않는다.
/// </para>
/// <para>
/// <b>단일 저장소.</b> Project/Conversation ViewModel은 이 상태를 그대로 반영하는 얇은 뷰일 뿐,
/// 각자 독립된 <c>IsSelected</c> 필드를 따로 저장하지 않는다 — 세 군데(Project/Conversation/이 클래스)에
/// 상태가 나뉘어 서로 어긋나는 구조를 피하기 위함이다.
/// </para>
/// </remarks>
public sealed class ConversationSelectionState
{
    private readonly HashSet<string> _selectedThreadIds = new(StringComparer.Ordinal);

    /// <summary>선택 집합이 바뀔 때마다 발생한다. 대량 변경(SelectMany 등)에도 한 번만 발생한다.</summary>
    public event Action? Changed;

    /// <summary>현재 선택된 대화 개수.</summary>
    public int Count => _selectedThreadIds.Count;

    /// <summary>이 ThreadId가 선택되어 있는지.</summary>
    public bool IsSelected(string threadId) => _selectedThreadIds.Contains(threadId);

    /// <summary>선택한다. 이미 선택되어 있으면 아무 일도 하지 않는다(중복 방지, 이벤트도 발생하지 않는다).</summary>
    public void Select(string threadId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        if (_selectedThreadIds.Add(threadId))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>선택을 해제한다. 선택되어 있지 않았으면 아무 일도 하지 않는다.</summary>
    public void Deselect(string threadId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        if (_selectedThreadIds.Remove(threadId))
        {
            Changed?.Invoke();
        }
    }

    /// <summary><paramref name="selected"/>에 따라 <see cref="Select"/> 또는 <see cref="Deselect"/>를 호출한다.</summary>
    public void SetSelected(string threadId, bool selected)
    {
        if (selected)
        {
            Select(threadId);
        }
        else
        {
            Deselect(threadId);
        }
    }

    /// <summary>
    /// 여러 개를 한 번에 선택한다. 실제로 하나 이상 바뀌었을 때만 <see cref="Changed"/>가 한 번만 발생한다
    /// (프로젝트/전체 선택처럼 수천 개를 한 번에 다룰 때 항목 수만큼 이벤트가 연쇄되지 않게 한다).
    /// </summary>
    public void SelectMany(IEnumerable<string> threadIds)
    {
        ArgumentNullException.ThrowIfNull(threadIds);

        bool changed = false;
        foreach (string threadId in threadIds)
        {
            changed |= _selectedThreadIds.Add(threadId);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>여러 개를 한 번에 해제한다. <see cref="SelectMany"/>와 동일한 이벤트 규칙을 따른다.</summary>
    public void DeselectMany(IEnumerable<string> threadIds)
    {
        ArgumentNullException.ThrowIfNull(threadIds);

        bool changed = false;
        foreach (string threadId in threadIds)
        {
            changed |= _selectedThreadIds.Remove(threadId);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>전체 선택을 초기화한다. 이미 비어 있으면 이벤트를 발생시키지 않는다.</summary>
    public void Clear()
    {
        if (_selectedThreadIds.Count == 0)
        {
            return;
        }

        _selectedThreadIds.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// <paramref name="validThreadIds"/>에 없는 선택은 제거하고, 있는 선택은 그대로 둔다.
    /// 같은 Codex Home을 "다시 확인"해서 카탈로그를 재구축했을 때, 더 이상 존재하지 않게 된 대화만
    /// 선택에서 빠지고 나머지 선택은 유지하기 위해 쓴다.
    /// </summary>
    public void RetainOnly(IEnumerable<string> validThreadIds)
    {
        ArgumentNullException.ThrowIfNull(validThreadIds);

        var valid = new HashSet<string>(validThreadIds, StringComparer.Ordinal);
        List<string>? toRemove = null;
        foreach (string threadId in _selectedThreadIds)
        {
            if (!valid.Contains(threadId))
            {
                (toRemove ??= []).Add(threadId);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (string threadId in toRemove)
        {
            _selectedThreadIds.Remove(threadId);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Codex Home이 바뀌었으면(이전 Home과 다르면) 선택을 전부 초기화한다. 같은 Home이면 아무것도 하지
    /// 않는다(호출자가 이어서 <see cref="RetainOnly"/>로 사라진 ID만 정리하면 된다).
    /// 서로 다른 Codex Home의 선택이 섞이지 않게 하기 위한 규칙이다.
    /// </summary>
    /// <param name="previousHome">직전에 성공적으로 로드했던 Codex Home. 처음 로드라면 <c>null</c>.</param>
    /// <param name="newHome">이번에 로드한 Codex Home.</param>
    /// <returns>Home이 바뀌어 초기화했으면 <c>true</c>.</returns>
    public bool ClearIfDifferentHome(CanonicalPath? previousHome, CanonicalPath newHome)
    {
        ArgumentNullException.ThrowIfNull(newHome);

        if (previousHome is not null && previousHome.Equals(newHome))
        {
            return false;
        }

        Clear();
        return true;
    }

    /// <summary>
    /// 현재 선택 상태의 불변 스냅샷. 반환된 집합은 호출 시점의 복사본이라, 이후 이 상태가 바뀌어도
    /// 스냅샷 자체는 변하지 않는다. Phase 5(Export)가 UI ViewModel을 직접 해석하지 않고도 백업 대상을
    /// 얻을 수 있도록 만든 API다.
    /// </summary>
    public IReadOnlySet<string> Snapshot() => _selectedThreadIds.ToHashSet(StringComparer.Ordinal);
}
