using UnityEngine;
using UnityEngine.SceneManagement;
using VRT.Login;

/// <summary>
/// Makes the sensor demo appear in the scenario list when a session is created.
///
/// Registered at runtime rather than by editing Tool_ScenarioRegistry. VR2Gather's
/// installation guide asks that samples and package prefabs are not modified, so that a
/// package update does not clobber the change; ScenarioRegistry exposes its list, so
/// appending to it costs nothing and leaves the sample untouched.
/// </summary>
public static class SensorScenario
{
    // Created once, as the documentation requires. Do not regenerate: a session advertises
    // this id, and a participant joining with a different one cannot resolve the scenario.
    public const string Id = "6899b37a-8818-48b4-9571-14f2635e4f18";
    public const string SceneName = "AMPlifySensorDemo";
    public const string Name = "AMPlify: The Sleeping Giant";
    public const string Description = "Knock to wake the giant, then calm it down together. Everyone's body sensors make the weather; everything is logged to the session statistics.";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Register();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Register();
    }

    static void Register()
    {
        ScenarioRegistry registry = ScenarioRegistry.Instance;
        if (registry == null || registry.Scenarios == null) return;
        foreach (ScenarioRegistry.ScenarioInfo existing in registry.Scenarios)
        {
            if (existing.scenarioId == Id) return;
        }
        // First in the list: the create dialog preselects entry 0, and a session created
        // with the wrong scenario looks exactly like "the sensors do not work".
        registry.Scenarios.Insert(0, new ScenarioRegistry.ScenarioInfo
        {
            scenarioId = Id,
            scenarioName = Name,
            scenarioDescription = Description,
            scenarioSceneName = SceneName,
        });
        Debug.Log($"SensorScenario: registered \"{Name}\" -> scene {SceneName}, as the default choice");
    }
}
