using UnityEngine;

/// <summary>カメラ変更の検出状態。取得範囲・視野判定はUpdate、投影比LODはUpdateProjection、距離LODはUpdatePositionを使う。</summary>
public struct LodViewState
{
    private bool initialized;
    private Vector3 position;
    private Quaternion rotation;
    private float projection, aspect;
    private int pixels;
    private bool orthographic;

    /// <summary>距離だけで決まる道路LOD用。回転・投影条件では距離が変わらない。</summary>
    public bool UpdatePosition(Camera camera, float moveThreshold = 4f)
    {
        if (camera == null) return false;
        if (initialized && (camera.transform.position-position).sqrMagnitude < moveThreshold*moveThreshold) return false;
        initialized = true;
        position = camera.transform.position;
        return true;
    }

    /// <summary>
    /// 投影比LOD用。位置・FOV(正射影サイズ)・投影方式だけを見て、回転と画面比では変化としない。
    /// 1つのインスタンスでは Update / UpdatePosition / UpdateProjection のどれか1つだけを使うこと。
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
