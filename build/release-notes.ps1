<#
.SYNOPSIS
Writes the user-facing notes for a WatchTower release.

.DESCRIPTION
The agent's console shows these notes as plain text under "What's new", and the GitHub release
page shows the same text, so they are written as short titled sections with "-" bullets and no
other Markdown.

Where the notes come from, in order:
1. The "## Release notes" section of the pull request merged for -Sha (written for users).
2. Otherwise the commits since -PreviousTag, grouped by their Sentry-style type:
   feat -> "New", fix -> "Fixes", perf -> "Faster". Other types (docs, ci, test, build, ref,
   chore, style, meta, license) are internal and left out.
3. Otherwise a one-line "maintenance release" note.
A "Good to know" line about updating and a link to the full comparison are always added.

.EXAMPLE
./build/release-notes.ps1 -Version 1.0.22 -PreviousTag v1.0.21 -Repository owner/name -Sha <commit> -OutFile notes.md
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    # The previous release tag (e.g. v1.0.21); empty for the first release.
    [string] $PreviousTag,
    # owner/name on GitHub, for the pull-request lookup and the comparison link.
    [Parameter(Mandatory)] [string] $Repository,
    # The commit being released.
    [Parameter(Mandatory)] [string] $Sha,
    [Parameter(Mandatory)] [string] $OutFile,
    # Skip the pull-request lookup (local runs without the gh CLI or a token).
    [switch] $NoPullRequest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-PullRequestNotes {
    if ($NoPullRequest -or -not (Get-Command gh -ErrorAction SilentlyContinue)) { return $null }
    try {
        $json = gh api "repos/$Repository/commits/$Sha/pulls" -H 'Accept: application/vnd.github+json' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
        $pr = @($json | ConvertFrom-Json) | Where-Object { $_.merged_at } | Select-Object -First 1
        if (-not $pr -or -not $pr.body) { return $null }
        Write-Host "Release notes source: pull request #$($pr.number)"
        return Get-NotesSection $pr.body
    }
    catch {
        Write-Host "Could not read the pull request ($($_.Exception.Message)); using the commits."
        return $null
    }
}

# The text between a "## Release notes" heading (any level, any case) and the next heading of the
# same or a higher level, without HTML comments.
function Get-NotesSection([string] $body) {
    $lines = ($body -replace '(?s)<!--.*?-->', '') -split "`r?`n"
    $start = -1; $level = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^(#{1,6})\s*release notes\s*$') { $start = $i + 1; $level = $Matches[1].Length; break }
    }
    if ($start -lt 0) { return $null }
    $section = New-Object System.Collections.Generic.List[string]
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^(#{1,6})\s' -and $Matches[1].Length -le $level) { break }
        $section.Add($lines[$i])
    }
    # Sub-headings ("### New") become plain section titles for the plain-text console view. Empty
    # template bullets ("- ") are dropped, and so is a title with nothing left under it, so an
    # untouched template yields no notes (and the commits are used instead).
    $groups = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($line in $section) {
        if ($line -match '^\s*$' -or $line -match '^\s*[-*]\s*$') { continue }
        if ($line -match '^#{1,6}\s*(.+?)\s*$') {
            $current = [pscustomobject]@{ Title = $Matches[1]; Lines = New-Object System.Collections.Generic.List[string] }
            $groups.Add($current); continue
        }
        if (-not $current) {
            $current = [pscustomobject]@{ Title = $null; Lines = New-Object System.Collections.Generic.List[string] }
            $groups.Add($current)
        }
        $current.Lines.Add($line.TrimEnd())
    }
    $parts = foreach ($g in $groups) {
        if (-not $g.Lines.Count) { continue }
        $bodyText = $g.Lines -join "`n"
        if ($g.Title) { "$($g.Title)`n`n$bodyText" } else { $bodyText }
    }
    if ($parts) { return ($parts -join "`n`n") } else { return $null }
}

function Get-CommitNotes {
    $range = if ($PreviousTag) { "$PreviousTag..$Sha" } else { $Sha }
    $subjects = @(git log $range --no-merges --format=%s)
    if ($LASTEXITCODE -ne 0) { throw "git log $range failed" }
    $groups = [ordered]@{ 'New' = @(); 'Fixes' = @(); 'Faster' = @() }
    foreach ($s in $subjects) {
        if ($s -match '^(feat|fix|perf)(\([^)]*\))?!?:\s*(.+)$') {
            $title = switch ($Matches[1]) { 'feat' { 'New' } 'fix' { 'Fixes' } 'perf' { 'Faster' } }
            $text = $Matches[3].Trim().TrimEnd('.')
            if ($groups[$title] -notcontains $text) { $groups[$title] += $text }
        }
    }
    $parts = foreach ($title in $groups.Keys) {
        if ($groups[$title].Count) { "$title`n`n" + (($groups[$title] | ForEach-Object { "- $_." }) -join "`n") }
    }
    if ($parts) { Write-Host "Release notes source: commits ($range)"; return ($parts -join "`n`n") }
    return $null
}

$notes = Get-PullRequestNotes
if (-not $notes) { $notes = Get-CommitNotes }
if (-not $notes) { $notes = "Fixes`n`n- Maintenance release with internal improvements; nothing changes in how WatchTower works." }

$footer = "Good to know`n`n- Updating keeps all your settings and history. WatchTower restarts once to finish installing."
if ($PreviousTag) { $footer += "`n`nFull list of changes: https://github.com/$Repository/compare/$PreviousTag...v$Version" }

$dir = Split-Path -Parent $OutFile
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllText($OutFile, "$notes`n`n$footer`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $OutFile"
