#requires -Version 5.1
<#
.SYNOPSIS
    Phase 1 검증 스크립트.

.DESCRIPTION
    다음을 순서대로 수행하고 결과를 출력합니다.

      0. 환경 확인          .NET SDK 목록 / WindowsDesktop 런타임 / CODEX_HOME 실측값
      1. restore            NuGet 복원 + 해석된 패키지 버전 출력
      2. build              Debug 빌드 (솔루션 전체)
      3. test               Domain.Tests + Codex.Tests
      4. Codex 스냅샷 (전)  실제 .codex의 핵심 파일 해시/크기/수정시각
      5. run                WPF 앱 실행 (사용자가 창을 닫으면 계속)
      6. Codex 스냅샷 (후)  스냅샷 비교 → 쓰기 발생 여부 판정

.PARAMETER SkipRun
    GUI 실행 단계를 건너뜁니다. (CI / 비대화 환경)

.PARAMETER CodexHome
    스냅샷 대상 Codex Home. 생략하면 CODEX_HOME → %USERPROFILE%\.codex 순으로 찾습니다.

.NOTES
    4~6단계를 의미 있게 비교하려면 **Codex를 완전히 종료한 상태**에서 실행하세요.
    Codex가 실행 중이면 Codex 자신이 파일을 계속 갱신하므로 우리 프로그램의 쓰기와 구분할 수 없습니다.
#>
[CmdletBinding()]
param(
    [switch]$SkipRun,
    [string]$CodexHome
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = @()

function Write-Section($title) {
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ('=' * 72) -ForegroundColor DarkGray
}

function Write-Result($label, $value) {
    Write-Host ('  {0,-34} {1}' -f $label, $value)
}

# ─────────────────────────────────────────────────────────────── 0. 환경
Write-Section '0. 환경 확인'

Write-Host '  설치된 .NET SDK:' -ForegroundColor Yellow
dotnet --list-sdks | ForEach-Object { Write-Host "    $_" }

Write-Host '  설치된 WindowsDesktop 런타임:' -ForegroundColor Yellow
dotnet --list-runtimes |
    Where-Object { $_ -like 'Microsoft.WindowsDesktop.App*' } |
    ForEach-Object { Write-Host "    $_" }

$sdk10 = (dotnet --list-sdks) -match '^10\.'
if (-not $sdk10) {
    $failures += '.NET 10 SDK가 설치되어 있지 않습니다. 이 프로젝트는 net10.0 / net10.0-windows를 대상으로 합니다.'
    Write-Host '  [FAIL] .NET 10 SDK 없음' -ForegroundColor Red
} else {
    Write-Host '  [OK] .NET 10 SDK 확인' -ForegroundColor Green
}

Write-Host ''
Write-Host '  CODEX_HOME 환경변수 실측값:' -ForegroundColor Yellow
foreach ($scope in 'Process', 'User', 'Machine') {
    $value = [Environment]::GetEnvironmentVariable('CODEX_HOME', $scope)
    Write-Result "  [$scope]" $(if ($value) { $value } else { '(설정되지 않음)' })
}

if (-not $CodexHome) {
    $CodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
    if (-not $CodexHome) { $CodexHome = Join-Path $env:USERPROFILE '.codex' }
}
Write-Result '→ 스냅샷 대상 Codex Home' $CodexHome

$codexRunning = Get-Process -Name 'codex*' -ErrorAction SilentlyContinue
if ($codexRunning) {
    Write-Host ''
    Write-Host '  [주의] Codex 프로세스가 실행 중입니다.' -ForegroundColor Yellow
    Write-Host '         Codex 자신이 파일을 갱신하므로 6단계 스냅샷 비교 결과를 신뢰할 수 없습니다.' -ForegroundColor Yellow
    Write-Host '         Codex를 완전히 종료한 뒤 다시 실행하세요.' -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────── 1. restore
Write-Section '1. dotnet restore'

Push-Location $repoRoot
try {
    dotnet restore 'CodexBackupManager.sln'
    if ($LASTEXITCODE -ne 0) { $failures += 'restore 실패' }

    Write-Host ''
    Write-Host '  해석된 패키지 버전 (floating 버전을 고정할 때 이 값을 사용하세요):' -ForegroundColor Yellow
    Get-ChildItem -Path $repoRoot -Filter 'project.assets.json' -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object {
            $project = Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $_.FullName))
            $assets = Get-Content $_.FullName -Raw | ConvertFrom-Json
            foreach ($library in $assets.libraries.PSObject.Properties.Name) {
                if ($library -match '^(Microsoft\.Data\.Sqlite|xunit|Microsoft\.NET\.Test\.Sdk)') {
                    Write-Host ('    {0,-34} {1}' -f $project, $library)
                }
            }
        } | Out-Null
} finally {
    Pop-Location
}

# ───────────────────────────────────────────────────────────── 2. build
Write-Section '2. dotnet build (Debug)'

Push-Location $repoRoot
try {
    dotnet build 'CodexBackupManager.sln' --configuration Debug --no-restore
    if ($LASTEXITCODE -ne 0) {
        $failures += 'build 실패'
        Write-Host '  [FAIL] 빌드 실패' -ForegroundColor Red
    } else {
        Write-Host '  [OK] 빌드 성공' -ForegroundColor Green
    }
} finally {
    Pop-Location
}

# ────────────────────────────────────────────────────────────── 3. test
Write-Section '3. dotnet test'

Push-Location $repoRoot
try {
    dotnet test 'CodexBackupManager.sln' --configuration Debug --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) {
        $failures += '테스트 실패'
        Write-Host '  [FAIL] 테스트 실패' -ForegroundColor Red
    } else {
        Write-Host '  [OK] 전체 테스트 통과' -ForegroundColor Green
    }
} finally {
    Pop-Location
}

# ────────────────────────────────────────── 4/6. Codex 원본 쓰기 방지 검증
function Get-CodexSnapshot($root) {
    $snapshot = @{}
    if (-not (Test-Path -LiteralPath $root)) { return $snapshot }

    # 핵심 파일만 본다. 1.45 GB 전체를 해시하지 않는다.
    $targets = @(
        'config.toml'
        'session_index.jsonl'
        '.codex-global-state.json'
    )
    $targets += (Get-ChildItem -LiteralPath $root -Filter 'state_*.sqlite*' -File -ErrorAction SilentlyContinue |
                 ForEach-Object { $_.Name })

    foreach ($name in ($targets | Select-Object -Unique)) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            $snapshot[$name] = [pscustomobject]@{
                Length = $item.Length
                Ticks  = $item.LastWriteTimeUtc.Ticks
                Hash   = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            }
        }
    }

    # 최상위 파일/폴더 이름 목록 (새 파일이 생겼는지 보기 위함)
    $snapshot['__entries__'] = [pscustomobject]@{
        Length = 0
        Ticks  = 0
        Hash   = (Get-ChildItem -LiteralPath $root -Force | Select-Object -ExpandProperty Name | Sort-Object) -join '|'
    }

    # sessions / archived_sessions 파일 개수
    foreach ($dir in 'sessions', 'archived_sessions') {
        $full = Join-Path $root $dir
        if (Test-Path -LiteralPath $full) {
            $count = (Get-ChildItem -LiteralPath $full -Recurse -File -Filter '*.jsonl*' -ErrorAction SilentlyContinue).Count
            $snapshot["__count_$dir"] = [pscustomobject]@{ Length = $count; Ticks = 0; Hash = "count=$count" }
        }
    }

    return $snapshot
}

Write-Section '4. Codex 스냅샷 (앱 실행 전)'
$before = Get-CodexSnapshot $CodexHome
if ($before.Count -eq 0) {
    Write-Host '  [SKIP] Codex Home을 찾을 수 없어 스냅샷을 건너뜁니다.' -ForegroundColor Yellow
} else {
    Write-Result '스냅샷 항목 수' $before.Count
    foreach ($key in ($before.Keys | Sort-Object)) {
        Write-Result $key $before[$key].Hash.Substring(0, [Math]::Min(16, $before[$key].Hash.Length))
    }
}

Write-Section '5. 앱 실행'
if ($SkipRun) {
    Write-Host '  [SKIP] -SkipRun 지정됨.' -ForegroundColor Yellow
} else {
    Write-Host '  창이 열립니다. 아래를 직접 확인한 뒤 창을 닫아 주세요.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '    (1) "Codex 연결됨 ●" 표시와 Codex Home 경로'
    Write-Host '    (2) Codex Desktop / Codex CLI 버전'
    Write-Host '    (3) State DB Generation / Migration'
    Write-Host '    (4) Sessions / Archived 개수'
    Write-Host '    (5) threadAssignmentsMigrated 값'
    Write-Host '    (6) [Codex 폴더 선택]으로 엉뚱한 폴더(예: 내 문서)를 골랐을 때'
    Write-Host '        "Codex를 찾을 수 없습니다" + 사유가 표시되는지'
    Write-Host ''

    Push-Location $repoRoot
    try {
        dotnet run --project 'src\CodexBackupManager.App\CodexBackupManager.App.csproj' --configuration Debug --no-build
        if ($LASTEXITCODE -ne 0) { $failures += '앱 실행이 0이 아닌 코드로 종료됨' }
    } finally {
        Pop-Location
    }
}

Write-Section '6. Codex 스냅샷 비교 (쓰기 발생 여부)'
if ($before.Count -eq 0) {
    Write-Host '  [SKIP] 전 스냅샷이 없어 비교하지 않습니다.' -ForegroundColor Yellow
} else {
    $after = Get-CodexSnapshot $CodexHome
    $changed = @()

    foreach ($key in $before.Keys) {
        if (-not $after.ContainsKey($key)) { $changed += "사라짐: $key"; continue }
        if ($after[$key].Hash -ne $before[$key].Hash)   { $changed += "내용 변경: $key" }
        elseif ($after[$key].Ticks -ne $before[$key].Ticks) { $changed += "수정 시각 변경: $key" }
        elseif ($after[$key].Length -ne $before[$key].Length) { $changed += "크기 변경: $key" }
    }
    foreach ($key in $after.Keys) {
        if (-not $before.ContainsKey($key)) { $changed += "새로 생김: $key" }
    }

    if ($changed.Count -eq 0) {
        Write-Host '  [OK] Codex Home의 핵심 파일과 최상위 항목 목록이 전혀 변하지 않았습니다.' -ForegroundColor Green
    } else {
        Write-Host '  [주의] 변경이 감지되었습니다:' -ForegroundColor Yellow
        $changed | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
        if ($codexRunning) {
            Write-Host '    Codex가 실행 중이었으므로 Codex 자신의 쓰기일 가능성이 높습니다.' -ForegroundColor Yellow
            Write-Host '    Codex를 종료한 상태로 다시 실행해 확인하세요.' -ForegroundColor Yellow
        } else {
            $failures += "Codex Home에 변경이 감지되었습니다: $($changed -join ', ')"
        }
    }
}

# ───────────────────────────────────────────────────── 우리 설정/로그 위치
Write-Section '참고: 이 프로그램이 쓰는 위치'
$appRoot = Join-Path $env:APPDATA 'CodexBackupManager'
Write-Result 'settings.json' (Join-Path $appRoot 'settings.json')
Write-Result 'logs' (Join-Path $appRoot 'logs')
if (Test-Path -LiteralPath $appRoot) {
    Get-ChildItem -LiteralPath $appRoot -Recurse -File |
        ForEach-Object { Write-Host ('    {0}  ({1} bytes)' -f $_.FullName, $_.Length) }
} else {
    Write-Host '    (아직 생성되지 않음)'
}

# ────────────────────────────────────────────────────────────── 요약
Write-Section '요약'
if ($failures.Count -eq 0) {
    Write-Host '  모든 자동 검증 단계를 통과했습니다.' -ForegroundColor Green
    exit 0
}

Write-Host '  실패한 항목:' -ForegroundColor Red
$failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
exit 1
