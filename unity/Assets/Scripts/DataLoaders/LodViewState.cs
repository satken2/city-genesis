using UnityEngine;

/// <summary>Camera change detection state. Use Update for fetch ranges/view checks, UpdateProjection for projected LOD, and UpdatePosition for distance LOD.</summary>
public struct LodViewState
{
    private bool initialized;
    private Vector3 position;
    private Quaternion rotation;
    private float projection, aspect;
    private int pixels;
    private bool orthographic;

    /// <summary>For road LOD based only on distance. Rotation and projection changes do not affect distance.</summary>
    public bool UpdatePosition(Camera camera, float moveThreshold = 4f)
    {
        if (camera == null) return false;
        if (initialized && (camera.transform.position-position).sqrMagnitude < moveThreshold*moveThreshold) return false;
        initialized = true;
        position = camera.transform.position;
        return true;
    }

    /// <summary>
    /// For projected LOD. Check only position, FOV (orthographic size), and projection type; ignore rotation and aspect ratio.
    /// Use only one of Update / UpdatePosition / UpdateProjection per instance.
    /// </summary>
    public bool UpdateProjection(Camera camera, float moveThreshold = 4f)
    {
        if (camera == null) return false;
        float nextProjection = camera.orthographic ? camera.orthographicSize : camera.fieldOfView;
        bool changed = !initialized || (camera.transform.position-position).sqrMagnitude >= moveThreshold*moveThreshold
            || Mathf.Abs(projection-nextProjection) > 0.01f || orthographic != camera.orthographic;
        if (!changed) return false;
        initialized = true;
        position = camera.transform.position;
        projection = nextProjection;
        orthographic = camera.orthographic;
        return true;
    }

    public bool Update(Camera camera, float moveThreshold = 4f)
    {
        if (camera == null) return false;
        float nextProjection = camera.orthographic ? camera.orthographicSize : camera.fieldOfView;
        bool changed = !initialized || (camera.transform.position-position).sqrMagnitude >= moveThreshold*moveThreshold
            || Quaternion.Angle(rotation, camera.transform.rotation) >= 1f
            || Mathf.Abs(projection-nextProjection) > 0.01f || Mathf.Abs(aspect-camera.aspect) > 0.001f
            || pixels != camera.pixelHeight || orthographic != camera.orthographic;
        if (!changed) return false;
        initialized = true;
        position = camera.transform.position;
        rotation = camera.transform.rotation;
        projection = nextProjection;
        aspect = camera.aspect;
        pixels = camera.pixelHeight;
        orthographic = camera.orthographic;
        return true;
    }
}
