using System;
using System.Collections.Generic;
using System.IO;
using CodexBackupManager.App.Services;

namespace CodexBackupManager.App.ViewModels.Import;

/// <summary>
/// (Phase 9_5-07 ~ 09) 가져오기 화면 [새 폴더 만들기]가 쓰는 외부 동작 묶음. 테스트는 temp 폴더를 쓰는 값으로 바꿔 넣는다.
/// 가져오기 화면에 이 값을 주지 않으면(<c>null</c>) [새 폴더 만들기]는 꺼진다.
/// </summary>
/// <param name="LoadSavedBase">설정에 저장된 기준 폴더(없으면 <c>null</c>).</param>
/// <param name="SaveBase">기준 폴더를 설정에 저장한다. 저장했으면 <c>true</c>.</param>
/// <param name="DefaultBase">기본 기준 폴더(<c>문서\ChatGPT</c>).</param>
/// <param name="ProtectedRoots">기준 폴더로 쓰면 안 되는 이 앱의 데이터 폴더들(설정·로그, Snapshot 루트 등).</param>
/// <param name="CreateFolder">(기준 폴더, 원래 이름) → 새 폴더 결과.</param>
public sealed record NewProjectFolderOptions(
    Func<string?> LoadSavedBase,
    Func<string, bool> SaveBase,
    Func<string> DefaultBase,
    Func<IReadOnlyList<string>> ProtectedRoots,
    Func<string, string, NewProjectFolderResult> CreateFolder)
{
    /// <summary>제품 기본값: 설정 파일(<see cref="SettingsStore"/>)과 실제 파일 시스템.</summary>
    public static NewProjectFolderOptions ForSettings(SettingsStore settings, Func<string> snapshotRootProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshotRootProvider);
        return new NewProjectFolderOptions(
            () => settings.Load().NewProjectFolderBase,
            path =>
            {
                AppSettings current = settings.Load();
                current.NewProjectFolderBase = path;
                return settings.Save(current);
            },
            NewProjectFolderService.DefaultBase,
            () =>
            [
                AppPaths.Root,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.ProductFolderName),
                snapshotRootProvider(),
            ],
            NewProjectFolderService.Create);
    }
}
