using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The board in the scene that answers three questions without anyone touching it:
/// whose signals are arriving from which device, what the garden is doing with them, and
/// what is being written to the statistics log.
///
/// One column per device, every channel, no paging - in a headset nobody has a keyboard
/// and the ray interactors are busy with the scene. Each column says whose device it is,
/// so a remote participant's signals are never mistaken for the local ones. A signal is
/// named in plain words where its meaning is established, and shown by its raw name where
/// it is not. A BITalino port's module is the name it was given on the capture's command
/// line (--channel A1=acc), because the hardware carries no module identification; captures
/// made before ports were named carry a guessed kind, and the board labels it inferred.
///
/// Built from code at Start so there is no prefab to keep in sync. Faces whoever looks at
/// it, and can be picked up and moved (GrabToMove) when it is too far to read.
/// </summary>
public class SensorBoard : MonoBehaviour
{
    [Tooltip("Metres wide. Height follows.")]
    public float width = 3.3f;
    [Tooltip("Seconds between text refreshes.")]
    public float refresh = 0.15f;

    const float W = 2700f, H = 1080f, ColumnW = 860f, RowH = 42f, Top = 100f;
    // Bark and cream, with the signal colours of the garden: the board belongs to the scene.
    static readonly Color Ink = GardenPalette.Ink, Dim = GardenPalette.Dim, Live = GardenPalette.Live, Warn = GardenPalette.Warn, Bad = GardenPalette.Bad, Surface = GardenPalette.Surface;
    // What the garden does with each signal, one line per sign, in the sign's colour.
    static readonly (GardenSignpost.Signal signal, string what)[] Legend =
    {
        (GardenSignpost.Signal.Knock, "BITalino button  ->  a knock on the giant, your swarm bursts"),
        (GardenSignpost.Signal.Wind, "BITalino accelerometer  ->  leaves fly, the giant frowns"),
        (GardenSignpost.Signal.Warmth, "Skin response (EmotiBit, or BITalino eda)  ->  the cracks glow orange"),
        (GardenSignpost.Signal.Heartbeat, "EmotiBit heart rate  ->  it breathes at the fastest pulse"),
        (GardenSignpost.Signal.Pulse, "EmotiBit pulse  ->  the heart-stone lights with every beat"),
        (GardenSignpost.Signal.Snow, "EmotiBit skin temperature  ->  cold hands make it snow"),
        (GardenSignpost.Signal.Night, "BITalino light sensor  ->  cover it and night falls"),
        (GardenSignpost.Signal.Muscle, "BITalino muscle sensor  ->  the giant clenches its fists"),
        (GardenSignpost.Signal.Dream, "BITalino brain sensor  ->  the sleeping giant dreams"),
    };
    const float LegendRow = 34f;

    class Row
    {
        public GameObject root;
        public Text name, meaning, value, unit, state, age, shape;
        public RectTransform fill;
    }
    class Column
    {
        public GameObject root;
        public Text title, state, summary, hint, more;
        public List<Row> rows = new List<Row>();
    }
    class Device
    {
        public string owner;
        public bool local;
        public double heard;
        public SensorStream.Table table;
        public List<SensorStream.Channel> channels;
        public SensorStream.Source Source { get { return channels[0].source; } }
    }

    Font _font;
    Canvas _canvas;
    Text _status, _participants, _logging, _written, _recent;
    readonly Column[] _columns = new Column[3];
    readonly Text[] _legendSource = new Text[Legend.Length];
    double _next;
    Camera _camera;
    CalmLantern _lantern;

    // Plain words for a signal and what it is technically, only where that is established.
    // Everything else is shown by its raw name.
    static readonly Dictionary<string, string[]> Words = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        { "acc", new[] { "Movement", "accelerometer" } }, { "btn", new[] { "Button", "push button, pressed reads low" } },
        { "eda", new[] { "Skin response", "electrodermal activity" } }, { "ecg", new[] { "Heartbeat", "electrocardiogram" } },
        { "emg", new[] { "Muscle", "electromyogram" } }, { "eeg", new[] { "Brain waves", "electroencephalogram" } },
        { "lux", new[] { "Light", "light sensor" } }, { "i1", new[] { "Switch 1", "digital input" } }, { "i2", new[] { "Switch 2", "digital input" } },
        { "EA", new[] { "Skin response", "electrodermal activity" } }, { "HR", new[] { "Heart rate", "beats per minute, from the pulse" } },
        { "BI", new[] { "Beat interval", "seconds between heartbeats" } }, { "IBI", new[] { "Beat interval", "seconds between heartbeats" } },
        { "PPG_IR", new[] { "Pulse", "photoplethysmogram, infrared" } }, { "PPG_RED", new[] { "Pulse", "photoplethysmogram, red" } },
        { "PPG_GRN", new[] { "Pulse", "photoplethysmogram, green" } }, { "PPG_GREEN", new[] { "Pulse", "photoplethysmogram, green" } },
        { "TEMP1", new[] { "Skin temperature", "thermistor beside the pulse sensor" } }, { "TEMP", new[] { "Skin temperature", "thermistor beside the pulse sensor" } },
        { "THERM", new[] { "Body warmth", "thermopile" } },
        { "ACC_X", new[] { "Movement X", "acceleration" } }, { "ACC_Y", new[] { "Movement Y", "acceleration" } }, { "ACC_Z", new[] { "Movement Z", "acceleration" } },
        { "GYRO_X", new[] { "Turning X", "rotation rate" } }, { "GYRO_Y", new[] { "Turning Y", "rotation rate" } }, { "GYRO_Z", new[] { "Turning Z", "rotation rate" } },
        { "MAG_X", new[] { "Compass X", "magnetic field" } }, { "MAG_Y", new[] { "Compass Y", "magnetic field" } }, { "MAG_Z", new[] { "Compass Z", "magnetic field" } },
        { "SCR_AMP", new[] { "Skin response peaks", "skin conductance response amplitude" } }, { "SCR_RIS", new[] { "Skin response rise", "skin conductance response rise time" } },
        { "SCR_FREQ", new[] { "Skin response rate", "skin conductance response frequency" } },
    };
    static readonly Dictionary<string, string[]> Kinds = new Dictionary<string, string[]>
    {
        { "button", new[] { "Button", "looks like a button" } }, { "moving", new[] { "Moving", "looks like something moving" } },
        { "steady", new[] { "Steady signal", "looks like a steady signal" } }, { "unplugged", new[] { "Nothing plugged in", "looks unplugged" } },
    };

    void Start()
    {
        if (_canvas != null) return;
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _lantern = LocalLantern();

        _canvas = new GameObject("Sensor board", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
        _canvas.transform.SetParent(transform, false);
        _canvas.renderMode = RenderMode.WorldSpace;
        var rect = (RectTransform)_canvas.transform;
        rect.sizeDelta = new Vector2(W, H);
        rect.localScale = Vector3.one * (Mathf.Max(0.5f, width) / W);

        Image back = Panel(_canvas.transform, "Background", GardenPalette.Panel, 0, 0, W, H);
        Panel(back.transform, "Accent", Warn, 0, 0, W, 5);
        Label(back.transform, "What the garden hears from your body", 36, Ink, 30, 12, 1400, 44, true);
        _participants = Label(back.transform, "", 18, Dim, 30, 58, 1800, 28);
        _status = Label(back.transform, "", 24, Warn, 1500, 12, W - 1530, 36, true);
        _status.alignment = TextAnchor.MiddleRight;
        Text move = Label(back.transform, "Too far?  Point at me and hold the trigger to move me  ·  B brings me to you and back", 15, Dim, 1500, 52, W - 1530, 24);
        move.alignment = TextAnchor.MiddleRight;
        // Anything can pick the board up: the whole face is the handle.
        GrabToMove grab = GetComponent<GrabToMove>();
        if (grab == null) grab = gameObject.AddComponent<GrabToMove>();
        grab.size = new Vector3(Mathf.Max(0.5f, width), Mathf.Max(0.5f, width) * H / W, 0.05f);

        _columns[0] = MakeColumn(back.transform, 30, Top, 8);
        _columns[1] = MakeColumn(back.transform, 30 + ColumnW + 20, Top, 20);
        _columns[2] = MakeColumn(back.transform, 30 + 2 * (ColumnW + 20), Top, 20);

        // Under the first (short) column: what the garden does with the data, then the log.
        float y = Top + 84 + 8 * RowH + 16;
        float legendH = 36 + Legend.Length * LegendRow;
        Image legend = Panel(back.transform, "Garden", Surface, 30, y, ColumnW, legendH);
        Label(legend.transform, "WHAT EACH SENSOR DOES  -  colours match the signs by the giant  -  not a measurement", 17, Dim, 14, 8, ColumnW - 28, 26);
        // One line per signal, in the signal's own colour, naming the device it comes from,
        // then what it is bound to right now.
        for (int i = 0; i < Legend.Length; i++)
        {
            Label(legend.transform, Legend[i].signal.ToString().ToUpperInvariant() + "  -  " + Legend[i].what, 15, GardenSignpost.ColourOf(Legend[i].signal), 14, 36 + i * LegendRow, ColumnW - 28, 18, true);
            _legendSource[i] = Label(legend.transform, "", 13, Dim, 40, 36 + i * LegendRow + 17, ColumnW - 54, 16);
        }

        y += legendH + 12;
        Image log = Panel(back.transform, "Logging", Surface, 30, y, ColumnW, H - y - 24);
        Label(log.transform, "FOR THE RESEARCHERS  -  what is being recorded", 17, Dim, 14, 8, ColumnW - 28, 26);
        _logging = Label(log.transform, "", 15, Ink, 14, 34, ColumnW - 28, 66);
        _logging.alignment = TextAnchor.UpperLeft;
        _written = Label(log.transform, "", 15, Dim, 14, 102, ColumnW - 28, 22);
        _recent = Label(log.transform, "", 13, Dim, 14, 126, ColumnW - 28, H - y - 24 - 130);
        _recent.alignment = TextAnchor.UpperLeft;

        Vector3 away = transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(away);
    }

    Column MakeColumn(Transform parent, float x, float y, int rows)
    {
        var col = new Column();
        Image panel = Panel(parent, "Device", Color.clear, x, y, ColumnW, 84 + rows * RowH);
        col.root = panel.gameObject;
        Image head = Panel(panel.transform, "Head", Surface, 0, 0, ColumnW, 78);
        col.title = Label(head.transform, "", 24, Ink, 14, 6, 620, 30, true);
        col.state = Label(head.transform, "", 20, Live, 640, 6, ColumnW - 654, 30, true);
        col.state.alignment = TextAnchor.MiddleRight;
        col.summary = Label(head.transform, "", 15, Dim, 14, 36, ColumnW - 28, 20);
        col.hint = Label(head.transform, "", 15, Warn, 14, 56, ColumnW - 28, 20);
        for (int i = 0; i < rows; i++)
        {
            Image r = Panel(panel.transform, "Row", i % 2 == 0 ? new Color(1f, 1f, 1f, 0.03f) : Color.clear, 0, 84 + i * RowH, ColumnW, RowH - 2);
            var row = new Row { root = r.gameObject };
            row.name = Label(r.transform, "", 20, Ink, 12, 1, 330, 24, true);
            row.meaning = Label(r.transform, "", 14, Dim, 12, 22, 400, 18);
            row.value = Label(r.transform, "", 20, Ink, 420, 1, 170, 24, true);
            row.value.alignment = TextAnchor.MiddleRight;
            row.unit = Label(r.transform, "", 14, Dim, 420, 22, 170, 18);
            row.unit.alignment = TextAnchor.MiddleRight;
            row.state = Label(r.transform, "", 17, Live, 610, 1, 120, 24);
            row.age = Label(r.transform, "", 14, Dim, 610, 22, 120, 18);
            row.shape = Label(r.transform, "", 14, Dim, 735, 1, ColumnW - 745, 40);
            row.shape.alignment = TextAnchor.MiddleRight;
            Image track = Panel(r.transform, "Track", new Color(1f, 1f, 1f, 0.08f), 12, RowH - 6, 395, 3);
            Image fill = Panel(track.transform, "Fill", Warn, 0, 0, 0, 3);
            row.fill = fill.rectTransform;
            col.rows.Add(row);
            r.gameObject.SetActive(false);
        }
        col.more = Label(panel.transform, "", 15, Dim, 12, 84 + rows * RowH, ColumnW - 24, 22);
        panel.gameObject.SetActive(false);
        return col;
    }

    /// <summary>Redraw now, ignoring the refresh interval.</summary>
    public void Refresh()
    {
        _next = 0;
        Update();
    }

    void Update()
    {
        if (_canvas == null || SensorStream.Now < _next) return;
        _next = SensorStream.Now + Math.Max(0.05, refresh);
        SensorStream stream = SensorStream.Instance;
        if (_lantern == null) _lantern = LocalLantern();

        // Devices: this machine's first, BITalino before the busiest, then each remote participant's.
        var devices = new List<Device>();
        if (stream != null) devices.AddRange(Devices(stream.Local, "You" + (SensorShare.SelfName.Length > 0 ? " (" + SensorShare.SelfName + ")" : ""), true, 0));
        var remote = SensorShare.Instance == null ? new List<SensorShare.Participant>() : SensorShare.Instance.Remote.Values.OrderBy(p => p.userId, StringComparer.Ordinal).ToList();
        foreach (SensorShare.Participant p in remote) devices.AddRange(Devices(p.table, p.userName.Length > 0 ? p.userName : p.userId, false, p.received));

        if (stream == null) Status("Sensor logger missing", Bad);
        else if (stream.error.Length > 0) Status("Listener failed - see the log section", Bad);
        else if (devices.Count == 0) Status($"Waiting - is sensors.json listing a capture? Captures send to --udp 127.0.0.1:{stream.port}", Warn);
        else if (devices.Count > _columns.Length) Status($"{devices.Count} devices, showing {_columns.Length}; all are logged", Warn);
        else if (devices.All(d => d.channels.Any(c => c.Available))) Status("Hearing every device", Live);
        else Status("A device has stopped - see its column", Warn);

        var names = new List<string>();
        foreach (var owner in devices.GroupBy(d => d.owner))
            names.Add($"{owner.Key} - {string.Join(", ", owner.Select(d => DeviceName(d.Source.sensor)))}" + (owner.First().local ? "" : $", heard {AgeText(SensorStream.Now - owner.First().heard)}"));
        _participants.text = names.Count == 0 ? "" : "In this session:  " + string.Join("   ·   ", names) + (remote.Count == 0 && SensorShare.Instance != null ? "   ·   nobody else yet" : "");

        for (int i = 0; i < _columns.Length; i++)
        {
            Column col = _columns[i];
            col.root.SetActive(i < devices.Count);
            if (i >= devices.Count) continue;
            FillColumn(col, devices[i]);
        }

        FillLegend();

        if (stream != null)
        {
            _logging.text = $"Destination: {stream.LogDestination}\n" +
                            "Every second, per channel: count, mean, min, max  (component = the channel key).  Every device event immediately.  " +
                            "Full-rate samples are in the capture .jsonl written by the sensor command, not here. Remote participants log their own.";
            _written.text = $"{stream.written} records written this session  ·  {stream.datagrams} datagrams on UDP {stream.port}" +
                            (stream.Dropped > 0 ? $"  ·  {stream.Dropped} dropped by the live queue" : "") +
                            (stream.error.Length > 0 ? $"  ·  {stream.error}" : "");
            _recent.text = string.Join("\n", SensorStream.Recent.Skip(Math.Max(0, SensorStream.Recent.Count - 5)).Select(Trim));
        }
    }

    static IEnumerable<Device> Devices(SensorStream.Table table, string owner, bool local, double heard)
    {
        return table.Channels.GroupBy(c => c.source.sensor + "|" + c.source.identity)
                    .OrderBy(g => g.Key.StartsWith("bitalino") ? 0 : 1).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new Device { owner = owner, local = local, heard = heard, table = table, channels = g.OrderBy(c => c.key, StringComparer.Ordinal).ToList() });
    }

    static string DeviceName(string sensor)
    {
        return sensor == "bitalino" ? "BITalino" : sensor == "emotibit" ? "EmotiBit" : sensor;
    }

    /// <summary>Plain-words name and the technical line under it, for one channel.</summary>
    static void Describe(SensorStream.Channel c, out string name, out string technical)
    {
        string raw = c.field == "value" ? c.source.stream : c.field;
        string axis = "";
        if (c.source.stream.Length > 0 && c.field != "value") { raw = c.source.stream; axis = " " + c.field.ToUpperInvariant(); }
        bool port = c.source.sensor == "bitalino" && IsPort(raw);
        if (Words.TryGetValue(raw, out string[] w)) { name = w[0] + axis; technical = raw + axis.ToLowerInvariant() + " - " + w[1]; }
        else if (Kinds.TryGetValue(c.kind, out string[] k)) { name = k[0]; technical = raw; }
        else { name = raw; technical = port ? "unnamed port - name it with --channel " + raw + "=<module>" : ""; }
        if (Kinds.TryGetValue(c.kind, out string[] kind))
        {
            technical += " - " + (c.inferred ? kind[1] : kind[0].ToLowerInvariant());
            if (port && c.kind == "steady") technical += " - say what it is: --channel " + raw + "=eda";
        }
    }

    void FillColumn(Column col, Device device)
    {
        List<SensorStream.Channel> channels = device.channels;
        SensorStream.Source first = device.Source;
        bool bitalino = first.sensor == "bitalino";
        col.title.text = $"{device.owner}  ·  {DeviceName(first.sensor)}";

        int live = channels.Count(c => c.Available);
        bool anyLive = live > 0;
        string state = anyLive ? (live == channels.Count ? "Live" : $"{live} of {channels.Count} live")
                     : channels.Any(c => c.source.state == "Disconnected") ? "Disconnected"
                     : channels.Any(c => c.Status == "Stale") ? "Stale" : "Waiting";
        col.state.text = state;
        col.state.color = anyLive ? Live : Warn;

        float rate = channels.Max(c => device.table.Rate(c.key));
        SensorStream.Channel newest = channels.OrderBy(c => c.Age).First();
        string rateText = rate > 0 ? rate.ToString("0.#") + " samples/s" : newest.Age < 2 ? "measuring rate" : "no samples arriving";
        SensorStream stream = SensorStream.Instance;
        col.summary.text = (first.identity.Length > 0 ? first.identity + "  ·  " : "") + $"{channels.Count} channels  ·  {rateText}  ·  last sample {AgeText(newest.Age)}" +
                           (device.local && stream != null && stream.lastEvent.Length > 0 && stream.lastEvent.StartsWith(first.sensor) ? $"  ·  last event: {stream.lastEvent}" : "");

        string hint = "";
        if (!anyLive && !device.local) hint = "Nothing live at their end - the reason is on their board.";
        else if (!anyLive)
        {
            if (bitalino) hint = "Board off or out of Bluetooth range. The capture reconnects by itself once it is back.";
            else hint = "Streams are advertised but no samples arrive: EmotiBit off or off WiFi (blue LED should blink), or the Oscilloscope is not receiving.";
        }
        else if (bitalino && channels.Where(c => c.kind.Length > 0).All(c => c.kind == "unplugged"))
            hint = "Every port reads 0-5: modules not attached, or unpowered.";
        col.hint.text = hint;

        for (int i = 0; i < col.rows.Count; i++)
        {
            Row row = col.rows[i];
            row.root.SetActive(i < channels.Count);
            if (i >= channels.Count) continue;
            SensorStream.Channel c = channels[i];
            Describe(c, out string name, out string technical);
            string raw = c.field == "value" ? c.source.stream : c.field;

            row.name.text = name;
            row.meaning.text = technical;
            row.value.text = c.value.ToString("0.###", CultureInfo.InvariantCulture);
            // A BITalino digital input is a line, not a count; the analog ports are 10-bit for
            // the first four acquired (a fifth and sixth come as 6-bit, which this does not know).
            bool digital = bitalino && (c.field == "btn" || c.field == "i1" || c.field == "i2");
            row.unit.text = digital ? "digital input, 0 or 1" : bitalino ? "raw counts, 0-1023" : raw.StartsWith("PPG") ? "raw counts" : "device value";
            // The button is the port named btn; "button" is what captures before named ports inferred.
            if (c.kind == "btn" || c.field == "btn" || c.kind == "button" || c.presses > 0)
            {
                row.value.text = c.pressed ? "PRESSED" : c.presses > 0 ? $"{c.presses} presses" : "released";
                if (bitalino) row.unit.text = $"line reads {c.value:0}";
            }
            row.value.color = c.Available ? Ink : Dim;
            row.state.text = c.Status;
            row.state.color = c.Available ? Live : Warn;
            float r = device.table.Rate(c.key);
            row.age.text = AgeText(c.Age) + (c.Available && r > 0 ? $"  {r:0.#} Hz" : "");
            row.shape.text = c.kind.Length > 0 && c.inferred ? "inferred from the signal" : "";
            float span = c.hi - c.lo;
            row.fill.sizeDelta = new Vector2(span > 0f ? 395f * Mathf.Clamp01((c.value - c.lo) / span) : 0f, 3f);
        }
        col.more.text = channels.Count > col.rows.Count ? $"+ {channels.Count - col.rows.Count} more channels, logged but not shown" : "";
    }

    void FillLegend()
    {
        SensorStream.Table table = _lantern == null ? null : _lantern.Table;
        for (int i = 0; i < Legend.Length; i++)
        {
            if (table == null) { _legendSource[i].text = "no lantern in this scene"; _legendSource[i].color = Warn; continue; }
            GardenSignpost.Signal signal = Legend[i].signal;
            string key = GardenSignpost.KeyOf(_lantern, signal);
            SensorStream.Channel c = key.Length > 0 ? table.Find(key) : null;
            if (c == null)
            {
                _legendSource[i].text = "none arriving" + (signal == GardenSignpost.Signal.Warmth ? " - a BITalino EDA port must be named: --channel A3=eda"
                                      : signal >= GardenSignpost.Signal.Night ? " - name the port: --channel A3=" + Module(signal) : "");
                _legendSource[i].color = Dim;
                continue;
            }
            string configured = Configured(signal);
            bool auto = c.key != configured && c.alias != configured;
            bool ok = table.Has(c.key);
            _legendSource[i].text = $"{c.key}{(auto ? "  (chosen automatically" + (c.kind.Length > 0 ? ", " + c.kind : "") + ")" : "")}  ·  {Reading(signal, c)}" + (ok ? "" : "  -  not live");
            _legendSource[i].color = ok ? Live : Warn;
        }
    }

    static string Module(GardenSignpost.Signal s) { return s == GardenSignpost.Signal.Night ? "lux" : s == GardenSignpost.Signal.Muscle ? "emg" : "eeg"; }

    string Configured(GardenSignpost.Signal s)
    {
        switch (s)
        {
            case GardenSignpost.Signal.Knock: return _lantern.markerChannel;
            case GardenSignpost.Signal.Wind: return _lantern.movementChannel;
            case GardenSignpost.Signal.Warmth: return _lantern.arousalChannel;
            case GardenSignpost.Signal.Heartbeat: return _lantern.heartRateChannel;
            case GardenSignpost.Signal.Pulse: return _lantern.pulseChannel;
            case GardenSignpost.Signal.Snow: return _lantern.temperatureChannel;
            case GardenSignpost.Signal.Night: return _lantern.lightChannel;
            case GardenSignpost.Signal.Muscle: return _lantern.tensionChannel;
            default: return _lantern.brainChannel;
        }
    }

    string Reading(GardenSignpost.Signal s, SensorStream.Channel c)
    {
        switch (s)
        {
            case GardenSignpost.Signal.Knock: return $"{(c.pressed ? "pressed" : "released")}, {_lantern.markers} presses this session";
            case GardenSignpost.Signal.Wind: return $"movement {_lantern.movement:0.00}  (raw {c.value:0}, full scale {_lantern.movementFullScale:0} counts from rest)";
            case GardenSignpost.Signal.Warmth: return $"arousal {_lantern.arousal:0.00}  (raw {SensorStream.F(c.value)}, {_lantern.arousalNote})";
            case GardenSignpost.Signal.Heartbeat: return _lantern.heartRate > 0 ? $"{_lantern.heartRate:0} per minute" : $"no usable heart rate (raw {SensorStream.F(c.value)}), pulsing at resting {_lantern.restingBreathsPerMinute:0} per minute";
            case GardenSignpost.Signal.Pulse: return $"pulse {_lantern.pulse:0.00}  (raw {SensorStream.F(c.value)})";
            case GardenSignpost.Signal.Snow: return $"cold {_lantern.cold:0.00}  (raw {SensorStream.F(c.value)}, {_lantern.temperatureNote})";
            case GardenSignpost.Signal.Night: return $"night {_lantern.night:0.00}  (raw {SensorStream.F(c.value)}, {_lantern.lightNote})";
            case GardenSignpost.Signal.Muscle: return $"tension {_lantern.tension:0.00}  (raw {c.value:0}, full scale {_lantern.tensionFullScale:0} counts of activity)";
            default: return $"brain {_lantern.brain:0.00}  (raw {c.value:0}, full scale {_lantern.brainFullScale:0} counts of activity)";
        }
    }

    void Status(string text, Color colour) { _status.text = text; _status.color = colour; }

    // The legend describes the lantern of the person at this machine, not a remote copy.
    static CalmLantern LocalLantern()
    {
        return FindObjectsByType<CalmLantern>(FindObjectsSortMode.None).FirstOrDefault(l => l.participant.Length == 0);
    }

    static bool IsPort(string name)
    {
        return name.Length == 2 && name[0] == 'A' && name[1] >= '1' && name[1] <= '6';
    }

    static string AgeText(double age)
    {
        return double.IsInfinity(age) ? "never" : age < 1 ? $"{age * 1000:0} ms ago" : $"{age:0.0} s ago";
    }

    static string Trim(string line)
    {
        return line.Length > 110 ? line.Substring(0, 107) + "..." : line;
    }

    void LateUpdate()
    {
        if (_camera == null || !_camera.isActiveAndEnabled)
            _camera = Camera.main ?? Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled && c.stereoEnabled) ?? Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled);
        if (_camera == null || _canvas == null) return;
        Vector3 away = transform.position - _camera.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(away);
    }

    Text Label(Transform parent, string text, int size, Color colour, float x, float y, float w, float h, bool bold = false)
    {
        var label = new GameObject("Label", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
        Place(label.rectTransform, parent, x, y, w, h);
        label.font = _font;
        label.fontSize = size;
        label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        label.text = text;
        label.supportRichText = false;
        label.color = colour;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.raycastTarget = false;
        return label;
    }

    static Image Panel(Transform parent, string name, Color colour, float x, float y, float w, float h)
    {
        var image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
        Place(image.rectTransform, parent, x, y, w, h);
        image.color = colour;
        image.raycastTarget = false;
        return image;
    }

    static void Place(RectTransform rect, Transform parent, float x, float y, float w, float h)
    {
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(w, h);
    }
}
