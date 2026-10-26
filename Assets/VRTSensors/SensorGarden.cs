using UnityEngine;

/// <summary>
/// Makes the garden around the lantern respond too, so the sensor state is something a
/// child can see everywhere they look, not only in one object:
///
///   calm        -> flowers open, sky stays clear
///   arousal     -> fireflies gather, sky warms
///   movement    -> the pond stirs, fireflies scatter
///   marker      -> a ripple crosses the pond and every flower pops
///   night       -> the sun goes down, the sky darkens, the fireflies come out (a covered light sensor)
///   cold        -> snow falls and the grass frosts over (cool skin)
///
/// Reads the lantern's already-normalised values rather than the raw channels, so the
/// two never disagree about what "calm" means; night and cold are the giant's, since they
/// belong to the whole garden rather than to one person. Like the lantern, this is a
/// demonstration and not a measurement: nothing here is in the measurement contract.
/// </summary>
public class SensorGarden : MonoBehaviour
{
    public CalmLantern lantern;
    public SleepingGiant giant;
    public ParticleSystem fireflies, snow;
    public Transform[] flowerHeads;
    public Transform pond;
    public Renderer ripple, grass;
    public Light sun;

    [Header("Ranges")]
    public float restingFireflies = 10f;
    public float extraFireflies = 45f;
    public float rippleSeconds = 1.6f;
    public Color calmSky = new Color(0.55f, 0.76f, 0.96f);
    public Color arousedSky = new Color(0.98f, 0.72f, 0.55f);

    [Header("Status, read-only")]
    public float night, cold;

    Material _sky;
    Vector3[] _flowerScale;
    Vector3 _pondScale;
    Vector3 _rippleScale;
    MaterialPropertyBlock _block;
    int _markers;
    float _rippleStart = -100f;
    float _pop, _sunIntensity, _ambient;
    Color _fog, _grassColour;
    static readonly int ColourId = Shader.PropertyToID("_Color");

    void Start()
    {
        // A scene built before sharing existed still shares: the rig grows the component.
        if (lantern != null && GetComponent<SensorShare>() == null) gameObject.AddComponent<SensorShare>().lanternTemplate = lantern;
        if (giant == null) giant = FindFirstObjectByType<SleepingGiant>();
        if (sun == null) sun = RenderSettings.sun;
        // A copy, so driving the tint never edits the saved skybox asset.
        if (RenderSettings.skybox != null)
        {
            _sky = new Material(RenderSettings.skybox);
            RenderSettings.skybox = _sky;
        }
        if (flowerHeads == null) flowerHeads = new Transform[0];
        _flowerScale = new Vector3[flowerHeads.Length];
        for (int i = 0; i < flowerHeads.Length; i++) _flowerScale[i] = flowerHeads[i].localScale;
        if (pond != null) _pondScale = pond.localScale;
        if (ripple != null) _rippleScale = ripple.transform.localScale;
        if (sun != null) _sunIntensity = sun.intensity;
        if (grass != null && grass.sharedMaterial != null) _grassColour = grass.sharedMaterial.color;
        _ambient = RenderSettings.ambientIntensity;
        _fog = RenderSettings.fogColor;
        _block = new MaterialPropertyBlock();
    }

    void Update()
    {
        float arousal = lantern != null ? lantern.arousal : 0f;
        float movement = lantern != null ? lantern.movement : 0f;
        night = giant != null ? giant.night : lantern != null ? lantern.night : 0f;
        cold = giant != null ? giant.cold : lantern != null ? lantern.cold : 0f;

        if (lantern != null && lantern.markers != _markers)
        {
            _markers = lantern.markers;
            _rippleStart = Time.time;
            _pop = 1f;
        }
        _pop = Mathf.MoveTowards(_pop, 0f, Time.deltaTime * 1.5f);

        float bloom = 0.55f + 0.45f * (1f - arousal) + 0.5f * _pop;
        for (int i = 0; i < flowerHeads.Length; i++)
        {
            if (flowerHeads[i] == null) continue;
            // Each flower sways on its own phase so the ring does not move as one block.
            float sway = 1f + 0.04f * Mathf.Sin(Time.time * 1.3f + i * 0.7f);
            flowerHeads[i].localScale = _flowerScale[i] * bloom * sway;
        }

        if (fireflies != null)
        {
            ParticleSystem.EmissionModule emission = fireflies.emission;
            emission.rateOverTime = restingFireflies + extraFireflies * Mathf.Clamp01(arousal + movement) + 40f * night;
            ParticleSystem.MainModule main = fireflies.main;
            main.startColor = Color.Lerp(new Color(0.85f, 0.95f, 0.55f), new Color(1f, 0.70f, 0.35f), arousal);
            main.startSpeed = 0.15f + 0.8f * movement;
        }

        if (pond != null)
        {
            float stir = 1f + 0.03f * movement * Mathf.Sin(Time.time * 9f);
            pond.localScale = new Vector3(_pondScale.x * stir, _pondScale.y, _pondScale.z / stir);
        }

        if (ripple != null)
        {
            float t = (Time.time - _rippleStart) / rippleSeconds;
            ripple.enabled = t >= 0f && t < 1f;
            if (ripple.enabled)
            {
                ripple.transform.localScale = new Vector3(_rippleScale.x * (0.15f + 0.85f * t), _rippleScale.y, _rippleScale.z * (0.15f + 0.85f * t));
                ripple.GetPropertyBlock(_block);
                _block.SetColor(ColourId, new Color(1f, 1f, 1f, 0.7f * (1f - t)));
                ripple.SetPropertyBlock(_block);
            }
        }

        // Night: the sun sets, the sky and the fog darken.
        if (_sky != null) _sky.SetColor("_SkyTint", Color.Lerp(Color.Lerp(calmSky, arousedSky, arousal), GardenPalette.NightSky, night));
        if (sun != null) sun.intensity = _sunIntensity * (1f - 0.85f * night);
        RenderSettings.ambientIntensity = _ambient * (1f - 0.6f * night);
        RenderSettings.fogColor = Color.Lerp(_fog, GardenPalette.NightSky, night);

        // Cold: snow, and the grass frosts over.
        if (snow != null)
        {
            ParticleSystem.EmissionModule emission = snow.emission;
            emission.rateOverTime = 80f * cold;
        }
        if (grass != null)
        {
            grass.GetPropertyBlock(_block);
            _block.SetColor(ColourId, Color.Lerp(_grassColour, GardenPalette.Frost, cold));
            grass.SetPropertyBlock(_block);
        }
    }
}
