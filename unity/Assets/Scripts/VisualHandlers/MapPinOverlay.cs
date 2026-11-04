using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 3D markers for pins placed on the 2D city map, used to plan routes while comparing the map with the 3D view.
/// Each pin is an inverted square pyramid whose tip touches the visible surface (the water surface over water) at the pin.
///
/// This class holds the session's pin list. The city map replaces the whole list with world:set_map_pins and reads it back
/// with world:query_map_pins when it opens, since closing the map must not remove the pins. Pins are a planning aid rather
/// than part of the city record, so they are not saved and are cleared on session:reset and game:end.
///
/// A detailed terrain tile can change the surface height under a pin, so re-ground every pin when a tile becomes ready.
/// </summary>
public sealed class MapPinOverlay : MonoBehaviour
{
    public static MapPinOverlay Instance { get; private set; }

    private const float PinHeight = 50f;      // Tip to base (m).
    private const float PinHalfWidth = 12.5f; // Half the side of the square base (m).

    public struct Pin { public float x, z; public string color; }

    private readonly List<Pin> pins = new List<Pin>();
    private readonly List<GameObject> markers = new List<GameObject>();
    private readonly List<Material> materials = new List<Material>();
    private Mesh mesh;
    private Shader shader;

    public IReadOnlyList<Pin> Pins => pins;

    public static MapPinOverlay Ensure()
    {
        if (Instance == null) new GameObject("MapPinOverlay").AddComponent<MapPinOverlay>();
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        mesh = BuildMesh();
        shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        TerrainManager.OnTerrainTileReady += OnTerrainTileReady;
        var ipc = IpcClient.Instance;
        if (ipc != null) { ipc.OnSessionReset += Clear; ipc.OnGameEnd += Clear; }
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        TerrainManager.OnTerrainTileReady -= OnTerrainTileReady;
        var ipc = IpcClient.Instance;
        if (ipc != null) { ipc.OnSessionReset -= Clear; ipc.OnGameEnd -= Clear; }
        Clear();
        Destroy(mesh);
        Instance = null;
    }

    public void SetPins(IReadOnlyList<Pin> next)
    {
        Clear();
        foreach (var pin in next)
        {
            var marker = new GameObject("MapPin");
            marker.transform.SetParent(transform, false);
            marker.transform.position = SurfacePoint(pin);
            marker.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(shader) { color = RoadManager.ParseColor(pin.color) };
            var renderer = marker.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            pins.Add(pin);
            markers.Add(marker);
            materials.Add(material);
        }
    }

    public void Clear()
    {
        foreach (var marker in markers) Destroy(marker);
        foreach (var material in materials) Destroy(material);
        pins.Clear();
        markers.Clear();
        materials.Clear();
    }

    private void OnTerrainTileReady(Vector2Int cellIndex, Terrain terrain)
    {
        for (int i = 0; i < pins.Count; i++) markers[i].transform.position = SurfacePoint(pins[i]);
    }

    private static Vector3 SurfacePoint(Pin pin)
    {
        return new Vector3(pin.x, Utils.GetPathSurfaceHeight(new Vector3(pin.x, 1000f, pin.z)), pin.z);
    }

    /// <summary>Inverted square pyramid with its tip at the origin. Faces have separate vertices for flat shading.</summary>
    private static Mesh BuildMesh()
    {
        var tip = Vector3.zero;
        var corners = new[]
        {
            new Vector3(-PinHalfWidth, PinHeight, -PinHalfWidth),
            new Vector3(PinHalfWidth, PinHeight, -PinHalfWidth),
            new Vector3(PinHalfWidth, PinHeight, PinHalfWidth),
            new Vector3(-PinHalfWidth, PinHeight, PinHalfWidth),
        };
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Cross(b - a, c - a).normalized;
            foreach (var v in new[] { a, b, c })
            {
                triangles.Add(vertices.Count);
                vertices.Add(v);
                normals.Add(normal);
            }
        }
        for (int i = 0; i < 4; i++) AddTriangle(corners[i], corners[(i + 1) % 4], tip);
        AddTriangle(corners[0], corners[3], corners[2]);
        AddTriangle(corners[0], corners[2], corners[1]);

        var result = new Mesh { name = "MapPin" };
        result.SetVertices(vertices);
        result.SetNormals(normals);
        result.SetTriangles(triangles, 0);
        result.RecalculateBounds();
        return result;
    }
}
