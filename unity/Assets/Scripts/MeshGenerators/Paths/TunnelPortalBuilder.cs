using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// Tunnel portal (headwall with arch ring and coping) built from the terrain at each tunnel mouth, plus its TunnelCutVolume.
///
/// When construction starts the core digs the open-cut section down to the road and cuts the mouth cross-section vertically,
/// leaving a step as tall as the clearance that widens upward along the open-cut side slopes. The headwall covers exactly that face:
/// a level parapet over the arch and a top clearing the hill behind wherever the face stands, falling like the open-cut slopes past it
/// until it meets the ground in front.
/// The arch opening spans the tunnel interior (inner half width = path width / 2, crown at the clearance height); the box-section
/// interior continues behind it. Terrain inside the interior within the headwall thickness is cut so the tunnel can be seen into.
/// A mouth without that step (a planned tunnel before excavation) gets no portal.
///
/// Paths whose bands meet on the mouth line share one opening built by the lowest path_id (joined double track forms one interior);
/// separate neighbors split the headwall halfway across the gap, so neither covers the other's opening regardless of build order.
/// </summary>
public static class TunnelPortalBuilder
{
    /// <summary>Another path near a portal: rendering points and half width.</summary>
    public readonly struct Neighbor
    {
        public readonly int PathId;
        public readonly List<Vector3> Points;
        public readonly float HalfWidth;

        public Neighbor(int pathId, List<Vector3> points, float halfWidth)
        {
            PathId = pathId;
            Points = points;
            HalfWidth = halfWidth;
        }
    }

    /// <summary>Supplied by PathManager: portal-building paths other than excludePathId whose data lies within radius of position.</summary>
    public static Func<Vector3, float, int, List<Neighbor>> NeighborProvider;

    private const string MaterialName = "PATH_CONCRETE";
    private const int ArchSegments = 16;          // Segments of the half ellipse above the springline.
    private const float RingWidth = 0.6f;         // Arch ring band around the opening.
    private const float RingProjection = 0.15f;   // Arch ring standing out of the headwall face.
    private const float CopingHeight = 0.3f;
    private const float CopingProjection = 0.15f; // Not less than RingProjection, so the top covers the whole cut box.
    private const float ParapetAboveCrown = 1.2f; // Headwall top (under the coping) above the arch crown.
    private const float ParapetSideMargin = 1f;   // Level parapet beyond the arch ring on each side.
    private const float ShoulderSlope = 0.5f;     // Fall of the headwall top beyond the parapet (rise:run = 1:2, the open-cut side slope).
    private const float CoverMargin = 0.2f;       // Headwall top above the hill just behind it.
    private const float Embed = 0.5f;             // Headwall bottom and opening edges reach this far below the ground and road.
    private const float StepThreshold = 0.3f;     // Ground at most this high above the road counts as the excavated floor.
    private const float FaceThreshold = 1f;       // Cut faces lower than this blend into the hillside and are left uncovered.
    private const float FloorClearance = 0.05f;   // The cut starts this far above the road so the excavated floor stays.
    private const float ProbeOffset = 0.5f;       // Ground samples this far outside the headwall faces.
    private const float SearchStep = 0.25f;
    private const float LateralStep = 1f;

    public static bool BuildsPortals(PathSummary path)
    {
        var store = MasterDataStore.Instance;
        var master = store != null && store.IsInitialized ? store.GetPathMaster(path.path_variant) : null;
        string modelType = master?.model_type;
        return (string.IsNullOrEmpty(modelType) || modelType == "default" || modelType == "track")
            && TunnelSectionUtil.GetClearanceHeight(path.path_variant) > 0f;
    }

    public static void Build(GameObject container, PathSummary path, List<TunnelSectionUtil.PortalInfo> portals)
    {
        if (container == null || portals == null || portals.Count == 0 || !BuildsPortals(path)) return;
        RoadPath.GetPathMasterParams(path, out float width, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        float clearance = TunnelSectionUtil.GetClearanceHeight(path.path_variant);
        if (width <= 0f) return;
        Material material = RoadPath.GetOrCreateRoadMaterialByName(MaterialName);
        foreach (var portal in portals) BuildPortal(container, path.path_id, portal, width * .5f, clearance, material);
    }

    private readonly struct Frame
    {
        public readonly Vector3 Origin, Forward, Right;
        public readonly float Grade;

        public Frame(Vector3 origin, Vector3 forward, float grade)
        {
            Origin = origin;
            Forward = forward;
            Right = Vector3.Cross(Vector3.up, forward);
            Grade = grade;
        }

        public Frame Shifted(float u) => new Frame(Origin + Right * u, Forward, Grade);
        public float Ground(float u, float z) => Utils.GetGroundHeight(Origin + Right * u + Forward * z);
        public float Road(float z) => Origin.y + Grade * z;
        public Vector3 Local(float u, float worldY, float z) => new Vector3(u, worldY - Origin.y, z);
    }

    private struct Sample
    {
        public float T, Front, Back;
    }

    private static void BuildPortal(GameObject container, int pathId, TunnelSectionUtil.PortalInfo portal,
        float halfWidth, float clearance, Material material)
    {
        if (!Utils.TryGetTerrainGrid(portal.position, out _, out Vector2 spacing)) return;
        var frame = new Frame(portal.position, portal.direction, portal.grade);

        // The vertical cut blurs over one terrain grid cell; its foot lies within that cell's diagonal of the boundary point,
        // and twice that leaves margin. A planned mouth reaches the road only far outside, so it has no step and no portal.
        float grid = Mathf.Max(spacing.x, spacing.y);
        float foot = float.NaN;
        for (float z = 0f; z >= -2f * grid * Mathf.Sqrt(2f); z -= SearchStep)
        {
            if (frame.Ground(0f, z) - frame.Road(z) > StepThreshold) continue;
            foot = z;
            break;
        }
        if (float.IsNaN(foot)) return;
        // Along the mouth line the cut wanders by up to one grid cell, so one more cell on each side keeps all of it inside the headwall.
        float zFront = foot - grid, zBack = grid;

        // The cut face ends at the top of the open-cut side slopes; this bounds the search well past it.
        float reach = halfWidth + 4f * clearance;
        if (!ResolveOpening(frame, pathId, halfWidth, clearance, reach, out float center, out float a, out float leftLimit, out float rightLimit)) return;
        frame = frame.Shifted(center);
        leftLimit -= center;
        rightLimit -= center;

        // A rise of at most half the clearance keeps the springline high, so the arch does not pinch the traffic space by the walls.
        float rise = Mathf.Min(a, clearance * .5f);
        float springline = clearance - rise;
        float ringOuter = a + RingWidth;
        float parapetHalf = ringOuter + ParapetSideMargin;
        float parapetTop = frame.Road(zFront) + clearance + ParapetAboveCrown;

        float Parapet(float t) => parapetTop - ShoulderSlope * Mathf.Max(0f, t - parapetHalf);
        // Above the cut face the top runs over the hill at the back of the headwall; past the face it falls like the parapet shoulders.
        float TopAt(List<Vector2> cover, float t) => Mathf.Max(Parapet(t), Extend(cover, t));

        // Sample one side outward: the headwall covers the parapet, then the cut face, then falls until it meets the ground in front.
        List<Sample> SampleSide(float side, float limit, out List<Vector2> cover)
        {
            var ts = new List<float> { ringOuter, parapetHalf };
            for (float t = 0f; t < Mathf.Min(reach, limit); t += LateralStep) ts.Add(t);
            ts.Add(Mathf.Min(reach, limit));
            ts.Sort();
            var samples = new List<Sample>();
            var faceTops = new List<Vector2>();
            cover = null;
            foreach (float t in ts)
            {
                if (samples.Count > 0 && t - samples[samples.Count - 1].T < 1e-3f) continue;
                float front = frame.Ground(side * t, zFront - ProbeOffset);
                float back = frame.Ground(side * t, zBack + ProbeOffset);
                samples.Add(new Sample { T = t, Front = front, Back = back });
                if (cover == null)
                {
                    // Depth of the cut: the hill behind extended forward minus the ground, deepest within the headwall thickness.
                    // Away from the road the cut is the open-cut side slope meeting the rising hill, so it recedes toward the mouth line.
                    float hillRise = Mathf.Max(0f, frame.Ground(side * t, zBack + ProbeOffset + 1f) - back);
                    float cut = 0f;
                    for (float z = zFront; z <= zBack; z += SearchStep)
                        cut = Mathf.Max(cut, back - hillRise * (zBack + ProbeOffset - z) - frame.Ground(side * t, z));
                    if (cut > FaceThreshold) faceTops.Add(new Vector2(t, frame.Ground(side * t, zBack) + CoverMargin));
                    if (cut > FaceThreshold || t <= parapetHalf) continue;
                    cover = UpperHull(faceTops);
                }
                if (TopAt(cover, t) <= front + CoverMargin) break;
            }
            cover ??= UpperHull(faceTops);
            return samples;
        }

        var left = SampleSide(-1f, -leftLimit, out var leftCover);
        var right = SampleSide(1f, rightLimit, out var rightCover);

        float Top(float u) => TopAt(u < 0f ? leftCover : rightCover, Mathf.Abs(u));
        float Back(float u) => Interpolate(u < 0f ? left : right, Mathf.Abs(u), s => s.Back);
        // Where the hill behind stands above the top it buries the back face, so the face stops at the top.
        float BackBottom(float u)
        {
            float bottom = Back(u) - Embed;
            if (Mathf.Abs(u) <= ringOuter) bottom = Mathf.Max(bottom, frame.Road(zBack) + clearance);
            return Mathf.Min(bottom, Top(u));
        }

        // Columns of the headwall from left to right: lower edge is the ground (minus Embed) outside the ring, the ring's outer arch inside.
        var columns = new List<Vector2>();
        for (int i = left.Count - 1; i >= 0; i--)
            if (left[i].T >= ringOuter - 1e-3f) columns.Add(new Vector2(-left[i].T, left[i].Front - Embed));
        var inner = ArchOutline(a, rise, springline);
        var outer = ArchOutline(ringOuter, rise + RingWidth, springline);
        float frontRoad = frame.Road(zFront);
        for (int i = 1; i < outer.Count - 1; i++) columns.Add(new Vector2(outer[i].x, frontRoad + outer[i].y));
        foreach (var sample in right)
            if (sample.T >= ringOuter - 1e-3f) columns.Add(new Vector2(sample.T, sample.Front - Embed));

        var mesh = new PortalMesh();
        float copingFront = zFront - CopingProjection;
        for (int i = 0; i + 1 < columns.Count; i++)
        {
            float u0 = columns[i].x, u1 = columns[i + 1].x;
            if (u1 - u0 < 1e-3f) continue;
            float top0 = Top(u0), top1 = Top(u1);
            mesh.Quad(frame.Local(u0, columns[i].y, zFront), frame.Local(u0, top0, zFront),
                frame.Local(u1, top1, zFront), frame.Local(u1, columns[i + 1].y, zFront), Vector3.back);
            mesh.Quad(frame.Local(u0, top0, copingFront), frame.Local(u0, top0 + CopingHeight, copingFront),
                frame.Local(u1, top1 + CopingHeight, copingFront), frame.Local(u1, top1, copingFront), Vector3.back);
            mesh.Quad(frame.Local(u0, top0 + CopingHeight, copingFront), frame.Local(u0, top0 + CopingHeight, zBack),
                frame.Local(u1, top1 + CopingHeight, zBack), frame.Local(u1, top1 + CopingHeight, copingFront), Vector3.up);
            mesh.Quad(frame.Local(u0, top0, copingFront), frame.Local(u0, top0, zFront),
                frame.Local(u1, top1, zFront), frame.Local(u1, top1, copingFront), Vector3.down);
            mesh.Quad(frame.Local(u0, BackBottom(u0), zBack), frame.Local(u0, top0 + CopingHeight, zBack),
                frame.Local(u1, top1 + CopingHeight, zBack), frame.Local(u1, BackBottom(u1), zBack), Vector3.forward);
        }
        for (int end = 0; end < 2; end++)
        {
            var column = columns[end == 0 ? 0 : columns.Count - 1];
            float u = column.x, top = Top(u);
            Vector3 outward = end == 0 ? Vector3.left : Vector3.right;
            mesh.Quad(frame.Local(u, column.y, zFront), frame.Local(u, top + CopingHeight, zFront),
                frame.Local(u, top + CopingHeight, zBack), frame.Local(u, BackBottom(u), zBack), outward);
            mesh.Quad(frame.Local(u, top, copingFront), frame.Local(u, top + CopingHeight, copingFront),
                frame.Local(u, top + CopingHeight, zFront), frame.Local(u, top, zFront), outward);
        }

        float ringFront = zFront - RingProjection;
        float ringRoad = frame.Road(ringFront);
        for (int i = 0; i + 1 < inner.Count; i++)
        {
            mesh.Quad(frame.Local(inner[i].x, ringRoad + inner[i].y, ringFront), frame.Local(outer[i].x, ringRoad + outer[i].y, ringFront),
                frame.Local(outer[i + 1].x, ringRoad + outer[i + 1].y, ringFront), frame.Local(inner[i + 1].x, ringRoad + inner[i + 1].y, ringFront),
                Vector3.back);
        }
        var archCenter = new Vector2(0f, springline);
        mesh.Strip(frame, outer, ringFront, zFront, archCenter, outward: true);
        mesh.Strip(frame, inner, ringFront, 0f, archCenter, outward: false);
        // The box interior begins at the boundary point; close the corners between it and the arch.
        float backRoad = frame.Road(0f);
        for (int i = 1; i + 2 < inner.Count; i++)
        {
            mesh.Quad(frame.Local(inner[i].x, backRoad + inner[i].y, 0f), frame.Local(inner[i].x, backRoad + clearance, 0f),
                frame.Local(inner[i + 1].x, backRoad + clearance, 0f), frame.Local(inner[i + 1].x, backRoad + inner[i + 1].y, 0f),
                Vector3.forward);
        }

        var go = new GameObject("TunnelPortal");
        go.transform.SetParent(container.transform, false);
        Quaternion rotation = Quaternion.LookRotation(frame.Forward, Vector3.up);
        go.transform.SetPositionAndRotation(frame.Origin, rotation);
        Mesh built = mesh.ToMesh();
        go.AddComponent<MeshFilter>().sharedMesh = built;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        go.AddComponent<IntersectionMeshOwner>().OwnedMesh = built;
        ConfigureCut(go.AddComponent<TunnelCutVolume>(), frame, rotation, a, clearance, ringFront, zBack);
    }

    /// <summary>
    /// Opening span on the mouth line (relative to the own centerline) and how far the headwall may extend on each side.
    /// False when a joined path with a lower id builds the shared portal.
    /// </summary>
    private static bool ResolveOpening(Frame frame, int pathId, float halfWidth, float clearance, float reach,
        out float center, out float halfSpan, out float leftLimit, out float rightLimit)
    {
        var bands = new List<(float left, float right, int id)> { (-halfWidth, halfWidth, pathId) };
        var neighbors = NeighborProvider?.Invoke(frame.Origin, reach, pathId);
        if (neighbors != null)
        {
            foreach (var neighbor in neighbors)
            {
                for (int i = 0; i + 1 < neighbor.Points.Count; i++)
                {
                    Vector3 p = neighbor.Points[i] - frame.Origin, q = neighbor.Points[i + 1] - frame.Origin;
                    float dp = p.x * frame.Forward.x + p.z * frame.Forward.z, dq = q.x * frame.Forward.x + q.z * frame.Forward.z;
                    if (dp * dq > 0f || dp == dq) continue;
                    Vector3 hit = Vector3.Lerp(p, q, dp / (dp - dq));
                    float u = hit.x * frame.Right.x + hit.z * frame.Right.z;
                    if (Mathf.Abs(u) > reach || Mathf.Abs(hit.y) > clearance) continue;
                    bands.Add((u - neighbor.HalfWidth, u + neighbor.HalfWidth, neighbor.PathId));
                }
            }
        }
        bands.Sort((x, y) => x.left.CompareTo(y.left));

        // Bands closer than the joined-track gap share one opening, as joined tracks share one interior.
        var groups = new List<(float left, float right, int minId, bool own)>();
        foreach (var band in bands)
        {
            int last = groups.Count - 1;
            if (last >= 0 && band.left - groups[last].right < LocalConstants.TRACK_MERGE_MAX_EDGE_GAP)
            {
                var g = groups[last];
                groups[last] = (g.left, Mathf.Max(g.right, band.right), Mathf.Min(g.minId, band.id), g.own || band.id == pathId);
            }
            else groups.Add((band.left, band.right, band.id, band.id == pathId));
        }
        int own = groups.FindIndex(g => g.own);
        var group = groups[own];
        center = (group.left + group.right) * .5f;
        halfSpan = (group.right - group.left) * .5f;
        leftLimit = own > 0 ? (groups[own - 1].right + group.left) * .5f : float.NegativeInfinity;
        rightLimit = own + 1 < groups.Count ? (group.right + groups[own + 1].left) * .5f : float.PositiveInfinity;
        return group.minId == pathId;
    }

    /// <summary>Cut box: the interior (|x| ≤ a, road + FloorClearance to the crown) through the headwall thickness, following the road grade.</summary>
    private static void ConfigureCut(TunnelCutVolume volume, Frame frame, Quaternion rotation, float a, float clearance, float zFrom, float zTo)
    {
        float centerY = (FloorClearance + clearance) * .5f, halfY = (clearance - FloorClearance) * .5f;
        float centerZ = (zFrom + zTo) * .5f, halfZ = (zTo - zFrom) * .5f;
        Matrix4x4 box = Matrix4x4.identity;
        box.m00 = 1f / a;
        box.m11 = 1f / halfY;
        box.m12 = -frame.Grade / halfY;
        box.m13 = -centerY / halfY;
        box.m22 = 1f / halfZ;
        box.m23 = -centerZ / halfZ;
        Matrix4x4 localToWorld = Matrix4x4.TRS(frame.Origin, rotation, Vector3.one);
        Vector3 center = localToWorld.MultiplyPoint3x4(new Vector3(0f, centerY + frame.Grade * centerZ, centerZ));
        float radius = new Vector3(a, halfY + Mathf.Abs(frame.Grade) * halfZ, halfZ).magnitude;
        volume.Configure(box * localToWorld.inverse, center, radius);
    }

    /// <summary>Opening outline from bottom left over the arch to bottom right, starting Embed below the road.</summary>
    private static List<Vector2> ArchOutline(float halfWidth, float rise, float springline)
    {
        var outline = new List<Vector2> { new Vector2(-halfWidth, -Embed) };
        for (int i = 0; i <= ArchSegments; i++)
        {
            float angle = Mathf.PI * (1f - (float)i / ArchSegments);
            outline.Add(new Vector2(halfWidth * Mathf.Cos(angle), springline + rise * Mathf.Sin(angle)));
        }
        outline.Add(new Vector2(halfWidth, -Embed));
        return outline;
    }

    /// <summary>Upper hull of the heights the top must clear over the cut face, so the headwall top runs in straight segments.</summary>
    private static List<Vector2> UpperHull(List<Vector2> points)
    {
        var hull = new List<Vector2>();
        foreach (var p in points)
        {
            while (hull.Count >= 2)
            {
                Vector2 o = hull[hull.Count - 2], m = hull[hull.Count - 1];
                if ((m.x - o.x) * (p.y - o.y) - (m.y - o.y) * (p.x - o.x) < 0f) break;
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p);
        }
        return hull;
    }

    /// <summary>Hull height at t; past its last point it falls at ShoulderSlope.</summary>
    private static float Extend(List<Vector2> hull, float t)
    {
        if (hull.Count == 0) return float.NegativeInfinity;
        for (int i = 0; i + 1 < hull.Count; i++)
            if (t <= hull[i + 1].x)
                return Mathf.Lerp(hull[i].y, hull[i + 1].y, Mathf.InverseLerp(hull[i].x, hull[i + 1].x, t));
        Vector2 last = hull[hull.Count - 1];
        return t <= last.x ? last.y : last.y - ShoulderSlope * (t - last.x);
    }

    private static float Interpolate(List<Sample> samples, float t, Func<Sample, float> value)
    {
        for (int i = 0; i + 1 < samples.Count; i++)
            if (t <= samples[i + 1].T)
                return Mathf.Lerp(value(samples[i]), value(samples[i + 1]), Mathf.InverseLerp(samples[i].T, samples[i + 1].T, t));
        return value(samples[samples.Count - 1]);
    }

    /// <summary>Portal mesh in the portal's local frame (x = right, z = into the tunnel). Flat quads, smooth strips along the arch.</summary>
    private sealed class PortalMesh
    {
        private readonly List<Vector3> vertices = new();
        private readonly List<Vector2> uvs = new();
        private readonly List<int> triangles = new();

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a) + Vector3.Cross(c - a, d - a);
            if (normal.sqrMagnitude < 1e-10f) return;
            if (Vector3.Dot(normal, facing) < 0f) (b, d) = (d, b);
            int start = vertices.Count;
            foreach (var v in new[] { a, b, c, d })
            {
                vertices.Add(v);
                uvs.Add(Mathf.Abs(facing.z) > .5f ? new Vector2(v.x, v.y) : Mathf.Abs(facing.y) > .5f ? new Vector2(v.x, v.z) : new Vector2(v.z, v.y));
            }
            triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }

        /// <summary>Extrude outline (x, height above the road) from z0 to z1, facing away from or toward center.</summary>
        public void Strip(Frame frame, List<Vector2> outline, float z0, float z1, Vector2 center, bool outward)
        {
            int start = vertices.Count;
            float arc = 0f;
            for (int i = 0; i < outline.Count; i++)
            {
                if (i > 0) arc += Vector2.Distance(outline[i - 1], outline[i]);
                vertices.Add(frame.Local(outline[i].x, frame.Road(z0) + outline[i].y, z0));
                vertices.Add(frame.Local(outline[i].x, frame.Road(z1) + outline[i].y, z1));
                uvs.Add(new Vector2(z0, arc));
                uvs.Add(new Vector2(z1, arc));
            }
            // Outline segments all turn the same way, so one check on the first segment orients the whole strip.
            Vector3 p0 = vertices[start], p1 = vertices[start + 1], p2 = vertices[start + 3];
            Vector2 mid = (outline[0] + outline[1]) * .5f - center;
            bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), new Vector3(mid.x, mid.y, 0f)) < 0f == outward;
            for (int i = 0; i + 1 < outline.Count; i++)
            {
                int a = start + 2 * i, b = a + 1, c = a + 3, d = a + 2;
                if (flip) triangles.AddRange(new[] { a, d, c, a, c, b });
                else triangles.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        public Mesh ToMesh()
        {
            var mesh = new Mesh { name = "TunnelPortal" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
