#requires -Version 5.1
<#
.SYNOPSIS
    Phase 8 — Windows 배포물을 재현 가능하게 만든다.

.DESCRIPTION
    다음을 순서대로 수행한다.

      1. clean          이전 release 산출물 정리(artifacts\publish, artifacts\release)
      2. restore        dotnet restore
      3. build          dotnet build -c Release
      4. test           dotnet test -c Release(전체 솔루션 — Restore.Tests의 CrashSim
                         child-process 테스트/App.Tests(WPF) 포함)
      5. publish        App을 win-x64/self-contained **폴더형**(단일 exe 아님)으로 publish
      6. audit          publish 결과물이 정상적인 self-contained 폴더인지 확인(exe/필수 앱 DLL/
                         런타임 파일/SQLite native 존재, PDB·테스트 DLL·소스 파일 부재)
      7. package        publish 폴더 전체를 `CodexBackupManager-v<버전>-win-x64\`로 복사하고
                         README.txt(`docs\dist-readme.txt`)(+ LICENSE, 있으면)를 그 안에 추가한
                         뒤, 그 폴더 자체를 ZIP으로 묶는다(압축 풀면 폴더 하나가 나온다)
      8. checksum       ZIP의 SHA-256을 SHA256SUMS.txt로 기록

    실제 사용자 `.codex`는 이 스크립트가 절대 건드리지 않는다 — Restore E2E는 전부 테스트가 만든
    합성/temp Codex Home에서만 일어난다(dotnet test 4단계가 그 테스트들을 실행한다).

    Phase 08_07 — 배포 형태를 self-contained/single-file exe에서 self-contained **폴더형**으로
    바꿨다(exe+dll+.NET 런타임 파일이 한 폴더에 그대로 존재, PublishSingleFile=false). Restore/
    Backup 로직은 이 변경과 무관하다.

.PARAMETER Version
    배포 버전 문자열(폴더/ZIP 이름에 쓴다). 기본값(생략 시)은 Directory.Build.props의 <Version>을
    자동으로 읽어 쓴다 — 버전의 single source of truth는 Directory.Build.props이고, 이 스크립트가
    별도 하드코딩 값을 갖지 않게 하기 위함이다. 명시적으로 넘기면 그 값으로 override한다(단, 실제
    빌드 결과물의 AssemblyVersion과 다르면 6단계 감사에서 실패한다 — 버전 불일치를 조용히 지나치지
    않기 위함).

.PARAMETER SkipTests
    4단계(dotnet test)를 건너뛴다. 빠른 반복 작업용 — 실제 릴리스 산출물을 만들 때는 쓰지 말 것.

.NOTES
    self-contained 폴더형 publish는 `CbmReleasePublish=true`를 함께 넘긴다 —
    Directory.Build.props가 이 값일 때만(Release 구성에서) 참조 프로젝트까지 포함해 PDB 생성을
    억제한다(평범한 dotnet build/test -c Release는 그대로 심볼을 유지한다).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$rid = 'win-x64'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$releaseDir = Join-Path $repoRoot 'artifacts\release'
$appProject = Join-Path $repoRoot 'src\CodexBackupManager.App\CodexBackupManager.App.csproj'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

function Write-Section($title) {
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ('=' * 72) -ForegroundColor DarkGray
}

# Directory.Build.props가 버전의 single source of truth다 — 이 스크립트가 별도로 버전을
# 하드코딩하면(예: 예전 '0.1.0' 기본값) 실제 프로젝트 버전을 올렸을 때 파일 이름만 예전 버전으로
# 남는 문제가 생긴다. -Version을 명시하지 않으면 항상 여기서 읽는다.
function Get-ProjectVersion([string]$PropsPath) {
    [xml]$props = Get-Content -LiteralPath $PropsPath -Raw
    $versionNode = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $versionNode) {
        throw "Directory.Build.props에서 <Version>을 찾지 못했습니다: $PropsPath"
    }
    return $versionNode.Trim()
}

if (-not $PSBoundParameters.ContainsKey('Version')) {
    $Version = Get-ProjectVersion -PropsPath $propsPath
}
$releaseFolderName = "CodexBackupManager-v$Version-$rid"
$releaseFolderPath = Join-Path $releaseDir $releaseFolderName
$zipName = "$releaseFolderName.zip"
$zipPath = Join-Path $releaseDir $zipName

Push-Location $repoRoot
try {
    Write-Host "  버전: $Version" -ForegroundColor Cyan
    # ────────────────────────────────────────────────────────── 1. clean
    Write-Section '1. clean release output'
    foreach ($dir in @($publishDir, $releaseDir)) {
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }
    Write-Host '  [OK] 이전 산출물 정리 완료' -ForegroundColor Green

    # ──────────────────────────────────────────────────────── 2. restore
    Write-Section '2. dotnet restore'
    dotnet restore 'CodexBackupManager.sln'
    if ($LASTEXITCODE -ne 0) { throw 'restore 실패' }

    # ────────────────────────────────────────────────────────── 3. build
    Write-Section '3. dotnet build -c Release'
    dotnet build 'CodexBackupManager.sln' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build 실패' }
    Write-Host '  [OK] Release build 성공' -ForegroundColor Green

    # ─────────────────────────────────────────────────────────── 4. test
    Write-Section '4. dotnet test -c Release'
    if ($SkipTests) {
        Write-Host '  [SKIP] -SkipTests 지정됨 — 실제 릴리스 산출물을 만들 때는 건너뛰지 말 것.' -ForegroundColor Yellow
    } else {
        dotnet test 'CodexBackupManager.sln' -c Release --no-build --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw '테스트 실패 — 실패한 테스트가 있는 채로 배포하지 않는다.' }
        Write-Host '  [OK] 전체 테스트 통과' -ForegroundColor Green
    }

    # ────────────────────────────────────────────────────── 5. publish
    # PublishSingleFile을 켜지 않는다 — Phase 08_07부터는 exe+dll+런타임 파일이 그대로 폴더에
    # 있는 self-contained 배포다. IncludeNativeLibrariesForSelfExtract는 single-file 전용
    # 옵션이라 더 이상 넘기지 않는다(폴더형에는 의미가 없다). PublishTrimmed=false는 csproj
    # 기본값과 같지만, WPF에서 trimming을 절대 켜지 않는다는 걸 이 명령에서도 명시적으로 보장한다.
    Write-Section '5. dotnet publish (self-contained, 폴더형)'
    dotnet publish $appProject `
        -c Release -r $rid `
        -p:SelfContained=true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:CbmReleasePublish=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }
    Write-Host '  [OK] publish 성공' -ForegroundColor Green

    # ───────────────────────────────────────────────────────── 6. audit
    # 폴더형이므로 "exe 외 파일이 있으면 실패"는 더 이상 맞지 않는다(수백 개의 DLL/런타임/리소스
    # 파일이 있는 게 정상이다). 대신 "정말 self-contained 폴더가 맞는지" + "있으면 안 되는 것이
    # 없는지"를 구체적으로 확인한다.
    Write-Section '6. publish 결과물 감사'
    $exePath = Join-Path $publishDir 'CodexBackupManager.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "CodexBackupManager.exe가 publish 결과물에 없습니다: $publishDir"
    }

    # 파일 이름(폴더/ZIP)에 쓰는 $Version과 실제로 빌드된 exe의 FileVersion이 어긋나면(예: -Version을
    # Directory.Build.props와 다르게 override했거나, props를 고치고 재빌드를 깜빡한 경우) 조용히
    # 잘못된 이름의 산출물을 만들지 않고 여기서 바로 실패시킨다.
    $builtFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
    $builtShortVersion = ([System.Version]$builtFileVersion).ToString(3)
    if ($builtShortVersion -ne $Version) {
        throw "버전 불일치: 폴더/ZIP 이름에 쓸 버전은 '$Version'인데, 실제 빌드된 exe의 FileVersion은 '$builtFileVersion'(단축: '$builtShortVersion')입니다. Directory.Build.props와 -Version 인자를 확인하세요."
    }
    Write-Host "  [OK] 버전 일치 확인 (exe FileVersion: $builtFileVersion)" -ForegroundColor Green

    # 필수 앱 DLL — AssemblyName이 CodexBackupManager라 메인 dll은 CodexBackupManager.dll이고
    # (App project 자체), 참조하는 4개 class library가 각각 별도 dll로 나온다.
    $requiredAppFiles = @(
        'CodexBackupManager.dll',
        'CodexBackupManager.Domain.dll',
        'CodexBackupManager.Codex.dll',
        'CodexBackupManager.Backup.dll',
        'CodexBackupManager.Restore.dll',
        'CodexBackupManager.deps.json',
        'CodexBackupManager.runtimeconfig.json'
    )
    foreach ($name in $requiredAppFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $name))) {
            throw "필수 앱 파일이 publish 결과물에 없습니다: $name"
        }
    }
    Write-Host '  [OK] 필수 앱 DLL/메타데이터 전부 존재' -ForegroundColor Green

    # self-contained 여부 실측 확인 — framework-dependent라면 이 핵심 런타임 네이티브 파일들이
    # 폴더에 없다(대신 시스템에 설치된 공유 런타임을 찾는다). 하나라도 없으면 실제로는
    # self-contained가 아니라는 뜻이므로 바로 실패시킨다.
    $requiredRuntimeFiles = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll')
    foreach ($name in $requiredRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $name))) {
            throw "self-contained 런타임 파일이 없습니다(framework-dependent로 잘못 만들어졌을 수 있음): $name"
        }
    }
    # runtimeconfig.json의 self-contained 표식: framework-dependent는 "framework"(단수, roll-forward
    # 정책)를 쓰고, self-contained는 실제로 번들된 정확한 버전을 "includedFrameworks"에 적는다.
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDir 'CodexBackupManager.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtimeConfig.runtimeOptions.includedFrameworks) {
        throw "runtimeconfig.json에 includedFrameworks가 없습니다 — self-contained publish가 아닌 것으로 보입니다."
    }
    Write-Host "  [OK] self-contained 런타임 파일 + runtimeconfig.json(includedFrameworks) 확인" -ForegroundColor Green

    # Microsoft.Data.Sqlite의 네이티브 의존성(e_sqlite3.dll)이 실제로 폴더에 포함됐는지 확인.
    # @()로 감싸 결과가 0개/1개/여러 개 어느 쪽이어도 항상 배열로 다뤄지게 한다(Windows
    # PowerShell 5.1에서는 단일 객체에 .Count가 없어 결과가 정확히 1개일 때 조용히 깨질 수 있다).
    $sqliteNative = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Filter 'e_sqlite3.dll' -File)
    if ($sqliteNative.Count -eq 0) {
        throw 'SQLite native dependency(e_sqlite3.dll)를 publish 결과물에서 찾지 못했습니다.'
    }
    $sqliteRelativePaths = $sqliteNative | ForEach-Object { $_.FullName.Substring($publishDir.Length).TrimStart('\') }
    Write-Host "  [OK] SQLite native dependency 확인 ($($sqliteNative.Count)개: $($sqliteRelativePaths -join ', '))" -ForegroundColor Green

    # 있으면 안 되는 것들 — PDB(디버그 심볼), 테스트 DLL, 소스 파일. 여기도 같은 이유로 @()로 감싼다.
    $pdbFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Filter '*.pdb' -File)
    if ($pdbFiles.Count -gt 0) {
        throw "PDB 파일이 publish 결과물에 남아 있습니다: $($pdbFiles.Name -join ', ')"
    }
    $testDlls = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Filter '*.Tests.dll' -File)
    if ($testDlls.Count -gt 0) {
        throw "테스트 DLL이 publish 결과물에 남아 있습니다: $($testDlls.Name -join ', ')"
    }
    # 주의: -Include는 -LiteralPath(+ -Recurse)와 함께 쓰면 조용히 필터링이 안 되고 전체 파일이
    # 그대로 매치된 것처럼 동작하는 PowerShell의 알려진 함정이다 — Where-Object로 확장자를 직접
    # 비교해 이 문제를 피한다.
    $sourceFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object { $_.Extension -in '.cs', '.csproj' })
    if ($sourceFiles.Count -gt 0) {
        throw "소스 파일이 publish 결과물에 남아 있습니다: $($sourceFiles.Name -join ', ')"
    }
    Write-Host '  [OK] PDB/테스트 DLL/소스 파일 없음 확인' -ForegroundColor Green

    $publishSizeBytes = (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $publishFileCount = (Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count
    Write-Host "  [OK] publish 폴더 감사 통과 — 파일 $publishFileCount 개, 총 $([math]::Round($publishSizeBytes / 1MB, 1)) MB" -ForegroundColor Green

    # ──────────────────────────────────────────────────────── 7. package
    # 폴더형이므로 exe 하나만 옮기지 않는다 — publish 결과물 전체를
    # "CodexBackupManager-v<버전>-win-x64" 폴더로 그대로 복사한 뒤, 그 폴더 자체를 ZIP으로 묶는다
    # (Compress-Archive에 파일이 아니라 디렉터리 경로를 주면 그 디렉터리 이름이 ZIP 최상위
    # 엔트리가 된다 — 사용자가 압축을 풀면 파일들이 바탕화면에 흩어지지 않고 폴더 하나로 나온다).
    Write-Section '7. release 폴더 + ZIP 생성'
    New-Item -ItemType Directory -Path $releaseFolderPath -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $releaseFolderPath -Recurse -Force

    $distReadme = Join-Path $repoRoot 'docs\dist-readme.txt'
    if (Test-Path -LiteralPath $distReadme) {
        Copy-Item -LiteralPath $distReadme -Destination (Join-Path $releaseFolderPath 'README.txt')
    } else {
        Write-Host '  [주의] docs\dist-readme.txt를 찾을 수 없어 배포 폴더에 사용법 안내가 빠집니다.' -ForegroundColor Yellow
    }

    $licensePath = Join-Path $repoRoot 'LICENSE'
    if (Test-Path -LiteralPath $licensePath) {
        Copy-Item -LiteralPath $licensePath -Destination (Join-Path $releaseFolderPath 'LICENSE')
    }

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path $releaseFolderPath -DestinationPath $zipPath -CompressionLevel Optimal

    $releaseFolderSizeBytes = (Get-ChildItem -LiteralPath $releaseFolderPath -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $releaseFolderFileCount = (Get-ChildItem -LiteralPath $releaseFolderPath -Recurse -File).Count
    $zipInfo = Get-Item -LiteralPath $zipPath
    Write-Host "  [OK] $releaseFolderName\ 생성 (파일 $releaseFolderFileCount 개, $([math]::Round($releaseFolderSizeBytes / 1MB, 1)) MB)" -ForegroundColor Green
    Write-Host "  [OK] $zipName 생성 ($([math]::Round($zipInfo.Length / 1MB, 1)) MB)" -ForegroundColor Green

    # ─────────────────────────────────────────────────────── 8. checksum
    Write-Section '8. SHA-256'
    $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    $sumsPath = Join-Path $releaseDir 'SHA256SUMS.txt'
    # 표준 `sha256sum -c` 형식과 호환되도록 BOM 없는 순수 ASCII로 쓴다(해시/파일명 전부 ASCII다) —
    # PowerShell 5.1의 기본 utf8 인코딩은 BOM을 붙여 그 도구들의 파싱을 깨뜨린다.
    [System.IO.File]::WriteAllText($sumsPath, "$($hash.Hash.ToLowerInvariant())  $zipName`n", [System.Text.Encoding]::ASCII)
    Write-Host "  $($hash.Hash)  $zipName" -ForegroundColor Green
    Write-Host "  [OK] $sumsPath 작성 완료" -ForegroundColor Green

    Write-Section '완료'
    Write-Host "  폴더:     $releaseFolderPath" -ForegroundColor Green
    Write-Host "  ZIP:      $zipPath" -ForegroundColor Green
    Write-Host "  SHA-256:  $sumsPath" -ForegroundColor Green
    exit 0
} finally {
    Pop-Location
}
