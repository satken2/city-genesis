using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using NetTopologySuite.Geometries;
using GeneratedModels;

/// <summary>Cut cross-section shared by roads and intersections. Coordinates are world-space; Direction points outward from the node.</summary>
public sealed class PathEndSection
{
    public Vector3 Position;
    public Vector2 Direction;
    public Vector3 Tangent;
    public Vector3 Normal => Vector3.Cross(Tangent, new Vector3(Direction.y, 0f, -Direction.x)).normalized;
    public float Width;
    public float Distance;
    public float BodyDepth;
    public Vector2 Center => new Vector2(Position.x, Position.z);
    public Vector2 Left => new Vector2(-Direction.y, Direction.x);
}

/// <summary>Boundary between adjacent entrances. Road surface, sidewalks, and walls share this outline.</summary>
public sealed class IntersectionCorner
{
    public int FromPath;
    public int ToPath;
    public readonly List<Vector2> Points = new List<Vector2>();
    public readonly List<Vector2> Directions = new List<Vector2>();
}

public static class IntersectionPolygonUtil
{
    public class IntersectionCalculationResult
    {
        public Geometry Geometry { get; set; }
        public bool RequiresMoreSpace { get; set; }
        public List<IntersectionCorner> Corners { get; } = new List<IntersectionCorner>();
        public Dictionary<int, Vector2> ConnectionPoints { get; } = new Dictionary<int, Vector2>();
        public Dictionary<int, PathEndSection> Sections { get; } = new Dictionary<int, PathEndSection>();
    }

    /// <summary>
    /// Connect entrance cross-sections in angular order. The caller sets cut limits based on adjacent-node connections.
    /// When rendered points are provided, derive cross-sections from that polyline without moving curve controls.
    /// Determine cut distance from node width (widthResolver). If sectionWidthResolver exists, take cross-section width
    /// at the cut's distance from the node because track-joint S-curves vary in width along their length.
    /// </summary>
    public static IntersectionCalculationResult ComputeIntersectionPolygon(int intersectionNodeId,
        Vector2 intersectionPos, List<PathSummary> paths, GeometryFactory gf,
        Func<PathSummary, float> widthResolver = null, Func<PathSummary, List<Vector3>> pointResolver = null, Func<PathSummary, float> cutLimitResolver = null,
        Func<PathSummary, float, float> sectionWidthResolver = null)
    {
        var entries = new List<(PathSummary path, List<Vector3> points, float width, Vector2 direction)>();
        foreach (var p in paths.OrderBy(p => p.path_id))
        {
            if (p.start_node_id != intersectionNodeId && p.end_node_id != intersectionNodeId) continue;
            float width = widthResolver != null ? widthResolver(p) : p.width;
            if (width <= 0f) continue;
            var source = pointResolver != null ? pointResolver(p) : SamplePlan(p);
            if (source == null || source.Count < 2) continue;
            var points = new List<Vector3>(source);
            if (p.end_node_id == intersectionNodeId) points.Reverse();
            Vector2 direction = XZ(points[1] - points[0]).normalized;
            if (direction.sqrMagnitude < 1e-6f) continue;
            entries.Add((p, points, width, direction));
        }
        var result = new IntersectionCalculationResult();
        if (entries.Count < 2) { result.Geometry = gf.CreatePoint(new Coordinate(intersectionPos.x, intersectionPos.y)); return result; }
        float maxWidth = entries.Max(e => e.width);
        foreach (var e in entries)
        {
            float length = Length(e.points);
            float cut = Math.Max(0.5f, maxWidth * 0.5f);
            foreach (var other in entries)
            {
                if (other.path.path_id == e.path.path_id) continue;
                float dot = Mathf.Clamp(Vector2.Dot(e.direction, other.direction), -1f, 1f);
                float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - dot * dot));
                // Opposing roads need only a width-transition section. Limit sharp same-direction branches by maximum length.
                if (dot > -0.98f)
                    cut = Mathf.Max(cut, (other.width + e.width * Mathf.Max(0f, dot)) * 0.5f / Mathf.Max(0.1f, sin));
            }
            float limit = cutLimitResolver != null ? cutLimitResolver(e.path) : length * 0.95f;
            cut = Mathf.Min(cut + maxWidth * 0.25f, Mathf.Max(0f, limit));
            var section = AtDistance(e.points, cut);
            section.Width = sectionWidthResolver != null ? sectionWidthResolver(e.path, cut) : e.width;
            result.Sections[e.path.path_id] = section;
            result.ConnectionPoints[e.path.path_id] = section.Center;
        }
        var ordered = result.Sections.OrderBy(s => Mathf.Atan2(s.Value.Direction.y, s.Value.Direction.x)).ThenBy(s => s.Key).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var pair = ordered[i];
            var next = ordered[(i + 1) % ordered.Count];
            var s = pair.Value; var n = next.Value;
            Vector2 left = s.Center + s.Left * s.Width * 0.5f;
            Vector2 end = n.Center - n.Left * n.Width * 0.5f;
            var corner = new IntersectionCorner { FromPath = pair.Key, ToPath = next.Key };
            float length = Vector2.Distance(left, end);
            Vector2 p1 = left - s.Direction * length / 3f;
            Vector2 p2 = end - n.Direction * length / 3f;
            Vector2 arcStart = left, arcEnd = end;
            float cross = s.Direction.x * n.Direction.y - s.Direction.y * n.Direction.x;
            if (Mathf.Abs(cross) > 1e-4f)
            {
                Vector2 delta = end - left;
                float fromDistance = -(delta.x * n.Direction.y - delta.y * n.Direction.x) / cross;
                float toDistance = -(delta.x * s.Direction.y - delta.y * s.Direction.x) / cross;
                if (fromDistance > 0f && toDistance > 0f)
                {
                    // Round corners by retreating equal distances from the road-edge intersection, retaining any extra distance as straight segments,
                    // so Bezier controls do not cross the other road's edge and create pinching when widths differ.
                    float turn = Mathf.Atan2(Mathf.Abs(cross), Vector2.Dot(-s.Direction, n.Direction));
                    float tangent = Mathf.Tan(turn * 0.5f);
                    float setback = Mathf.Min(maxWidth * 0.25f * tangent, Mathf.Min(fromDistance, toDistance));
                    arcStart = left - s.Direction * (fromDistance - setback);
                    arcEnd = end - n.Direction * (toDistance - setback);
                    float handle = 4f / 3f * setback / tangent * Mathf.Tan(turn * 0.25f);
                    p1 = arcStart - s.Direction * handle;
                    p2 = arcEnd - n.Direction * handle;
                }
            }
            if ((arcStart - left).sqrMagnitude > 1e-8f)
            {
                corner.Points.Add(left);
                corner.Directions.Add(-s.Direction);
            }
            int count = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(arcStart, arcEnd) / 0.5f), 4, 32);
            for (int j = 0; j <= count; j++)
            {
                float t = (float)j / count, u = 1f - t;
                Vector2 p = u*u*u*arcStart + 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t*arcEnd;
                Vector2 direction = (3*u*u*(p1-arcStart) + 6*u*t*(p2-p1) + 3*t*t*(arcEnd-p2)).normalized;
                corner.Points.Add(p); corner.Directions.Add(direction);
            }
            if ((end - arcEnd).sqrMagnitude > 1e-8f)
            {
                corner.Points.Add(end);
                corner.Directions.Add(n.Direction);
            }
            result.Corners.Add(corner);
        }
        var corners = new List<Coordinate>();
        foreach (var corner in result.Corners)
        {
            var section = result.Sections[corner.FromPath];
            Vector2 right = section.Center - section.Left * section.Width * 0.5f;
            corners.Add(new Coordinate(right.x, right.y));
            for (int i = 0; i < corner.Points.Count - 1; i++)
                corners.Add(new Coordinate(corner.Points[i].x, corner.Points[i].y));
        }
        corners.Add(corners[0].Copy());
        var polygon = gf.CreatePolygon(corners.ToArray());
        // Trying to repair an overly short branch would erase its entrance section, so explicitly report insufficient junction space.
        bool valid = polygon.IsValid && polygon.Area > 1e-6;
        if (valid) foreach (var section in result.Sections.Values)
        {
            var center = gf.CreatePoint(new Coordinate(section.Center.x, section.Center.y));
            if (polygon.Boundary.Distance(center) > 0.001) { valid = false; break; }
        }
        result.RequiresMoreSpace = !valid;
        result.Geometry = valid ? polygon : gf.CreatePoint(new Coordinate(intersectionPos.x, intersectionPos.y));
        if (!valid)
        {
            result.Sections.Clear();
            result.ConnectionPoints.Clear();
            result.Corners.Clear();
        }
        return result;
    }

    public static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);
    public static float Length(List<Vector3> points)
    {
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(XZ(points[i - 1]), XZ(points[i]));
        return length;
    }

    public static PathEndSection AtDistance(List<Vector3> points, float distance)
    {
        float remaining = distance;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 delta = XZ(points[i] - points[i - 1]);
            float length = delta.magnitude;
            if (length < 1e-6f) continue;
            if (remaining <= length || i == points.Count - 1)
                return new PathEndSection { Position = Vector3.Lerp(points[i - 1], points[i], remaining / length), Direction = delta / length, Tangent = (points[i] - points[i - 1]).normalized, Distance = distance };
            remaining -= length;
        }
        return new PathEndSection { Position = points[0], Direction = Vector2.up };
    }

    /// <summary>Find the route position nearest the cut point and retain only the interior portion of the original polyline.</summary>
    public static List<Vector3> Trim(List<Vector3> points, Vector3? start, Vector3? end)
    {
        if (points.Count < 2 || (!start.HasValue && !end.HasValue)) return points;
        float total = Length(points);
        float from = start.HasValue ? ProjectDistance(points, start.Value) : 0f;
        float to = end.HasValue ? ProjectDistance(points, end.Value) : total;
        if (to - from < 1e-5f) return points;
        var result = new List<Vector3> { AtDistance(points, from).Position };
        float distance = 0f;
        for (int i = 1; i < points.Count - 1; i++)
        {
            distance += Vector2.Distance(XZ(points[i - 1]), XZ(points[i]));
            if (distance > from + 1e-5f && distance < to - 1e-5f) result.Add(points[i]);
        }
        result.Add(AtDistance(points, to).Position);
        return result;
    }

    private static float ProjectDistance(List<Vector3> points, Vector3 point)
    {
        float best = float.PositiveInfinity, result = 0f, distance = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 delta = XZ(points[i] - points[i - 1]);
            float length = delta.magnitude;
            if (length < 1e-6f) continue;
            float t = PolylineUtils.ProjectOnSegment(XZ(point), XZ(points[i - 1]), XZ(points[i]));
            float error = (XZ(point) - Vector2.Lerp(XZ(points[i - 1]), XZ(points[i]), t)).sqrMagnitude;
            if (error < best) { best = error; result = distance + length * t; }
            distance += length;
        }
        return result;
    }

    /// <summary>Planar Bezier evaluation prioritizing endpoint controls, for test scenes and virtual polyline nodes.</summary>
    public static List<Vector3> SamplePlan(PathSummary path)
    {
        var start = path.GetStartPointList();
        var end = path.GetEndPointList();
        if (start == null || end == null || start.Count < 3 || end.Count < 3) return null;
        var anchors = new List<Vector3> { new Vector3(start[0], start[1], start[2]) };
        var waypoints = path.GetWaypointList();
        if (waypoints != null) foreach (var w in waypoints) anchors.Add(new Vector3(w.position[0], w.position[1], w.position[2]));
        anchors.Add(new Vector3(end[0], end[1], end[2]));
        if (!path.curved) return anchors;
        var result = new List<Vector3> { anchors[0] };
        for (int i = 0; i < anchors.Count - 1; i++)
        {
            Vector3 p0 = anchors[i], p3 = anchors[i + 1];
            Vector3 p1 = p0 + (anchors[Math.Min(i + 1, anchors.Count - 1)] - anchors[Math.Max(0, i - 1)]) / 6f;
            Vector3 p2 = p3 - (anchors[Math.Min(i + 2, anchors.Count - 1)] - anchors[i]) / 6f;
            if (anchors.Count == 2) { p1 = Vector3.Lerp(p0, p3, 1f / 3f); p2 = Vector3.Lerp(p0, p3, 2f / 3f); }
            var c1 = i == 0 ? path.GetStartControlList()?.ToArray() : waypoints[i - 1].control_out;
            var c2 = i == anchors.Count - 2 ? path.GetEndControlList()?.ToArray() : waypoints[i].control_in;
            if (c1 != null && c1.Length >= 3) p1 = new Vector3(c1[0], c1[1], c1[2]);
            if (c2 != null && c2.Length >= 3) p2 = new Vector3(c2[0], c2[1], c2[2]);
            int count = Mathf.Max(2, Mathf.CeilToInt((Vector3.Distance(p0,p1) + Vector3.Distance(p1,p2) + Vector3.Distance(p2,p3)) / 1f));
            for (int j = 1; j <= count; j++) { float t = (float)j / count, u = 1f - t; result.Add(u*u*u*p0 + 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t*p3); }
        }
        return result;
    }
}
