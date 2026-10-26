using System.Linq;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Lets a thing in the scene be moved: point at it with a controller and hold the trigger,
/// and it follows the hand until the trigger is released - the way the XR toolkit moves
/// anything, without a rigidbody, so whatever turns the object to face the viewer keeps
/// doing so. For a desktop test, or when the board is simply too far, B on the keyboard
/// brings it to arm's length in front of you and B again sends it home.
///
/// The controller ray in VR2Gather's player selects on the Default interaction layer,
/// which is what a fresh interactable listens on. The handsfree desktop ray only
/// teleports, hence the key.
/// </summary>
public class GrabToMove : MonoBehaviour
{
    [Tooltip("The box a ray can hit, in local metres, centred on this object.")]
    public Vector3 size = new Vector3(1f, 1f, 0.1f);
    [Tooltip("Metres in front of the viewer that B brings the object to.")]
    public float bringTo = 1.5f;
    [Header("Status, read-only")]
    public bool held, brought;

    XRSimpleInteractable _grab;
    IXRSelectInteractor _holder;
    Vector3 _offset, _home;
    Camera _camera;

    void Start()
    {
        if (_grab != null) return;
        _home = transform.position;
        BoxCollider box = GetComponent<BoxCollider>();
        if (box == null) box = gameObject.AddComponent<BoxCollider>();
        box.size = size;
        _grab = GetComponent<XRSimpleInteractable>();
        if (_grab == null) _grab = gameObject.AddComponent<XRSimpleInteractable>();
        _grab.selectEntered.AddListener(OnGrab);
        _grab.selectExited.AddListener(OnRelease);
    }

    void OnGrab(SelectEnterEventArgs args)
    {
        _holder = args.interactorObject;
        // Where the object sits relative to the hand at the moment of grabbing; it keeps
        // that relation, so turning the hand swings it like anything held at a distance.
        _offset = _holder.GetAttachTransform(_grab).InverseTransformPoint(transform.position);
        held = true;
    }

    void OnRelease(SelectExitEventArgs args)
    {
        if (args.interactorObject != _holder) return;
        _holder = null;
        held = false;
    }

    void Update()
    {
        if (_holder != null) transform.position = _holder.GetAttachTransform(_grab).TransformPoint(_offset);
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.bKey.wasPressedThisFrame) { if (brought) GoHome(); else BringToMe(); }
#endif
    }

    /// <summary>Put the object at arm's length in front of the viewer, at their eye height.</summary>
    public void BringToMe()
    {
        if (_camera == null || !_camera.isActiveAndEnabled)
            _camera = Camera.main ?? Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled && c.stereoEnabled) ?? Camera.allCameras.FirstOrDefault(c => c.isActiveAndEnabled);
        if (_camera == null) return;
        Vector3 forward = _camera.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        transform.position = _camera.transform.position + forward.normalized * bringTo;
        brought = true;
    }

    public void GoHome()
    {
        transform.position = _home;
        brought = false;
    }
}
