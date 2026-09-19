#Requires -Version 7.0
<#
.SYNOPSIS
  Register Dexicon as an MCP server with an agent client.

.DESCRIPTION
  Dexicon is a remote MCP server over streamable HTTP with bearer auth, so for most clients
  this is one URL and one header in a JSON file. The value here is not the writing — it is
  knowing which file, which key spelling, and which clients cannot take a URL at all.

  It MERGES into an existing config rather than replacing it, backs the file up first, and
  never prints the token. docs/12-clients.md has the same thing by hand.

.PARAMETER Client
  claude-code | cursor | vscode | windsurf | cline | claude-desktop | zed

.PARAMETER Token
  A Dexicon API key. If omitted, DEXICON_BOOTSTRAP_TOKEN is read from the repo's .env when
  it has a value. Never generated — a credential this script invented would be one nobody
  chose, and it would not work.

.PARAMETER Scope
  project (default) or user, for the clients that have both.

.PARAMETER Project
  The project directory to write into for -Scope project. Defaults to the current directory,
  which is almost certainly where you want it — not the Dexicon checkout.

.PARAMETER Url
  Defaults to http://localhost:8477/mcp. Use host.docker.internal if the AGENT runs in a
  container: localhost inside a container is that container.

.EXAMPLE
  ./scripts/install-mcp.ps1 -List
  ./scripts/install-mcp.ps1 -Client cursor
  ./scripts/install-mcp.ps1 -Client vscode -Project ~/src/my-app -WhatIf
  ./scripts/install-mcp.ps1 -Client claude-code -Scope user -Token dex_...
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [ValidateSet('claude-code', 'cursor', 'vscode', 'windsurf', 'cline', 'claude-desktop', 'zed')]
  [string]$Client,

  [string]$Token,
  [ValidateSet('project', 'user')][string]$Scope = 'project',
  [string]$Project = '.',
  [string]$Url = 'http://localhost:8477/mcp',
  [string]$Name = 'dexicon',
  [switch]$List
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$Clients = @('claude-code', 'cursor', 'vscode', 'windsurf', 'cline', 'claude-desktop', 'zed')

function Info($m) { Write-Host "  $m" }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Warn($m) { Write-Host $m -ForegroundColor Yellow }
function Die($m)  { Write-Host $m -ForegroundColor Red; exit 1 }

# ── Where each client keeps its configuration ────────────────────────────────
# $null means "this client has no file we can safely write" — the UI owns it, or the scope
# does not exist. Saying so beats writing a file the client will never read.
function Get-ConfigPath([string]$client, [string]$scope, [string]$projectDir) {
  switch ($client) {
    'cursor' {
      if ($scope -eq 'user') { Join-Path $HOME '.cursor/mcp.json' }
      else { Join-Path $projectDir '.cursor/mcp.json' }
    }
    'vscode' {
      # User scope lives inside VS Code's own settings.json, which is full of unrelated
      # preferences and often JSONC. Not ours to rewrite.
      if ($scope -eq 'user') { $null }
      else { Join-Path $projectDir '.vscode/mcp.json' }
    }
    'windsurf' { Join-Path $HOME '.codeium/windsurf/mcp_config.json' }
    'cline'    { $null }   # extension storage; the MCP Servers panel owns it
    'zed'      { Join-Path $HOME '.config/zed/settings.json' }
    'claude-desktop' {
      if ($IsMacOS)     { Join-Path $HOME 'Library/Application Support/Claude/claude_desktop_config.json' }
      elseif ($IsLinux) { Join-Path $HOME '.config/Claude/claude_desktop_config.json' }
      else              { Join-Path $env:APPDATA 'Claude/claude_desktop_config.json' }
    }
    default { $null }
  }
}

function Test-IsBridged([string]$client) { $client -in @('claude-desktop', 'zed') }

# ── The entry each client expects ────────────────────────────────────────────
# One server, five spellings. Windsurf wants serverUrl where everyone else wants url;
# VS Code wants an explicit type; Claude Desktop and Zed cannot take a URL at all and go
# through mcp-remote as a stdio bridge.
function Get-ServerEntry([string]$client, [string]$url, [string]$token) {
  $auth = "Bearer $token"

  # NO SPACE after the colon in the bridge form. mcp-remote splits each --header on the
  # first colon, so a space lands inside the header value and the server rejects the token.
  $bridgeArgs = @('-y', 'mcp-remote', $url, '--header', "Authorization:$auth")

  switch ($client) {
    'vscode'         { @{ type = 'http'; url = $url; headers = @{ Authorization = $auth } } }
    'windsurf'       { @{ serverUrl = $url; headers = @{ Authorization = $auth } } }
    'cline'          { @{ type = 'streamableHttp'; url = $url; headers = @{ Authorization = $auth } } }
    'cursor'         { @{ url = $url; headers = @{ Authorization = $auth } } }
    'zed'            { @{ source = 'custom'; command = 'npx'; args = $bridgeArgs } }
    'claude-desktop' { @{ command = 'npx'; args = $bridgeArgs } }
    default          { throw "no entry shape for '$client'" }
  }
}

function Get-RootKey([string]$client) {
  switch ($client) {
    'vscode' { 'servers' }
    'zed'    { 'context_servers' }
    default  { 'mcpServers' }
  }
}

function Read-TokenFromEnv {
  $envFile = Join-Path $repoRoot '.env'
  if (-not (Test-Path $envFile)) { return $null }

  $line = Get-Content $envFile |
    Where-Object { $_ -match '^\s*DEXICON_BOOTSTRAP_TOKEN\s*=\s*\S' } |
    Select-Object -First 1
  if (-not $line) { return $null }

  ($line -split '=', 2)[1].Trim().Trim('"').Trim("'")
}

function Hide-Token([string]$text, [string]$token) {
  if (-not $token) { return $text }
  $text -replace [regex]::Escape($token), 'dex_***'
}

# ── Listing ──────────────────────────────────────────────────────────────────
if ($List -or -not $Client) {
  $projectDir = (Resolve-Path -LiteralPath $Project -ErrorAction SilentlyContinue)?.Path
  if (-not $projectDir) { $projectDir = $Project }

  Write-Host "`nDexicon MCP clients   (scope: $Scope)`n"
  Info ('{0,-16} {1,-13} {2,-16} {3}' -f 'CLIENT', 'TRANSPORT', 'STATE', 'CONFIG')

  foreach ($c in $Clients) {
    $p = Get-ConfigPath $c $Scope $projectDir

    $state =
      if ($c -eq 'claude-code') { 'via CLI' }
      elseif (-not $p)          { 'via client UI' }
      elseif (Test-Path $p)     {
        if ((Get-Content $p -Raw) -match [regex]::Escape("`"$Name`"")) { 'configured' } else { 'file exists' }
      }
      else { 'not configured' }

    $transport = if (Test-IsBridged $c) { 'stdio bridge' } else { 'http' }
    $shown = if ($p) { $p } else { '—' }
    Info ('{0,-16} {1,-13} {2,-16} {3}' -f $c, $transport, $state, $shown)
  }

  Write-Host ''
  Info './scripts/install-mcp.ps1 -Client <name>      add it'
  Info './scripts/install-mcp.ps1 -Client <name> -WhatIf   show the change without making it'
  Info 'docs/12-clients.md                           the manual version'
  Write-Host ''
  exit 0
}

# ── Token ────────────────────────────────────────────────────────────────────
if (-not $Token) { $Token = Read-TokenFromEnv }
if (-not $Token) {
  Die @"

No key, and this script will not invent one.

  Pass -Token dex_..., or set DEXICON_BOOTSTRAP_TOKEN in .env.

Issue one on the Access screen, which is also where you tick the corpora it may reach.
Nothing is minted on first run; what is printed once is the admin password that signs
you in:
  docker compose logs dexicon | Select-String "admin password"

A search-only key cannot reindex, and no key can carry admin, so none of them can delete
a corpus.
"@
}

if ($Token -notmatch '^dex_') {
  Warn "  Token does not start with 'dex_' — check it is a Dexicon token, not a provider key."
}

# ── Claude Code has its own CLI, and it owns the file ────────────────────────
if ($Client -eq 'claude-code') {
  $header = "Authorization: Bearer $Token"
  $args_ = @('mcp', 'add', '--transport', 'http', $Name, $Url, '--header', $header)
  if ($Scope -eq 'user') { $args_ += @('--scope', 'user') }

  if (-not $PSCmdlet.ShouldProcess('claude', "mcp add $Name $Url")) {
    Info "would run: claude $((Hide-Token ($args_ -join ' ') $Token))"
    exit 0
  }

  if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Die "  'claude' is not on PATH. Install Claude Code, or configure another client."
  }

  & claude @args_
  if ($LASTEXITCODE -ne 0) { Die "`n  claude mcp add failed (exit $LASTEXITCODE)." }

  Ok "`nAdded '$Name'."
  Info "Verify with: claude mcp list   (Dexicon should report Connected)"
  Write-Host ''
  exit 0
}

# ── Everyone else is a JSON file ─────────────────────────────────────────────
$projectDir = (Resolve-Path -LiteralPath $Project -ErrorAction SilentlyContinue)?.Path
if (-not $projectDir) { Die "  -Project path does not exist: $Project" }

$path = Get-ConfigPath $Client $Scope $projectDir
if (-not $path) {
  $why = if ($Client -eq 'vscode') {
    "VS Code's user scope lives in its own settings.json alongside every other preference.`n  Use -Scope project, or add it through the UI."
  } else {
    "$Client keeps MCP servers in extension storage rather than a file with a stable path."
  }
  Die @"

  Cannot write $Client at -Scope $Scope.

  $why

  The JSON to paste is in docs/12-clients.md — for $Client it is a URL and an
  Authorization header, nothing Dexicon-specific.
"@
}

$rootKey = Get-RootKey $Client
$entry = Get-ServerEntry $Client $Url $Token

$config = [ordered]@{}
if (Test-Path $path) {
  $existing = Get-Content $path -Raw
  if ($existing.Trim()) {
    try {
      # -AsHashtable so unrelated keys survive the round trip. Parsing to PSCustomObject and
      # back is how a script quietly drops someone else's settings.
      $config = $existing | ConvertFrom-Json -AsHashtable
    } catch {
      $hint = if ($Client -eq 'zed') {
        "`n  Zed ships settings.json with comments, and PowerShell's JSON parser rejects those.`n  Paste the block from docs/12-clients.md instead — it is four lines."
      } else { '' }
      Die "`n  $path is not valid JSON, so this script will not touch it.$hint"
    }
  }
}

if (-not $config.Contains($rootKey)) { $config[$rootKey] = @{} }

$replacing = [bool]$config[$rootKey][$Name]
$config[$rootKey][$Name] = $entry

$json = $config | ConvertTo-Json -Depth 10

if (-not $PSCmdlet.ShouldProcess($path, $(if ($replacing) { "replace '$Name'" } else { "add '$Name'" }))) {
  Info "would write $path :"
  Write-Host ''
  (Hide-Token $json $Token) -split "`r?`n" | ForEach-Object { Info "  $_" }
  Write-Host ''
  exit 0
}

New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null

if (Test-Path $path) {
  # Timestamped rather than .bak: a second run should not destroy the first run's safety net.
  $backup = "$path.$(Get-Date -Format yyyyMMddHHmmss).bak"
  Copy-Item $path $backup
  Info "backed up  $(Split-Path -Leaf $backup)"
}

Set-Content -LiteralPath $path -Value $json -Encoding utf8

Ok "`n$(if ($replacing) { 'Updated' } else { 'Added' }) '$Name' for $Client"
Info "file   $path"
Info "url    $Url"
Info "token  dex_*** — written to the file, not shown here"

if (Test-IsBridged $Client) {
  Warn "`n  $Client cannot take a URL directly, so this uses 'npx mcp-remote' as a stdio bridge."
  Info "npx must be on PATH, and the first launch downloads mcp-remote."
  if ($Client -eq 'claude-desktop') {
    Info "Settings -> Connectors -> add custom connector skips the bridge, and is the path Anthropic maintains."
  }
}

Info "`nRestart $Client to pick it up.`n"
