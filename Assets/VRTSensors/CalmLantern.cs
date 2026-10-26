using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The demo object: something in the shared scene that reflects one participant's sensor
/// state back at everyone, in a form that reads without numbers.
///
///   arousal   -> colour, calm through to aroused
///   heartRate -> how fast it breathes
///   movement  -> how much it wobbles
///   marker    -> a flash when the participant presses their button
///
/// It also derives the signals the garden and the giant read from this participant, so that
/// nothing in the scene normalises a channel a second time and disagrees:
///
///   pulse     -> each beat of a PPG waveform (EmotiBit PPG)
///   cold      -> how far skin temperature sits below the warmest seen (EmotiBit TEMP)
///   night     -> how far a light sensor sits below the brightest seen (BITalino LUX)
///   tension   -> muscle activity (BITalino EMG)
///   brain     -> brain activity (BITalino EEG)
///
/// Which participant is <see cref="participant"/>: empty for the person at this machine,
/// a VR2Gather user id for a remote one, whose values SensorShare keeps in a table of
/// their own. The lantern reads that table and nothing else.
///
/// Works on a primitive or on an imported model (Meshy, Sketchfab, anything): put it on the
/// model's root and it drives every renderer and light underneath. It multiplies the object's
/// authored scale and rotation rather than replacing them, so an imported prop keeps the
/// orientation and size it was built with.
///
/// The mappings are a **demonstration**, not a measurement. No threshold here is defined in
/// the measurement contract, and none of it is a validated inference about how anyone feels.
/// Read the statistics log for the data; read the lantern for the idea.
/// </summary>
public class CalmLantern : MonoBehaviour
{
    [Tooltip("Whose sensors this lantern shows: empty for the person at this machine, otherwise a VR2Gather user id. SensorShare sets it on the lanterns it makes for remote participants.")]
    public string participant = "";

    [Header("Channels (as they appear in the statistics log)")]
    [Tooltip("Movement channel. Empty, or a channel that is not arriving, means: a port named acc, else a port an older capture labelled as moving (before ports were named), else the widest-ranging accelerometer stream from any device, such as an EmotiBit ACC_ axis.")]
    public string movementChannel = "bitalino.acc";
    [Tooltip("Marker channel. Empty, or a channel that is not arriving, means: whichever channel the acquisition reports presses on - a BITalino port named btn.")]
    public string markerChannel = "bitalino.btn";
    [Tooltip("Arousal channel. Empty, or a channel that is not arriving, means: any channel named EDA (EmotiBit), or eda (a BITalino port named with --channel A3=eda). A BITalino port cannot be told to be EDA from its signal, so it has to be named.")]
    public string arousalChannel = "emotibit.EDA.value";
    [Tooltip("Heart-rate channel in bpm. Empty, or a channel that is not arriving, means any channel named HR or hr. Event-driven, so it stays empty until the device has seen enough beats.")]
    public string heartRateChannel = "emotibit.HR.value";
    [Tooltip("Pulse waveform. Empty, or not arriving, means an EmotiBit PPG stream.")]
    public string pulseChannel = "";
    [Tooltip("Skin temperature. Empty, or not arriving, means an EmotiBit TEMP or THERM stream.")]
    public string temperatureChannel = "";
    [Tooltip("Light. Empty, or not arriving, means a BITalino port named lux.")]
    public string lightChannel = "bitalino.lux";
    [Tooltip("Muscle. Empty, or not arriving, means a BITalino port named emg.")]
    public string tensionChannel = "bitalino.emg";
    [Tooltip("Brain. Empty, or not arriving, means a BITalino port named eeg.")]
    public string brainChannel = "bitalino.eeg";

    [Header("What to drive")]
    public bool driveColour = true;
    [Tooltip("Also drive emission. Only visible if the material already has emission enabled - a property block cannot switch the shader keyword on.")]
    public bool driveEmission = false;
    [Tooltip("Drive any Light under this object. This is what makes an imported lamp actually glow.")]
    public bool driveLights = true;
    public bool drivePulse = true;
    public bool driveWobble = true;

    [Tooltip("Renderers to tint. Leave empty to use every renderer on this object and its children, which is what an imported model needs.")]
    public Renderer[] renderers;
    [Tooltip("Lights to drive. Leave empty to use every light on this object and its children.")]
    public Light[] lights;

    [Header("Ranges (demonstration values, not measurements)")]
    [Tooltip("Arousal values mapped onto calm..aroused when arousalAuto is off. Depends on the sensor and the person; adjust while watching.")]
    public float arousalMin = 0f;
    public float arousalMax = 5f;
    [Tooltip("Movement away from resting that counts as fully restless, in a BITalino accelerometer's raw counts.")]
    public float movementFullScale = 150f;
    [Tooltip("The same for an accelerometer that reports in g, such as an EmotiBit ACC_ axis. A display choice, set to feel like the BITalino default, not a measured equivalence.")]
    public float movementFullScaleG = 0.9f;
    [Tooltip("EMG counts of activity away from rest that count as a full clench.")]
    public float tensionFullScale = 120f;
    [Tooltip("EEG counts of activity away from rest that count as a busy mind.")]
    public float brainFullScale = 600f;
    [Tooltip("Degrees the temperature channel must have spanned before cold means anything.")]
    public float temperatureMinSpan = 0.3f;
    [Tooltip("Counts the light channel must have spanned before night means anything.")]
    public float lightMinSpan = 20f;

    [Header("Appearance")]
    public Color calmColour = new Color(0.30f, 0.62f, 0.85f);
    public Color arousedColour = new Color(0.93f, 0.55f, 0.20f);
    public Color markerColour = Color.white;
    [Tooltip("Breaths per minute when no heart-rate channel is available.")]
    public float restingBreathsPerMinute = 30f;
    [Tooltip("Fraction of its own size the object grows and shrinks by.")]
    public float pulseDepth = 0.12f;
    public float wobbleDegrees = 12f;
    [Tooltip("Seconds the marker flash lasts.")]
    public float markerFlash = 0.6f;

    [Header("Automatic binding")]
    [Tooltip("If a configured channel is not arriving, use what is, going by what the acquisition says each channel is. The board shows what was chosen.")]
    public bool autoBind = true;
    [Tooltip("Map arousal onto the range the channel has actually shown this session rather than arousalMin..arousalMax. A demonstration choice: a flat signal stays calm and any variation becomes visible.")]
    public bool arousalAuto = true;
    [Tooltip("Below this much variation (in the channel's own units) the channel counts as flat and arousal stays 0.")]
    public float arousalMinSpan = 0.25f;

    [Header("Status, read-only")]
    public float arousal;
    public float movement;
    public int markers;
    public float heartRate;
    public float pulse, cold, night, tension, brain;
    [Tooltip("Channel keys actually in use, after automatic binding. Empty means nothing suitable is arriving.")]
    public string markerKey = "", movementKey = "", arousalKey = "", heartRateKey = "";
    public string pulseKey = "", temperatureKey = "", lightKey = "", tensionKey = "", brainKey = "";
    public string arousalNote = "", temperatureNote = "", lightNote = "";

    static readonly int BaseColourId = Shader.PropertyToID("_BaseColor");
    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    // Port names that say what a module is; none of them is movement, whatever its signal looks like.
    static readonly string[] NotMotion = { "eda", "emg", "eeg", "lux", "btn" };

    Renderer[] _renderers;
    int[] _colourIds;
    Light[] _lights;
    float[] _lightIntensity;
    MaterialPropertyBlock _block;
    Vector3 _baseScale;
    Quaternion _baseRotation;
    float _phase;
    float _restingMovement = float.NaN;
    float _flashUntil = -1f;
    float _pulseBaseline = float.NaN, _pulseScale;
    readonly Dictionary<string, int> _seen = new Dictionary<string, int>();

    /// <summary>Take the press counts a table already holds as this lantern's starting point.
    /// Called once, on the first snapshot from a remote participant, so joining a session in
    /// progress does not replay that person's earlier presses.</summary>
    public void SeedMarkerBaseline(SensorStream.Table table)
    {
        foreach (SensorStream.Channel c in table.Channels) _seen[c.key] = c.presses;
    }
    bool _warned;

    /// <summary>This participant's channels, or null while they have none.</summary>
    public SensorStream.Table Table { get { return SensorShare.TableFor(participant); } }

    /// <summary>
    /// The configured channel while it is arriving; otherwise, with autoBind, the best live
    /// channel that `fits` (highest `score` wins); otherwise the configured channel anyway,
    /// if there has ever been one. Empty when nothing suitable has ever arrived.
    ///
    /// The last case is the one worth explaining. A key that has been bound is kept even
    /// after its device goes quiet, so that a sign goes on naming what it is for and a
    /// two-second dropout does not unbind everything. Whether that channel is *arriving* is
    /// a separate question, and the one every effect and every status line has to ask - see
    /// <see cref="Arriving"/>. Answering it with "the key is not empty" made an unplugged
    /// electrode look like a calm one.
    /// </summary>
    string Resolve(SensorStream.Table table, string configured, Func<SensorStream.Channel, bool> fits, Func<SensorStream.Channel, float> score = null)
    {
        SensorStream.Channel c = table.Find(configured);
        if (c != null && c.Available) return c.key;
        if (!autoBind) return c == null ? "" : c.key;
        SensorStream.Channel best = null;
        float bestScore = float.MinValue;
        foreach (SensorStream.Channel candidate in table.Channels)
        {
            // Only what is live: a dead device's channel must not be chosen over a working one.
            if (!candidate.Available || !fits(candidate)) continue;
            float s = score == null ? 0f : score(candidate);
            if (best == null || s > bestScore) { best = candidate; bestScore = s; }
        }
        // Nothing live: fall back to the configured channel if it has ever been seen, so an
        // explicitly named device that dies is still named rather than silently forgotten.
        if (best == null) return c == null ? "" : c.key;
        return best.key;
    }

    /// <summary>
    /// Whether a channel is arriving now, as against having been bound at some point.
    /// This is what gates an effect or a status line; <see cref="Resolve"/> explains why
    /// a nonempty key is not the same question.
    /// </summary>
    public bool Arriving(string key)
    {
        SensorStream.Table t = Table;
        SensorStream.Channel c = t == null ? null : t.Find(key);
        return c != null && c.Available;
    }

    /// <summary>Whether a channel carries one of these names, as its field or as its stream. Case-insensitive: `EDA` and `eda` are the same signal from two devices.</summary>
    static bool Named(SensorStream.Channel c, params string[] names)
    {
        foreach (string name in names)
            if (string.Equals(c.field, name, StringComparison.OrdinalIgnoreCase) || string.Equals(c.source.stream, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    // The acquisition names the button `btn` (on a BITalino digital input); `kind == "button"` is what older captures inferred.
    static bool IsButton(SensorStream.Channel c) { return Named(c, "btn") || c.kind == "button" || c.presses > 0; }
    // A port that has been named is what it was named, so a flexed muscle never becomes the wind.
    static bool IsMotion(SensorStream.Channel c) { return Named(c, "acc") || (!Named(c, NotMotion) && (c.kind == "moving" || c.kind == "steady" || c.source.stream.StartsWith("acc", StringComparison.OrdinalIgnoreCase) || c.field.StartsWith("acc", StringComparison.OrdinalIgnoreCase))); }

    /// <summary>Where the channel's value sits in the range it has shown this session, or -1 while that range is narrower than `minSpan`.</summary>
    static float Ranged(SensorStream.Channel c, float minSpan)
    {
        return c.hi - c.lo < minSpan ? -1f : Mathf.Clamp01(Mathf.InverseLerp(c.lo, c.hi, c.value));
    }

    /// <summary>
    /// Take `original`'s resting size and orientation as this lantern's own. A copy made with
    /// Instantiate inherits the original mid-pulse and mid-wobble, and Start would otherwise
    /// take that moment as the authored transform: up to about 12% off-size and 20 degrees
    /// tilted for the rest of the session. Call it before this lantern's first frame.
    /// </summary>
    public void RestFrom(CalmLantern original)
    {
        if (original == null || original._block == null) return;
        transform.localScale = original._baseScale;
        transform.localRotation = original._baseRotation;
    }

    void Start()
    {
        // Update calls Start when it runs first (a test driving it directly); once is enough,
        // and a second time would take an already pulsing scale as the resting one.
        if (_block != null) return;
        // Multiply the authored transform rather than overwrite it: an imported model is
        // usually not unit-scaled and not axis-aligned, and replacing these would move it.
        _baseScale = transform.localScale;
        _baseRotation = transform.localRotation;

        _renderers = renderers != null && renderers.Length > 0 ? renderers : GetComponentsInChildren<Renderer>();
        _colourIds = new int[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            // URP and Built-in name the main colour differently, and an imported model can
            // arrive with either. Ask the shader instead of assuming.
            Material m = _renderers[i].sharedMaterial;
            _colourIds[i] = m != null && m.HasProperty(BaseColourId) ? BaseColourId : ColourId;
        }

        _lights = lights != null && lights.Length > 0 ? lights : GetComponentsInChildren<Light>();
        _lightIntensity = new float[_lights.Length];
        for (int i = 0; i < _lights.Length; i++) _lightIntensity[i] = _lights[i].intensity;

        _block = new MaterialPropertyBlock();

        if (_renderers.Length == 0 && _lights.Length == 0)
            Debug.LogWarning($"CalmLantern on '{name}': no renderers or lights found under this object. It will still move, but nothing will change colour.");
    }

    /// <summary>
    /// Resolve this participant's channels, derive every signal from them, and drive the
    /// object's colour, scale, wobble and lights.
    ///
    /// Derivation happens once, here, and everything else in the scene reads the result -
    /// so the giant, the signs and the board cannot normalise the same channel twice and
    /// disagree about it. The exception is `tension` and `brain`, whose envelope is
    /// measured on the channel itself as each sample arrives; this only picks a full scale.
    ///
    /// Every signal falls back to zero when its channel is not arriving, rather than
    /// holding the last value a dead device sent.
    /// </summary>
    void Update()
    {
        if (_block == null) Start();
        SensorStream.Table table = Table;
        if (table == null)
        {
            if (!_warned && participant.Length == 0)
            {
                _warned = true;
                Debug.LogWarning("CalmLantern: no SensorStream; nothing will move. It is created at application start, so this means play was entered in an unusual way.");
            }
            return;
        }
        float dt = Time.deltaTime;

        markerKey = Resolve(table, markerChannel, IsButton, c => Named(c, "btn") ? 2f : c.kind == "button" ? 1f : 0f);
        movementKey = Resolve(table, movementChannel, IsMotion, c => Named(c, "acc") ? float.MaxValue : c.kind == "moving" ? 1e6f + c.hi - c.lo : c.hi - c.lo);
        arousalKey = Resolve(table, arousalChannel, c => Named(c, "eda", "EA"));
        heartRateKey = Resolve(table, heartRateChannel, c => Named(c, "hr"));
        pulseKey = Resolve(table, pulseChannel, c => Named(c, "PPG_IR", "PPG_RED", "PPG_GRN", "PPG_GREEN"));
        temperatureKey = Resolve(table, temperatureChannel, c => Named(c, "TEMP", "TEMP1", "THERM"));
        lightKey = Resolve(table, lightChannel, c => Named(c, "lux"));
        tensionKey = Resolve(table, tensionChannel, c => Named(c, "emg"));
        brainKey = Resolve(table, brainChannel, c => Named(c, "eeg"));

        // Presses are counted by the acquisition, per channel; a rise since this lantern last
        // looked at that channel is a new press. Local counts start at zero with the session,
        // so every one is real-time. A remote lantern's baseline is taken once, from the first
        // snapshot that participant sends (SeedMarkerBaseline), so a lantern joining late does
        // not flash for presses that happened before it existed. A channel that appears after
        // that starts at zero, because its first press is one this lantern was there to see.
        SensorStream.Channel marker = table.Find(markerKey);
        if (marker != null)
        {
            if (!_seen.TryGetValue(marker.key, out int seen)) seen = 0;
            if (marker.presses > seen)
            {
                markers += marker.presses - seen;
                _flashUntil = Time.time + markerFlash;
            }
            _seen[marker.key] = marker.presses;
        }

        // Movement: distance from where the accelerometer has been resting. A running
        // baseline, because "at rest" depends on how the board happens to be lying.
        if (table.Has(movementKey))
        {
            float raw = table.Get(movementKey);
            // NaN, not a negative number, means "no resting level yet": an EmotiBit axis is
            // signed, and a negative sentinel reset the level every frame and stilled the wind.
            if (float.IsNaN(_restingMovement)) _restingMovement = raw;
            _restingMovement = Mathf.Lerp(_restingMovement, raw, dt / 3f);
            // An EmotiBit reports in g and a BITalino in raw counts, so each has its own full scale.
            SensorStream.Channel moving = table.Find(movementKey);
            float fullScale = moving != null && moving.source.sensor == "emotibit" ? Mathf.Max(0.01f, movementFullScaleG) : Mathf.Max(1f, movementFullScale);
            movement = Mathf.Clamp01(Mathf.Abs(raw - _restingMovement) / fullScale);
        }
        else { movement = 0f; _restingMovement = float.NaN; }

        arousal = 0f;
        arousalNote = "no arousal channel arriving";
        if (table.Has(arousalKey))
        {
            SensorStream.Channel source = table.Find(arousalKey);
            float lo = arousalAuto ? source.lo : arousalMin, hi = arousalAuto ? source.hi : arousalMax;
            if (hi - lo < arousalMinSpan)
                arousalNote = $"range seen {SensorStream.F(source.lo)}-{SensorStream.F(source.hi)}: flat, no variation yet";
            else
            {
                arousal = Mathf.Clamp01(Mathf.InverseLerp(lo, hi, source.value));
                arousalNote = (arousalAuto ? "range seen " : "range set ") + $"{SensorStream.F(lo)}-{SensorStream.F(hi)}";
            }
        }

        float bpm = restingBreathsPerMinute;
        heartRate = 0f;
        if (table.Has(heartRateKey))
        {
            float hr = table.Get(heartRateKey);
            // Ignore obvious nonsense rather than letting the object seize up.
            if (hr > 30f && hr < 220f) { bpm = hr; heartRate = hr; }
        }

        // The pulse waveform: what rises above its recent baseline, against the biggest rise seen lately.
        if (table.Has(pulseKey))
        {
            float v = table.Get(pulseKey);
            if (float.IsNaN(_pulseBaseline)) _pulseBaseline = v;
            _pulseBaseline = Mathf.Lerp(_pulseBaseline, v, Mathf.Clamp01(dt / 1.5f));
            float rise = v - _pulseBaseline;
            _pulseScale = Mathf.Max(_pulseScale * Mathf.Clamp01(1f - dt / 4f), Mathf.Abs(rise));
            pulse = _pulseScale > 0.001f ? Mathf.Clamp01(rise / _pulseScale) : 0f;
        }
        else { pulse = 0f; _pulseBaseline = float.NaN; _pulseScale = 0f; }

        // Cold and night: how far below the warmest and brightest this channel has shown.
        cold = 0f;
        temperatureNote = "no temperature channel arriving";
        if (table.Has(temperatureKey))
        {
            float where = Ranged(table.Find(temperatureKey), temperatureMinSpan);
            cold = where < 0f ? 0f : 1f - where;
            temperatureNote = where < 0f ? "flat, no variation yet" : "range seen " + SensorStream.F(table.Find(temperatureKey).lo) + "-" + SensorStream.F(table.Find(temperatureKey).hi);
        }
        night = 0f;
        lightNote = "no light channel arriving";
        if (table.Has(lightKey))
        {
            float where = Ranged(table.Find(lightKey), lightMinSpan);
            night = where < 0f ? 0f : 1f - where;
            lightNote = where < 0f ? "flat, cover the sensor to see night fall" : "range seen " + SensorStream.F(table.Find(lightKey).lo) + "-" + SensorStream.F(table.Find(lightKey).hi);
        }

        // Muscle and brain: activity about the resting level. The envelope is the channel's
        // own, measured over every sample as it arrived and shared with the other
        // participants, so a burst that fits inside one frame is not lost and a remote
        // lantern shows the same figure. Full scale is this lantern's to choose.
        SensorStream.Channel muscle = table.Has(tensionKey) ? table.Find(tensionKey) : null;
        tension = muscle == null ? 0f : muscle.Activity(tensionFullScale);
        SensorStream.Channel mind = table.Has(brainKey) ? table.Find(brainKey) : null;
        brain = mind == null ? 0f : mind.Activity(brainFullScale);

        _phase += dt * bpm / 60f;
        float breath = Mathf.Sin(_phase * Mathf.PI * 2f);

        if (drivePulse)
            transform.localScale = _baseScale * (1f + breath * pulseDepth * (0.4f + 0.6f * (1f - arousal)));
        if (driveWobble)
            transform.localRotation = _baseRotation * Quaternion.Euler(
                Mathf.Sin(_phase * 5.1f) * wobbleDegrees * movement,
                Mathf.Sin(_phase * 3.7f) * wobbleDegrees * movement,
                0f);

        Color c = Color.Lerp(calmColour, arousedColour, arousal);
        if (Time.time < _flashUntil)
            c = Color.Lerp(markerColour, c, 1f - (_flashUntil - Time.time) / Mathf.Max(0.01f, markerFlash));

        if (driveColour || driveEmission)
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] == null) continue;
                // A property block tints the renderer without creating a material instance
                // per object, so an imported model's shared materials are left alone.
                _renderers[i].GetPropertyBlock(_block);
                if (driveColour) _block.SetColor(_colourIds[i], c);
                if (driveEmission) _block.SetColor(EmissionId, c * (0.5f + 0.5f * Mathf.Abs(breath)));
                _renderers[i].SetPropertyBlock(_block);
            }
        }

        if (driveLights)
        {
            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] == null) continue;
                _lights[i].color = c;
                _lights[i].intensity = _lightIntensity[i] * (0.7f + 0.3f * breath);
            }
        }
    }
}
