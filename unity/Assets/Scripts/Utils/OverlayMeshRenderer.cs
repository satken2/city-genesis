using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared overlay renderer building meshes directly in 3D space.
/// Uses a dedicated material with ZTest Always / ZWrite Off / Queue Transparent+100
/// for guide lines, handles, and outlines always visible in front of terrain and buildings.
///
/// All inputs are world coordinates, without Canvas screen-coordinate conversion.
/// This eliminates per-frame CPU and GC cost from
/// WorldToScreenPoint calls and RectTransform updates.
///
/// Specify line widths and circle/square sizes in screen pixels;
/// internally convert to world thickness accounting for camera distance.
/// </summary>
public class OverlayMeshRenderer : MonoBehaviour
{
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh;

    private List<Vector3> verts = new List<Vector3>();
    private List<Color> colors = new List<Color>();
    private List<int> indices = new List<int>();
    private bool sharedBillboardMaterial;
    private static readonly Material[] billboardMaterials = new Material[2];
    private static int billboardUsers;

    private struct LineEntry
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        public float pixelThickness;
    }

    private struct DashedEntry
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        public float pixelThickness;
        public float dashLen;
        public float gapLen;
    }

    /// <summary>
    /// Dashed-line entry with fixed world-space width and spacing.
    /// Specified in meters rather than screen-space pixels.
    /// </summary>
    private struct WorldDashedEntry
    {
        public Vector3 a;
        public Vector3 b;
        public Color color;
        public float worldThickness;
        public float worldDashLen;
        public float worldGapLen;
        public Vector3 upVector;
    }


    private struct CircleEntry
    {
        public Vector3 pos;
        public Color color;
        public float pixelRadius;
    }

    private struct SquareEntry
    {
        public Vector3 pos;
        public Color color;
        public float pixelHalfSize;
    }

    private struct PolygonEntry
    {
        public List<Vector3> points;
        public Color color;
    }

    private struct PreTriangulatedEntry
    {
        public List<Vector3> verts;
        public Color color;
        /// <summary>Per-vertex colors. If null, apply color to every vertex.</summary>
        public List<Color> vertexColors;
    }

    private List<LineEntry> frameLines = new List<LineEntry>();
    private List<DashedEntry> frameDashed = new List<DashedEntry>();
    private List<WorldDashedEntry> frameWorldDashed = new List<WorldDashedEntry>();
    private List<CircleEntry> frameCircles = new List<CircleEntry>();
    private List<SquareEntry> frameSquares = new List<SquareEntry>();
    private List<PolygonEntry> framePolygons = new List<PolygonEntry>();
    private List<PreTriangulatedEntry> framePreTriangulated = new List<PreTriangulatedEntry>();

    /// <summary>OverlayLine shader reference. Shader.Find searches by name, so resolve once and reuse.</summary>
    private static Shader overlayLineShader;

    /// <summary>
    /// Create an overlay GameObject initialized with a dedicated OverlayLine material.
    /// Vertices are world-space, so keep the object at the world origin even with a parent.
    /// alwaysOnTop=true disables depth testing (_ZTest=Always), keeping overlays visible over terrain and buildings
    /// (an X-ray-like view through hills or foreground buildings is acceptable for gizmos/highlights).
    /// false retains the shader's default LEqual with polygon offset, letting buildings naturally occlude fills.
    /// persistent=true survives scene transitions through DontDestroyOnLoad.
    /// Callers configure settings not exposed as arguments, such as render queue or additive blending, on the returned <see cref="Material"/>.
    /// </summary>
    public static OverlayMeshRenderer Create(string name, bool alwaysOnTop, Transform parent = null, bool persistent = false)
    {
        var renderer = CreateObject(name, parent, persistent);
        renderer.Initialize(CreateOverlayLineMaterial(alwaysOnTop));
        return renderer;
    }

    /// <summary>
    /// Batch fixed-position lines and circles that face the camera on the GPU. Share materials and avoid CPU vertex updates during camera movement.
    /// Call EndStaticFrame after AddSolidLine / AddCircle to finalize. Do not modify shared material settings.
    /// </summary>
    public static OverlayMeshRenderer CreateStatic(string name, bool alwaysOnTop, Transform parent = null)
    {
        int index = alwaysOnTop ? 1 : 0;
        if (billboardMaterials[index] == null)
        {
            billboardMaterials[index] = CreateOverlayLineMaterial(alwaysOnTop);
            billboardMaterials[index].EnableKeyword("OVERLAY_GPU_BILLBOARD");
        }
        var renderer = CreateObject(name, parent, false);
        renderer.sharedBillboardMaterial = true;
        billboardUsers++;
        renderer.Initialize(billboardMaterials[index]);
        return renderer;
    }

    private static OverlayMeshRenderer CreateObject(string name, Transform parent, bool persistent)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, true);
        if (persistent) DontDestroyOnLoad(go);
        return go.AddComponent<OverlayMeshRenderer>();
    }

    /// <summary>Create an OverlayLine material. Depth testing follows <see cref="Create"/>.</summary>
    public static Material CreateOverlayLineMaterial(bool alwaysOnTop)
    {
        if (overlayLineShader == null) overlayLineShader = Shader.Find("Custom/OverlayLine");
        var mat = new Material(overlayLineShader);
        if (alwaysOnTop) mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        return mat;
    }

    /// <summary>
    /// Rendering material: exclusive for Create, shared for CreateStatic. Do not modify shared settings.
    /// </summary>
    public Material Material => meshRenderer != null ? meshRenderer.sharedMaterial : null;

    /// <summary>
    /// Initialize with the specified material, automatically adding MeshFilter/MeshRenderer.
    /// Release exclusive materials with the mesh; release CreateStatic's shared material when its last user is destroyed.
    /// </summary>
    public void Initialize(Material material)
    {
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        mesh = new Mesh();
        // Large cell fills such as effect-range overlays may exceed the 16-bit vertex-index
        // limit of 65535, so use 32-bit indices to remove that restriction.
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        if (!sharedBillboardMaterial)
        {
            mesh.MarkDynamic();
            verts.Capacity = 4096; colors.Capacity = 4096; indices.Capacity = 8192;
            frameLines.Capacity = 512; frameDashed.Capacity = 128; frameWorldDashed.Capacity = 128;
            frameCircles.Capacity = 128; frameSquares.Capacity = 128;
            framePolygons.Capacity = 16; framePreTriangulated.Capacity = 32;
        }
        meshFilter.mesh = mesh;
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (sharedBillboardMaterial)
        {
            if (--billboardUsers == 0)
                for (int i = 0; i < billboardMaterials.Length; i++)
                {
                    Destroy(billboardMaterials[i]); billboardMaterials[i] = null;
                }
        }
        else if (meshRenderer != null && meshRenderer.sharedMaterial != null) Destroy(meshRenderer.sharedMaterial);
    }

    /// <summary>
    /// Set the overall color (_Tint), multiplied by vertex colors.
    /// Used to animate flashing without rebuilding the mesh.
    /// </summary>
    public void SetTint(Color tint)
    {
        if (meshRenderer == null) return;
        meshRenderer.sharedMaterial.SetColor("_Tint", tint);
    }

    /// <summary>
    /// Clear the frame's draw list. Call before drawing each frame.
    /// </summary>
    public void BeginFrame()
    {
        frameLines.Clear();
        frameDashed.Clear();
        frameWorldDashed.Clear();
        frameCircles.Clear();
        frameSquares.Clear();
        framePolygons.Clear();
        framePreTriangulated.Clear();
    }

    /// <summary>
    /// Add a solid line between two world-space points.
    /// </summary>
    public void AddSolidLine(Vector3 worldA, Vector3 worldB, Color color, float pixelThickness)
    {
        frameLines.Add(new LineEntry { a = worldA, b = worldB, color = color, pixelThickness = pixelThickness });
    }

    /// <summary>
    /// Add a dashed line between two world-space points.
    /// dashLen / gapLen are in pixels.
    /// </summary>
    public void AddDashedLine(Vector3 worldA, Vector3 worldB, Color color, float pixelThickness, float dashLen = 8f, float gapLen = 6f)
    {
        frameDashed.Add(new DashedEntry
        {
            a = worldA,
            b = worldB,
            color = color,
            pixelThickness = pixelThickness,
            dashLen = dashLen,
            gapLen = gapLen
        });
    }

    /// <summary>
    /// Add a dashed line between world-space points with fixed world-space width in meters.
    /// Width direction is based on upVector; no camera-facing billboard orientation is applied.
    /// </summary>
    public void AddDashedLineWorld(Vector3 worldA, Vector3 worldB, Color color, float worldThickness, float worldDashLen = 4f, float worldGapLen = 3f, Vector3? upVector = null)
    {
        frameWorldDashed.Add(new WorldDashedEntry
        {
            a = worldA,
            b = worldB,
            color = color,
            worldThickness = worldThickness,
            worldDashLen = worldDashLen,
            worldGapLen = worldGapLen,
            upVector = upVector ?? Vector3.up
        });
    }

    /// <summary>
    /// Add a circle at a world position, billboarded toward the camera.
    /// </summary>
    public void AddCircle(Vector3 worldPos, float pixelRadius, Color color)
    {
        frameCircles.Add(new CircleEntry { pos = worldPos, color = color, pixelRadius = pixelRadius });
    }

    /// <summary>
    /// Add a square at a world position, billboarded toward the camera.
    /// </summary>
    public void AddSquare(Vector3 worldPos, float pixelHalfSize, Color color)
    {
        frameSquares.Add(new SquareEntry { pos = worldPos, color = color, pixelHalfSize = pixelHalfSize });
    }

    /// <summary>
    /// Add a polygon bounded by world-space coordinates.
    /// Used for highlight fills and similar overlays.
    /// </summary>
    public void AddPolygon(List<Vector3> worldPoints, Color color)
    {
        framePolygons.Add(new PolygonEntry { points = new List<Vector3>(worldPoints), color = color });
    }

    /// <summary>
    /// Apply the frame's draw list to the mesh. Call after drawing each frame.
    /// </summary>
    public void EndFrame(Camera cam)
    {
        verts.Clear();
        colors.Clear();
        indices.Clear();

        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        cachedTanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        cachedHalfScreenHeight = Screen.height * 0.5f;

        foreach (var line in frameLines)
        {
            AddLineQuads(cam, line.a, line.b, line.color, line.pixelThickness);
        }

        foreach (var dash in frameDashed)
        {
            AddDashedLineQuads(cam, dash.a, dash.b, dash.color, dash.pixelThickness, dash.dashLen, dash.gapLen);
        }

        foreach (var dash in frameWorldDashed)
        {
            AddWorldDashedLineQuads(dash.a, dash.b, dash.color, dash.worldThickness, dash.worldDashLen, dash.worldGapLen, dash.upVector);
        }

        foreach (var circle in frameCircles)
        {
            AddCircleMesh(cam, circle.pos, circle.color, circle.pixelRadius);
        }

        foreach (var sq in frameSquares)
        {
            AddSquareMesh(cam, sq.pos, sq.color, sq.pixelHalfSize);
        }

        foreach (var poly in framePolygons)
        {
            AddPolygonMesh(poly.points, poly.color);
        }

        foreach (var pt in framePreTriangulated)
        {
            AddPreTriangulatedMesh(pt.verts, pt.color, pt.vertexColors);
        }

        mesh.Clear();
        if (verts.Count > 0)
        {
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
        }
    }

    /// <summary>
    /// Finalize static solid lines and circles with four vertices each. NORMAL stores the opposite endpoint; UV stores direction, pixel dimensions, and shape,
    /// expanded by the shared shader after screen projection. Camera pose and perspective/orthographic projection require no CPU rebuild.
    /// </summary>
    public void EndStaticFrame()
    {
        verts.Clear(); colors.Clear(); indices.Clear();
        var ends = new List<Vector3>();
        var parameters = new List<Vector4>();
        foreach (var line in frameLines)
        {
            int start = verts.Count;
            for (int i = 0; i < 4; i++)
            {
                verts.Add(i < 2 ? line.a : line.b);
                ends.Add(i < 2 ? line.b : line.a);
                colors.Add(line.color);
                parameters.Add(new Vector4(i % 2 == 0 ? -1f : 1f, 0f, line.pixelThickness * .5f, 0f));
            }
            AddQuadIndices(start);
        }
        foreach (var circle in frameCircles)
        {
            int start = verts.Count;
            for (int i = 0; i < 4; i++)
            {
                verts.Add(circle.pos); ends.Add(Vector3.zero); colors.Add(circle.color);
                parameters.Add(new Vector4(i < 2 ? -1f : 1f, i == 0 || i == 3 ? -1f : 1f, circle.pixelRadius, 1f));
            }
            AddQuadIndices(start);
        }
        mesh.Clear();
        mesh.SetVertices(verts); mesh.SetNormals(ends); mesh.SetColors(colors);
        mesh.SetUVs(0, parameters); mesh.SetTriangles(indices, 0);
        // Pad rendering bounds so GPU-expanded symbols are not clipped at frustum edges.
        Bounds bounds = mesh.bounds; bounds.Expand(16f); mesh.bounds = bounds;
        BeginFrame();
    }

    private void AddQuadIndices(int start)
    {
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    /// <summary>
    /// Clear the mesh so nothing is displayed.
    /// </summary>
    public void ClearMesh()
    {
        BeginFrame();
        mesh.Clear();
    }

    /// <summary>
    /// Camera parameters already calculated in EndFrame.
    /// FOV and screen size are constant within a frame, avoiding recalculation per vertex.
    /// </summary>
    private float cachedTanHalfFov;
    private float cachedHalfScreenHeight;

    /// <summary>
    /// Convert pixel thickness to world-space thickness at the specified world position.
    /// </summary>
    private float GetWorldThickness(Camera cam, Vector3 worldPos, float pixelThickness)
    {
        float distance = Vector3.Distance(cam.transform.position, worldPos);
        float worldPerPixel = distance * cachedTanHalfFov / cachedHalfScreenHeight;
        return pixelThickness * worldPerPixel;
    }

    /// <summary>
    /// Add a line segment as a quad: four vertices and two triangles.
    /// Use the camera direction at the segment midpoint for the billboard normal,
    /// and apply thickness based on camera distance at each endpoint.
    /// </summary>
    private void AddLineQuads(Camera cam, Vector3 a, Vector3 b, Color color, float pixelThickness)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 0.0001f) return;
        dir /= len;

        Vector3 mid = (a + b) * 0.5f;
        Vector3 viewDirMid = (mid - cam.transform.position).normalized;
        Vector3 side = Vector3.Cross(viewDirMid, dir).normalized;
        if (side.sqrMagnitude < 0.0001f)
        {
            side = cam.transform.up;
        }

        float thickA = GetWorldThickness(cam, a, pixelThickness);
        float thickB = GetWorldThickness(cam, b, pixelThickness);

        int baseIdx = verts.Count;
        verts.Add(a + side * thickA * 0.5f); colors.Add(color);
        verts.Add(a - side * thickA * 0.5f); colors.Add(color);
        verts.Add(b - side * thickB * 0.5f); colors.Add(color);
        verts.Add(b + side * thickB * 0.5f); colors.Add(color);

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    /// <summary>
    /// Draw dashes as separate pieces in 3D space.
    /// Convert dashLen / gapLen from pixels to world space for placement.
    /// </summary>
    private void AddDashedLineQuads(Camera cam, Vector3 a, Vector3 b, Color color, float pixelThickness, float dashLen, float gapLen)
    {
        Vector3 dir = b - a;
        float totalLen = dir.magnitude;
        if (totalLen < 0.0001f) return;
        dir /= totalLen;

        Vector3 mid = (a + b) * 0.5f;
        float worldPerPixel = GetWorldThickness(cam, mid, 1f);
        float worldDashLen = dashLen * worldPerPixel;
        float worldGapLen = gapLen * worldPerPixel;

        float pos = 0f;
        bool draw = true;
        while (pos < totalLen)
        {
            float segLen = draw ? worldDashLen : worldGapLen;
            float segEnd = Mathf.Min(pos + segLen, totalLen);

            if (draw)
            {
                Vector3 segA = a + dir * pos;
                Vector3 segB = a + dir * segEnd;
                AddLineQuads(cam, segA, segB, color, pixelThickness);
            }

            pos = segEnd;
            draw = !draw;
        }
    }

    /// <summary>
    /// Add a camera-facing billboard circle as a triangle fan.
    /// </summary>
    private void AddCircleMesh(Camera cam, Vector3 pos, Color color, float pixelRadius)
    {
        const int segments = 16;
        float worldRadius = GetWorldThickness(cam, pos, pixelRadius);

        Vector3 toCam = cam.transform.position - pos;
        Vector3 forward = toCam.normalized;
        Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.Cross(forward, Vector3.right).normalized;
        Vector3 up = Vector3.Cross(right, forward).normalized;

        int centerIdx = verts.Count;
        verts.Add(pos);
        colors.Add(color);

        for (int s = 0; s < segments; s++)
        {
            float angle = (float)s / segments * Mathf.PI * 2f;
            Vector3 offset = right * Mathf.Cos(angle) * worldRadius + up * Mathf.Sin(angle) * worldRadius;
            verts.Add(pos + offset);
            colors.Add(color);
        }

        for (int s = 0; s < segments; s++)
        {
            int next = (s + 1) % segments;
            indices.Add(centerIdx);
            indices.Add(centerIdx + 1 + s);
            indices.Add(centerIdx + 1 + next);
        }
    }

    /// <summary>
    /// Add a camera-facing billboard square as a quad.
    /// </summary>
    private void AddSquareMesh(Camera cam, Vector3 pos, Color color, float pixelHalfSize)
    {
        float worldHalfSize = GetWorldThickness(cam, pos, pixelHalfSize);

        Vector3 toCam = cam.transform.position - pos;
        Vector3 forward = toCam.normalized;
        Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.Cross(forward, Vector3.right).normalized;
        Vector3 up = Vector3.Cross(right, forward).normalized;

        int baseIdx = verts.Count;
        verts.Add(pos - right * worldHalfSize - up * worldHalfSize); colors.Add(color);
        verts.Add(pos + right * worldHalfSize - up * worldHalfSize); colors.Add(color);
        verts.Add(pos + right * worldHalfSize + up * worldHalfSize); colors.Add(color);
        verts.Add(pos - right * worldHalfSize + up * worldHalfSize); colors.Add(color);

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    /// <summary>
    /// Append a pre-triangulated vertex list (3 vertices x N triangles) to the frame queue.
    /// EndFrame applies it to the mesh together with other entries.
    /// </summary>
    public void AddPreTriangulated(List<Vector3> triangleVerts, Color color)
    {
        if (triangleVerts == null || triangleVerts.Count < 3) return;
        framePreTriangulated.Add(new PreTriangulatedEntry { verts = triangleVerts, color = color });
    }

    /// <summary>
    /// Append a pre-triangulated vertex list with per-vertex colors to the frame queue.
    /// Used for within-face color changes such as glow gradients, opaque near the ground and transparent at the top.
    /// Ignore vertexColors unless its count matches triangleVerts.
    /// </summary>
    public void AddPreTriangulated(List<Vector3> triangleVerts, List<Color> vertexColors)
    {
        if (triangleVerts == null || triangleVerts.Count < 3) return;
        if (vertexColors == null || vertexColors.Count != triangleVerts.Count) return;
        framePreTriangulated.Add(new PreTriangulatedEntry
        {
            verts = triangleVerts,
            color = Color.white,
            vertexColors = vertexColors
        });
    }

    /// <summary>
    /// Add a polygon as a triangle fan.
    /// Use vertices in world coordinates unchanged.
    /// </summary>
    private void AddPolygonMesh(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 3) return;

        int startIdx = verts.Count;
        for (int i = 0; i < points.Count; i++)
        {
            verts.Add(points[i]);
            colors.Add(color);
        }

        for (int i = 1; i < points.Count - 1; i++)
        {
            indices.Add(startIdx);
            indices.Add(startIdx + i);
            indices.Add(startIdx + i + 1);
        }
    }

    /// <summary>
    /// Add a pre-triangulated vertex list (3 vertices x N triangles) to the mesh.
    /// Use per-vertex colors when vertexColors is provided.
    /// </summary>
    private void AddPreTriangulatedMesh(List<Vector3> triangleVerts, Color color, List<Color> vertexColors = null)
    {
        if (triangleVerts == null || triangleVerts.Count < 3) return;
        int startIdx = verts.Count;
        for (int i = 0; i < triangleVerts.Count; i++)
        {
            verts.Add(triangleVerts[i]);
            colors.Add(vertexColors != null ? vertexColors[i] : color);
        }
        for (int i = 0; i + 2 < triangleVerts.Count; i += 3)
        {
            indices.Add(startIdx + i);
            indices.Add(startIdx + i + 1);
            indices.Add(startIdx + i + 2);
        }
    }

    /// <summary>
    /// Build fixed-length, fixed-spacing world-space dashes as multiple quads.
    /// They follow perspective independently of screen-space pixel size.
    /// </summary>
    private void AddWorldDashedLineQuads(Vector3 a, Vector3 b, Color color, float worldThickness, float worldDashLen, float worldGapLen, Vector3 upVector)
    {
        Vector3 dir = b - a;
        float totalLen = dir.magnitude;
        if (totalLen < 0.0001f) return;
        dir /= totalLen;

        float pos = 0f;
        bool draw = true;
        while (pos < totalLen)
        {
            float segLen = draw ? worldDashLen : worldGapLen;
            float segEnd = Mathf.Min(pos + segLen, totalLen);

            if (draw)
            {
                Vector3 segA = a + dir * pos;
                Vector3 segB = a + dir * segEnd;
                AddWorldLineQuads(segA, segB, color, worldThickness, upVector);
            }

            pos = segEnd;
            draw = !draw;
        }
    }

    /// <summary>
    /// Build a fixed-world-width line segment as a quad based on the up vector.
    /// The cross product of up and line direction defines width, keeping the shape flat along the ground.
    /// </summary>
    private void AddWorldLineQuads(Vector3 a, Vector3 b, Color color, float worldThickness, Vector3 upVector)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 0.0001f) return;
        dir /= len;

        // Calculate the width-direction vector from the line direction and up vector.
        Vector3 side = Vector3.Cross(upVector, dir).normalized;
        if (side.sqrMagnitude < 0.0001f)
        {
            // If the cross product is extremely small because the vectors are parallel, substitute another perpendicular vector.
            side = Vector3.Cross(Vector3.forward, dir).normalized;
            if (side.sqrMagnitude < 0.0001f)
            {
                side = Vector3.right;
            }
        }

        int baseIdx = verts.Count;
        verts.Add(a + side * worldThickness * 0.5f); colors.Add(color);
        verts.Add(a - side * worldThickness * 0.5f); colors.Add(color);
        verts.Add(b - side * worldThickness * 0.5f); colors.Add(color);
        verts.Add(b + side * worldThickness * 0.5f); colors.Add(color);

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }
}

