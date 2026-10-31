using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Box of one tunnel mouth whose terrain is not drawn (Assets/Shaders/TerrainTunnelCut.hlsl), so the tunnel can be seen into.
/// Registered only while its portal object is active, so parked LOD copies and destroyed paths drop out with their objects.
/// </summary>
public sealed class TunnelCutVolume : MonoBehaviour
{
    public const int MaxCount = 32; // Matches TUNNEL_CUT_MAX in TerrainTunnelCut.hlsl.

    private static readonly int BoxesId = Shader.PropertyToID("_TunnelCutWorldToBox");
    private static readonly int SpheresId = Shader.PropertyToID("_TunnelCutSpheres");
    private static readonly int CountId = Shader.PropertyToID("_TunnelCutCount");
    private static readonly HashSet<TunnelCutVolume> Active = new();
    private static readonly List<TunnelCutVolume> Ordered = new();
    private static readonly Matrix4x4[] Boxes = new Matrix4x4[MaxCount];
    private static readonly Vector4[] Spheres = new Vector4[MaxCount];

    private Matrix4x4 worldToBox;
    private Vector3 center;
    private float radiusSq;
    private bool configured;

    /// <summary>worldToBox maps world positions inside the box to [-1, 1] on every axis.</summary>
    public void Configure(Matrix4x4 worldToBox, Vector3 center, float radius)
    {
        this.worldToBox = worldToBox;
        this.center = center;
        radiusSq = radius * radius;
        configured = true;
        if (isActiveAndEnabled) Register();
    }

    private void OnEnable()
    {
        if (configured) Register();
    }

    private void OnDisable()
    {
        if (Active.Remove(this)) Upload();
    }

    private void Register()
    {
        Active.Add(this);
        Upload();
    }

    /// <summary>The shader array has a fixed length; past it, the mouths nearest the camera keep their cut.</summary>
    private static void Upload()
    {
        Ordered.Clear();
        Ordered.AddRange(Active);
        if (Ordered.Count > MaxCount)
        {
            Vector3 eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            Ordered.Sort((a, b) => (a.center - eye).sqrMagnitude.CompareTo((b.center - eye).sqrMagnitude));
        }
        int count = Mathf.Min(Ordered.Count, MaxCount);
        for (int i = 0; i < count; i++)
        {
            Boxes[i] = Ordered[i].worldToBox;
            Spheres[i] = new Vector4(Ordered[i].center.x, Ordered[i].center.y, Ordered[i].center.z, Ordered[i].radiusSq);
        }
        Shader.SetGlobalMatrixArray(BoxesId, Boxes);
        Shader.SetGlobalVectorArray(SpheresId, Spheres);
        Shader.SetGlobalInteger(CountId, count);
    }
}
