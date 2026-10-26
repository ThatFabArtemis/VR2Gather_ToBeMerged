using UnityEngine;

/// <summary>
/// One participant's body weather, made visible: a swarm of fireflies in that person's own
/// colour, circling their lantern. It sits beside the CalmLantern on the same object and
/// reads its normalised values, so the swarm and the giant can never disagree about a
/// person's state.
///
///   arousal  -> the swarm warms and gathers close
///   movement -> the swarm scatters and speeds up (the wind that person is making)
///   press    -> the swarm bursts outward, the knock leaving them
///
/// The colour comes from the VR2Gather user id, so every client paints a given person the
/// same colour without anyone agreeing on it. Demonstration, not measurement.
/// </summary>
public class WeatherSwarm : MonoBehaviour
{
    [Tooltip("The swarm's particle system, a child of this object, set in the scene.")]
    public ParticleSystem swarm;
    public float restingRate = 18f, extraRate = 50f;
    [Header("Status, read-only")]
    public Color colour = Color.white;
    public string owner = "";

    static readonly Color[] Palette =
    {
        new Color(0.45f, 0.85f, 1f), new Color(1f, 0.75f, 0.35f), new Color(0.75f, 0.6f, 1f), new Color(0.55f, 0.95f, 0.6f),
        new Color(1f, 0.55f, 0.65f), new Color(1f, 0.95f, 0.45f), new Color(0.5f, 0.95f, 0.9f), new Color(1f, 0.65f, 0.95f),
    };

    CalmLantern _lantern;
    int _markers;
    string _colouredFor = null;

    /// <summary>The colour every client gives this participant: a stable function of the user id.</summary>
    public static Color ColourFor(string userId)
    {
        int hash = 17;
        foreach (char c in userId ?? "") hash = hash * 31 + c;
        return Palette[(hash & 0x7fffffff) % Palette.Length];
    }

    void Update()
    {
        if (_lantern == null) _lantern = GetComponent<CalmLantern>();
        if (_lantern == null || swarm == null) return;
        owner = _lantern.participant.Length == 0 ? SensorShare.SelfId : _lantern.participant;
        if (_colouredFor != owner)
        {
            _colouredFor = owner;
            colour = ColourFor(owner);
            _lantern.calmColour = colour;
            _markers = _lantern.markers;
        }
        float arousal = _lantern.arousal, movement = _lantern.movement;
        ParticleSystem.MainModule main = swarm.main;
        main.startColor = new ParticleSystem.MinMaxGradient(colour, Color.Lerp(colour, new Color(1f, 0.6f, 0.3f), arousal));
        main.startSpeed = 0.1f + 1.8f * movement;
        ParticleSystem.EmissionModule emission = swarm.emission;
        emission.rateOverTime = restingRate + extraRate * Mathf.Clamp01(arousal + movement);
        ParticleSystem.ShapeModule shape = swarm.shape;
        shape.radius = Mathf.Lerp(0.9f, 0.35f, arousal) + 0.6f * movement;
        ParticleSystem.NoiseModule noise = swarm.noise;
        noise.strength = 0.3f + 1.5f * movement;
        if (_lantern.markers != _markers)
        {
            _markers = _lantern.markers;
            swarm.Emit(45);
        }
    }

    /// <summary>A floating name over an object, readable from anywhere: "You", or the participant's name.</summary>
    public static TextMesh NameLabel(Transform parent, string text, float height)
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var label = new GameObject("Name").AddComponent<TextMesh>();
        label.font = font;
        label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        label.text = text;
        label.fontSize = 64;
        label.characterSize = 0.03f / Mathf.Max(0.01f, parent.lossyScale.y);
        label.anchor = TextAnchor.LowerCenter;
        label.alignment = TextAlignment.Center;
        label.transform.SetParent(parent, false);
        label.transform.localPosition = Vector3.up * height;
        label.gameObject.AddComponent<FaceViewer>();
        return label;
    }
}
