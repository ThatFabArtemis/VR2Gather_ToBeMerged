using UnityEngine;

/// <summary>Turns its object to face whoever is looking, around the vertical axis. For labels.</summary>
public class FaceViewer : MonoBehaviour
{
    Camera _camera;
    void LateUpdate()
    {
        if (_camera == null || !_camera.isActiveAndEnabled) _camera = Camera.main ?? System.Linq.Enumerable.FirstOrDefault(Camera.allCameras, c => c.isActiveAndEnabled);
        if (_camera == null) return;
        Vector3 away = transform.position - _camera.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(away);
    }
}
