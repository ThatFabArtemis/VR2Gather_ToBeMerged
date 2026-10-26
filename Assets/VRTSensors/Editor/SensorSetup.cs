using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds the sensor rig to whatever scene is open, for trying it somewhere other than the
/// demo scene.
/// </summary>
public static class SensorSetup
{
    const string RigName = "SensorRig";

    [MenuItem("VR2Gather/Sensors/Add sensor rig to open scene")]
    public static void AddSensorRig()
    {
        GameObject existing = GameObject.Find(RigName);
        if (existing != null)
        {
            Selection.activeGameObject = existing;
            EditorGUIUtility.PingObject(existing);
            Debug.LogWarning($"{RigName} is already in this scene; selected it instead of adding a second one.");
            return;
        }

        // No SensorStream here: SensorLogger, created at application start, listens in
        // every scene and the lantern reads SensorStream.Instance.
        var rig = new GameObject(RigName);
        rig.AddComponent<SensorBoard>();

        GameObject lantern = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        lantern.name = "CalmLantern";
        lantern.transform.SetParent(rig.transform);
        SceneView view = SceneView.lastActiveSceneView;
        lantern.transform.position = view != null && view.camera != null
            ? view.camera.transform.position + view.camera.transform.forward * 3f
            : new Vector3(0f, 1.2f, 1.5f);
        lantern.transform.localScale = Vector3.one * 0.35f;
        Object.DestroyImmediate(lantern.GetComponent<Collider>());
        lantern.AddComponent<CalmLantern>();

        Undo.RegisterCreatedObjectUndo(rig, "Add sensor rig");
        Selection.activeGameObject = lantern;
        EditorGUIUtility.PingObject(lantern);
        Debug.Log($"{RigName} added. Press Play: SensorCaptures starts what sensors.json lists (.\\sensors.ps1 picks one); by hand it is VRTsensors-bitalino --address COMn --channel A1=acc --udp 127.0.0.1:<SensorConfig.udpPort>");
    }

    [MenuItem("VR2Gather/Sensors/Make selected object the lantern")]
    public static void MakeSelectionTheLantern()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null) return;

        CalmLantern existing = go.GetComponent<CalmLantern>();
        if (existing != null)
        {
            Debug.LogWarning($"'{go.name}' already has a CalmLantern.");
            EditorGUIUtility.PingObject(existing);
            return;
        }

        Undo.AddComponent<CalmLantern>(go);
        int renderers = go.GetComponentsInChildren<Renderer>().Length;
        int lights = go.GetComponentsInChildren<Light>().Length;
        Debug.Log($"CalmLantern added to '{go.name}': {renderers} renderer(s), {lights} light(s) will be driven. " +
                  (renderers == 0 && lights == 0
                      ? "Neither found - it will still move, but nothing will change colour."
                      : "Its authored scale and rotation are preserved."));

    }

    [MenuItem("VR2Gather/Sensors/Make selected object the lantern", true)]
    static bool MakeSelectionTheLanternValidate()
    {
        return Selection.activeGameObject != null;
    }
}
