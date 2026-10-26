# Which sensors this machine acquires, switched in one step.
#
# sensors.json is the file SensorCaptures reads on every editor Play, and VRTrun uploads to
# a role for a built player. It is gitignored - it describes what is physically plugged into
# *this* machine, which git cannot know - so a pull never updates it and a stale one is
# invisible until a run produces nothing. On 2026-09-19 one machine kept a one-line EmotiBit
# file from a two-machine test, so no BITalino capture started in five consecutive sessions
# and the failure looked like dead hardware.
#
# So: pick a scenario by name, and be told what is now active before pressing Play. The
# scenarios are the sensors-<name>.json files in sensor-scenarios/ beside this script.
#
#   .\sensors.ps1                        show what is active, and the scenarios available
#   .\sensors.ps1 bitalino+emotibit      both devices on this machine
#   .\sensors.ps1 bitalino -Port COM5    fill the port placeholder while switching
#   .\sensors.ps1 none                   no captures at all, for scene-only scenarios
#
# Every command is resolved on PATH the way SensorCaptures.Resolve does, so a missing venv
# is reported here rather than as a red Console line after the scene has already loaded.

[CmdletBinding()]
param(
    # The scenario to activate: the <name> in sensor-scenarios/sensors-<name>.json.
    [Parameter(Position = 0)][string]$Scenario,
    # Replaces COM_REPLACE_ME, for the templates that do not name a port.
    [string]$Port
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$active = Join-Path $root "sensors.json"
$scenarios = Join-Path $root "sensor-scenarios"

function Show-Commands($path, $label) {
    if (-not (Test-Path $path)) { "${label}: none - the editor will start no captures"; return }
    $text = Get-Content $path -Raw
    # ConvertFrom-Json emits the JSON array as a single object, so @(...) around it nests
    # rather than unrolls: every command then prints on one line, and an empty list counts
    # as one null entry. A plain assignment unrolls it; only a lone entry needs @().
    # (A one-entry file written in the argument-array form would be read here as several
    # commands. SensorCaptures does the real parsing; this display is the only thing affected.)
    $parsed = $text | ConvertFrom-Json
    $commands = if ($parsed -is [array]) { $parsed } else { @($parsed) }
    if ($commands.Count -eq 0) { "${label}: empty - the editor will start no captures"; return }
    "${label}:"
    foreach ($c in $commands) {
        # An argument array is the form for paths containing spaces; a bare string is the common case.
        $line = if ($c -is [array]) { $c -join " " } else { $c }
        $program = ($line -split " ", 2)[0]
        $found = Get-Command $program -ErrorAction SilentlyContinue
        $mark = if ($found) { "ok " } else { "NOT ON PATH" }
        "  [$mark] $line"
        if ($found) { "          $($found.Source)" }
    }
    if ($text -match "COM_REPLACE_ME") {
        Write-Warning "A port placeholder is still in place. Re-run with -Port <COMn>."
    }
}

if (-not $Scenario) {
    Show-Commands $active "Active"
    ""
    "Scenarios:"
    Get-ChildItem (Join-Path $scenarios "sensors-*.json") | ForEach-Object {
        "  " + ($_.BaseName -replace "^sensors-", "")
    }
    return
}

$template = Join-Path $scenarios "sensors-$Scenario.json"
if (-not (Test-Path $template)) {
    throw "No scenario '$Scenario'. Run this script with no arguments to list them."
}

$text = Get-Content $template -Raw
if ($Port) { $text = $text.Replace("COM_REPLACE_ME", $Port) }
[System.IO.File]::WriteAllText($active, $text, (New-Object System.Text.UTF8Encoding($false)))

# Report the file that was actually written, not the command that was meant to write it: a
# cd into a directory that does not exist leaves the file somewhere harmless and invisible.
"Wrote $active from sensors-$Scenario.json"
""
Show-Commands $active "Active"
