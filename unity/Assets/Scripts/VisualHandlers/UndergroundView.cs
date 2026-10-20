using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Add underground background and waterfront guidance over the existing terrain-transparency mode.</summary>
public sealed class UndergroundView : MonoBehaviour
{
    public static bool IsActive { get; private set; }
    public static event Action Changed;
    private static UndergroundView instance;
    private Camera viewCamera;
    private CameraClearFlags savedClearFlags;
    private Color savedBackground;
    private GameObject soil, shores;
    private Mesh soilMesh, shoreMesh;
    private Material soilMaterial, shoreMaterial;
    private int shoreVersion = -1;

    public static void Ensure()
    {
        if (instance == null) new GameObject("UndergroundView").AddComponent<UndergroundView>();
    }

    private void Awake() => instance = this;

    private void Start()
    {
        // The sea surface is scene-placed. Enforce the same display contract as lakes/rivers regardless of scene setup.
        foreach (var sea in GameObject.FindGameObjectsWithTag("SeaObject"))
            if (sea.GetComponent<Renderer>() != null && sea.GetComponent<VisualSeaObject>() == null) sea.AddComponent<VisualSeaObject>();
    }

    public static void SetActive(bool active)
    {
        Ensure();
        if (IsActive == active) return;
        IsActive = active;
        if (!active) instance.RestoreCamera();
        if (instance.soil != null) instance.soil.SetActive(active);
        if (instance.shores != null) instance.shores.SetActive(active);
        Changed?.Invoke();
    }

    private void LateUpdate()
    {
        bool visible = IsActive && (GameStateManager.Instance == null || GameStateManager.Instance.IsWorldVisible);
        if (!visible)
        {
            RestoreCamera();
            if (soil != null) soil.SetActive(false);
            if (shores != null) shores.SetActive(false);
            return;
        }
        Camera camera = Camera.main;
        if (camera == null) return;
        if (viewCamera != camera)
        {
            RestoreCamera();
            viewCamera = camera;
            savedClearFlags = camera.clearFlags;
            savedBackground = camera.backgroundColor;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.16f, .105f, .065f);
        }
        if (soil == null)
        {
            soilMaterial = new Material(Resources.Load<Shader>("Shaders/UndergroundSoil"));
            soilMesh = new Mesh { name = "UndergroundBackground" };
            soilMesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(-1,1,0), new Vector3(1,1,0), new Vector3(1,-1,0) };
            soilMesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            soilMesh.triangles = new[] { 0,1,2,0,2,3 };
            soilMesh.RecalculateBounds();
            soil = CreateMeshObject("SoilBackground", soilMesh, soilMaterial);
        }
        soil.SetActive(true);
        // Background is a plane just in front of the camera. Without depth writes, underground facilities render in front regardless of elevation.
        float distance = camera.nearClipPlane + .05f;
        float halfHeight = camera.orthographic ? camera.orthographicSize : Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f) * distance;
        soil.transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * distance, camera.transform.rotation);
        soil.transform.localScale = new Vector3(halfHeight * camera.aspect, halfHeight, 1);
        if (shoreVersion != TerrainManager.ShoreDataVersion || shoreMesh == null) RebuildShores();
        shores?.SetActive(true);
    }

    private GameObject CreateMeshObject(string name, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go;
    }

    private void RebuildShores()
    {
        shoreVersion = TerrainManager.ShoreDataVersion;
        var vertices = new List<Vector3>();
        var uv = new List<Vector2>();
        var triangles = new List<int>();
        foreach (var ring in CoastlineStore.Rings)
            AddShore(ring, p => Mathf.Max(0, Utils.GetGroundHeight(new Vector3(p.x, 0, p.y))), vertices, uv, triangles);
        foreach (var lake in WaterSurfaceStore.Surfaces)
            foreach (var ring in lake.Rings) AddShore(ring, p => lake.Level, vertices, uv, triangles);
        foreach (var river in RiverStore.Rivers)
            foreach (var ring in river.Rings) AddShore(ring, p => river.WaterLevelAt(p.x, p.y), vertices, uv, triangles);
        if (shoreMesh != null) Destroy(shoreMesh);
        shoreMesh = new Mesh { name = "UndergroundShorelines", indexFormat = IndexFormat.UInt32 };
        shoreMesh.SetVertices(vertices);
        shoreMesh.SetUVs(0, uv);
        shoreMesh.SetTriangles(triangles, 0);
        shoreMesh.RecalculateBounds();
        if (shores == null)
        {
            shoreMaterial = new Material(Resources.Load<Shader>("Shaders/UndergroundShore"));
            shores = CreateMeshObject("Shorelines", shoreMesh, shoreMaterial);
        }
        else shores.GetComponent<MeshFilter>().sharedMesh = shoreMesh;
    }

    private static void AddShore(IReadOnlyList<Vector2> ring, Func<Vector2, float> height,
        List<Vector3> vertices, List<Vector2> uv, List<int> triangles)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            Vector2 a = ring[i], b = ring[(i + 1) % ring.Count];
            Vector2 delta = b - a;
            if (delta.sqrMagnitude < .001f) continue;
            Vector2 side = new Vector2(-delta.y, delta.x).normalized * 2f;
            int start = vertices.Count;
            float ay = height(a) + .15f, by = height(b) + .15f;
            vertices.Add(new Vector3(a.x-side.x, ay, a.y-side.y));
            vertices.Add(new Vector3(a.x+side.x, ay, a.y+side.y));
            vertices.Add(new Vector3(b.x+side.x, by, b.y+side.y));
            vertices.Add(new Vector3(b.x-side.x, by, b.y-side.y));
            uv.Add(new Vector2(0,0)); uv.Add(new Vector2(0,1)); uv.Add(new Vector2(delta.magnitude,1)); uv.Add(new Vector2(delta.magnitude,0));
            triangles.Add(start); triangles.Add(start+1); triangles.Add(start+2);
            triangles.Add(start); triangles.Add(start+2); triangles.Add(start+3);
        }
    }

    private void RestoreCamera()
    {
        if (viewCamera == null) return;
        viewCamera.clearFlags = savedClearFlags;
        viewCamera.backgroundColor = savedBackground;
        viewCamera = null;
    }

    private void OnDestroy()
    {
        RestoreCamera();
        Destroy(soilMesh); Destroy(shoreMesh); Destroy(soilMaterial); Destroy(shoreMaterial);
        if (instance != this) return;
        IsActive = false;
        Changed?.Invoke();
        instance = null;
        UndergroundMaterials.Clear();
    }
}
