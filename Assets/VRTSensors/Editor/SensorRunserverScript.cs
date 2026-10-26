using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Debug = UnityEngine.Debug;

/// <summary>
/// Replaces the VRTrunserver.ps1 that VR2Gather's CopyConfigOnBuild puts beside a Windows player.
///
/// Theirs installs a VRTrunserver from GitHub when none is there, and a VRTrunserver without sensor
/// support starts no sensor captures: a participant machine set up with it records a whole session
/// without its sensor data and nothing says so. Ours starts the server from a VRTstatistics with
/// sensor support when that is installed - its VRTrunserver and the VRTsensors commands on PATH, a VRTsensors writing
/// capture format 2, and a VRTrunserver module declaring SENSOR_SUPPORT 2 - with --executable
/// set to this player, and otherwise stops and says what to install. It never installs anything.
///
/// Runs after CopyConfigOnBuild (callbackOrder 0), so it is the copy that stays.
/// </summary>
public class SensorRunserverScript : IPostprocessBuildWithReport
{
    public int callbackOrder { get { return 100; } }

    public const string Script = @"# VRTrunserver for this player, with sensor captures (VRTstatistics with VRTsensors).
# Written by VRTSensors' SensorRunserverScript at build time, in place of VR2Gather's script,
# which installs a VRTrunserver without checking that it starts sensor captures.
#   .\VRTrunserver.ps1          check this machine, then start the server for this player
#   .\VRTrunserver.ps1 -Check   only check, and say what is missing
param([switch]$Check)
$player = Join-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) 'PLAYER_EXE'
$server = Get-Command VRTrunserver -ErrorAction SilentlyContinue
$capture = Get-Command VRTsensors-bitalino -ErrorAction SilentlyContinue
if (-not $server -or -not $capture) {
    'Not ready: this player records sensor data only through a VRTrunserver with sensor support (VRTstatistics with VRTsensors).'
    'Install it as its docs/02-installing.md says, activate its .venv, and run this script again.'
    'Not found on PATH:' + $(if (-not $server) { ' VRTrunserver' }) + $(if (-not $capture) { ' VRTsensors-bitalino' })
    exit 1
}
$scripts = Split-Path $server.Source -Parent
if ($scripts -ne (Split-Path $capture.Source -Parent)) {
    'Not ready: VRTrunserver and the VRTsensors commands come from different environments:'
    '  ' + $server.Source
    '  ' + $capture.Source
    exit 1
}
# The environment's own VRTsensors must write capture format 2 or later; an older copy (such as
# one left in an older VRTstatistics clone) records data the checker and the docs do not describe.
$python = Join-Path $scripts 'python.exe'
$format = & $python -c 'import VRTsensors; print(vars(VRTsensors).get(''FORMAT'', 0))' 2>$null
if ($LASTEXITCODE -ne 0 -or [int]$format -lt 2) {
    'Not ready: the VRTsensors in ' + $scripts + ' is too old (capture format ' + $(if ($format) { $format } else { 'unknown' }) + ', need 2 or later).'
    'Activate the .venv of a current VRTstatistics checkout and run this script again.'
    exit 1
}
# And the server itself must be the one that starts captures. The command name proves nothing:
# a VRTrunserver module without sensor support earlier on the path (PYTHONPATH, an old checkout) answers to it and
# runs a whole session without recording a sensor. Its SENSOR_SUPPORT line is read from the
# module this environment would import, without importing it.
$support = & $python -c 'import importlib.util as u, re; s = u.find_spec(''VRTrunserver''); t = open(s.origin, encoding=''utf-8'').read() if s and s.origin else ''''; m = re.search(''^SENSOR_SUPPORT = ([0-9]+)'', t, re.M); print((m.group(1) if m else ''0'') + '' '' + (s.origin if s and s.origin else ''none''))' 2>$null
$supportLevel, $serverModule = [string]$support -split ' ', 2
if ($LASTEXITCODE -ne 0 -or [int]$supportLevel -lt 2) {
    'Not ready: the VRTrunserver this environment runs starts no sensor captures (sensor support ' + $(if ($supportLevel) { $supportLevel } else { 'unknown' }) + ', need 2 or later):'
    '  ' + $(if ($serverModule) { $serverModule } else { 'not found' })
    'That is a server from before sensor support. Remove it from PYTHONPATH, or activate the .venv of a current VRTstatistics checkout, and run this script again.'
    exit 1
}
if (-not (Test-Path $player)) { 'Not ready: no player at ' + $player; exit 1 }
'Ready: ' + $serverModule + ' (sensor support ' + $supportLevel + ', capture format ' + $format + ') for ' + $player
if ($Check) { exit 0 }
& $server.Source --executable $player
";

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.StandaloneWindows && report.summary.platform != BuildTarget.StandaloneWindows64) return;
        string path = Path.Combine(Path.GetDirectoryName(report.summary.outputPath), "VRTrunserver.ps1");
        // The player is whatever the build was called, not necessarily VR2Gather.exe.
        File.WriteAllText(path, Script.Replace("PLAYER_EXE", Path.GetFileName(report.summary.outputPath)));
        Debug.Log($"SensorRunserverScript: wrote {path} (starts a VRTrunserver with sensor support; never installs one)");
    }
}
