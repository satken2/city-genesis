using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure data for the lanes of a mashup building: the lines between adjacent wings where the building's lane part
/// (footpaths between huts, pipes between tanks) is tiled.
/// Wings are authored smaller than the cells they occupy, so the boundary between two wings' cell blocks runs down the middle of the gap between them.
/// A lane lies on every cell-length piece of boundary shared by two wings; boundaries facing a vacant cell or the parcel edge get none,
/// so lanes never leave the site. Wings off the first wing's cell grid (free placement) share no boundary and get no lanes.
/// Lanes along the first wing's width run through junctions; lanes across them stop half a lane width short, so flat parts never overlap.
/// All positions use local XZ relative to center_point.
/// </summary>
public static class MashupLaneLayout
{
    private const float GRID_TOLERANCE = 0.05f;  // Largest offset from the half-cell grid, in half cells.

    public struct Wing
    {
        /// <summary>Center of the occupied cell block (planned placement, before any loose shift).</summary>
        public Vector2 Position;
        /// <summary>Rotation in degrees, counterclockwise from +X; the block's width follows it.</summary>
        public float Rotation;
        public int CellsWide, CellsDeep;
    }

    public struct Run
    {
        public Vector2 Start, End;
    }

    /// <summary>Plan the lane runs. laneWidth is the lane part's extent across the run.</summary>
    public static List<Run> Plan(IReadOnlyList<Wing> wings, float cellSize, float laneWidth)
    {
        var runs = new List<Run>();
        if (wings == null || wings.Count < 2 || cellSize <= 0f)
        {
            return runs;
        }

        // Grid frame of the first wing. Coordinates count half cells, so block edges of odd and even spans are both integers.
        float half = cellSize * 0.5f;
        float angle = wings[0].Rotation * Mathf.Deg2Rad;
        var axisA = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        var axisB = new Vector2(-axisA.y, axisA.x);
        Vector2 origin = wings[0].Position;

        // Cell-length boundary pieces keyed by (line, start along the line); a piece seen from two blocks separates two wings.
        var alongA = new Dictionary<Vector2Int, int>();
        var alongB = new Dictionary<Vector2Int, int>();
        foreach (Wing wing in wings)
        {
            float turns = (wing.Rotation - wings[0].Rotation) / 90f;
            int quarter = Mathf.RoundToInt(turns);
            if (Mathf.Abs(turns - quarter) > GRID_TOLERANCE)
            {
                continue;
            }
            bool turned = (quarter & 1) != 0;
            int spanA = turned ? wing.CellsDeep : wing.CellsWide;
            int spanB = turned ? wing.CellsWide : wing.CellsDeep;
            Vector2 relative = wing.Position - origin;
            float a = Vector2.Dot(relative, axisA) / half, b = Vector2.Dot(relative, axisB) / half;
            int lowA = Mathf.RoundToInt(a - spanA), lowB = Mathf.RoundToInt(b - spanB);
            if (Mathf.Abs(a - spanA - lowA) > GRID_TOLERANCE || Mathf.Abs(b - spanB - lowB) > GRID_TOLERANCE)
            {
                continue;
            }
            int highA = lowA + spanA * 2, highB = lowB + spanB * 2;
            for (int s = lowA; s < highA; s += 2)
            {
                Count(alongA, new Vector2Int(lowB, s));
                Count(alongA, new Vector2Int(highB, s));
            }
            for (int s = lowB; s < highB; s += 2)
            {
                Count(alongB, new Vector2Int(lowA, s));
                Count(alongB, new Vector2Int(highA, s));
            }
        }

        // Grid nodes (a, b) touched by lanes of each direction.
        var nodesA = new HashSet<Vector2Int>();
        var nodesB = new HashSet<Vector2Int>();
        List<Vector2Int> lanesA = Shared(alongA), lanesB = Shared(alongB);
        foreach (Vector2Int lane in lanesA)
        {
            nodesA.Add(new Vector2Int(lane.y, lane.x));
            nodesA.Add(new Vector2Int(lane.y + 2, lane.x));
        }
        foreach (Vector2Int lane in lanesB)
        {
            nodesB.Add(new Vector2Int(lane.x, lane.y));
            nodesB.Add(new Vector2Int(lane.x, lane.y + 2));
        }

        // Lanes along A merge into straight runs and reach half a lane width past an end where a crossing lane meets it, filling the corner.
        float reach = laneWidth * 0.5f / half;
        for (int i = 0; i < lanesA.Count; i++)
        {
            int line = lanesA[i].x, start = lanesA[i].y, end = start + 2;
            while (i + 1 < lanesA.Count && lanesA[i + 1].x == line && lanesA[i + 1].y == end)
            {
                end += 2;
                i++;
            }
            float from = start - (nodesB.Contains(new Vector2Int(start, line)) ? reach : 0f);
            float to = end + (nodesB.Contains(new Vector2Int(end, line)) ? reach : 0f);
            runs.Add(new Run { Start = origin + (axisA * from + axisB * line) * half, End = origin + (axisA * to + axisB * line) * half });
        }
        // Lanes along B break at every node a lane along A touches and stop half a lane width short of it.
        for (int i = 0; i < lanesB.Count; i++)
        {
            int line = lanesB[i].x, start = lanesB[i].y, end = start + 2;
            while (i + 1 < lanesB.Count && lanesB[i + 1].x == line && lanesB[i + 1].y == end && !nodesA.Contains(new Vector2Int(line, end)))
            {
                end += 2;
                i++;
            }
            float from = start + (nodesA.Contains(new Vector2Int(line, start)) ? reach : 0f);
            float to = end - (nodesA.Contains(new Vector2Int(line, end)) ? reach : 0f);
            runs.Add(new Run { Start = origin + (axisA * line + axisB * from) * half, End = origin + (axisA * line + axisB * to) * half });
        }
        return runs;
    }

    private static void Count(Dictionary<Vector2Int, int> pieces, Vector2Int key)
    {
        pieces.TryGetValue(key, out int count);
        pieces[key] = count + 1;
    }

    /// <summary>Pieces seen from two blocks, ordered by line and then along it.</summary>
    private static List<Vector2Int> Shared(Dictionary<Vector2Int, int> pieces)
    {
        var shared = new List<Vector2Int>();
        foreach (KeyValuePair<Vector2Int, int> piece in pieces)
        {
            if (piece.Value >= 2)
            {
                shared.Add(piece.Key);
            }
        }
        shared.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
        return shared;
    }
}
