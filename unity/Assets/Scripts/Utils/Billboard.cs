using UnityEngine;

/// <summary>
/// Makes an object always face the camera.
/// Used for icons, text, and similar objects that should face the viewer.
/// </summary>
public class Billboard : MonoBehaviour
{
    private Camera mainCamera;

    private void Start()
    {
        CacheMainCamera();
    }

    private void CacheMainCamera()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            // Search with FindObjectsByType instead of Camera.main.
            // Fallback for scenes containing multiple Camera-tagged objects
            // or dynamically created cameras.
            var cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude);
            foreach (var cam in cameras)
            {
                if (cam.CompareTag("MainCamera"))
                {
                    mainCamera = cam;
                    break;
                }
            }
        }
        if (mainCamera == null)
        {
            Debug.LogWarning("Billboard: メインカメラが見つかりません。");
        }
    }

    private void LateUpdate()
    {
        // Accessing Camera.main every frame performs an internal hash-table lookup,
        // so reuse the reference cached in Start.
        // Search again only if the camera is dynamically destroyed and recreated.
        if (mainCamera == null)
        {
            CacheMainCamera();
            if (mainCamera == null) return;
        }

        // Face the camera.
        transform.rotation = mainCamera.transform.rotation;
    }
}

