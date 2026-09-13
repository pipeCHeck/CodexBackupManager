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
      5. publish        App을 win-x64/self-contained/single-file로 publish
      6. audit          publish 결과물에 exe 외의 파일이 남았는지 확인(임의로 지우지 않는다 —
                         남아 있으면 원인을 알 수 있게 실패시킨다)
      7. package        exe + docs\dist-readme.txt(+ LICENSE, 있으면)로 ZIP 생성
      8. checksum       ZIP의 SHA-256을 SHA256SUMS.txt로 기록

    실제 사용자 `.codex`는 이 스크립트가 절대 건드리지 않는다 — Restore E2E는 전부 테스트가 만든
    합성/temp Codex Home에서만 일어난다(dotnet test 4단계가 그 테스트들을 실행한다).

.PARAMETER Version
    배포 버전 문자열(파일 이름에 쓴다). 기본값(생략 시)은 Directory.Build.props의 <Version>을
    자동으로 읽어 쓴다 — 버전의 single source of truth는 Directory.Build.props이고, 이 스크립트가
    별도 하드코딩 값을 갖지 않게 하기 위함이다. 명시적으로 넘기면 그 값으로 override한다(단, 실제
    빌드 결과물의 AssemblyVersion과 다르면 6단계 감사에서 실패한다 — 버전 불일치를 조용히 지나치지
    않기 위함).

.PARAMETER SkipTests
    4단계(dotnet test)를 건너뛴다. 빠른 반복 작업용 — 실제 릴리스 산출물을 만들 때는 쓰지 말 것.

.NOTES
    self-contained/single-file publish는 `CbmReleasePublish=true`를 함께 넘긴다 —
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
$stageDir = Join-Path $repoRoot 'artifacts\stage'
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
$zipName = "CodexBackupManager-v$Version-$rid.zip"
$zipPath = Join-Path $releaseDir $zipName

Push-Location $repoRoot
try {
    Write-Host "  버전: $Version" -ForegroundColor Cyan
    # ────────────────────────────────────────────────────────── 1. clean
    Write-Section '1. clean release output'
    foreach ($dir in @($publishDir, $releaseDir, $stageDir)) {
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
    Write-Section '5. dotnet publish (self-contained, single-file)'
    dotnet publish $appProject `
        -c Release -r $rid `
        -p:SelfContained=true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:CbmReleasePublish=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }
    Write-Host '  [OK] publish 성공' -ForegroundColor Green

    # ───────────────────────────────────────────────────────── 6. audit
    Write-Section '6. publish 결과물 감사'
    $publishedFiles = Get-ChildItem -LiteralPath $publishDir -File
    $exePath = Join-Path $publishDir 'CodexBackupManager.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "CodexBackupManager.exe가 publish 결과물에 없습니다: $publishDir"
    }

    # 파일 이름(ZIP)에 쓰는 $Version과 실제로 빌드된 exe의 FileVersion이 어긋나면(예: -Version을
    # Directory.Build.props와 다르게 override했거나, props를 고치고 재빌드를 깜빡한 경우) 조용히
    # 잘못된 이름의 ZIP을 만들지 않고 여기서 바로 실패시킨다.
    $builtFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
    $builtShortVersion = ([System.Version]$builtFileVersion).ToString(3)
    if ($builtShortVersion -ne $Version) {
        throw "버전 불일치: ZIP 이름에 쓸 버전은 '$Version'인데, 실제 빌드된 exe의 FileVersion은 '$builtFileVersion'(단축: '$builtShortVersion')입니다. Directory.Build.props와 -Version 인자를 확인하세요."
    }
    Write-Host "  [OK] 버전 일치 확인 (exe FileVersion: $builtFileVersion)" -ForegroundColor Green

    $unexpected = $publishedFiles | Where-Object { $_.Name -ne 'CodexBackupManager.exe' }
    if ($unexpected) {
        Write-Host '  [FAIL] exe 외의 파일이 publish 결과물에 남아 있습니다 — 무작정 지우지 않는다.' -ForegroundColor Red
        $unexpected | ForEach-Object { Write-Host "    - $($_.Name) ($($_.Length) bytes)" -ForegroundColor Red }
        Write-Host '  single-file/native library 설정으로 해결 가능한지 먼저 확인할 것(스크립트가 임의로 삭제하지 않는다).' -ForegroundColor Red
        throw 'publish 결과물 감사 실패'
    }

    $exeInfo = Get-Item -LiteralPath $exePath
    Write-Host "  [OK] CodexBackupManager.exe 단독 확인 ($([math]::Round($exeInfo.Length / 1MB, 1)) MB)" -ForegroundColor Green

    # ──────────────────────────────────────────────────────── 7. package
    Write-Section '7. release ZIP 생성'
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

    Copy-Item -LiteralPath $exePath -Destination (Join-Path $stageDir 'CodexBackupManager.exe')

    $distReadme = Join-Path $repoRoot 'docs\dist-readme.txt'
    if (Test-Path -LiteralPath $distReadme) {
        Copy-Item -LiteralPath $distReadme -Destination (Join-Path $stageDir 'README.txt')
    } else {
        Write-Host '  [주의] docs\dist-readme.txt를 찾을 수 없어 ZIP에 사용법 안내가 빠집니다.' -ForegroundColor Yellow
    }

    $licensePath = Join-Path $repoRoot 'LICENSE'
    if (Test-Path -LiteralPath $licensePath) {
        Copy-Item -LiteralPath $licensePath -Destination (Join-Path $stageDir 'LICENSE')
    }

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $zipInfo = Get-Item -LiteralPath $zipPath
    Write-Host "  [OK] $zipName 생성 ($([math]::Round($zipInfo.Length / 1MB, 1)) MB)" -ForegroundColor Green
    Write-Host '  포함된 파일:' -ForegroundColor Yellow
    Get-ChildItem -LiteralPath $stageDir -File | ForEach-Object { Write-Host "    - $($_.Name)" }

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
    Write-Host "  ZIP:      $zipPath" -ForegroundColor Green
    Write-Host "  SHA-256:  $sumsPath" -ForegroundColor Green
    exit 0
} finally {
    Pop-Location
}
