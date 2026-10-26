using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using VRT.Core;

/// <summary>
/// Application-owned live view of the sensor records arriving over UDP, and their summary
/// into the statistics log. The acquisition .jsonl remains the full record.
///
/// Channels live in a <see cref="Table"/>, one per participant. This machine's own is
/// <see cref="Local"/>; SensorShare fills one per remote participant from the snapshots it
/// receives. Scene objects read tables, so nothing in a scene needs to know where a signal
/// came from to show it, and nothing can mix one participant's signal with another's.
///
/// What a channel *is* comes from the acquisition, not from here: a BITalino port's kind
/// is the name it was given on the capture's command line (such as acc, eda or btn) and
/// arrives as a `module` event; presses arrive as `press` events. Unity displays what it is told.
/// </summary>
public class SensorStream : MonoBehaviour
{
    /// <summary>Range relaxation time constant in seconds: after this time, 1/e of an excursion remains.</summary>
    const float RangeTimeConstant = 30f;

    public static SensorStream Instance { get; private set; }
    public int port = 9010;
    public float statsInterval = 1f;
    public int datagrams, channels;
    public float secondsSinceLastDatagram = -1f;
    public bool logging;
    public string error = "", lastEvent = "";
    public bool Listening { get { return _running; } }
    public string LoggingStatus
    {
        get
        {
#if VRT_WITH_STATS
            if (logging && VRTConfig.InstanceExists())
            {
                string file = VRTConfig.Instance.StatisticsConfig.outputFile;
                return file == "-" ? "Statistics muted" : string.IsNullOrEmpty(file) ? "Statistics to console" : "Summaries to log file";
            }
#endif
            return "Statistics not ready";
        }
    }
    public int Dropped { get { return _dropped; } }
    public static readonly List<string> Recent = new List<string>();
    public const int RecentMax = 8;

    public class Source
    {
        public string prefix, alias, sensor, stream, identity, state = "Waiting";
        public double nominalRate = -1;
    }
    public class Channel
    {
        public string key, alias, field;
        /// <summary>
        /// What the acquisition says this channel is - for a BITalino port, the name it was
        /// given (such as acc, eda or btn) - or empty when it says nothing. `inferred`
        /// means it was read off the signal rather than known; the current acquisition never
        /// infers and always sends false, and the field stays for captures made before it stopped.
        /// </summary>
        public string kind = "";
        public bool inferred;
        public Source source;
        public float value, previous, min, max, rate = -1;
        /// <summary>
        /// The range a bar or an auto-range is drawn against. Every sample widens it at once,
        /// and it relaxes back towards the present value over <see cref="RangeTimeConstant"/>, so
        /// one transient cannot fix the scale for a whole session. The reporting window's
        /// extremes are <see cref="min"/> and <see cref="max"/>.
        /// </summary>
        public float lo = float.MaxValue, hi = float.MinValue;
        /// <summary>
        /// The resting level this signal has settled to, and how far it has lately swung
        /// away from it. Both are advanced once per sample, in <see cref="FeedActivity"/>,
        /// and both are in the signal's own units.
        ///
        /// It has to be per sample. A BITalino delivers 100 samples a second in batches,
        /// so a whole burst of muscle or brain activity arrives inside one Unity frame; a
        /// consumer that read only the last value of the frame saw the signal back at its
        /// resting level and reported nothing at all. Computing it here also means there
        /// is one envelope per channel rather than one per object looking at it, so the
        /// giant, the sign and the remote lantern cannot disagree about the same signal.
        /// </summary>
        public float baseline = float.NaN, envelope;
        public double envelopeAt = -1;
        public double received = -1, timestamp;
        public bool valid, pressed;
        /// <summary>Presses the acquisition has reported on this channel. A scene reacts to it changing.</summary>
        public int presses;
        public int count;
        public double sum;
        public double Age { get { return received < 0 ? double.PositiveInfinity : Math.Max(0, Now - received); } }
        public string Status
        {
            get
            {
                if (source.state != "Live") return source.state;
                if (!valid) return "Waiting";
                if (source.nominalRate == 0) return "Last event";
                return Age > Math.Max(5, source.nominalRate > 0 ? 3 / source.nominalRate : 5) ? "Stale" : "Live";
            }
        }
        public bool Available { get { return Status == "Live" || Status == "Last event"; } }

        /// <summary>Take one sample into the resting level and the excursion envelope. `dt` is the time since the previous sample of this channel, `at` when this one arrived.</summary>
        public void FeedActivity(float value, float dt, double at)
        {
            if (float.IsNaN(baseline)) baseline = value;
            baseline = Mathf.Lerp(baseline, value, Mathf.Clamp01(dt / BaselineTimeConstant));
            envelope = Mathf.Max(EnvelopeAt(at), Mathf.Abs(value - baseline));
            envelopeAt = at;
        }

        /// <summary>Take an envelope measured on the machine that owns this channel. Its decay continues from here, so a snapshot does not freeze it until the next one.</summary>
        public void TakeActivity(float value, double at) { envelope = value; envelopeAt = at; }

        /// <summary>The excursion as it stands at `when`. Takes its time explicitly so that the decay can be exercised at a stated instant rather than at whatever the clock happens to say.</summary>
        public float EnvelopeAt(double when)
        {
            if (envelopeAt < 0) return 0f;
            return envelope * Mathf.Clamp01(1f - (float)(when - envelopeAt) / EnvelopeDecay);
        }

        /// <summary>How far this signal is swinging from its resting level, decayed to now. In the signal's own units, so it does not depend on any one object's idea of full scale.</summary>
        public float Envelope { get { return EnvelopeAt(Now); } }

        /// <summary>The same excursion as 0..1 against `fullScale`, which is a display choice and belongs to whoever is drawing it.</summary>
        public float Activity(float fullScale) { return ActivityAt(fullScale, Now); }
        /// <summary>The same, at a stated instant rather than now, so a test can exercise the decay without a clock.</summary>
        public float ActivityAt(float fullScale, double when) { return Mathf.Clamp01(EnvelopeAt(when) / Mathf.Max(1f, fullScale)); }
    }

    // How fast the resting level follows the signal, and how fast an excursion above it
    // fades. Both are in seconds, and both are deliberately independent of the sample rate
    // and the frame rate: a 100 Hz signal and a 4 Hz one produce the same envelope for the
    // same swing, which is what lets one full scale serve a channel whatever supplies it.
    public const float BaselineTimeConstant = 2f;
    public const float EnvelopeDecay = 0.4f;

    /// <summary>The channels of one participant, keyed as they appear in the statistics log.</summary>
    public class Table
    {
        readonly Dictionary<string, Channel> _channels = new Dictionary<string, Channel>();
        readonly Dictionary<string, Source> _sources = new Dictionary<string, Source>();
        public IEnumerable<Channel> Channels { get { return _channels.Values; } }
        public IEnumerable<Source> Sources { get { return _sources.Values; } }
        public IEnumerable<string> Keys { get { return _channels.Keys; } }
        public int Count { get { return _channels.Count; } }

        public Source Source(string sensor, string stream, string identity)
        {
            string alias = Safe(sensor) + (stream.Length == 0 ? "" : "." + Safe(stream));
            string prefix = alias + (identity.Length == 0 ? "" : "@" + Safe(identity));
            if (!_sources.TryGetValue(prefix, out Source source))
            {
                source = new Source { prefix = prefix, alias = alias, sensor = sensor, stream = stream, identity = identity };
                _sources.Add(prefix, source);
            }
            return source;
        }
        public Channel Channel(Source source, string field, out bool created)
        {
            string key = source.prefix + "." + Safe(field);
            created = !_channels.TryGetValue(key, out Channel c);
            if (created)
            {
                c = new Channel { key = key, alias = source.alias + "." + Safe(field), field = field, source = source };
                _channels.Add(key, c);
            }
            return c;
        }
        // Unqualified legacy names resolve only when exactly one device supplies the channel.
        public Channel Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.Contains("@") && _channels.TryGetValue(key, out Channel exact)) return exact;
            Channel found = null;
            foreach (Channel c in _channels.Values)
                if (c.alias == key) { if (found != null) return null; found = c; }
            return found;
        }
        /// <summary>A channel's latest value, or `fallback` when it is not arriving. Never the last value of a dead device.</summary>
        public float Get(string key, float fallback = 0f) { return Has(key) ? Find(key).value : fallback; }

        /// <summary>
        /// Whether this channel exists and is arriving now: available by its source's state
        /// and its own nominal rate, and heard from within the last ten seconds.
        ///
        /// This is the question a scene should ask before showing anything. A channel that
        /// merely *exists* has usually existed since the first sample of the session, and
        /// treating that as live shows an unplugged sensor as a calm one.
        /// </summary>
        public bool Has(string key)
        {
            Channel c = Find(key);
            return c != null && c.Available && c.Age <= 10;
        }

        /// <summary>The measured rate, or 0 unless the channel was heard from within `within` seconds - a stale rate is worse than no rate.</summary>
        public float Rate(string key, double within = 2)
        {
            Channel c = Find(key);
            return c != null && c.Available && c.Age <= within ? c.rate : 0;
        }
    }

    public readonly Table Local = new Table();
    // Fired once per numeric sample, including all samples received within one Unity frame.
    public event Action<Channel, bool> SampleReceived;
    readonly ConcurrentQueue<Tuple<string, double>> _incoming = new ConcurrentQueue<Tuple<string, double>>();
    UdpClient _client;
    Thread _thread;
    volatile bool _running;
    int _dropped;
    double _lastStats, _lastDatagram = -1;
    public static double Now { get { return (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency; } }

    [Serializable] class SensorConfig { public int udpPort = 9010; }
    [Serializable] class ConfigFile { public SensorConfig SensorConfig = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; Recent.Clear(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void EnsureExists()
    {
        if (Instance != null) return;
        new GameObject("SensorLogger").AddComponent<SensorStream>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { enabled = false; return; }
        // A legacy scene component delegates to a dedicated root; never preserve its garden.
        if (gameObject.name != "SensorLogger")
        {
            EnsureExists();
            enabled = false;
            return;
        }
        Instance = this;
        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        if (Instance != this) return;
        try
        {
            string file = VRTConfig.ConfigFilename();
            if (File.Exists(file))
            {
                ConfigFile config = JsonUtility.FromJson<ConfigFile>(File.ReadAllText(file));
                if (config?.SensorConfig != null) port = config.SensorConfig.udpPort;
            }
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port", "UDP port must be 1..65535");
            if (float.IsNaN(statsInterval) || float.IsInfinity(statsInterval) || statsInterval <= 0) statsInterval = 1f;
            _client = new UdpClient(port);
            KeepOutOfChildProcesses(_client);
            _running = true;
            _lastStats = Now;
            _thread = new Thread(Receive) { IsBackground = true };
            _thread.Start();
        }
        catch (Exception e)
        {
            error = $"Cannot listen on UDP {port}: {e.Message}";
            Debug.LogError("SensorStream: " + error);
            _client?.Close();
            _running = false;
        }
    }

    /// <summary>
    /// Stop child processes inheriting the listening socket, so the port is free the moment
    /// this object releases it.
    ///
    /// .NET creates sockets with an inheritable handle on Windows, and `SensorCaptures`
    /// starts each capture with redirected output - which makes CreateProcess inherit every
    /// inheritable handle, this socket included. The capture then holds the port open for as
    /// long as it lives, even after <see cref="OnDestroy"/> has closed our own client. The
    /// symptom is the next Play failing with "Cannot listen on UDP 9010" while nothing
    /// visible is listening, and it lasts exactly as long as the orphaned capture does.
    ///
    /// Best effort: if the call is unavailable or fails, the socket still works and the only
    /// loss is this guarantee, so it must not stop the listener from starting.
    /// </summary>
    static void KeepOutOfChildProcesses(UdpClient client)
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try { SetHandleInformation(client.Client.Handle, HandleFlagInherit, 0); }
        catch (Exception e) { Debug.LogWarning("SensorStream: could not clear socket inheritance: " + e.Message); }
#endif
    }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    const uint HandleFlagInherit = 1;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
#endif

    /// <summary>
    /// The socket thread: take datagrams off the wire and queue them, doing nothing else.
    ///
    /// Parsing happens on the main thread in <see cref="Drain"/>, because a Unity scene may
    /// only be touched there. The queue is bounded at 8192 and overflow is counted rather
    /// than blocking - the capture file is the record, and a live view that stalls the
    /// socket to keep up is worse than one that drops a frame of it.
    /// </summary>
    void Receive()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _client.Receive(ref any);
                double arrived = Now;
                if (_incoming.Count >= 8192) { Interlocked.Increment(ref _dropped); continue; }
                _incoming.Enqueue(Tuple.Create(System.Text.Encoding.UTF8.GetString(data), arrived));
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (_running) Thread.Sleep(10); }
        }
    }

    void Update()
    {
        if (Instance != this) return;
        if (!logging && StatsReady && _running)
        {
            logging = true;
            Log("sensors", $"listening=1, port={port}");
            foreach (string key in Local.Keys) Log("sensors", $"channel={key}");
        }
        Drain(4096);
        channels = Local.Count;
        secondsSinceLastDatagram = _lastDatagram < 0 ? -1f : (float)(Now - _lastDatagram);
        if (Now - _lastStats >= statsInterval) EmitAggregate();
    }

    /// <summary>
    /// Parse up to `limit` queued datagrams on the main thread.
    ///
    /// The limit is what stops a backlog turning into a frozen frame: at 100 Hz across
    /// several devices a burst can arrive faster than a frame, and whatever is left stays
    /// queued for the next one. Shutdown drains without a limit, so nothing is lost.
    /// </summary>
    void Drain(int limit)
    {
        while (limit-- > 0 && _incoming.TryDequeue(out var packet))
        {
            datagrams++;
            _lastDatagram = packet.Item2;
            Consume(packet.Item1, packet.Item2);
        }
    }

    static bool StatsReady
    {
        get
        {
#if VRT_WITH_STATS
            return VRTConfig.InstanceExists();
#else
            return false;
#endif
        }
    }

    /// <summary>Feed one record as if it had arrived on the socket. For a keyboard knock, or a test.</summary>
    public void Inject(string line) { Consume(line, Now); }

    /// <summary>
    /// Turn one record into channel state: an event through <see cref="Event"/>, otherwise
    /// every finite numeric field becomes or updates a <see cref="Channel"/>.
    ///
    /// Nothing here knows what a device is. A field named `eeg` is a channel because the
    /// acquisition named the port `eeg`, not because Unity recognises the word, which is
    /// what lets a new device arrive without a line of Unity changing.
    ///
    /// `arrived` is when the datagram reached the socket, not the sensor's own timestamp.
    /// Both are kept: the sensor's on the channel for reporting, arrival for everything
    /// that has to be monotonic here - staleness, rate, the drawing range and the activity
    /// envelope. Sensor timestamps are not strictly increasing and cannot carry that load.
    /// </summary>
    void Consume(string line, double arrived)
    {
        string sensor = SensorJson.GetString(line, "sensor");
        if (string.IsNullOrEmpty(sensor)) return;
        string stream = SensorJson.GetString(line, "stream") ?? "";
        string identity = SensorJson.GetString(line, "source_id");
        if (string.IsNullOrEmpty(identity)) identity = SensorJson.GetString(line, "device");
        if (string.IsNullOrEmpty(identity)) identity = SensorJson.GetString(line, "stream_uid") ?? "";
        Source source = Local.Source(sensor, stream, identity);
        string ev = SensorJson.GetString(line, "event");
        if (ev != null) { Event(source, ev, line, arrived); return; }
        if (!SensorJson.TryGetNumber(line, "timestamp", out double timestamp)) return;
        bool receivedValue = false;
        foreach (string field in SensorJson.NumericFields(line))
        {
            if (field == "timestamp" || field == "seq" || field == "arrived") continue;
            if (!SensorJson.TryGetNumber(line, field, out double number) || Math.Abs(number) > float.MaxValue) continue;
            Channel c = Local.Channel(source, field, out bool created);
            if (created && logging) Log("sensors", $"channel={c.key}");
            bool continuous = c.valid && c.received >= 0 && source.state == "Live" && arrived - c.received <= 5;
            double sinceLast = c.received < 0 ? 0 : Math.Max(0, arrived - c.received);
            c.previous = c.value;
            c.value = (float)number;
            c.timestamp = timestamp;
            c.received = arrived;
            c.valid = true;
            if (c.count == 0) c.min = c.max = c.value;
            c.min = Mathf.Min(c.min, c.value);
            c.max = Mathf.Max(c.max, c.value);
            c.lo = Mathf.Min(c.lo, c.value);
            c.hi = Mathf.Max(c.hi, c.value);
            // Widen at once, then relax back towards the present value. Without this an
            // electrode settling to 14.7 in the first half-minute leaves an EDA bar moving
            // 0.002 of its travel for the remaining three minutes - measured on the capture
            // of 2026-09-20. min/max above are left alone, so reporting still sees the real
            // extremes; only what a bar is drawn against follows the signal.
            if (sinceLast > 0)
            {
                float relax = 1f - Mathf.Exp((float)-sinceLast / RangeTimeConstant);
                c.lo = Mathf.Lerp(c.lo, c.value, relax);
                c.hi = Mathf.Lerp(c.hi, c.value, relax);
            }
            c.count++;
            c.sum += number;
            // Every sample, not one per frame: see Channel.baseline. `sinceLast` is this
            // channel's own interval, so a batch of ten that arrived together is fed as
            // ten samples a hundredth of a second apart rather than as one step.
            c.FeedActivity(c.value, (float)sinceLast, arrived);
            receivedValue = true;
            SampleReceived?.Invoke(c, continuous);
        }
        if (receivedValue) source.state = "Live";
    }

    /// <summary>
    /// Apply one acquisition event: `module` (what a port is), `press`/`release` (the
    /// button), and the connection states `connected`, `disconnected` and `stale`.
    ///
    /// A press is counted, never levelled: the count only ever rises, so a scene reacts to
    /// it changing and cannot miss an edge between two frames or across a network hop. A
    /// device-level event - one with no stream - applies to every stream of that device.
    /// </summary>
    void Event(Source source, string ev, string line, double arrived)
    {
        lastEvent = $"{source.sensor}{(source.stream.Length == 0 ? "" : " / " + source.stream)}: {ev}";
        // Per-channel events name the field they concern; an event that names none is about the stream's value.
        string field = SensorJson.GetString(line, "channel") ?? "value";
        if (ev == "module")
        {
            Channel c = Local.Channel(source, field, out bool created);
            c.kind = SensorJson.GetString(line, "kind") ?? "";
            c.inferred = SensorJson.IsTrue(line, "inferred");
            if (created && logging) Log("sensors", $"channel={c.key}");
            Log(c.key, $"event=module, kind={Safe(c.kind)}, inferred={(c.inferred ? 1 : 0)}");
            return;
        }
        if (ev == "press" || ev == "release")
        {
            Channel c = Local.Channel(source, field, out bool created);
            c.pressed = ev == "press";
            if (c.pressed) c.presses++;
            // A press is a datum: a channel that carries nothing but presses is kept live by them.
            c.valid = true;
            c.received = arrived;
            if (source.state == "Waiting") source.state = "Live";
            if (created && logging) Log("sensors", $"channel={c.key}");
            SensorJson.TryGetNumber(line, "timestamp", out double at);
            Log(c.key, $"event={ev}, presses={c.presses}, source_timestamp={at.ToString("R", CultureInfo.InvariantCulture)}");
            return;
        }
        if (ev == "connected" || ev == "disconnected" || ev == "stale")
        {
            string state = ev == "connected" ? "Waiting" : ev == "stale" ? "Stale" : "Disconnected";
            if (ev == "connected" && SensorJson.TryGetNumber(line, "rate", out double rate)) source.nominalRate = rate;
            // A stream that is advertised but silent (an EmotiBit whose Oscilloscope relays nothing) has to
            // be visible as such, and the board lists channels, not sources: its one channel exists from here.
            if (ev == "connected" && source.stream.Length > 0)
            {
                Local.Channel(source, "value", out bool created);
                if (created && logging) Log("sensors", $"channel={source.prefix}.value");
            }
            // A device-level event (no stream) covers every stream of that device.
            foreach (Source s in Local.Sources)
                if (s == source || (source.stream.Length == 0 && s.sensor == source.sensor && s.identity == source.identity))
                    s.state = state;
            foreach (Channel c in Local.Channels)
                if (c.source.state == state && (c.source == source || (source.stream.Length == 0 && c.source.sensor == source.sensor && c.source.identity == source.identity)))
                    c.valid = c.pressed = false;
        }
        // Resumed is only a notification. A new sample makes a reading valid again.
        Log(source.prefix, $"event={Safe(ev)}");
    }

    /// <summary>
    /// Close the statistics window: write one summary line per channel that had samples,
    /// update each channel's measured rate, and reset the window's counters.
    ///
    /// `min`/`max` belong to the window and are reset here; `lo`/`hi`, which a meter is
    /// drawn against, are not - they follow the signal across windows. Mixing the two is
    /// how a bar ends up redrawn from scratch once a second.
    /// </summary>
    void EmitAggregate()
    {
        double elapsed = Math.Max(0.001, Now - _lastStats);
        foreach (Channel c in Local.Channels)
        {
            c.rate = (float)(c.count / elapsed);
            if (c.count > 0 && logging) Log(c.key, $"count={c.count}, mean={F(c.sum / c.count)}, min={F(c.min)}, max={F(c.max)}");
            c.count = 0;
            c.sum = 0;
        }
        _lastStats = Now;
    }

    public Channel Find(string key) { return Local.Find(key); }
    public float Get(string key, float fallback = 0f) { return Local.Get(key, fallback); }
    public bool Has(string key) { return Local.Has(key); }
    public float Rate(string key) { return Local.Rate(key, Math.Max(1, statsInterval * 2)); }
    public IEnumerable<string> Keys { get { return Local.Keys; } }
    public IEnumerable<Channel> Channels { get { return Local.Channels; } }
    public IEnumerable<Source> Sources { get { return Local.Sources; } }

    /// <summary>Where the statistics records are going, in words a person can act on.</summary>
    public string LogDestination
    {
        get
        {
#if VRT_WITH_STATS
            if (!VRTConfig.InstanceExists()) return "statistics not initialised yet - console only";
            string file = VRTConfig.Instance.StatisticsConfig.outputFile;
            if (file == "-") return "statistics muted in config.json";
            if (string.IsNullOrEmpty(file)) return "console only (StatisticsConfig.outputFile is empty in config.json)";
            return Application.persistentDataPath + "/" + file;
#else
            return "console only (VRT_WITH_STATS not defined)";
#endif
        }
    }
    public int written;
    public static string Safe(string value) { return Uri.EscapeDataString(value).Replace(".", "%2E"); }
    public static string F(double value) { return value.ToString("0.######", CultureInfo.InvariantCulture); }
    public static void Log(string component, string fields)
    {
#if VRT_WITH_STATS
        if (StatsReady) Cwipc.Statistics.Output(component, fields);
        else Debug.Log($"stats: component={component}, {fields}");
#else
        Debug.Log($"stats: component={component}, {fields}");
#endif
        Recent.Add($"{DateTime.Now:HH:mm:ss}  {component}  {fields}");
        if (Recent.Count > RecentMax) Recent.RemoveAt(0);
        if (Instance != null) Instance.written++;
    }

    void OnDestroy()
    {
        if (Instance != this) return;
        _running = false;
        _client?.Close();
        _thread?.Join(500);
        Drain(int.MaxValue);
        EmitAggregate();
        if (logging) Log("sensors", $"listening=0, datagrams={datagrams}, dropped={Dropped}");
        SampleReceived = null;
        Instance = null;
    }
}
