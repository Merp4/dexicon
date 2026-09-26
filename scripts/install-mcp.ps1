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
  project (default) or user, for the clients that have both. Claude Code (-Client
  claude-code, -What skill or hooks, -What all with no other client, -Uninstall) defaults to
  user instead: its project/local scope is keyed by the exact working-directory string, so a
  server or hook added from one shell can be invisible to a session launched from another
  that reports the same path differently. -Scope project writes a per-repo entry instead,
  and for Claude Code that .mcp.json holds the key in plain text: keep it out of version
  control. An install made before this default is removed with -Uninstall -Scope project.

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
  ./scripts/install-mcp.ps1 -Client claude-code -Token dex_...
  ./scripts/install-mcp.ps1 -Client claude-code -Scope project -Project ~/src/my-app
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
  [switch]$List,

  # mcp registers the server, as this script always has. skill and hooks install into
  # .claude/, and are Claude Code only. all does the three together.
  [ValidateSet('mcp', 'skill', 'hooks', 'all')][string]$What = 'mcp',

  # The per-prompt hook searches on every message and a query it has not embedded before
  # costs seconds. Installed either way; registered only when this is passed.
  [switch]$WithContextHook,

  [switch]$Uninstall
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

# ── The skill and the hooks ──────────────────────────────────────────────────
# Everything below installs into `.claude/`, which belongs to the user. Two rules hold
# throughout: name what we own so uninstall never has to guess, and read a version marker
# before overwriting so an edited copy can be left alone.

$SkillName = 'dexicon-search'
$HookFiles = @('dexicon-corpora.py', 'dexicon-context.py', 'dexicon_hook_lib.py')
$ConfigName = 'dexicon-hooks.env'

function Get-BaseUrl([string]$mcpUrl) {
  # -Url names the MCP endpoint. The hooks call the REST API beside it.
  ($mcpUrl -replace '/mcp/?$', '')
}

function Get-ClaudeHome([string]$scope, [string]$projectDir) {
  if ($scope -eq 'user') { Join-Path $HOME '.claude' } else { Join-Path $projectDir '.claude' }
}

function Get-Interpreter {
  <#
    Resolved once, at install time, and written into settings.json: deciding it at run time
    would mean a shebang, which Windows does not honour.

    Each candidate is RUN, not just found. Windows ships a zero-byte `python3.exe` alias in
    WindowsApps that opens the Microsoft Store when no Python is installed, and
    `Get-Command` finds it either way. Taking it on trust installs a hook that cannot start,
    and hooks are built to fail quietly.
  #>
  foreach ($c in @('python3', 'python')) {
    $cmd = Get-Command $c -ErrorAction SilentlyContinue
    if (-not $cmd) { continue }
    try {
      $probe = & $cmd.Source -c 'import sys; print(sys.version_info[0])' 2>$null
      if ($LASTEXITCODE -eq 0 -and $probe -eq '3') { return $cmd.Source }
      Info "skipping $($cmd.Source): not a working Python 3"
    }
    catch {
      Info "skipping $($cmd.Source): would not run"
    }
  }
  $null
}

function Get-MarkerVersion([string]$path, [string]$marker) {
  if (-not (Test-Path $path)) { return $null }
  $line = Select-String -LiteralPath $path -Pattern "$marker(?::\s*)(\d+)" -List
  if ($line) { [int]$line.Matches[0].Groups[1].Value } else { $null }
}

function Copy-Versioned([string]$from, [string]$to, [string]$marker) {
  <#
    Copy unless the target is the same version or newer, or has lost its marker. A file with
    no marker is one someone edited or wrote themselves, and overwriting it silently is how
    an installer earns a reputation.
  #>
  $ours = Get-MarkerVersion $from $marker
  $theirs = Get-MarkerVersion $to $marker
  $leaf = Split-Path -Leaf $to

  if ((Test-Path $to) -and $null -eq $theirs) {
    Warn "  $leaf has no $marker marker; leaving it alone. Delete it to take this version."
    return $false
  }
  if ($null -ne $theirs -and $theirs -ge $ours) {
    Info "$leaf already at version $theirs"
    return $false
  }

  New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
  Copy-Item -LiteralPath $from -Destination $to -Force
  if ($null -eq $theirs) { Info "installed  $leaf (version $ours)" }
  else { Info "upgraded   $leaf ($theirs -> $ours)" }
  $true
}

function Install-Skill([string]$claudeHome) {
  $src = Join-Path $repoRoot "skills/$SkillName/SKILL.md"
  if (-not (Test-Path $src)) { Die "  cannot find $src" }

  $dest = Join-Path $claudeHome "skills/$SkillName/SKILL.md"
  if (-not $PSCmdlet.ShouldProcess($dest, 'install skill')) {
    Info "would install $dest"; return
  }

  [void](Copy-Versioned $src $dest 'dexicon-skill-version')
  Info "skill    $dest"
  Info "invoke   /$SkillName <query>, or let Claude reach for it on its own"
}

function Write-HookConfig([string]$path, [string]$token, [string]$baseUrl) {
  <#
    One file: the connection, the credential and every knob. Named after the
    POST /api/context fields so there is no second vocabulary. Commented-out lines are the
    documentation -- an unset key is not sent, so the server's own default applies.
  #>
  if (Test-Path $path) {
    Info "config   $path (kept; delete it to regenerate)"
    return
  }

  $lines = @(
    '# Dexicon hooks. Read by dexicon-corpora.py and dexicon-context.py.',
    '# An unset key is not sent, so the server default applies.',
    '',
    "DEXICON_URL=$baseUrl",
    "DEXICON_TOKEN=$token",
    '',
    '# Seconds, covering connect and read. Above the cold cost rather than near it: a',
    '# query the embedder has not seen measured about 5.4s on a 15,213-chunk index.',
    'DEXICON_TIMEOUT=10',
    '',
    '# POST /api/context, used by the UserPromptSubmit hook.',
    '# maxChars: the server cuts its last block to fit and says how much it dropped,',
    '# so this is how much to put in front of every prompt. The hook defaults to 2000.',
    '#DEXICON_CONTEXT_MAX_CHARS=2000',
    '#DEXICON_CONTEXT_LIMIT=10',
    '#DEXICON_CONTEXT_MODE=hybrid',
    '#DEXICON_CONTEXT_NEIGHBOURS=1',
    '',
    '# Comma-separated. Narrows the per-prompt hook without narrowing the key, so the',
    '# agent''s own searches still reach every corpus.',
    '#DEXICON_CONTEXT_CORPUS=docs',
    '',
    '# Prompts shorter than this are not worth a search. "ok", "yes", "carry on".',
    '#DEXICON_CONTEXT_MIN_PROMPT_CHARS=25',
    '',
    '# SessionStart: characters of each corpus description to announce.',
    '#DEXICON_CORPORA_DESCRIPTION_CHARS=180'
  )

  New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
  Set-Content -LiteralPath $path -Value $lines -Encoding utf8
  Info "config   $path"
}

function Add-HookEntry([hashtable]$hooks, [string]$eventName, [string]$command) {
  <#
    Claude Code takes { matcher?, hooks: [ { type, command } ] } per event. Ours is added
    beside anything already registered, and replaced rather than duplicated on a re-run --
    matched on the command containing our file name, since the interpreter path can move.
  #>
  if (-not $hooks.Contains($eventName)) { $hooks[$eventName] = @() }

  $leaf = Split-Path -Leaf ($command -split '"' | Where-Object { $_ -match '\.py$' } | Select-Object -First 1)
  if (-not $leaf) { $leaf = $eventName }

  $kept = @($hooks[$eventName] | Where-Object {
      $json = $_ | ConvertTo-Json -Depth 10 -Compress
      $json -notmatch [regex]::Escape($leaf)
    })

  $hooks[$eventName] = $kept + @(@{ hooks = @(@{ type = 'command'; command = $command }) })
}

function Install-Hooks([string]$claudeHome, [string]$token, [string]$baseUrl, [bool]$withContext) {
  $interpreter = Get-Interpreter
  if (-not $interpreter) {
    Die @"

  No python3 or python on PATH, and the hooks are Python.

  They use only the standard library, so any Python 3 will do. Install one, or skip the
  hooks with -What skill.
"@
  }

  $hookDir = Join-Path $claudeHome 'hooks'
  $configPath = Join-Path $claudeHome $ConfigName
  $settingsPath = Join-Path $claudeHome 'settings.json'

  if (-not $PSCmdlet.ShouldProcess($hookDir, 'install hooks')) {
    Info "would install $($HookFiles -join ', ') to $hookDir"
    Info "would write   $configPath"
    Info "would register SessionStart$(if ($withContext) { ' and UserPromptSubmit' }) in $settingsPath"
    return
  }

  foreach ($f in $HookFiles) {
    $src = Join-Path $repoRoot "hooks/claude/$f"
    if (-not (Test-Path $src)) { Die "  cannot find $src" }
    [void](Copy-Versioned $src (Join-Path $hookDir $f) 'dexicon-hook-version')
  }

  Write-HookConfig $configPath $token $baseUrl

  # ── settings.json ──────────────────────────────────────────────────────────
  $settings = [ordered]@{}
  if (Test-Path $settingsPath) {
    $raw = Get-Content $settingsPath -Raw
    if ($raw.Trim()) {
      try { $settings = $raw | ConvertFrom-Json -AsHashtable }
      catch { Die "`n  $settingsPath is not valid JSON, so this script will not touch it." }
    }
    $backup = "$settingsPath.$(Get-Date -Format yyyyMMddHHmmss).bak"
    Copy-Item $settingsPath $backup
    Info "backed up  $(Split-Path -Leaf $backup)"
  }

  if (-not $settings.Contains('hooks')) { $settings['hooks'] = @{} }
  $hooks = $settings['hooks']

  Add-HookEntry $hooks 'SessionStart' "`"$interpreter`" `"$(Join-Path $hookDir 'dexicon-corpora.py')`""

  if ($withContext) {
    Add-HookEntry $hooks 'UserPromptSubmit' "`"$interpreter`" `"$(Join-Path $hookDir 'dexicon-context.py')`""
  }
  elseif ($hooks.Contains('UserPromptSubmit')) {
    $hooks['UserPromptSubmit'] = @($hooks['UserPromptSubmit'] | Where-Object {
        ($_ | ConvertTo-Json -Depth 10 -Compress) -notmatch 'dexicon-context\.py'
      })
    if (-not $hooks['UserPromptSubmit']) { $hooks.Remove('UserPromptSubmit') }
  }

  Set-Content -LiteralPath $settingsPath -Value ($settings | ConvertTo-Json -Depth 10) -Encoding utf8
  Ok "`nHooks installed for Claude Code"
  Info "settings $settingsPath"
  Info "session  dexicon-corpora.py  announces the corpora once per session"

  if ($withContext) {
    Warn "`n  UserPromptSubmit is now live: every prompt runs a search."
    Info "A query it has not embedded before costs seconds, not milliseconds. Remove the"
    Info "UserPromptSubmit entry, or re-run without -WithContextHook, to turn it off."
  }
  else {
    Info "prompt   dexicon-context.py  installed but NOT registered; add -WithContextHook"
  }

  Info "`nEdit $ConfigName to change the budget, the mode or which corpora it reaches."
  Info "Restart Claude Code to pick the hooks up."
}

function Uninstall-Artefacts([string]$claudeHome) {
  $settingsPath = Join-Path $claudeHome 'settings.json'
  $targets = @(
    (Join-Path $claudeHome "skills/$SkillName"),
    (Join-Path $claudeHome $ConfigName)
  ) + ($HookFiles | ForEach-Object { Join-Path $claudeHome "hooks/$_" })

  $present = @($targets | Where-Object { Test-Path $_ })
  $hasEntry = (Test-Path $settingsPath) -and ((Get-Content $settingsPath -Raw) -match 'dexicon-(corpora|context)\.py')

  if (-not $present -and -not $hasEntry) {
    Info "`nNothing of Dexicon's found under $claudeHome"
    return
  }

  Write-Host "`nWould remove:"
  $present | ForEach-Object { Info $_ }
  if ($hasEntry) { Info "the Dexicon hook entries in $settingsPath" }

  if (-not $PSCmdlet.ShouldProcess($claudeHome, 'remove the Dexicon skill, hooks and config')) { return }

  foreach ($t in $present) { Remove-Item -LiteralPath $t -Recurse -Force; Info "removed  $t" }

  if ($hasEntry) {
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    $backup = "$settingsPath.$(Get-Date -Format yyyyMMddHHmmss).bak"
    Copy-Item $settingsPath $backup
    Info "backed up  $(Split-Path -Leaf $backup)"

    foreach ($e in @('SessionStart', 'UserPromptSubmit')) {
      if (-not $settings['hooks'].Contains($e)) { continue }
      $settings['hooks'][$e] = @($settings['hooks'][$e] | Where-Object {
          ($_ | ConvertTo-Json -Depth 10 -Compress) -notmatch 'dexicon-(corpora|context)\.py'
        })
      if (-not $settings['hooks'][$e]) { $settings['hooks'].Remove($e) }
    }
    if (-not $settings['hooks'].Count) { $settings.Remove('hooks') }

    Set-Content -LiteralPath $settingsPath -Value ($settings | ConvertTo-Json -Depth 10) -Encoding utf8
    Info "removed  the hook entries from settings.json"
  }

  Ok "`nRemoved. The API key still exists; revoke it on the Access screen if it was only for this."
  Info "Restart Claude Code.`n"
}

# ── What to install ──────────────────────────────────────────────────────────
# The skill and the hooks are Claude Code's, so -What all does not need to be told.
if ($What -eq 'all' -and -not $Client) { $Client = 'claude-code' }

# Dexicon is meant to work the same way everywhere: install once and every session in every
# repo has it. Claude Code's own project/local scope is keyed by the literal cwd string (case
# and slash direction included), which this script cannot steer reliably, so anything that
# only ever touches Claude Code defaults to the one scope that is not cwd-dependent, unless
# a scope was explicitly asked for. -Uninstall only ever removes Claude Code artefacts too.
# -What all counts through $Client, set just above when no other client was named: with
# -Client cursor it writes Cursor's config, which keeps the project default.
$targetsClaudeCode = $Uninstall -or ($Client -eq 'claude-code') -or ($What -in @('skill', 'hooks'))
if (-not $PSBoundParameters.ContainsKey('Scope') -and $targetsClaudeCode) { $Scope = 'user' }

$claudeProject = (Resolve-Path -LiteralPath $Project -ErrorAction SilentlyContinue)?.Path
if (-not $claudeProject) { $claudeProject = $Project }
$claudeHome = Get-ClaudeHome $Scope $claudeProject

if ($Uninstall) {
  Uninstall-Artefacts $claudeHome
  exit 0
}

if ($What -in @('skill', 'all')) { Install-Skill $claudeHome }

if ($What -in @('hooks', 'all')) {
  if (-not $Token) { $Token = Read-TokenFromEnv }
  if (-not $Token) {
    Die @"

No key, and this script will not invent one.

  Pass -Token dex_..., or set DEXICON_BOOTSTRAP_TOKEN in .env.

The hooks read, so a key with `search` alone is enough and is what they should have.
Issue one on the Access screen and tick the corpora it may reach.
"@
  }
  Install-Hooks $claudeHome $Token (Get-BaseUrl $Url) $WithContextHook.IsPresent
}

# mcp and all carry on into the client configuration; the other two are done.
if ($What -in @('skill', 'hooks')) { exit 0 }

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
  $args_ = @('mcp', 'add', '--transport', 'http', $Name, $Url, '--header', $header, '--scope', $Scope)

  if (-not $PSCmdlet.ShouldProcess('claude', "mcp add $Name $Url (scope: $Scope)")) {
    Info "would run: claude $((Hide-Token ($args_ -join ' ') $Token))"
    if ($Scope -eq 'project') { Info "in directory: $claudeProject" }
    exit 0
  }

  if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Die "  'claude' is not on PATH. Install Claude Code, or configure another client."
  }

  # --scope project writes .mcp.json into the CLI's own working directory, so run it from
  # -Project rather than wherever this script happens to be standing. --scope user is one
  # global registration and does not care where it runs.
  if ($Scope -eq 'project') {
    Push-Location -LiteralPath $claudeProject
    try { & claude @args_ } finally { Pop-Location }
  } else {
    & claude @args_
  }
  if ($LASTEXITCODE -ne 0) { Die "`n  claude mcp add failed (exit $LASTEXITCODE)." }

  Ok "`nAdded '$Name' ($Scope scope)."
  if ($Scope -eq 'user') {
    Info "Global: every project's Claude Code session picks this up, with no per-repo setup."
  } else {
    Info "Written to .mcp.json in ${claudeProject}: sessions started in that project only."
    Info "That file holds the key in plain text. Keep it out of version control."
  }
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
