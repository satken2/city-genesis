using UnityEngine;

/// <summary>取得範囲・描画LODが同じカメラ変更条件を使うための状態。</summary>
public struct LodViewState
{
    private bool initialized;
    private Vector3 position;
    private Quaternion rotation;
    private float projection, aspect;
    private int pixels;
    private bool orthographic;

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
