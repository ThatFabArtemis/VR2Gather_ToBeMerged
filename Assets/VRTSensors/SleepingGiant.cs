using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The Sleeping Giant: one shared creature in the garden that every participant's body
/// weather acts on, so the sensors become a story rather than a display.
///
///   button press     -> a knock: a thud, a ring in the grass, the giant peeks
///   movement         -> wind: leaves fly, the giant frowns
///   skin response    -> warmth: the cracks in its stone glow from cool blue to amber
///   heart rate       -> the giant breathes at the pulse of whoever's heart is fastest
///   pulse waveform   -> the heart-stone in its chest lights with every beat
///   muscle           -> it clenches its fists; a clenched child is not a calm one
///   light            -> night falls, and the giant sleeps more easily in the dark
///   brain            -> while it sleeps, it dreams: busy thoughts, busy dream
///   skin temperature -> cold, which the garden shows as snow (SensorGarden reads it here)
///
/// The game loop kids can follow without being told: knock to wake it, then sit still,
/// relax and breathe slowly, together, until its eyelids close again. Its eyelids *are*
/// the progress bar. Then it sleeps, and they can do it again. Only knocks wake it:
/// movement never counts as a knock, however hard.
///
/// The caption over its head tells the story - knock, calm down, well done - and nothing
/// else; what each sensor is doing is said on its own GardenSignpost beside the effect,
/// in the effect's own colour, so the lines never overwrite one another.
///
/// Every client computes the giant from the same participant lanterns (local and remote
/// tables), so nothing extra is networked; clients agree to within the snapshot latency.
/// Like the lantern this is a demonstration, not a measurement: "calm" here is a demo
/// threshold on normalised values, defined nowhere in the measurement contract.
/// </summary>
public class SleepingGiant : MonoBehaviour
{
    [Header("Parts, set in the scene")]
    public Transform body, head, brow, mouth, mouthLeft, mouthRight;
    public Transform[] eyelids, pupils;
    public Renderer[] cracks;
    public ParticleSystem leaves, petals, dream;
    public Renderer knockRing, heart;
    public Transform armLeft, armRight, fistLeft, fistRight;
    public TextMesh caption;
    public Light glow;
    public AudioSource voice;

    [Header("Feel (demonstration thresholds, not measurements)")]
    [Tooltip("Knocks needed to wake the giant.")]
    public int knocksToWake = 3;
    [Tooltip("Seconds everyone must stay calm for the giant to fall asleep again.")]
    public float soothedAfter = 8f;
    [Tooltip("Seconds the giant enjoys being calm before it sleeps and the game restarts.")]
    public float soothedFor = 12f;
    [Tooltip("Movement (0-1) below which a participant counts as still.")]
    public float stillBelow = 0.25f;
    [Tooltip("Arousal (0-1) below which a participant counts as calm.")]
    public float calmBelow = 0.6f;
    [Tooltip("Muscle tension (0-1) below which a participant counts as relaxed.")]
    public float relaxedBelow = 0.5f;
    public Color coolCrack = GardenPalette.Cool;
    public Color warmCrack = GardenPalette.Warmth;
    public float restingBreathsPerMinute = 10f;
    [Tooltip("Seconds a caption stays before it may change, so it can be read. A state change or a knock shows at once.")]
    public float captionHold = 2.5f;

    [Header("Status, read-only")]
    public string state = "Asleep";
    public float wind, warmth, breathsPerMinute, calmProgress, lidOpen;
    public float pulse, tension, night, brain, cold;
    public int knocks;
    public string restless = "";

    readonly Dictionary<CalmLantern, int> _seenMarkers = new Dictionary<CalmLantern, int>();
    readonly List<CalmLantern> _lanterns = new List<CalmLantern>();
    float _lidTarget, _peekUntil = -1f, _ringStart = -100f, _stateSince, _phase, _smile, _browDown, _clench, _time, _captionSince = -100f;
    bool _captionNow, _dreaming;
    Vector3 _gaze, _gazeTarget;
    Vector3 _bodyScale, _ringScale, _heartScale, _fistScale;
    Vector3[] _lidRest;
    MaterialPropertyBlock _block;
    Camera _camera;
    AudioClip _thud, _chime;
    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    static readonly int ColourId = Shader.PropertyToID("_Color");

    void Start()
    {
        if (_block != null) return;
        _block = new MaterialPropertyBlock();
        if (body != null) _bodyScale = body.localScale;
        if (knockRing != null) { _ringScale = knockRing.transform.localScale; knockRing.enabled = false; }
        if (heart != null) _heartScale = heart.transform.localScale;
        if (fistLeft != null) _fistScale = fistLeft.localScale;
        _lidRest = new Vector3[eyelids == null ? 0 : eyelids.Length];
        for (int i = 0; i < _lidRest.Length; i++) _lidRest[i] = eyelids[i].localPosition;
        _thud = Tone("thud", 0.5f, 55f, 6f, 0.9f);
        _chime = Tone("chime", 2.2f, 660f, 1.6f, 0.35f);
        Enter("Asleep");
    }

    /// <summary>The lanterns whose weather acts on the giant: everyone in the session.</summary>
    public List<CalmLantern> Participants()
    {
        _lanterns.Clear();
        SensorShare share = SensorShare.Instance;
        if (share != null && share.lanternTemplate != null)
        {
            _lanterns.Add(share.lanternTemplate);
            foreach (SensorShare.Participant p in share.Remote.Values) if (p.lantern != null) _lanterns.Add(p.lantern);
        }
        else _lanterns.AddRange(FindObjectsByType<CalmLantern>(FindObjectsSortMode.None));
        return _lanterns;
    }

    void Update()
    {
        if (_block == null) Start();
#if ENABLE_INPUT_SYSTEM
        // K knocks from the keyboard, through the same path a sensor press takes, so it is
        // logged as what it is and shared like any other press. For testing without a board.
        if (Keyboard.current != null && Keyboard.current.kKey.wasPressedThisFrame && SensorStream.Instance != null)
            SensorStream.Instance.Inject("{\"sensor\":\"keyboard\",\"device\":\"local\",\"timestamp\":0,\"event\":\"press\",\"channel\":\"k\",\"value\":1}");
#endif
        Tick(Time.deltaTime);
    }

    /// <summary>One step of the giant. Public so a test can move it through time without waiting.</summary>
    public void Tick(float dt)
    {
        _time += dt;
        List<CalmLantern> lanterns = Participants();

        // The weather everyone makes together: the worst wind, the warmest skin on average,
        // the fastest heart, the strongest pulse, the hardest clench, the darkest room.
        wind = pulse = tension = night = brain = cold = 0f;
        float warmthSum = 0f, fastest = 0f;
        int warmthCount = 0;
        _dreaming = false;
        var restlessNames = new List<string>();
        CalmLantern loudest = null;
        float loudness = -1f;
        foreach (CalmLantern l in lanterns)
        {
            wind = Mathf.Max(wind, l.movement);
            if (l.Arriving(l.arousalKey)) { warmthSum += l.arousal; warmthCount++; }
            fastest = Mathf.Max(fastest, l.heartRate);
            pulse = Mathf.Max(pulse, l.pulse);
            tension = Mathf.Max(tension, l.tension);
            night = Mathf.Max(night, l.night);
            brain = Mathf.Max(brain, l.brain);
            cold = Mathf.Max(cold, l.cold);
            // Arriving, not merely bound: a key stays after its device goes quiet, and
            // gating on the key alone kept the dream going over an unplugged electrode.
            _dreaming |= l.Arriving(l.brainKey);
            bool still = l.movement < stillBelow && l.arousal < calmBelow && l.tension < relaxedBelow;
            if (!still) restlessNames.Add(NameOf(l));
            float loud = l.movement + l.arousal + l.tension;
            if (loud > loudness) { loudness = loud; loudest = l; }
            if (!_seenMarkers.TryGetValue(l, out int seen)) seen = l.markers;
            for (int marker = seen; marker < l.markers; marker++) Knock(l);
            _seenMarkers[l] = l.markers;
        }
        warmth = warmthCount == 0 ? 0f : warmthSum / warmthCount;
        restless = string.Join(", ", restlessNames);
        bool allCalm = restlessNames.Count == 0;

        switch (state)
        {
            case "Asleep":
                breathsPerMinute = restingBreathsPerMinute;
                _lidTarget = _time < _peekUntil ? 0.6f : 0f;
                calmProgress = 0f;
                if (knocks >= knocksToWake) Enter("Awake");
                break;
            case "Awake":
                breathsPerMinute = fastest > 0f ? fastest : 20f + 40f * wind;
                // Darkness helps: the calm counts faster and the lids sit lower.
                calmProgress = Mathf.Clamp01(calmProgress + (allCalm ? dt / soothedAfter * (1f + night) : -dt / 2f));
                _lidTarget = (1f - 0.85f * calmProgress) * (1f - 0.4f * night);
                if (calmProgress >= 1f) Enter("Soothed");
                break;
            case "Soothed":
                breathsPerMinute = restingBreathsPerMinute;
                _lidTarget = 0.15f;
                if (_time - _stateSince > soothedFor) { knocks = 0; Enter("Asleep"); }
                break;
        }
        lidOpen = Mathf.MoveTowards(lidOpen, _lidTarget, dt * 3f);
        _smile = Mathf.MoveTowards(_smile, state == "Soothed" ? 1f : 0f, dt * 2f);
        _browDown = Mathf.MoveTowards(_browDown, state == "Awake" ? Mathf.Clamp01(wind * 1.5f) : 0f, dt * 3f);
        _clench = Mathf.MoveTowards(_clench, tension, dt * 4f);
        _gazeTarget = loudest != null && state == "Awake" ? loudest.transform.position : Vector3.zero;
        Caption(lanterns.Count);
        Animate(dt);
    }

    /// <summary>Move to a state and restart its clock. Every timed transition measures from here.</summary>
    void Enter(string next)
    {
        state = next;
        _stateSince = _time;
        _captionNow = true;
        if (next == "Asleep") calmProgress = 0f;
        if (next == "Soothed")
        {
            if (petals != null) petals.Emit(160);
            if (voice != null && _chime != null) voice.PlayOneShot(_chime, 0.8f);
            SensorStream.Log("giant", $"state=soothed, participants={_lanterns.Count}");
        }
        else SensorStream.Log("giant", $"state={next.ToLowerInvariant()}");
    }

    /// <summary>
    /// One knock from one participant: rings, captions who it was, and wakes the giant on
    /// the knocksToWake-th (three by default).
    ///
    /// Knocks do not decay while it sleeps, so two presses a minute apart still wake it -
    /// which is deliberate, and has been reported as a bug more than once.
    /// </summary>
    void Knock(CalmLantern from)
    {
        knocks++;
        _ringStart = _time;
        _captionNow = true;
        if (state == "Asleep") _peekUntil = _time + 0.9f;
        if (voice != null && _thud != null) voice.PlayOneShot(_thud, 1f);
        SensorStream.Log("giant", $"knock={knocks}, from={SensorStream.Safe(NameOf(from))}, state={state.ToLowerInvariant()}");
    }

    static string NameOf(CalmLantern l)
    {
        if (l.participant.Length == 0) return "You";
        SensorShare share = SensorShare.Instance;
        return share != null && share.Remote.TryGetValue(l.participant, out SensorShare.Participant p) && p.userName.Length > 0 ? p.userName : l.participant;
    }

    // The caption tells the story and nothing else: what each sensor is doing is on its own
    // signpost, so no line here has to fight the wind. A line stays up for captionHold
    // seconds before it may change; a state change or a knock replaces it at once.
    void Caption(int participants)
    {
        if (caption == null) return;
        string text;
        switch (state)
        {
            case "Asleep":
                text = knocks == 0 ? "Shh... the giant is sleeping.\nPress your button to knock!"
                     : $"It's stirring! Knock again  ({knocks} of {knocksToWake})";
                break;
            case "Awake":
                text = restless.Length > 0 ? $"It's awake! Sit still, relax and\nbreathe slowly, {restless}"
                     : calmProgress < 0.5f ? "That's it... keep still, breathe slowly"
                     : "It's getting sleepy... keep going!";
                break;
            default:
                text = participants > 1 ? "You calmed the giant together!\nWell done" : "You calmed the giant!\nWell done";
                break;
        }
        if (caption.text == text) return;
        if (!_captionNow && _time - _captionSince < captionHold) return;
        caption.text = text;
        _captionSince = _time;
        _captionNow = false;
    }

    /// <summary>
    /// Everything the giant does that is a function of its state and the participants'
    /// signals: breathing, eyelids, brow, the cracks' colour, the heart-stone, the fists,
    /// the leaves, and the dream.
    ///
    /// Separated from the state machine above so that a change to how it *looks* cannot
    /// alter when it wakes or settles.
    /// </summary>
    void Animate(float dt)
    {
        _phase += dt * breathsPerMinute / 60f;
        float breath = Mathf.Sin(_phase * Mathf.PI * 2f);
        if (body != null) body.localScale = new Vector3(_bodyScale.x, _bodyScale.y * (1f + 0.04f * breath), _bodyScale.z * (1f + 0.02f * breath));
        if (head != null) head.localRotation = Quaternion.Euler(-2f + 2f * breath - 6f * _smile, 0f, 0f);
        for (int i = 0; i < _lidRest.Length; i++)
            eyelids[i].localPosition = _lidRest[i] + Vector3.up * (0.62f * lidOpen);
        if (brow != null) brow.localPosition = new Vector3(brow.localPosition.x, 0.6f - 0.22f * _browDown, brow.localPosition.z);
        if (brow != null) brow.localRotation = Quaternion.Euler(-20f * _browDown, 0f, 0f);
        // Eyes follow whoever is making the most weather.
        _gaze = Vector3.Lerp(_gaze, _gazeTarget, dt * 3f);
        if (pupils != null && head != null)
        {
            Vector3 local = head.InverseTransformPoint(_gaze);
            Vector2 look = _gaze == Vector3.zero ? Vector2.zero : new Vector2(Mathf.Clamp(local.x * 0.15f, -0.2f, 0.2f), Mathf.Clamp(local.y * 0.08f, -0.12f, 0.12f));
            foreach (Transform p in pupils) p.localPosition = new Vector3(look.x, look.y, p.localPosition.z);
        }
        if (mouth != null) mouth.localScale = new Vector3(0.9f + 0.5f * _smile, 0.08f, 0.2f);
        if (mouthLeft != null) mouthLeft.localPosition = new Vector3(-0.5f - 0.15f * _smile, 0.12f * _smile, 0f);
        if (mouthRight != null) mouthRight.localPosition = new Vector3(0.5f + 0.15f * _smile, 0.12f * _smile, 0f);

        Color crack = Color.Lerp(coolCrack, warmCrack, state == "Soothed" ? 0f : warmth);
        float pulseGlow = 0.6f + 0.4f * Mathf.Abs(breath);
        if (cracks != null)
            foreach (Renderer r in cracks)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_block);
                _block.SetColor(EmissionId, crack * (1.5f + 2.5f * warmth) * pulseGlow);
                r.SetPropertyBlock(_block);
            }
        if (glow != null) { glow.color = crack; glow.intensity = (0.8f + 2.5f * warmth) * pulseGlow; }

        // The heart-stone: lit by the pulse waveform, beat by beat, not by a rate.
        if (heart != null)
        {
            heart.GetPropertyBlock(_block);
            _block.SetColor(EmissionId, GardenPalette.Pulse * (0.3f + 4f * pulse));
            heart.SetPropertyBlock(_block);
            heart.transform.localScale = _heartScale * (1f + 0.3f * pulse);
        }

        // The fists: the arms lift and the hands ball up with muscle tension.
        if (armLeft != null) armLeft.localRotation = Quaternion.Euler(0f, 0f, 75f - 40f * _clench);
        if (armRight != null) armRight.localRotation = Quaternion.Euler(0f, 0f, -75f + 40f * _clench);
        if (fistLeft != null && armLeft != null) { fistLeft.position = armLeft.TransformPoint(0f, 1f, 0f); fistLeft.localScale = _fistScale * (1f + 0.5f * _clench); }
        if (fistRight != null && armRight != null) { fistRight.position = armRight.TransformPoint(0f, 1f, 0f); fistRight.localScale = _fistScale * (1f + 0.5f * _clench); }

        if (leaves != null)
        {
            ParticleSystem.EmissionModule emission = leaves.emission;
            emission.rateOverTime = 2f + 120f * wind;
            ParticleSystem.MainModule main = leaves.main;
            main.startSpeed = 0.3f + 3f * wind;
            ParticleSystem.NoiseModule noise = leaves.noise;
            noise.strength = 0.4f + 2f * wind;
        }

        // The dream: only while somebody's brain is heard, and only while the eyes are shut.
        if (dream != null)
        {
            ParticleSystem.EmissionModule emission = dream.emission;
            emission.rateOverTime = _dreaming ? (3f + 45f * brain) * (1f - lidOpen) : 0f;
            ParticleSystem.MainModule main = dream.main;
            main.startSpeed = 0.15f + 1.5f * brain;
            ParticleSystem.NoiseModule noise = dream.noise;
            noise.strength = 0.3f + 2f * brain;
        }

        if (knockRing != null)
        {
            float t = (_time - _ringStart) / 1.1f;
            knockRing.enabled = t >= 0f && t < 1f;
            if (knockRing.enabled)
            {
                knockRing.transform.localScale = new Vector3(_ringScale.x * (0.1f + 0.9f * t), _ringScale.y, _ringScale.z * (0.1f + 0.9f * t));
                knockRing.GetPropertyBlock(_block);
                _block.SetColor(ColourId, new Color(1f, 1f, 1f, 0.8f * (1f - t)));
                knockRing.SetPropertyBlock(_block);
            }
        }
    }

    void LateUpdate()
    {
        if (caption == null) return;
        if (_camera == null || !_camera.isActiveAndEnabled)
            _camera = Camera.main ?? Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled);
        if (_camera == null) return;
        Vector3 away = caption.transform.position - _camera.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.01f) caption.transform.rotation = Quaternion.LookRotation(away);
    }

    /// <summary>A short synthesised note, so the giant has a voice without any imported audio.</summary>
    static AudioClip Tone(string name, float seconds, float hz, float decay, float volume)
    {
        const int rate = 22050;
        int n = Mathf.CeilToInt(seconds * rate);
        var data = new float[n];
        var random = new System.Random(3);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Min(1f, t * 200f);
            float s = Mathf.Sin(2f * Mathf.PI * hz * t) + 0.35f * Mathf.Sin(2f * Mathf.PI * hz * 2.01f * t) + 0.15f * Mathf.Sin(2f * Mathf.PI * hz * 3.02f * t);
            if (hz < 200f) s += 0.3f * ((float)random.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 40f);
            data[i] = Mathf.Clamp(s * env * volume, -1f, 1f);
        }
        AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
