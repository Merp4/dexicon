<#
.SYNOPSIS
  Local development loop for Dexicon.

.DESCRIPTION
  Dexicon runs as a detached process during development so it survives between
  shell invocations, which means a rebuild fails while it holds its own DLLs.
  This script does the stop/build/start dance in the right order.

  Dependencies come from the compose stack with the DEBUG overlay, which publishes
  Qdrant and Ollama on NON-standard host ports so they cannot collide with an
  existing Qdrant or Ollama on this machine:

      docker compose -f docker-compose.yml -f docker-compose.debug.yml up -d

.PARAMETER Command
  up       start dependencies, build, and run
  restart  stop, rebuild, run  (the usual inner-loop command)
  stop     stop the app only
  down     stop the app and the dependency containers
  logs     tail the app log
  token    print the bootstrap token from the log
  reset    stop, delete the local catalogue and Qdrant collections, start fresh
  ui       build the SPA and copy it into wwwroot (the container does this at image build)
  test     stop the app, run the test suite, and put the app back as it was

.EXAMPLE
  ./scripts/dev.ps1 restart
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('up', 'restart', 'stop', 'down', 'logs', 'token', 'reset', 'ui', 'test')]
  [string]$Command = 'restart'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logFile = Join-Path $env:TEMP 'dexicon-run.log'
$errFile = Join-Path $env:TEMP 'dexicon-run.err'
$exe = Join-Path $root 'src/Dexicon/bin/Debug/net10.0/Dexicon.exe'

function Stop-Dexicon {
  $procs = Get-Process -Name Dexicon -ErrorAction SilentlyContinue
  if (-not $procs) { Write-Host 'dexicon: not running'; return }
  foreach ($p in $procs) { Stop-Process -Id $p.Id -Force; Write-Host "dexicon: stopped pid $($p.Id)" }
  Start-Sleep -Milliseconds 1500
}

function Start-Dexicon {
  $env:ASPNETCORE_URLS = 'http://127.0.0.1:8477'
  $env:DEXICON__QDRANT__ENDPOINT = 'http://127.0.0.1:16334'
  $env:DEXICON__QDRANT__APIKEY = 'dexicon-local-dev-key'
  $env:DEXICON__OLLAMA__ENDPOINT = 'http://127.0.0.1:21434'
  $env:DEXICON__STORAGE__DATAPATH = (Join-Path $root '.dexicon/data')
  $env:DEXICON__INDEXING__WORKSPACEROOT = $root
  $env:DEXICON__LOG__LEVEL = 'Information'

  $p = Start-Process -FilePath $exe -WorkingDirectory (Join-Path $root 'src/Dexicon') `
    -RedirectStandardOutput $logFile -RedirectStandardError $errFile -PassThru -WindowStyle Hidden

  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    try {
      Invoke-RestMethod -Uri 'http://127.0.0.1:8477/healthz/live' -TimeoutSec 2 | Out-Null
      Write-Host "dexicon: up on http://127.0.0.1:8477 (pid $($p.Id))"
      return
    } catch { if ($p.HasExited) { break } }
  }

  Write-Warning 'dexicon: did not become healthy. Last log lines:'
  Get-Content $logFile -Tail 25 -ErrorAction SilentlyContinue
  Get-Content $errFile -Tail 25 -ErrorAction SilentlyContinue
}

function Test-Dexicon {
  # The test project references the host, so `dotnet test` rebuilds it -- and that fails
  # with MSB3027 while the detached dev instance holds its own DLLs. Rather than leave
  # that as folklore, the script stops the app, tests, and restarts it only if it was
  # running when you started.
  $wasRunning = [bool](Get-Process -Name Dexicon -ErrorAction SilentlyContinue)
  Stop-Dexicon
  Push-Location $root
  try {
    dotnet test --nologo -v q 2>&1 |
      Select-String -Pattern ': error|error CS|\[FAIL\]|^\s+(Error Message|Assert|Shouldly)|Failed!|Passed!'
    $failed = $LASTEXITCODE -ne 0
  } finally { Pop-Location }
  if ($wasRunning) { Start-Dexicon }
  if ($failed) { throw 'tests failed' }
}

function Build-Dexicon {
  Push-Location $root
  try {
    dotnet build --nologo 2>&1 | Select-String -Pattern ': error|Build succeeded'
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
  } finally { Pop-Location }
}

function Start-Dependencies {
  Push-Location $root
  try {
    docker compose -f docker-compose.yml -f docker-compose.debug.yml up -d dexicon-qdrant dexicon-ollama 2>&1 |
      Select-Object -Last 4
  } finally { Pop-Location }
}

function Build-Ui {
  $ui = Join-Path $root 'clients/web-ui'
  Push-Location $ui
  try {
    if (-not (Test-Path (Join-Path $ui 'node_modules'))) { npm install --no-audit --no-fund | Out-Null }
    npm run build 2>&1 | Select-Object -Last 3
  } finally { Pop-Location }

  $dest = Join-Path $root 'src/Dexicon/wwwroot'
  # The built bundle is gitignored; only the placeholder index.html is tracked.
  Get-ChildItem (Join-Path $dest 'assets') -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
  Copy-Item (Join-Path $ui 'dist/*') $dest -Recurse -Force
  Write-Host "dexicon: UI copied to wwwroot"
}

switch ($Command) {
  'ui' { Build-Ui }
  'up' { Start-Dependencies; Build-Dexicon; Stop-Dexicon; Start-Dexicon }
  'restart' { Stop-Dexicon; Build-Dexicon; Start-Dexicon }
  'stop' { Stop-Dexicon }
  'test' { Test-Dexicon }
  'down' {
    Stop-Dexicon
    Push-Location $root
    try { docker compose -f docker-compose.yml -f docker-compose.debug.yml down 2>&1 | Select-Object -Last 3 }
    finally { Pop-Location }
  }
  'logs' { Get-Content $logFile -Tail 60 -ErrorAction SilentlyContinue }
  'token' {
    $m = Select-String -Path $logFile -Pattern 'dex_[A-Za-z0-9_-]+' -ErrorAction SilentlyContinue |
      Select-Object -First 1
    if ($m) { $m.Matches[0].Value } else { Write-Warning 'No bootstrap token in the log. It is printed once, on first run only.' }
  }
  'reset' {
    Stop-Dexicon
    Remove-Item (Join-Path $root '.dexicon') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'dexicon: local catalogue deleted'
    Build-Dexicon
    Start-Dexicon
    Write-Host 'dexicon: NOTE Qdrant collections are NOT deleted by reset -- they are keyed by corpus id, so a fresh catalogue simply stops referencing the old points.'
  }
}
