using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Restore;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: CrashSim apply|lock-hold ...");
    return 2;
}

return args[0] switch
{
    "apply" => RunApply(args),
    "lock-hold" => RunLockHold(args),
    _ => Unknown(args[0]),
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"알 수 없는 command: {command}");
    return 2;
}

static int RunApply(string[] args)
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

    ImportPlan? plan = ImportPlanBuilder.Build(preview, backupFilePath);
    if (plan is null)
    {
        Console.Error.WriteLine("ImportPlan을 만들 수 없습니다.");
        return 5;
    }

    var hook = new CrashAtPointHook(crashPoint, sentinelFilePath);

    // production 진입점을 그대로 쓴다 — 실제 앱이 부르는 것과 동일한 경로다.
    RestoreResult result = RestoreExecutor.Apply(plan, codexHomePath, snapshotRoot: snapshotRoot, faultInjection: hook);

    // 정상적으로 여기까지 실행이 돌아왔다면(=crashPoint에서 멈추지 않았다면) 부모가 기대한 시나리오가
    // 아니다 — 부모가 이 출력으로 그 사실을 알 수 있게 한다.
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
    File.WriteAllText(readySentinelPath, lockResult.Acquired ? "acquired" : "not-acquired");

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

        File.WriteAllText(sentinelFilePath, "ready");
        Thread.Sleep(Timeout.Infinite);
    }
}
