using System;
using System.Linq;
using UnityEngine;

/// <summary>
/// One sign per signal, standing beside the thing that signal moves, so a child sees the
/// cause and the effect side by side - and no sign ever talks over another. Each says
/// which device on your body it listens to, what to do, and what is happening right now,
/// in its own colour: the colour the effect itself has. On top of each post sits a small
/// copy of the effect: the warmth sign has a stone whose crack glows exactly as the
/// giant's do, the heartbeat sign a heart beating at your pulse, the wind sign its own
/// leaves, the knock sign a button that sinks when yours is pressed, the pulse sign a
/// heart-stone lit beat by beat, the snow sign its own flurry, the night sign a lamp
/// that dims, the muscle sign a fist, the dream sign a drift of sparks.
///
/// The four signs of the original game stand there always, so a missing device can be seen
/// from the garden. The others grow out of the ground when their sensor first arrives, so
/// plugging in a new module visibly gives the garden a new thing to do.
///
/// A sign is about *your* body: it reads the lantern of the person at this machine. The
/// giant's caption is left to tell the story. The "now" line is held for a couple of
/// seconds before it may change, so it can be read; a press shows at once.
/// Like everything in the garden, a demonstration and not a measurement.
/// </summary>
public class GardenSignpost : MonoBehaviour
{
    public enum Signal { Knock, Wind, Warmth, Heartbeat, Pulse, Snow, Night, Muscle, Dream }
    public Signal signal;
    [Tooltip("Whose body the sign is about. Empty: found at runtime, the lantern with no participant - yours.")]
    public CalmLantern lantern;
    public SleepingGiant giant;
    [Tooltip("Seconds the 'now' line stays before it may change.")]
    public float hold = 2.5f;
    [Tooltip("Stand there even while no device drives this signal. Off: the sign appears when its signal first binds.")]
    public bool showWhenIdle = true;

    [Header("Parts, set in the scene")]
    public TextMesh title, what, now;
    public Transform meter, mirror;
    public Renderer mirrorGlow;
    public ParticleSystem mirrorLeaves;

    [Header("Status, read-only")]
    public float level;
    /// <summary>
    /// Whether the sign's children are showing. It is runtime state, not configuration:
    /// established from the children at Start and changed only by <see cref="Show"/>, which
    /// is the one place that touches their active state.
    ///
    /// It used to be a serialized field, so the scene could be saved with it disagreeing
    /// with the children - and <see cref="Show"/> returns early when it already holds the
    /// value asked for. A saved false with active children therefore hid nothing, and a
    /// saved true with hidden children never revealed them.
    /// </summary>
    public bool shown { get; private set; }

    int _markers = -1;
    float _pressedAt = -100f, _phase, _since = -100f, _meterWidth;
    bool _everBound;
    Vector3 _meterScale, _mirrorScale;
    MaterialPropertyBlock _block;
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    public static Color ColourOf(Signal s)
    {
        switch (s)
        {
            case Signal.Knock: return GardenPalette.Knock;
            case Signal.Wind: return GardenPalette.Wind;
            case Signal.Warmth: return GardenPalette.Warmth;
            case Signal.Heartbeat: return GardenPalette.Heart;
            case Signal.Pulse: return GardenPalette.Pulse;
            case Signal.Snow: return GardenPalette.Snow;
            case Signal.Night: return GardenPalette.Night;
            case Signal.Muscle: return GardenPalette.Muscle;
            default: return GardenPalette.Dream;
        }
    }

    /// <summary>The channel key the lantern is using for a signal; empty while nothing suitable arrives.</summary>
    public static string KeyOf(CalmLantern l, Signal s)
    {
        switch (s)
        {
            case Signal.Knock: return l.markerKey;
            case Signal.Wind: return l.movementKey;
            case Signal.Warmth: return l.arousalKey;
            case Signal.Heartbeat: return l.heartRateKey;
            case Signal.Pulse: return l.pulseKey;
            case Signal.Snow: return l.temperatureKey;
            case Signal.Night: return l.lightKey;
            case Signal.Muscle: return l.tensionKey;
            default: return l.brainKey;
        }
    }

    void Start()
    {
        if (_block != null) return;
        // The children are the truth at startup; from here on this object owns them.
        shown = transform.childCount == 0 || transform.GetChild(0).gameObject.activeSelf;
        _block = new MaterialPropertyBlock();
        if (meter != null) { _meterScale = meter.localScale; _meterWidth = _meterScale.x; }
        if (mirror != null) _mirrorScale = mirror.localScale;
    }

    /// <summary>
    /// Drive this sign: resolve its channel, decide whether it is arriving, then run the one
    /// branch that belongs to this sign's signal and set its text, meter, mirror, glow and
    /// particles from it.
    ///
    /// One switch with a branch per signal, rather than a class per signal. The branches
    /// have almost nothing in common - a knock is an event count, warmth is a range, the
    /// heartbeat carries its own phase - so sharing them costs more than it saves. What is
    /// common is factored out instead: <see cref="Waiting"/> for the not-arriving wording,
    /// <see cref="Flurry"/>, <see cref="Glow"/> and <see cref="Say"/> for the effects, and
    /// the meter below, which every branch feeds through `level`.
    /// </summary>
    void Update()
    {
        if (_block == null) Start();
        if (lantern == null) lantern = FindObjectsByType<CalmLantern>(FindObjectsSortMode.None).FirstOrDefault(l => l.participant.Length == 0);
        if (lantern == null) { Say("Waiting for a lantern..."); return; }
        string key = KeyOf(lantern, signal);
        _everBound |= key.Length > 0;
        Show(showWhenIdle || _everBound);
        if (!shown) return;
        // Bound once is not the same as arriving now. A key survives its device going quiet
        // so that the sign goes on naming what it is for; without this distinction an
        // unplugged electrode read as a calm one, and the sign said so.
        bool live = lantern.Arriving(key);
        float dt = Time.deltaTime;
        switch (signal)
        {
            case Signal.Knock:
                if (_markers < 0) _markers = lantern.markers;
                if (lantern.markers != _markers) { _markers = lantern.markers; _pressedAt = Time.time; }
                bool down = Time.time - _pressedAt < 0.35f;
                if (mirror != null) mirror.localPosition = new Vector3(mirror.localPosition.x, down ? 0.02f : 0.06f, mirror.localPosition.z);
                level = down ? 1f : 0f;
                Say(!live ? Waiting(key, "your BITalino button")
                  : lantern.markers == 0 ? "No knock yet - press your button!"
                  : $"KNOCK!  You have knocked {lantern.markers} time{(lantern.markers == 1 ? "" : "s")}", down);
                break;
            case Signal.Wind:
                level = lantern.movement;
                float still = giant != null ? giant.stillBelow : 0.25f;
                Flurry(1f + 40f * level, 0.1f + 1.2f * level);
                Say(!live ? Waiting(key, "your BITalino")
                  : level < still ? "Still air - the giant likes that"
                  : level < 0.6f ? "A breeze... the leaves are stirring"
                  : "A GALE!  The giant is frowning");
                break;
            case Signal.Warmth:
                level = lantern.arousal;
                Glow(giant != null ? Color.Lerp(giant.coolCrack, giant.warmCrack, level) : Color.Lerp(GardenPalette.Cool, GardenPalette.Warmth, level), 1.5f + 2.5f * level);
                Say(!live ? Waiting(key, "your skin sensor")
                  : lantern.arousalNote.Contains("flat") ? "Your skin is quiet - nothing changing yet"
                  : level < 0.35f ? "Cool and calm - the cracks glow blue"
                  : level < 0.7f ? "Warming up... the cracks turn orange"
                  : "HOT!  The cracks are blazing");
                break;
            case Signal.Heartbeat:
                float bpm = lantern.heartRate;
                level = bpm > 0f ? Mathf.InverseLerp(50f, 130f, bpm) : 0f;
                _phase += dt * (bpm > 0f ? bpm : 0f) / 60f;
                float beat = bpm > 0f ? Mathf.Max(0f, Mathf.Sin(_phase * Mathf.PI * 2f)) : 0f;
                if (mirror != null) mirror.localScale = _mirrorScale * (1f + 0.35f * beat);
                float fastest = giant != null ? giant.breathsPerMinute : 0f;
                Say(!live ? Waiting(key, "your EmotiBit")
                  : bpm <= 0f ? "No heartbeat heard yet - is the EmotiBit on your skin?"
                  : $"Your heart: {bpm:0} beats a minute" + (giant != null && giant.state == "Awake" && fastest > bpm + 1f ? $"  (fastest here: {fastest:0})" : ""));
                break;
            case Signal.Pulse:
                level = lantern.pulse;
                Glow(GardenPalette.Pulse, 0.3f + 4f * level);
                if (mirror != null) mirror.localScale = _mirrorScale * (1f + 0.3f * level);
                Say(!live ? Waiting(key, "your EmotiBit") : "Your pulse lights the giant's heart - watch the stone");
                break;
            case Signal.Snow:
                level = lantern.cold;
                Flurry(30f * level, 0.05f + 0.2f * level);
                Say(!live ? Waiting(key, "your EmotiBit")
                  : lantern.temperatureNote.Contains("flat") ? "Feeling how warm your skin is..."
                  : level < 0.35f ? "Warm hands - no snow today"
                  : level < 0.7f ? "Cooling... a few flakes are falling"
                  : "Brr!  Cold hands make it snow");
                break;
            case Signal.Night:
                level = lantern.night;
                Glow(new Color(1f, 0.9f, 0.6f), 3f * (1f - level));
                Say(!live ? Waiting(key, "your BITalino light sensor")
                  : lantern.lightNote.Contains("flat") ? "Daylight - cover the sensor to see night fall"
                  : level < 0.3f ? "Daylight - the garden is bright"
                  : level < 0.7f ? "Dusk... the sky is darkening"
                  : "Night!  The giant sleeps better now");
                break;
            case Signal.Muscle:
                level = lantern.tension;
                if (mirror != null) mirror.localScale = _mirrorScale * (1f + 0.5f * level);
                Say(!live ? Waiting(key, "your BITalino muscle sensor")
                  : level < 0.25f ? "Relaxed - the giant's hands rest"
                  : level < 0.65f ? "Tensing... its fingers curl"
                  : "SQUEEZE!  The giant clenches its fists");
                break;
            default:
                level = lantern.brain;
                Flurry(1f + 25f * level, 0.1f + 1f * level);
                Say(!live ? Waiting(key, "your BITalino brain sensor")
                  : level < 0.3f ? "A quiet mind - a quiet dream"
                  : level < 0.7f ? "Thinking... the dream stirs"
                  : "Busy thoughts!  What a dream");
                break;
        }
        if (meter != null)
        {
            float w = _meterWidth * Mathf.Clamp01(level);
            meter.localScale = new Vector3(Mathf.Max(0.001f, w), _meterScale.y, _meterScale.z);
            meter.localPosition = new Vector3(-_meterWidth / 2f + w / 2f, meter.localPosition.y, meter.localPosition.z);
        }
    }

    /// <summary>What to say while nothing is arriving: never connected, or connected and now silent.</summary>
    static string Waiting(string key, string what)
    {
        return key.Length == 0 ? $"Waiting for {what}..." : $"Lost {what} - it has stopped sending";
    }

    /// <summary>Show or hide the sign's children. The only place their active state is touched - see <see cref="shown"/>.</summary>
    void Show(bool visible)
    {
        if (shown == visible) return;
        shown = visible;
        foreach (Transform child in transform) child.gameObject.SetActive(visible);
    }

    /// <summary>Drive the mirror's particles. A no-op when this sign has none, so every branch can call it unconditionally.</summary>
    void Flurry(float rate, float speed)
    {
        if (mirrorLeaves == null) return;
        ParticleSystem.EmissionModule emission = mirrorLeaves.emission;
        emission.rateOverTime = rate;
        ParticleSystem.MainModule main = mirrorLeaves.main;
        main.startSpeed = speed;
    }

    void Glow(Color colour, float strength)
    {
        if (mirrorGlow == null) return;
        mirrorGlow.GetPropertyBlock(_block);
        _block.SetColor(EmissionId, colour * strength);
        mirrorGlow.SetPropertyBlock(_block);
    }

    /// <summary>Show a line, once the last one has been up long enough to read; `atOnce` for an event.</summary>
    void Say(string text, bool atOnce = false)
    {
        if (now == null || now.text == text) return;
        if (!atOnce && Time.time - _since < hold) return;
        now.text = text;
        _since = Time.time;
    }
}
