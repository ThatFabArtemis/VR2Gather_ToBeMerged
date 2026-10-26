using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRT.Orchestrator;
using VRT.OrchestratorComm;

/// <summary>
/// Shares each participant's sensor state with the others over VR2Gather's own session
/// event channel - the one that carries hand positions and network triggers - and keeps
/// one table per remote participant, so nothing in the scene can confuse whose signal is
/// whose: a lantern is given a participant, and reads only that participant's table.
///
/// Each participant has a stand in the garden (the lantern on its base). Alone - which is
/// what a standalone session is - the one stand keeps the centre; as soon as someone else
/// is heard from, all the stands share a ring, so a single player and a room of them each
/// get a layout that makes sense.
///
/// Full-rate samples never leave the machine; they go to its capture file and statistics
/// log. What travels is a snapshot a few times a second - every channel's value, range,
/// state, kind, press count and activity envelope - which is what a scene needs to show a
/// remote lantern. The envelope travels because it is the one thing here that cannot be
/// recomputed from what arrives: it is measured over every sample, and a machine seeing
/// two values a second would derive a different one from the same signal.
/// Events go through the master as VR2Gather relays everything else, and remote values
/// are not logged again here: each machine's own log is the record of its own sensors.
/// </summary>
public class SensorShare : MonoBehaviour
{
    public static SensorShare Instance { get; private set; }
    [Tooltip("Seconds between snapshots sent to the other participants. A press is sent at once.")]
    public float interval = 0.5f;
    [Tooltip("Lantern to copy for every remote participant. Leave empty for no remote lanterns.")]
    public CalmLantern lanternTemplate;
    [Tooltip("What is copied and moved for each participant: the lantern's stand, if it has one, else the lantern itself. Alone in the session - a standalone session included - it stays where it was built, the centre; once anyone else's snapshot arrives, everyone's stands share a ring around that spot.")]
    public Transform stand;
    public float remoteRadius = 1.3f;
    [Header("Status, read-only")]
    public int sent, received;
    [Tooltip("No other participant has been heard from: the one stand stays at the centre.")]
    public bool alone = true;

    [Serializable]
    public class ChannelState
    {
        public string key, alias, field, sensor, stream, identity, kind;
        public bool inferred, live;
        public float value, lo, hi, rate;
        /// <summary>How far the signal is swinging from its resting level, in its own units,
        /// measured on the machine that owns it. It travels because it cannot be recovered
        /// from `value`: a remote machine sees two values a second and would compute a
        /// different envelope from the same signal.</summary>
        public float envelope;
        public int presses;
    }
    public class SensorStateMessage : BaseMessage
    {
        public string userName;
        public ChannelState[] channels;
    }
    public class Participant
    {
        public string userId, userName;
        public double received;
        public bool seeded;
        public readonly SensorStream.Table table = new SensorStream.Table();
        public CalmLantern lantern;
        public Transform stand;
    }

    // Clear of the ids VR2Gather's MessageForwarder defines (100-117); the registry refuses a clash.
    const MessageTypeID TypeId = (MessageTypeID)140;
    public readonly Dictionary<string, Participant> Remote = new Dictionary<string, Participant>();
    double _next;
    int _presses;
    Vector3 _home;
    bool _homeKnown;

    /// <summary>The thing that stands for the local participant in the garden: the stand, or the lantern itself.</summary>
    Transform Stand { get { return stand != null ? stand : lanternTemplate == null ? null : lanternTemplate.transform; } }

    /// <summary>The table for a participant: this machine's own for an empty id, else the remote one, or null if unknown.</summary>
    public static SensorStream.Table TableFor(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return SensorStream.Instance == null ? null : SensorStream.Instance.Local;
        return Instance != null && Instance.Remote.TryGetValue(userId, out Participant p) ? p.table : null;
    }
    public static string SelfName { get { return VRTOrchestratorSingleton.Comm?.SelfUser?.userName ?? ""; } }
    public static string SelfId { get { return VRTOrchestratorSingleton.Comm?.SelfUser?.userId ?? "self"; } }

    void Awake()
    {
        Instance = this;
        // Without an orchestrator connection there is nobody to share with; the scene still runs.
        VRTOrchestratorSingleton.Comm?.RegisterEventType(TypeId, typeof(SensorStateMessage));
        if (Stand != null) { _home = Stand.position; _homeKnown = true; }
    }
    void OnEnable()
    {
        IVRTOrchestratorComm comm = VRTOrchestratorSingleton.Comm;
        if (comm == null) return;
        comm.Subscribe<SensorStateMessage>(OnState);
        comm.OnUserLeaveSessionEvent += OnLeft;
    }
    void OnDisable()
    {
        IVRTOrchestratorComm comm = VRTOrchestratorSingleton.Comm;
        if (comm == null) return;
        comm.Unsubscribe<SensorStateMessage>(OnState);
        comm.OnUserLeaveSessionEvent -= OnLeft;
    }
    void OnDestroy() { if (Instance == this) Instance = null; }

    /// <summary>
    /// Send this machine's snapshot when the interval is up - or at once when a press has
    /// happened, because a knock is the one thing in the scene nobody should wait for.
    ///
    /// Everything else is rate-limited on purpose: the session event channel also carries
    /// hand positions and network triggers, and a per-sample sensor feed would crowd it for
    /// no gain. What that costs, and what travels to make up for it, is in the class
    /// comment above.
    /// </summary>
    void Update()
    {
        SensorStream stream = SensorStream.Instance;
        IVRTOrchestratorComm comm = VRTOrchestratorSingleton.Comm;
        if (stream == null || comm == null || comm.SelfUser == null) return;
        int presses = stream.Local.Channels.Sum(c => c.presses);
        if (Time.unscaledTime < _next && presses == _presses) return;
        _next = Time.unscaledTime + Mathf.Max(0.1f, interval);
        _presses = presses;
        var message = new SensorStateMessage
        {
            userName = comm.SelfUser.userName,
            channels = stream.Local.Channels.Select(c => new ChannelState
            {
                key = c.key, alias = c.alias, field = c.field, sensor = c.source.sensor, stream = c.source.stream, identity = c.source.identity,
                kind = c.kind, inferred = c.inferred, live = c.Available && c.Age <= 10,
                value = c.value, lo = c.lo, hi = c.hi, rate = stream.Rate(c.key), presses = c.presses,
                envelope = c.Envelope,
            }).ToArray(),
        };
        if (comm.UserIsMaster) comm.SendTypeEventToAll(message);
        else comm.SendTypeEventToMaster(message);
        sent++;
    }

    /// <summary>
    /// A snapshot arrived. The master relays it to everyone else before applying it, with
    /// `forward: true` so the original sender id survives and every machine attributes the
    /// data to whoever actually owns the sensor rather than to the relay.
    /// </summary>
    void OnState(SensorStateMessage data)
    {
        IVRTOrchestratorComm comm = VRTOrchestratorSingleton.Comm;
        if (comm == null || data.SenderId == comm.SelfUser?.userId) return;
        // forward: true keeps the original SenderId, so everyone attributes the data to its owner.
        if (comm.UserIsMaster) comm.SendTypeEventToAll(data, forward: true);
        Apply(data);
    }

    /// <summary>Put a received snapshot into its participant's table. Public so a loopback test can drive it.</summary>
    public void Apply(SensorStateMessage data)
    {
        if (string.IsNullOrEmpty(data.SenderId)) return;
        if (!Remote.TryGetValue(data.SenderId, out Participant p))
        {
            p = new Participant { userId = data.SenderId, userName = data.userName ?? "" };
            Remote.Add(data.SenderId, p);
            SensorStream.Log("sensors", $"remote={SensorStream.Safe(p.userId)}, name={SensorStream.Safe(p.userName)}");
            if (lanternTemplate != null) p.lantern = SpawnLantern(p);
        }
        p.userName = data.userName ?? p.userName;
        p.received = SensorStream.Now;
        received++;
        foreach (ChannelState s in data.channels ?? new ChannelState[0])
        {
            SensorStream.Source source = p.table.Source(s.sensor ?? "", s.stream ?? "", s.identity ?? "");
            SensorStream.Channel c = p.table.Channel(source, s.field ?? "value", out _);
            source.state = s.live ? "Live" : "Stale";
            c.kind = s.kind ?? "";
            c.inferred = s.inferred;
            c.previous = c.value;
            c.value = s.value;
            c.lo = s.lo;
            c.hi = s.hi;
            c.rate = s.rate;
            // Re-anchored at arrival, so it goes on decaying here at the rate it would
            // there rather than standing still until the next snapshot. The two agree at
            // each snapshot and differ in between by the link's latency; a swing that both
            // begins and ends between two snapshots is seen here only as far as it has
            // already decayed, which is the price of sending a snapshot and not samples.
            c.TakeActivity(s.envelope, SensorStream.Now);
            c.presses = s.presses;
            c.valid = s.live;
            c.received = SensorStream.Now;
        }
        // The first snapshot is this participant's baseline: whatever they had pressed before
        // we heard from them is theirs, not news. Everything after it is a press we were here for.
        if (!p.seeded)
        {
            p.seeded = true;
            if (p.lantern != null) p.lantern.SeedMarkerBaseline(p.table);
        }
    }

    /// <summary>Drop a participant who has left: their table, their stand, and their place in the ring.</summary>
    void OnLeft(string userId)
    {
        if (!Remote.TryGetValue(userId, out Participant p)) return;
        Remote.Remove(userId);
        SensorStream.Log("sensors", $"remote_left={SensorStream.Safe(userId)}");
        if (p.stand != null) Destroy(p.stand.gameObject);
        Arrange();
    }

    /// <summary>
    /// Give a remote participant a stand of their own, cloned from the local one and bound
    /// to their user id, so it reads their table and nobody else's.
    /// </summary>
    CalmLantern SpawnLantern(Participant p)
    {
        Transform template = Stand;
        p.stand = Instantiate(template, template.parent);
        p.stand.name = $"Lantern of {p.userName}";
        CalmLantern copy = p.stand.GetComponentInChildren<CalmLantern>();
        copy.RestFrom(lanternTemplate);
        copy.participant = p.userId;
        // Instantiate copies the template's serialized status too; this lantern's count starts with its owner.
        copy.markers = 0;
        // A name over it, so nobody has to ask whose lantern it is. The template's own label
        // came along with the copy; it says "You", which is wrong for everyone else.
        Transform old = copy.transform.Find("Name");
        if (old != null) Destroy(old.gameObject);
        WeatherSwarm.NameLabel(copy.transform, p.userName.Length > 0 ? p.userName : p.userId, 1.6f);
        Arrange();
        return copy;
    }

    /// <summary>
    /// Alone, the one stand stays where it was built. With company, every stand - the local
    /// one included - takes a place on a ring around that spot, in a stable order, so all
    /// clients agree on who stands where. Public so a test can call it.
    /// </summary>
    public void Arrange()
    {
        Transform mine = Stand;
        if (mine == null) return;
        if (!_homeKnown) { _home = mine.position; _homeKnown = true; }
        var stands = Remote.Values.Where(r => r.stand != null).Select(r => new KeyValuePair<string, Transform>(r.userId, r.stand)).ToList();
        alone = stands.Count == 0;
        if (alone) { mine.position = _home; return; }
        stands.Add(new KeyValuePair<string, Transform>(SelfId, mine));
        stands.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        for (int i = 0; i < stands.Count; i++)
        {
            float a = (i + 0.5f) / stands.Count * Mathf.PI * 2f;
            stands[i].Value.position = _home + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * remoteRadius;
        }
    }
}
