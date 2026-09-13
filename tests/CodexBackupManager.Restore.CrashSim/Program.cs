using System;
using System.IO;
using System.Threading;
using CodexBackupManager.Backup.Import;
using CodexBackupManager.Codex;
using CodexBackupManager.Codex.Catalog;
using CodexBackupManager.Domain.Codex.Catalog;
using CodexBackupManager.Restore;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: CrashSim <codexHomePath> <backupFilePath> <crashPoint> <sentinelFilePath> [snapshotRoot]");
    return 2;
}

string codexHomePath = args[0];
string backupFilePath = args[1];
RestoreFaultInjectionPoint crashPoint = Enum.Parse<RestoreFaultInjectionPoint>(args[2]);
string sentinelFilePath = args[3];
string? snapshotRoot = args.Length > 4 ? args[4] : null;

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
