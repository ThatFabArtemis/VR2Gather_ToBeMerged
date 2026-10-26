using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using VRT.Core;
using Debug = UnityEngine.Debug;

/// <summary>
/// Starts the sensor captures for an editor Play the way VRTrunserver starts them for a
/// built player: from the sensors.json beside config.json, running in a fresh capture directory,
/// stopped when Play ends. Editor-only, so a build never carries a second launcher; there
/// the runner owns the captures and this file does not exist.
///
/// sensors.json is the same file VRTrun uploads to a role: a JSON list of command lines,
/// or of argument arrays for paths with spaces. A bare command is looked up on PATH, as
/// the runner does, so the venv holding the VRTsensors commands must be on the editor's
/// PATH, or the command written as a full path. Each capture's console output goes to
/// &lt;command&gt;-&lt;index&gt;.log in sensor-runs/&lt;run&gt; inside the config directory.
/// Relative output paths are resolved in that run, preserving earlier Play captures.
/// </summary>
[InitializeOnLoad]
public static class SensorCaptures
{
    // Survives the domain reloads Play mode causes, so a capture is never orphaned.
    const string StartedKey = "VRTSensors.captures";
    // The captures started in this domain, by pid, whose stdin can still be closed to ask them
    // to stop. A domain reload loses them; the pids in SessionState still get them killed.
    static readonly Dictionary<int, Process> Running = new Dictionary<int, Process>();
    // Milliseconds a capture gets to stop by itself before it is killed, as VRTrunserver allows.
    const int StopGrace = 5000;

    static SensorCaptures()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.quitting += StopAll;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode) StartAll();
        else if (state == PlayModeStateChange.ExitingPlayMode) StopAll();
    }

    /// <summary>
    /// Start this machine's captures for an editor Play, in a fresh directory of their own.
    ///
    /// Any still running are stopped first: a domain reload or a crashed Play can leave one
    /// behind, and a second copy writing the same file would corrupt both. The pids are
    /// kept in SessionState rather than a field, because Play mode reloads the domain and a
    /// field would not survive it - an orphaned capture holds the UDP port and the next Play
    /// then fails to listen at all.
    /// </summary>
    static void StartAll()
    {
        StopAll();
        var started = new List<string>();
        string file = "sensors.json";
        try
        {
            string dir = Path.GetDirectoryName(VRTConfig.ConfigFilename());
            // A sensors.local.json beside it wins, the same rule the runner follows: a
            // checked-in sensors.json is a documented default, and what is plugged into this
            // machine today goes in its own file. Which one was used is logged, because a
            // stale one is otherwise invisible until the run comes back with no samples.
            string local = Path.Combine(dir, "sensors.local.json");
            file = File.Exists(local) ? local : Path.Combine(dir, file);
            if (!File.Exists(file)) return;
            JArray commands = JArray.Parse(File.ReadAllText(file));
            WarnLeftOut(dir, file, commands);
            if (commands.Count == 0) return;
            string runDir = Path.Combine(dir, "sensor-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDir);
            // Copied under the name that ran, so the run says for itself which file it was.
            File.Copy(file, Path.Combine(runDir, Path.GetFileName(file)));
            Debug.Log($"SensorCaptures: captures in {runDir}, from {Path.GetFileName(file)}");
            for (int index = 0; index < commands.Count; index++)
            {
                string program, arguments;
                if (commands[index].Type == JTokenType.String)
                {
                    string command = commands[index].ToString().Trim();
                    if (command.StartsWith("\""))
                    {
                        int end = command.IndexOf('"', 1);
                        if (end < 0) throw new FormatException("Unclosed executable quote in sensors.json");
                        program = command.Substring(1, end - 1);
                        arguments = command.Substring(end + 1).TrimStart();
                    }
                    else
                    {
                        string[] parts = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        program = parts[0];
                        arguments = parts.Length > 1 ? parts[1] : "";
                    }
                }
                else
                {
                    string[] args = commands[index].ToObject<string[]>();
                    program = args[0];
                    arguments = string.Join(" ", args.Skip(1).Select(QuoteArgument));
                }
                if (Resolve(program) == null)
                    throw new FileNotFoundException($"'{program}' is not on the editor's PATH. The VRTsensors commands live in the VRTstatistics venv's Scripts directory; put that on your user PATH and restart Unity Hub - the editor inherits the Hub's environment, so restarting only the editor keeps the old PATH. A full path in sensors.json works too.");
                string log = Path.Combine(runDir, $"{Path.GetFileNameWithoutExtension(program)}-{index + 1}.log");
                Process process = Start(program, arguments, runDir, log);
                // ProcessName can fail before the new process has finished loading.
                started.Add($"{process.Id}:{Path.GetFileNameWithoutExtension(program)}");
                Debug.Log($"SensorCaptures: started {program} {arguments} (pid {process.Id}, output in {log})");
            }
        }
        catch (Exception e)
        {
            SessionState.SetString(StartedKey, string.Join(",", started));
            StopAll();
            started.Clear();
            Debug.LogError($"SensorCaptures: {file}: {e.Message}");
        }
        SessionState.SetString(StartedKey, string.Join(",", started));
    }

    /// <summary>
    /// Name every program sensors.json starts that the sensors.local.json in use leaves out.
    ///
    /// A local file replaces the shared one rather than adding to it, so that a machine can
    /// leave a sensor out; the price is that a sensor listed only in the shared file vanishes
    /// without an error. The runner warns the same way.
    /// </summary>
    static void WarnLeftOut(string dir, string file, JArray commands)
    {
        string shared = Path.Combine(dir, "sensors.json");
        if (Path.GetFileName(file) != "sensors.local.json" || !File.Exists(shared)) return;
        JArray listed;
        try { listed = JArray.Parse(File.ReadAllText(shared)); }
        catch (Exception) { return; }
        var running = new HashSet<string>(commands.Select(ProgramName));
        foreach (string program in listed.Select(ProgramName).Distinct())
            if (program.Length > 0 && !running.Contains(program))
                Debug.LogWarning($"SensorCaptures: sensors.json also starts {program}, which sensors.local.json does not; it will not run");
    }

    /// <summary>The program a sensors-file entry starts, without directory or extension, in lower case.</summary>
    static string ProgramName(JToken command)
    {
        string first;
        if (command.Type == JTokenType.String)
        {
            string text = command.ToString().Trim();
            first = text.StartsWith("\"") ? text.Substring(1, Math.Max(0, text.IndexOf('"', 1) - 1)) : text.Split(' ')[0];
        }
        else
        {
            string[] args = command.ToObject<string[]>();
            first = args.Length > 0 ? args[0] : "";
        }
        return first.Length == 0 ? "" : Path.GetFileNameWithoutExtension(first).ToLowerInvariant();
    }

    // Quote argv elements, including empty strings, embedded quotes and trailing backslashes.
    static string QuoteArgument(string value)
    {
        var quoted = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(c);
            slashes = 0;
        }
        quoted.Append('\\', slashes * 2);
        return quoted.Append('"').ToString();
    }

    /// <summary>Where the OS will find this program, or null for a bare name that is not on PATH. As the runner's shutil.which does, so both paths fail the same way.</summary>
    static string Resolve(string program)
    {
        if (program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(program) ? program : null;
        string[] extensions = Environment.OSVersion.Platform == PlatformID.Win32NT ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string extension in extensions)
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, program + extension)))
                    return Path.Combine(dir, program + extension);
        return null;
    }

    static Process Start(string program, string arguments, string dir, string log)
    {
        TextWriter writer = TextWriter.Synchronized(new StreamWriter(log) { AutoFlush = true });
        int closedStreams = 0;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(program, arguments)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Held so that closing it asks the capture to stop: see StopAll.
                RedirectStandardInput = true,
            },
            EnableRaisingEvents = true,
        };
        DataReceivedEventHandler receive = (s, e) =>
        {
            if (e.Data != null) writer.WriteLine(e.Data);
            else if (System.Threading.Interlocked.Increment(ref closedStreams) == 2) writer.Close();
        };
        process.OutputDataReceived += receive;
        process.ErrorDataReceived += receive;
        process.StartInfo.EnvironmentVariables["VRTSENSORS_STOP_ON_STDIN_EOF"] = "1";
        try { process.Start(); }
        catch { writer.Close(); process.Dispose(); throw; }
        Running[process.Id] = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Stop every capture this editor started, by the pids left in SessionState.
    ///
    /// Killed as a tree: a `VRTsensors-*.exe` console wrapper runs Python as a child, and
    /// killing only the wrapper orphans the capture that is actually holding the device.
    /// </summary>
    static void StopAll()
    {
        string started = SessionState.GetString(StartedKey, "");
        SessionState.EraseString(StartedKey);
        // Ask first: a capture whose stdin closes stops as Ctrl-C stops it - the device
        // released, a final `stopped` record written - and only what is left gets killed.
        foreach (Process process in Running.Values)
        {
            try { process.StandardInput.Close(); } catch (Exception) { }
        }
        var asked = DateTime.UtcNow;
        foreach (Process process in Running.Values)
        {
            int left = StopGrace - (int)(DateTime.UtcNow - asked).TotalMilliseconds;
            try { if (left > 0) process.WaitForExit(left); } catch (Exception) { }
        }
        Running.Clear();
        foreach (string entry in started.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':');
            int pid = int.Parse(parts[0]);
            try
            {
                // The name check guards against the pid having been reused since.
                if (Process.GetProcessById(pid).ProcessName != parts[1]) continue;
            }
            catch (ArgumentException) { continue; }   // already gone, or stopped when asked
            // A console-script wrapper (VRTsensors-bitalino.exe) runs Python as a child, so the
            // whole tree has to go, which Process.Kill() alone does not do.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit();
            else
                Process.GetProcessById(pid).Kill();
            Debug.Log($"SensorCaptures: stopped {parts[1]} (pid {pid})");
        }
    }
}
