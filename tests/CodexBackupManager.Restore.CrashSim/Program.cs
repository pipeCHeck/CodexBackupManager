using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Restore;

// Phase 9_1-01 — 테스트 전용 인자. 이 자식 프로세스가 호스트에서 실제로 실행 중인 Codex와 무관하게
// 크래시 지점까지 도달하도록, 실제 프로세스 목록 대신 "Codex 없음" 목록을 쓴다. CrashSim은 테스트
// 전용 exe이며 배포되지 않는다(제품 App에는 이런 인자가 없다). 가드 자체의 동작은
// CodexProcessGuardTests가 검증한다.
const string NoProcessGuardArgument = "--process-guard=none";
bool noProcessGuard = Array.IndexOf(args, NoProcessGuardArgument) >= 0;
args = Array.FindAll(args, a => !string.Equals(a, NoProcessGuardArgument, StringComparison.Ordinal));

// Phase 9_5-T3 — 테스트 전용 인자. 선택 없는 이전 방식 Plan 대신 기본 사용자 선택(ImportUserChoices.CreateDefault)으로 Plan을 만든다
// (미등록 원본 폴더 → 새 프로젝트 만들기 경로를 크래시 시험하기 위함).
const string WithChoicesArgument = "--with-choices";
bool withChoices = Array.IndexOf(args, WithChoicesArgument) >= 0;
args = Array.FindAll(args, a => !string.Equals(a, WithChoicesArgument, StringComparison.Ordinal));

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: CrashSim apply|lock-hold ... [--process-guard=none]");
    return 2;
}

return args[0] switch
{
    "apply" => RunApply(args, noProcessGuard, withChoices),
    "repair" => RunRepair(args, noProcessGuard),
    "undo" => RunUndo(args, noProcessGuard),
    "lock-hold" => RunLockHold(args),
    _ => Unknown(args[0]),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"알 수 없는 command: {command}");
    return 2;
}

static int RunApply(string[] args, bool noProcessGuard, bool withChoices)
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("usage: CrashSim apply <codexHomePath> <backupFilePath> <crashPoint> <sentinelFilePath> [snapshotRoot]");
        return 2;
    }

    string codexHomePath = args[1];
    string backupFilePath = args[2];
    RestoreFaultInjectionPoint crashPoint = Enum.Parse<RestoreFaultInjectionPoint>(args[3]);
    string sentinelFilePath = args[4];
    string? snapshotRoot = args.Length > 5 ? args[5] : null;

    var detection = new CodexDetectionService().DetectFromUserSelection(codexHomePath);
    if (detection.Installation is not { } installation)
    {
        Console.Error.WriteLine("Codex Home을 확인할 수 없습니다.");
        return 3;
    }

    CodexCatalog catalog = CodexCatalogBuilder.Build(installation);
    ImportPreview preview = ImportPreviewBuilder.Build(backupFilePath, catalog);
    if (!preview.Success)
    {
        Console.Error.WriteLine("Import Preview 실패.");
        return 4;
    }

    ImportPlan? plan = withChoices
        ? ImportPlanBuilder.Build(preview, backupFilePath, ImportUserChoices.CreateDefault(preview))
        : ImportPlanBuilder.Build(preview, backupFilePath);
    if (plan is null)
    {
        Console.Error.WriteLine("ImportPlan을 만들 수 없습니다.");
        return 5;
    }

    var hook = new CrashAtPointHook(crashPoint, sentinelFilePath);

    // 기본은 production 진입점을 그대로 쓴다 — 실제 앱이 부르는 것과 동일한 경로다.
    // --process-guard=none이면 프로세스 목록만 "Codex 없음"으로 바꾸고, fresh catalog 생성을 포함한
    // 나머지는 production과 같은 코드(RestoreExecutor.BuildFreshCatalog)를 쓴다.
    RestoreResult result = noProcessGuard
        ? RestoreExecutor.Apply(
            plan, codexHomePath, static () => [], RestoreExecutor.BuildFreshCatalog,
            snapshotRoot: snapshotRoot, faultInjection: hook)
        : RestoreExecutor.Apply(plan, codexHomePath, snapshotRoot: snapshotRoot, faultInjection: hook);

    // 정상적으로 여기까지 실행이 돌아왔다면(=crashPoint에서 멈추지 않았다면) 부모가 기대한 시나리오가
    // 아니다 — 부모가 이 출력으로 그 사실을 알 수 있게 한다.
    Console.WriteLine($"UNEXPECTED-COMPLETION:{result.Outcome}:{result.Message}");
    return 0;
}

/// <summary>(Phase 9_4-T3) 되돌리기(<see cref="CodexBackupManager.Restore.Undo.ImportUndoService"/>)를 지정 지점에서 멈춘다.</summary>
static int RunUndo(string[] args, bool noProcessGuard)
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("usage: CrashSim undo <codexHomePath> <importSnapshotDirectory> <crashPoint> <sentinelFilePath> [snapshotRoot]");
        return 2;
    }

    var hook = new CrashAtPointHook(Enum.Parse<RestoreFaultInjectionPoint>(args[3]), args[4]);
    CodexBackupManager.Restore.Undo.UndoResult result = CodexBackupManager.Restore.Undo.ImportUndoService.Undo(
        args[1], args[2], args.Length > 5 ? args[5] : SnapshotService.DefaultSnapshotRoot(),
        noProcessGuard ? static () => [] : CodexProcessGuard.SystemRunningProcessLister, hook);
    Console.WriteLine($"UNEXPECTED-COMPLETION:{result.Outcome}:{result.Message}");
    return 0;
}

/// <summary>
/// (Phase 9_5a-T1) 사이드바 보정(<see cref="SidebarRepairService"/>)을 지정 지점에서 멈춘다 — 부모가 강제 종료하면 진짜 크래시와 같다.
/// </summary>
static int RunRepair(string[] args, bool noProcessGuard)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: CrashSim repair <codexHomePath> <crashPoint> <sentinelFilePath> [snapshotRoot]");
        return 2;
    }

    string codexHomePath = args[1];
    RestoreFaultInjectionPoint crashPoint = Enum.Parse<RestoreFaultInjectionPoint>(args[2]);
    string sentinelFilePath = args[3];
    string? snapshotRoot = args.Length > 4 ? args[4] : null;
    var hook = new CrashAtPointHook(crashPoint, sentinelFilePath);

    SidebarRepairResult result = SidebarRepairService.Repair(
        codexHomePath,
        noProcessGuard ? static () => [] : CodexProcessGuard.SystemRunningProcessLister,
        snapshotRoot, hook, catalogBuilder: null);
    Console.WriteLine($"UNEXPECTED-COMPLETION:{result.Outcome}:{result.Message}");
    return 0;
}

/// <summary>
/// Phase 07_03 요구사항 5 — <see cref="RestoreProcessLock"/>이 실제로 프로세스 경계를 넘어 동작하는지
/// 진짜 두 프로세스로 검증하기 위한 모드다. 지정된 Codex Home의 Restore lock을 실제로 획득하고,
/// 획득 성공/실패를 sentinel 파일에 남긴 뒤, 부모가 releaseSignalPath를 만들어 줄 때까지(또는 부모가
/// 이 프로세스를 강제 종료할 때까지) 그대로 lock을 쥐고 있는다.
/// </summary>
static int RunLockHold(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: CrashSim lock-hold <codexHomePath> <readySentinelPath> <releaseSignalPath>");
        return 2;
    }

    string codexHomePath = args[1];
    string readySentinelPath = args[2];
    string releaseSignalPath = args[3];

    RestoreProcessLock.AcquireResult lockResult = RestoreProcessLock.TryAcquire(codexHomePath, TimeSpan.Zero);
    SentinelFile.WriteAtomically(readySentinelPath, lockResult.Acquired ? "acquired" : "not-acquired");

    if (!lockResult.Acquired)
    {
        return 0;
    }

    try
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(releaseSignalPath) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }
    finally
    {
        lockResult.Handle!.Dispose();
    }

    return 0;
}

/// <summary>
/// 지정된 지점에 도달하면 부모 프로세스에게 sentinel 파일로 신호만 남기고 그대로 블록한다 — 정상
/// 예외를 던지지 않으므로 <see cref="RestoreExecutor"/>의 catch/Rollback 경로가 전혀 실행되지
/// 않는다(부모가 이 프로세스를 강제 종료하면 진짜 크래시와 동일한 상태가 된다).
/// </summary>
sealed class CrashAtPointHook(RestoreFaultInjectionPoint crashAt, string sentinelFilePath) : IRestoreFaultInjectionHook
{
    public void Check(RestoreFaultInjectionPoint point)
    {
        if (point != crashAt)
        {
            return;
        }

        SentinelFile.WriteAtomically(sentinelFilePath, "ready");
        Thread.Sleep(Timeout.Infinite);
    }
}

/// <summary>
/// (Phase 9_1-17) 부모가 sentinel 파일을 "보였을 때" 이미 내용이 다 써져 있도록 temp에 쓴 뒤 같은 폴더 안에서 이동한다.
/// </summary>
static class SentinelFile
{
    public static void WriteAtomically(string path, string content)
    {
        string temp = path + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
