using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Inspection;
using CodexBackupManager.Codex.Locating;
using CodexBackupManager.Codex.Sqlite;

namespace CodexBackupManager.Restore.Undo;

/// <summary>
/// (Phase 9_4-06) 대화가 Codex에서 열린 흔적을 센다(<b>읽기 전용</b>). 세 곳을 본다(9_0-A 실측).
/// <list type="bullet">
///   <item>(a) <c>thread_history_*.sqlite</c>에서 <c>thread_id</c> 컬럼을 가진 모든 테이블의 그 대화 행 수
///     (이 PC 실측: thread_turns, thread_items, thread_history_projection_state, thread_realtime_items)</item>
///   <item>(b) global-state <c>electron-persisted-atom-state</c> 원문 안의 thread ID 등장 횟수</item>
///   <item>(c) <c>session_index.jsonl</c>에서 그 thread ID가 든 줄 수</item>
/// </list>
/// 파일이 없으면 0이다. 파일은 있는데 열 수 없거나(WAL을 반영할 수 없는 immutable 폴백 포함) <c>thread_id</c> 테이블이 하나도 없거나 JSON을
/// 해석할 수 없으면 <c>null</c>(알 수 없음)이다 — 되돌리기는 보수적으로 거부한다.
/// </summary>
public static class CodexTraceInspector
{
    /// <summary>global-state의 대화 흔적 키.</summary>
    public const string AtomStateKey = "electron-persisted-atom-state";

    /// <summary>대화별 흔적 수.</summary>
    public static IReadOnlyDictionary<string, UndoTraceBaseline> Count(string codexHomePath, IReadOnlyCollection<string> threadIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHomePath);
        ArgumentNullException.ThrowIfNull(threadIds);
        var result = new Dictionary<string, UndoTraceBaseline>(StringComparer.OrdinalIgnoreCase);
        if (threadIds.Count == 0)
        {
            return result;
        }

        Dictionary<string, long>? history = CountHistoryRows(codexHomePath, threadIds);
        Dictionary<string, long>? atom = CountAtomStateMentions(codexHomePath, threadIds);
        Dictionary<string, long>? index = CountSessionIndexLines(codexHomePath, threadIds);
        foreach (string id in threadIds)
        {
            result[id] = new UndoTraceBaseline(id, history?[id], atom?[id], index?[id]);
        }

        return result;
    }

    /// <summary>기준값과 지금 값이 같은지(어느 쪽이든 알 수 없으면 <c>null</c>).</summary>
    public static bool? Unchanged(UndoTraceBaseline baseline, UndoTraceBaseline current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        if (baseline.HistoryRows is null || baseline.AtomStateMentions is null || baseline.SessionIndexLines is null ||
            current.HistoryRows is null || current.AtomStateMentions is null || current.SessionIndexLines is null)
        {
            return null;
        }

        return baseline.HistoryRows == current.HistoryRows &&
               baseline.AtomStateMentions == current.AtomStateMentions &&
               baseline.SessionIndexLines == current.SessionIndexLines;
    }

    private static Dictionary<string, long> Zero(IReadOnlyCollection<string> ids)
        => ids.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(id => id, _ => 0L, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, long>? CountHistoryRows(string home, IReadOnlyCollection<string> ids)
    {
        Dictionary<string, long> counts = Zero(ids);
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(home, "thread_history_*.sqlite", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string file in files)
        {
            using ReadOnlyDatabase? database = ReadOnlySqlite.TryOpen(file, out _);
            if (database is null || database.OpenMode != Domain.Codex.SqliteOpenMode.ReadOnly)
            {
                return null; // 열 수 없거나 WAL을 반영하지 못하는 폴백 — 알 수 없음
            }

            try
            {
                List<string> tables = database.ReadFirstColumnStrings("SELECT name FROM sqlite_master WHERE type = 'table'")
                    .Where(t => t.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    .Where(t => database.GetColumnNames(t).Contains("thread_id"))
                    .ToList();
                if (tables.Count == 0)
                {
                    return null; // 파일은 있는데 스키마를 알 수 없다
                }

                foreach (string id in counts.Keys.ToList())
                {
                    string literal = id.Replace("'", "''", StringComparison.Ordinal);
                    foreach (string table in tables)
                    {
                        object? value = database.ReadScalar($"SELECT COUNT(*) FROM \"{table}\" WHERE thread_id = '{literal}' COLLATE NOCASE");
                        counts[id] += Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        return counts;
    }

    private static Dictionary<string, long>? CountAtomStateMentions(string home, IReadOnlyCollection<string> ids)
    {
        Dictionary<string, long> counts = Zero(ids);
        string path = Path.Combine(home, CodexHomeLayout.GlobalStateFileName);
        try
        {
            if (!File.Exists(path))
            {
                return counts;
            }

            string text = GlobalStateJson.StrictUtf8.GetString(File.ReadAllBytes(path));
            if (GlobalStateJson.Parse(text) is not GlobalStateJsonObject root)
            {
                return null;
            }

            if (root.Get(AtomStateKey) is not { } atom)
            {
                return counts;
            }

            string raw = text[atom.SourceStart..atom.SourceEnd];
            foreach (string id in counts.Keys.ToList())
            {
                counts[id] = CountOccurrences(raw, id);
            }

            return counts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, long>? CountSessionIndexLines(string home, IReadOnlyCollection<string> ids)
    {
        Dictionary<string, long> counts = Zero(ids);
        string path = Path.Combine(home, CodexHomeLayout.SessionIndexFileName);
        try
        {
            if (!File.Exists(path))
            {
                return counts;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                foreach (string id in counts.Keys.ToList())
                {
                    if (line.Contains(id, StringComparison.OrdinalIgnoreCase))
                    {
                        counts[id]++;
                    }
                }
            }

            return counts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long CountOccurrences(string text, string value)
    {
        long count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
