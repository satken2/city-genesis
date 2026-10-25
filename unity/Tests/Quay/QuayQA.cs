using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Isolated verification of quays on reclaimed land (README.md). run-unity.ps1 copies this to the isolated project in Temp/QuayQA and calls Run in Edit mode with batchmode.
/// The main code is called through reflection using Temp/bin/Debug/Assembly-CSharp.dll created by Tests/BuildRuntime.ps1.
///
/// A: Mirror the terrain-write rules on completion of Earthworks (the Earthworks completion handler in genesis-core; private) and terrain-tile resampling
///    (genesis-core's terrain_surface.tile), then use actual Terrain to inspect mounds outside the quay, trenches beyond the top, and
///    terrain protruding above the top. Use the same mirrored calculation to find the minimum width with no leakage of influence.
///    If the rules on the core side change, update this mirror to match.
/// B: The shape of ReclaimedLandMeshBuilder (top surface and vertical faces).
/// C: PortBuildingMeshBuilder.FindFacingReclaimedQuays (whether a port faces a quay on reclaimed land), and
///    that the straight cut (BuildQuayFootprint) for a port that does not face the quay follows the backend's berth_line exactly.
///    Choosing the line itself (chord endpoints next to reclaimed land) is the core's rule and is tested there (test_port_berth_depth.py).
/// </summary>
public static class QuayQA
{
    const double TileSpacing = 500.0 / 512.0;  // Terrain-tile sample spacing (513 points across a 500 m cell)
    const int WindowResolution = 129;          // Verification Terrain resolution. Use the same spacing as the tile and cut out part of a 513-point tile
    const float BelowSea = 200f;               // TerrainManager.SeaFloorDepthSpan
    const float HeightSpan = 1200f;            // Terrain vertical dimension in TerrainManager
    const float HeightTolerance = 0.05f;       // Tolerance (m) accounting for 16-bit height quantization (about 3.7 cm)
    const float ModelTolerance = 0.03f;        // Maximum height difference (m) between the mirrored calculation and actual Terrain
    const float LeakRange = 4f;                // Distance (m) over which to check for mounds outside the quay
    const float SideMargin = 3f;               // Do not check points closer than this to the landward edge (farther than the rendering influence radius of about 2.8 m)
    const float UnityLattice = 0.4f;           // Spacing (m) between points where actual Terrain height is read
    const float ModelLattice = 0.1f;           // Spacing (m) between points used to find the minimum width; the resulting width has about the same precision
    const double LandWidth = 60;               // Width of the reclaimed-land rectangle (local u)
    const double LandDepth = 40;               // Depth of the reclaimed-land rectangle (local v; v=0 is the seaward edge)
    const double CoastV = 28;                  // Original coastline (v > CoastV is land)
    const double Seabed = -6;                  // Offshore seabed elevation (the difference from the top becomes the fill height)

    static readonly float[] Angles = { 0f, 10f, 22.5f, 30f, 45f, 60f, 67.5f, 90f };
    static readonly float[] Phases = { 0f, 0.25f, 0.5f, 0.75f };
    static readonly Vector2[] Bases = { new Vector2(200f, 200f), new Vector2(221.3f, 187.9f) };
    static readonly double[][] QuaySegments =
    {
        new[] { 0, CoastV, 0, 0 },
        new[] { 0, 0, LandWidth, 0 },
        new[] { LandWidth, 0, LandWidth, CoastV },
    };
    static readonly double[][] SideSegments =
    {
        new[] { LandWidth, CoastV, LandWidth, LandDepth },
        new[] { LandWidth, LandDepth, 0, LandDepth },
        new[] { 0, LandDepth, 0, CoastV },
    };

    static Assembly source;
    static readonly List<string> results = new List<string>();
    static int checks;
    static float keepWidth, bandWidth, probeDistance, seaTop;

    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-quaySourceRoot") + 1];
        string output = root + "/Temp/QuayQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { "/Temp/bin/Debug", "/Library/ScriptAssemblies", "/Assets/Plugins" })
                    if (File.Exists(root + dir + "/" + name)) return Assembly.LoadFrom(root + dir + "/" + name);
                return null;
            };
            source = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Type constants = T("Constants");
            keepWidth = ConstF(constants, "QUAY_KEEP_WIDTH");
            bandWidth = ConstF(constants, "QUAY_BAND_WIDTH");
            probeDistance = ConstF(constants, "QUAY_PROBE_DISTANCE");
            seaTop = ConstF(constants, "SEA_GRADING_TOP");
            results.Add($"Constants: QUAY_KEEP_WIDTH={keepWidth} QUAY_BAND_WIDTH={bandWidth} QUAY_PROBE_DISTANCE={probeDistance} SEA_GRADING_TOP={seaTop}");

            CheckTerrainLeak();
            CheckQuayMesh();
            CheckPortFacing();

            File.WriteAllText(output, $"PASS checks={checks}\n" + string.Join("\n", results));
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, $"FAIL checks={checks}\n" + string.Join("\n", results) + "\n" + ex);
            EditorApplication.Exit(1);
        }
    }

    static Type T(string name) => source.GetType(name) ?? throw new Exception("type not found: " + name);

    static float ConstF(Type type, string name)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static) ?? throw new Exception("constant not found: " + type.Name + "." + name);
        return Convert.ToSingle(field.GetRawConstantValue());
    }

    static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    // =====================================================================
    // A. Terrain leakage sweep
    // =====================================================================

    /// <summary>
    /// Synthetic terrain for one case. Reclaimed land is a rectangle in local coordinates u∈[0,W] and v∈[0,D], rotated by Angle into world (tile-local) coordinates and
    /// placed at the origin (Ox, Oz). The original coastline is the line v=CoastV; the quay section consists of the seaward edge and the portions of both side edges up to the coastline.
    /// </summary>
    sealed class LeakCase
    {
        public float Angle;
        public double Ox, Oz, Cos, Sin;
        public int K0X, K0Z;
        public int NodeX0, NodeZ0, NodeCols, NodeRows;
        public float[] Original;
        public double[] QuayDistance;
        public bool[] Inside;

        public void ToLocal(double x, double z, out double u, out double v)
        {
            double dx = x - Ox, dz = z - Oz;
            u = dx * Cos + dz * Sin;
            v = -dx * Sin + dz * Cos;
        }

        public void ToWorld(double u, double v, out double x, out double z)
        {
            x = Ox + u * Cos - v * Sin;
            z = Oz + u * Sin + v * Cos;
        }

        public int Node(int x, int z) => (z - NodeZ0) * NodeCols + (x - NodeX0);
    }

    enum Zone { None, Outside, Band, Beyond }

    static void CheckTerrainLeak()
    {
        var trash = new List<UnityEngine.Object>();
        try
        {
            bool diagonalA = CalibrateDiagonal(CreateCase(45f, Bases[0].x + 0.25, Bases[0].y + 0.5), trash);

            // First pass: verify the terrain written at the configured width using actual Terrain, and find the minimum width with the mirrored calculation.
            int caseCount = 0, outsideCount = 0, bandCount = 0, beyondCount = 0;
            double maxLeak = 0, maxGroove = 0, maxPoke = double.NegativeInfinity, maxModelError = 0;
            var strictKeep = new double[Angles.Length];
            var strictBand = new double[Angles.Length];
            var toleranceKeep = new double[Angles.Length];
            var toleranceBand = new double[Angles.Length];
            foreach ((int a, LeakCase c) in Cases())
            {
                caseCount++;
                VerifyOnTerrain(c, diagonalA, keepWidth, bandWidth, trash, ref outsideCount, ref bandCount, ref beyondCount,
                    ref maxLeak, ref maxGroove, ref maxPoke, ref maxModelError);
                DestroyAll(trash);
                CasePoints points = CollectPoints(c, diagonalA);
                strictKeep[a] = Math.Max(strictKeep[a], StrictKeep(c, points));
                strictBand[a] = Math.Max(strictBand[a], StrictBand(c, points, keepWidth));
                toleranceKeep[a] = Math.Max(toleranceKeep[a], ToleranceKeep(c, points));
                toleranceBand[a] = Math.Max(toleranceBand[a], ToleranceBand(c, points, keepWidth));
            }
            double minStrictKeep = MaxOf(strictKeep), minToleranceKeep = MaxOf(toleranceKeep);

            // Second pass: the BAND required for the minimum KEEP found above.
            double strictBandAtMin = 0, toleranceBandAtMin = 0;
            foreach ((int a, LeakCase c) in Cases())
            {
                CasePoints points = CollectPoints(c, diagonalA);
                strictBandAtMin = Math.Max(strictBandAtMin, StrictBand(c, points, minStrictKeep));
                toleranceBandAtMin = Math.Max(toleranceBandAtMin, ToleranceBand(c, points, minToleranceKeep));
            }

            // Verify the minimum width pair found above using actual Terrain as well (add one grid spacing of clearance to the band).
            double strictLeak = 0, strictGroove = 0, toleranceLeak = 0, toleranceGroove = 0;
            foreach (float angle in Angles)
                foreach (Vector2 origin in Bases)
                {
                    LeakCase c = CreateCase(angle, origin.x + 0.25, origin.y + 0.75);
                    VerifyNarrow(c, minStrictKeep, strictBandAtMin + ModelLattice, trash, ref strictLeak, ref strictGroove);
                    DestroyAll(trash);
                    VerifyNarrow(c, minToleranceKeep, toleranceBandAtMin + ModelLattice, trash, ref toleranceLeak, ref toleranceGroove);
                    DestroyAll(trash);
                }

            string diagonal = diagonalA ? "(0,0)-(1,1)" : "(1,0)-(0,1)";
            results.Add($"Terrain leak: {caseCount} cases (angles {string.Join("/", Angles)} deg, node phases {Phases.Length}x{Phases.Length}, tile offsets {Bases.Length}), " +
                        $"samples outside={outsideCount} band={bandCount} beyond={beyondCount}");
            results.Add($"Terrain leak (KEEP={keepWidth}, BAND={bandWidth}): max bump outside quay {maxLeak:F4}m, max groove beyond band {maxGroove:F4}m, " +
                        $"max poke above top in band {maxPoke:F4}m, mirror-vs-terrain {maxModelError:F4}m (diagonal {diagonal})");
            results.Add($"No-influence widths per angle (sampled alignments, {ModelLattice}m lattice, band with KEEP={keepWidth}): " + AngleTable(strictKeep, strictBand));
            results.Add($"Within-{HeightTolerance}m widths per angle ({seaTop - Seabed:F0}m fill step, band with KEEP={keepWidth}): " + AngleTable(toleranceKeep, toleranceBand));
            results.Add($"Minimum widths (no influence, sampled): KEEP >= {minStrictKeep:F2}m, BAND >= {MaxOf(strictBand):F2}m with KEEP={keepWidth}, " +
                        $"BAND >= {strictBandAtMin:F2}m with KEEP={minStrictKeep:F2}; terrain at those widths: bump {strictLeak:F4}m, groove {strictGroove:F4}m");
            results.Add($"Minimum widths (within {HeightTolerance}m): KEEP >= {minToleranceKeep:F2}m, BAND >= {MaxOf(toleranceBand):F2}m with KEEP={keepWidth}, " +
                        $"BAND >= {toleranceBandAtMin:F2}m with KEEP={minToleranceKeep:F2}; terrain at those widths: bump {toleranceLeak:F4}m, groove {toleranceGroove:F4}m");

            Require(maxModelError <= ModelTolerance, $"Mirror of the backend tile differs from the Unity terrain by {maxModelError:F4}m");
            Require(maxLeak <= HeightTolerance, $"Terrain bumps outside the quay by {maxLeak:F4}m");
            Require(maxGroove <= HeightTolerance, $"Terrain groove beyond the band by {maxGroove:F4}m");
            Require(maxPoke <= HeightTolerance, $"Terrain pokes above the quay top by {maxPoke:F4}m");
            Require(minStrictKeep <= keepWidth + 1e-6, $"QUAY_KEEP_WIDTH={keepWidth} is narrower than the required {minStrictKeep:F2}m");
            Require(MaxOf(strictBand) <= bandWidth + 1e-6, $"QUAY_BAND_WIDTH={bandWidth} is narrower than the required {MaxOf(strictBand):F2}m");
            Require(strictLeak <= HeightTolerance && strictGroove <= HeightTolerance && toleranceLeak <= HeightTolerance && toleranceGroove <= HeightTolerance,
                "The minimum widths do not pass on the Unity terrain");

            if (!diagonalA)
            {
                results.Add("Worst alignment: skipped (the terrain is not split along (0,0)-(1,1))");
                return;
            }
            (double worstKeep, double worstBand) = WorstAlignment();
            double bound = (TileSpacing + 1.0) * Math.Sqrt(2.0);
            results.Add($"Worst alignment (no influence; quay along the triangle diagonal, tile sample 1/128m from a node): KEEP >= {worstKeep:F3}m, " +
                        $"BAND >= {worstBand:F3}m with KEEP={keepWidth} (bound: tile diagonal + node diagonal = {bound:F3}m)");
            Require(worstKeep >= bound - 0.05 && worstBand >= keepWidth + bound - 0.05, "Worst alignment construction did not reach the bound");
            Require(worstKeep <= keepWidth + 1e-6 && worstBand <= bandWidth + 1e-6,
                $"The configured widths do not cover the worst alignment (KEEP {worstKeep:F3}m, BAND {worstBand:F3}m)");
        }
        finally
        {
            DestroyAll(trash);
        }
    }

    static IEnumerable<(int angle, LeakCase c)> Cases()
    {
        for (int a = 0; a < Angles.Length; a++)
            foreach (Vector2 origin in Bases)
                foreach (float px in Phases)
                    foreach (float pz in Phases)
                        yield return (a, CreateCase(Angles[a], origin.x + px, origin.y + pz));
    }

    static string AngleTable(double[] keep, double[] band)
    {
        var table = new List<string>();
        for (int a = 0; a < Angles.Length; a++) table.Add($"{Angles[a]}deg keep>={keep[a]:F2} band>={band[a]:F2}");
        return string.Join(", ", table);
    }

    static double MaxOf(double[] values)
    {
        double max = double.NegativeInfinity;
        foreach (double v in values) max = Math.Max(max, v);
        return max;
    }

    static void DestroyAll(List<UnityEngine.Object> trash)
    {
        foreach (UnityEngine.Object o in trash)
            if (o != null) UnityEngine.Object.DestroyImmediate(o);
        trash.Clear();
    }

    static LeakCase CreateCase(float angle, double ox, double oz)
    {
        double radians = angle * Math.PI / 180.0;
        var c = new LeakCase { Angle = angle, Ox = ox, Oz = oz, Cos = Math.Cos(radians), Sin = Math.Sin(radians) };
        c.ToWorld(LandWidth / 2, LandDepth / 2, out double cx, out double cz);
        double half = (WindowResolution - 1) * TileSpacing / 2;
        c.K0X = (int)Math.Floor((cx - half) / TileSpacing);
        c.K0Z = (int)Math.Floor((cz - half) / TileSpacing);
        c.NodeX0 = (int)Math.Floor(c.K0X * TileSpacing) - 1;
        c.NodeZ0 = (int)Math.Floor(c.K0Z * TileSpacing) - 1;
        c.NodeCols = (int)Math.Ceiling((c.K0X + WindowResolution - 1) * TileSpacing) - c.NodeX0 + 3;
        c.NodeRows = (int)Math.Ceiling((c.K0Z + WindowResolution - 1) * TileSpacing) - c.NodeZ0 + 3;
        int count = c.NodeCols * c.NodeRows;
        c.Original = new float[count];
        c.QuayDistance = new double[count];
        c.Inside = new bool[count];
        for (int j = 0; j < c.NodeRows; j++)
            for (int i = 0; i < c.NodeCols; i++)
            {
                int n = j * c.NodeCols + i;
                c.ToLocal(c.NodeX0 + i, c.NodeZ0 + j, out double u, out double v);
                c.Original[n] = OriginalHeight(u, v);
                c.QuayDistance[n] = DistanceTo(QuaySegments, u, v);
                c.Inside[n] = u >= -1e-9 && u <= LandWidth + 1e-9 && v >= -1e-9 && v <= LandDepth + 1e-9;
            }
        return c;
    }

    /// <summary>Original terrain. Before v=CoastV, the seabed rises offshore toward the beach; beyond it is land below the top. Add slight variation.</summary>
    static float OriginalHeight(double u, double v)
    {
        double ripple = 0.25 * Math.Sin(0.37 * u + 0.11 * v) * Math.Cos(0.23 * v - 0.05 * u);
        if (v >= CoastV) return (float)(Math.Min(1.5 + 0.05 * (v - CoastV), 2.3) + 0.4 * ripple);
        double t = Math.Max(0, Math.Min(1, (v - (CoastV - 15)) / 15));
        return (float)(Seabed + (1.5 - Seabed) * t + ripple);
    }

    static double DistanceTo(double[][] segments, double u, double v)
    {
        double best = double.PositiveInfinity;
        foreach (double[] s in segments)
        {
            double dx = s[2] - s[0], dz = s[3] - s[1];
            double t = Math.Max(0, Math.Min(1, ((u - s[0]) * dx + (v - s[1]) * dz) / (dx * dx + dz * dz)));
            double ex = s[0] + dx * t - u, ez = s[1] + dz * t - v;
            best = Math.Min(best, ex * ex + ez * ez);
        }
        return Math.Sqrt(best);
    }

    /// <summary>
    /// Mirror of the write rules on completion: among 1 m grid points on reclaimed land (including the boundary) farther than keep from the quay section,
    /// raise points below the top to the top's height and leave the others at their original height.
    /// </summary>
    static double NodeValue(LeakCase c, int x, int z, double keep, bool edited)
    {
        int n = c.Node(x, z);
        return edited && c.Inside[n] && c.QuayDistance[n] > keep ? Math.Max(seaTop, c.Original[n]) : c.Original[n];
    }

    /// <summary>
    /// Mirror of terrain-tile sampling: bilinearly sample the 1 m grid at sample (K0+i)·500/512 and convert to float32
    /// (using the same order of weight multiplication as terrain_surface.sample).
    /// </summary>
    static float[] BuildTile(LeakCase c, double keep, bool edited)
    {
        var tile = new float[WindowResolution * WindowResolution];
        for (int j = 0; j < WindowResolution; j++)
        {
            double z = (c.K0Z + j) * TileSpacing;
            int iz = (int)Math.Floor(z);
            double fz = z - iz;
            for (int i = 0; i < WindowResolution; i++)
            {
                double x = (c.K0X + i) * TileSpacing;
                int ix = (int)Math.Floor(x);
                double fx = x - ix;
                double sum = NodeValue(c, ix, iz, keep, edited) * (1 - fz) * (1 - fx);
                sum += NodeValue(c, ix + 1, iz, keep, edited) * (1 - fz) * fx;
                sum += NodeValue(c, ix, iz + 1, keep, edited) * fz * (1 - fx);
                sum += NodeValue(c, ix + 1, iz + 1, keep, edited) * fz * fx;
                tile[j * WindowResolution + i] = (float)sum;
            }
        }
        return tile;
    }

    /// <summary>Grid points (up to four) with bilinear weights for sample (i, j).</summary>
    static int SupportNodes(LeakCase c, int i, int j, int[] nodes)
    {
        double x = (c.K0X + i) * TileSpacing, z = (c.K0Z + j) * TileSpacing;
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        double fx = x - ix, fz = z - iz;
        int count = 0;
        if ((1 - fz) * (1 - fx) > 1e-12) nodes[count++] = c.Node(ix, iz);
        if ((1 - fz) * fx > 1e-12) nodes[count++] = c.Node(ix + 1, iz);
        if (fz * (1 - fx) > 1e-12) nodes[count++] = c.Node(ix, iz + 1);
        if (fz * fx > 1e-12) nodes[count++] = c.Node(ix + 1, iz + 1);
        return count;
    }

    /// <summary>
    /// Vertices (sample indices) and weights of the rendered triangle containing world coordinates (x, z). diagonalA splits along the (0,0)–(1,1) diagonal
    /// (as in TerrainData.GetInterpolatedHeight); false uses (1,0)–(0,1). Determine this by comparing with actual Terrain.
    /// </summary>
    static void Triangle(LeakCase c, double x, double z, bool diagonalA, int[] index, double[] weight)
    {
        double gx = x / TileSpacing - c.K0X, gz = z / TileSpacing - c.K0Z;
        int i = Math.Max(0, Math.Min(WindowResolution - 2, (int)Math.Floor(gx)));
        int j = Math.Max(0, Math.Min(WindowResolution - 2, (int)Math.Floor(gz)));
        double a = gx - i, b = gz - j;
        int s00 = j * WindowResolution + i, s10 = s00 + 1, s01 = s00 + WindowResolution, s11 = s01 + 1;
        if (diagonalA)
        {
            if (a > b) { index[0] = s00; index[1] = s10; index[2] = s11; weight[0] = 1 - a; weight[1] = a - b; weight[2] = b; }
            else { index[0] = s00; index[1] = s01; index[2] = s11; weight[0] = 1 - b; weight[1] = b - a; weight[2] = a; }
        }
        else
        {
            if (a + b < 1) { index[0] = s00; index[1] = s10; index[2] = s01; weight[0] = 1 - a - b; weight[1] = a; weight[2] = b; }
            else { index[0] = s11; index[1] = s10; index[2] = s01; weight[0] = a + b - 1; weight[1] = 1 - b; weight[2] = 1 - a; }
        }
    }

    static double ModelHeight(LeakCase c, float[] tile, double x, double z, bool diagonalA, int[] index, double[] weight)
    {
        Triangle(c, x, z, diagonalA, index, weight);
        return tile[index[0]] * weight[0] + tile[index[1]] * weight[1] + tile[index[2]] * weight[2];
    }

    static TerrainCollider CreateTerrain(LeakCase c, float[] tile, List<UnityEngine.Object> trash)
    {
        var heights = new float[WindowResolution, WindowResolution];
        for (int j = 0; j < WindowResolution; j++)
            for (int i = 0; i < WindowResolution; i++)
                heights[j, i] = Mathf.Clamp01((tile[j * WindowResolution + i] + BelowSea) / HeightSpan);
        float size = (float)((WindowResolution - 1) * TileSpacing);
        var data = new TerrainData { heightmapResolution = WindowResolution, size = new Vector3(size, HeightSpan, size) };
        data.SetHeights(0, 0, heights);
        GameObject go = Terrain.CreateTerrainGameObject(data);
        go.transform.position = new Vector3((float)(c.K0X * TileSpacing), -BelowSea, (float)(c.K0Z * TileSpacing));
        trash.Add(go);
        trash.Add(data);
        Physics.SyncTransforms();
        return go.GetComponent<TerrainCollider>();
    }

    static float TerrainHeight(TerrainCollider collider, double x, double z)
    {
        var ray = new Ray(new Vector3((float)x, 1000f, (float)z), Vector3.down);
        if (!collider.Raycast(ray, out RaycastHit hit, 3000f)) throw new Exception($"terrain raycast missed at ({x:F2}, {z:F2})");
        return hit.point.y;
    }

    /// <summary>Classification of a point in local coordinates. Do not check near the landward edge, which is affected by the landward step (by design).</summary>
    static Zone Classify(double u, double v, double band, out double quayDistance)
    {
        quayDistance = DistanceTo(QuaySegments, u, v);
        if (DistanceTo(SideSegments, u, v) < SideMargin) return Zone.None;
        bool inside = u > 0 && u < LandWidth && v > 0 && v < LandDepth;
        if (!inside) return quayDistance > 1e-6 && quayDistance <= LeakRange ? Zone.Outside : Zone.None;
        return quayDistance <= band ? Zone.Band : Zone.Beyond;
    }

    /// <summary>Iterate over grid points in local coordinates. Skip the interior of reclaimed land far enough from the quay (beyond depth).</summary>
    static IEnumerable<(double u, double v)> Lattice(float step, double depth)
    {
        int columns = (int)Math.Round((LandWidth + 2 * LeakRange + 1) / step);
        int rows = (int)Math.Round((LandDepth + LeakRange + 0.5) / step);
        for (int j = 0; j <= rows; j++)
        {
            double v = -LeakRange - 0.5 + j * step;
            for (int i = 0; i <= columns; i++)
            {
                double u = -LeakRange - 0.5 + i * step;
                if (v > depth && u > depth && u < LandWidth - depth) continue;
                yield return (u, v);
            }
        }
    }

    static bool CalibrateDiagonal(LeakCase c, List<UnityEngine.Object> trash)
    {
        float[] tile = BuildTile(c, keepWidth, true);
        TerrainCollider collider = CreateTerrain(c, tile, trash);
        var index = new int[3];
        var weight = new double[3];
        double errorA = 0, errorB = 0;
        foreach ((double u, double v) in Lattice(UnityLattice, bandWidth + 4))
        {
            if (Classify(u, v, bandWidth, out _) == Zone.None) continue;
            c.ToWorld(u, v, out double x, out double z);
            float h = TerrainHeight(collider, x, z);
            errorA = Math.Max(errorA, Math.Abs(ModelHeight(c, tile, x, z, true, index, weight) - h));
            errorB = Math.Max(errorB, Math.Abs(ModelHeight(c, tile, x, z, false, index, weight) - h));
        }
        DestroyAll(trash);
        results.Add($"Terrain triangulation: mirror error {errorA:F4}m with diagonal (0,0)-(1,1), {errorB:F4}m with (1,0)-(0,1)");
        Require(Math.Min(errorA, errorB) <= ModelTolerance, $"Neither diagonal reproduces the Unity terrain ({errorA:F4}m / {errorB:F4}m)");
        return errorA <= errorB;
    }

    /// <summary>Create actual Terrain from terrain written at the configured width, then check heights outside the quay, within the band, and beyond the band. Compare with the mirrored calculation as well.</summary>
    static void VerifyOnTerrain(LeakCase c, bool diagonalA, float keep, float band, List<UnityEngine.Object> trash,
        ref int outsideCount, ref int bandCount, ref int beyondCount,
        ref double maxLeak, ref double maxGroove, ref double maxPoke, ref double maxModelError)
    {
        float[] original = BuildTile(c, keep, false);
        float[] edited = BuildTile(c, keep, true);
        TerrainCollider originalCollider = CreateTerrain(c, original, trash);
        TerrainCollider editedCollider = CreateTerrain(c, edited, trash);
        var index = new int[3];
        var weight = new double[3];
        foreach ((double u, double v) in Lattice(UnityLattice, band + 4))
        {
            Zone zone = Classify(u, v, band, out double quayDistance);
            if (zone == Zone.None || (zone == Zone.Beyond && quayDistance > band + 4)) continue;
            c.ToWorld(u, v, out double x, out double z);
            float before = TerrainHeight(originalCollider, x, z);
            float after = TerrainHeight(editedCollider, x, z);
            maxModelError = Math.Max(maxModelError, Math.Abs(ModelHeight(c, original, x, z, diagonalA, index, weight) - before));
            maxModelError = Math.Max(maxModelError, Math.Abs(ModelHeight(c, edited, x, z, diagonalA, index, weight) - after));
            switch (zone)
            {
                case Zone.Outside:
                    outsideCount++;
                    maxLeak = Math.Max(maxLeak, Math.Abs(after - before));
                    break;
                case Zone.Band:
                    bandCount++;
                    maxPoke = Math.Max(maxPoke, after - seaTop);
                    break;
                case Zone.Beyond:
                    beyondCount++;
                    maxGroove = Math.Max(maxGroove, Math.Abs(after - seaTop));
                    break;
            }
        }
    }

    /// <summary>Create actual Terrain from terrain written at the minimum width found, and check only outside the quay and beyond the band.</summary>
    static void VerifyNarrow(LeakCase c, double keep, double band, List<UnityEngine.Object> trash, ref double maxLeak, ref double maxGroove)
    {
        TerrainCollider originalCollider = CreateTerrain(c, BuildTile(c, keep, false), trash);
        TerrainCollider editedCollider = CreateTerrain(c, BuildTile(c, keep, true), trash);
        foreach ((double u, double v) in Lattice(UnityLattice, band + 4))
        {
            Zone zone = Classify(u, v, band, out double quayDistance);
            if (zone != Zone.Outside && !(zone == Zone.Beyond && quayDistance <= band + 4)) continue;
            c.ToWorld(u, v, out double x, out double z);
            float after = TerrainHeight(editedCollider, x, z);
            if (zone == Zone.Outside) maxLeak = Math.Max(maxLeak, Math.Abs(after - TerrainHeight(originalCollider, x, z)));
            else maxGroove = Math.Max(maxGroove, Math.Abs(after - seaTop));
        }
    }

    /// <summary>Vertices (sample indices) and weights of the rendered triangle containing a point, and its distance from the quay.</summary>
    struct TrianglePoint
    {
        public int I0, I1, I2;
        public double W0, W1, W2, QuayDistance;
    }

    sealed class CasePoints
    {
        public readonly List<TrianglePoint> Outside = new List<TrianglePoint>();
        public readonly List<TrianglePoint> Inside = new List<TrianglePoint>();
    }

    static TrianglePoint ToTrianglePoint(LeakCase c, double x, double z, double quayDistance, bool diagonalA)
    {
        var index = new int[3];
        var weight = new double[3];
        Triangle(c, x, z, diagonalA, index, weight);
        return new TrianglePoint { I0 = index[0], I1 = index[1], I2 = index[2], W0 = weight[0], W1 = weight[1], W2 = weight[2], QuayDistance = quayDistance };
    }

    /// <summary>Points used to find the minimum width: within LeakRange outside the quay and within KEEP+4 m of the quay on reclaimed land (excluding points near the landward edge in both cases).</summary>
    static CasePoints CollectPoints(LeakCase c, bool diagonalA)
    {
        var points = new CasePoints();
        double depth = keepWidth + 4;
        foreach ((double u, double v) in Lattice(ModelLattice, depth))
        {
            Zone zone = Classify(u, v, double.PositiveInfinity, out double quayDistance);
            if (zone == Zone.None || quayDistance > depth) continue;
            c.ToWorld(u, v, out double x, out double z);
            (zone == Zone.Outside ? points.Outside : points.Inside).Add(ToTrianglePoint(c, x, z, quayDistance, diagonalA));
        }
        return points;
    }

    /// <summary>For each sample, the greatest distance from the quay among reclaimed-land grid points with bilinear weights (or -1 if none).</summary>
    static double[] SampleKeepRequirement(LeakCase c)
    {
        var requirement = new double[WindowResolution * WindowResolution];
        var nodes = new int[4];
        for (int j = 0; j < WindowResolution; j++)
            for (int i = 0; i < WindowResolution; i++)
            {
                double value = -1;
                int count = SupportNodes(c, i, j, nodes);
                for (int k = 0; k < count; k++)
                    if (c.Inside[nodes[k]]) value = Math.Max(value, c.QuayDistance[nodes[k]]);
                requirement[j * WindowResolution + i] = value;
            }
        return requirement;
    }

    /// <summary>Whether, for each sample, all grid points with weights are written at the top height.</summary>
    static bool[] SampleWritten(LeakCase c, double keep)
    {
        var written = new bool[WindowResolution * WindowResolution];
        var nodes = new int[4];
        for (int j = 0; j < WindowResolution; j++)
            for (int i = 0; i < WindowResolution; i++)
            {
                bool all = true;
                int count = SupportNodes(c, i, j, nodes);
                for (int k = 0; k < count; k++)
                    all &= c.Inside[nodes[k]] && c.QuayDistance[nodes[k]] > keep;
                written[j * WindowResolution + i] = all;
            }
        return written;
    }

    static double PointRequirement(TrianglePoint p, double[] requirement)
    {
        double value = -1;
        if (p.W0 > 1e-9) value = Math.Max(value, requirement[p.I0]);
        if (p.W1 > 1e-9) value = Math.Max(value, requirement[p.I1]);
        if (p.W2 > 1e-9) value = Math.Max(value, requirement[p.I2]);
        return value;
    }

    static bool PointWritten(TrianglePoint p, bool[] written) =>
        (p.W0 <= 1e-9 || written[p.I0]) && (p.W1 <= 1e-9 || written[p.I1]) && (p.W2 <= 1e-9 || written[p.I2]);

    static double Height(TrianglePoint p, float[] tile) => tile[p.I0] * p.W0 + tile[p.I1] * p.W1 + tile[p.I2] * p.W2;

    /// <summary>
    /// Minimum KEEP for which writes have no effect at all on points outside the quay. If a vertex (sample) of the triangle containing a point has a weight on a grid point on reclaimed land,
    /// KEEP must extend to at least that grid point's distance from the quay.
    /// </summary>
    static double StrictKeep(LeakCase c, CasePoints points)
    {
        double[] requirement = SampleKeepRequirement(c);
        double required = 0;
        foreach (TrianglePoint p in points.Outside) required = Math.Max(required, PointRequirement(p, requirement));
        return required;
    }

    /// <summary>For KEEP=keep, the maximum distance from the quay of any point on reclaimed land affected by a retained grid point (the band must extend this far).</summary>
    static double StrictBand(LeakCase c, CasePoints points, double keep)
    {
        bool[] written = SampleWritten(c, keep);
        double required = 0;
        foreach (TrianglePoint p in points.Inside)
            if (!PointWritten(p, written)) required = Math.Max(required, p.QuayDistance);
        return required;
    }

    /// <summary>
    /// Minimum KEEP for which mounds outside the quay stay within the tolerance. Narrow the configured width in 0.05 m steps and return the width just before it exceeds the tolerance
    /// (return +∞ if the configured width already exceeds it).
    /// </summary>
    static double ToleranceKeep(LeakCase c, CasePoints points)
    {
        float[] original = BuildTile(c, 0, false);
        double passing = double.PositiveInfinity;
        for (int step = 0; keepWidth - 0.05 * step >= 0; step++)
        {
            double keep = keepWidth - 0.05 * step;
            float[] edited = BuildTile(c, keep, true);
            double leak = 0;
            foreach (TrianglePoint p in points.Outside) leak = Math.Max(leak, Math.Abs(Height(p, edited) - Height(p, original)));
            if (leak > HeightTolerance) break;
            passing = keep;
        }
        return passing;
    }

    /// <summary>For KEEP=keep, the maximum distance from the quay of points on reclaimed land that differ from the top height by more than the tolerance.</summary>
    static double ToleranceBand(LeakCase c, CasePoints points, double keep)
    {
        float[] edited = BuildTile(c, keep, true);
        double required = 0;
        foreach (TrianglePoint p in points.Inside)
            if (Math.Abs(Height(p, edited) - seaTop) > HeightTolerance) required = Math.Max(required, p.QuayDistance);
        return required;
    }

    /// <summary>
    /// Create an arrangement where the influence reaches the farthest, and find the required KEEP and BAND (the width beyond which there is no influence).
    /// Rendered triangles split along the (0,0)–(1,1) diagonal. When the quay's inward normal points in the (+1,+1) direction, the distance from a point to a triangle vertex (sample)
    /// can reach at most the sample diagonal, and the distance from a sample to a weighted grid point can reach at most the grid diagonal. Sample positions advance in 500/512 m steps and shift relative to the grid by
    /// 1/128 m increments, so arrange samples 1/128 m beyond (KEEP) and before (BAND) a grid point.
    /// </summary>
    static (double keep, double band) WorstAlignment()
    {
        const double delta = 0.01, epsilon = 0.002, alongSide = 14;
        double e = Math.Sqrt(0.5);

        // KEEP: Sample 213 is 1/128 m beyond grid point 208 and has a weight on grid point 209 on the (+1,+1) side.
        // Position the left-side quay of the 45-degree reclaimed land (local u=0) so it passes delta outside the corner of the preceding sample 212.
        double q = 212 * TileSpacing;
        LeakCase keepCase = CreateCase(45f, q + delta * e + alongSide * e, q + delta * e - alongSide * e);
        double kx = q + epsilon;
        keepCase.ToLocal(kx, kx, out double ku, out double kv);
        Require(Classify(ku, kv, bandWidth, out double keepDistance) == Zone.Outside, "worst alignment: the keep probe is not outside the quay");
        double keep = PointRequirement(ToTrianglePoint(keepCase, kx, kx, keepDistance, true), SampleKeepRequirement(keepCase));

        // BAND: Sample 299 is 1/128 m before grid point 292 and has a weight on grid point 291 on the (-1,-1) side. Place grid point 291
        // delta inside KEEP, and check that a point just before the corner on the (+1,+1) side of sample 299 is at the top height.
        const double node = 291;
        LeakCase bandCase = CreateCase(45f, node - (keepWidth - delta) * e + alongSide * e, node - (keepWidth - delta) * e - alongSide * e);
        double bx = 300 * TileSpacing - epsilon;
        bandCase.ToLocal(bx, bx, out double bu, out double bv);
        Require(Classify(bu, bv, double.PositiveInfinity, out double bandDistance) == Zone.Band, "worst alignment: the band probe is not inside the land");
        bool clean = PointWritten(ToTrianglePoint(bandCase, bx, bx, bandDistance, true), SampleWritten(bandCase, keepWidth));
        return (keep, clean ? 0 : bandDistance);
    }

    // =====================================================================
    // B. Quay mesh (ReclaimedLandMeshBuilder)
    // =====================================================================

    struct Wall
    {
        public Vector2 A, B, Normal;
        public Wall(Vector2 a, Vector2 b, Vector2 normal) { A = a; B = b; Normal = normal; }
    }

    sealed class QuayMesh
    {
        public List<Vector3> Vertices;
        public List<Vector3> Normals;
        public List<Vector2> Uvs;
        public List<int> Top;
        public List<int> Walls;
    }

    static MethodInfo buildMethod;
    static float wallSpacing, wallFooting, wallMinHeight;

    static void CheckQuayMesh()
    {
        Type builder = T("ReclaimedLandMeshBuilder");
        buildMethod = builder.GetMethod("Build", BindingFlags.Public | BindingFlags.Static) ?? throw new Exception("ReclaimedLandMeshBuilder.Build not found");
        wallSpacing = ConstF(builder, "WALL_SAMPLE_SPACING");
        wallFooting = ConstF(builder, "WALL_FOOTING_DEPTH");
        wallMinHeight = ConstF(builder, "WALL_MIN_HEIGHT");
        float top = seaTop;
        float w = bandWidth;
        // The seabed rises toward the east and is higher than the top at x>55, also exercising clamping of the bottom of the face.
        Func<float, float, float> seabed = (x, z) => -6f + 0.05f * x + 0.3f * Mathf.Sin(0.2f * z) + (x > 55f ? 11f : 0f);
        Func<float, float, float> flat = (x, z) => -6f;

        // 1. Rectangle (only the seaward edge is a quay; both ends of the band form returns along the landward edge)
        Vector2[][][] rectBand = { new[] { new[] { V(0, 0), V(60, 0), V(60, w), V(0, w) } } };
        Wall[] rectWalls = { new Wall(V(0, 0), V(60, 0), V(0, -1)), new Wall(V(0, w), V(0, 0), V(-1, 0)), new Wall(V(60, 0), V(60, w), V(1, 0)) };
        QuayMesh rect = BuildQuay(rectBand, rectWalls, top, seabed);
        VerifyQuayMesh("rectangle", rect, rectBand, rectWalls, top, seabed);
        QuayMesh again = BuildQuay(rectBand, rectWalls, top, seabed);
        Require(SameMesh(rect, again), "Quay mesh is not deterministic");

        // 2. 45-degree rotation and translation (area remains unchanged; translation moves the vertices as-is)
        QuayMesh rectFlat = BuildQuay(rectBand, rectWalls, top, flat);
        var shift = V(137.3f, -42.9f);
        QuayMesh moved = BuildQuay(Transform(rectBand, 0f, shift), Transform(rectWalls, 0f, shift), top, flat);
        Require(moved.Vertices.Count == rectFlat.Vertices.Count && SameIndices(moved.Top, rectFlat.Top) && SameIndices(moved.Walls, rectFlat.Walls),
            "Translated quay mesh has a different topology");
        float moveError = 0;
        for (int i = 0; i < moved.Vertices.Count; i++)
            moveError = Mathf.Max(moveError, (moved.Vertices[i] - rectFlat.Vertices[i] - new Vector3(shift.x, 0, shift.y)).magnitude);
        Require(moveError <= 1e-3f, $"Translated quay mesh moved by {moveError}m more than the translation");
        Vector2[][][] rotatedBand = Transform(rectBand, 45f, shift);
        Wall[] rotatedWalls = Transform(rectWalls, 45f, shift);
        QuayMesh rotated = BuildQuay(rotatedBand, rotatedWalls, top, flat);
        VerifyQuayMesh("rotated 45deg", rotated, rotatedBand, rotatedWalls, top, flat);
        double rectTop = TopArea(rectFlat), rotTop = TopArea(rotated), rectWall = WallArea(rectFlat), rotWall = WallArea(rotated);
        Require(Math.Abs(rectTop - rotTop) <= 1e-3 * rectTop && Math.Abs(rectWall - rotWall) <= 1e-3 * rectWall,
            $"Rotation changed the areas (top {rectTop:F3}/{rotTop:F3}, wall {rectWall:F3}/{rotWall:F3})");

        // 3. Concave L shape (the band at the inner corner is an arc)
        var lBandRing = new List<Vector2> { V(0, 0), V(60, 0), V(60, 20), V(20, 20), V(20, 50), V(20 - w, 50), V(20 - w, 20) };
        for (int k = 1; k < 8; k++)
        {
            float angle = Mathf.PI + k * Mathf.PI / 2f / 8f;
            lBandRing.Add(V(20 + w * Mathf.Cos(angle), 20 + w * Mathf.Sin(angle)));
        }
        lBandRing.AddRange(new[] { V(20, 20 - w), V(60 - w, 20 - w), V(60 - w, w), V(0, w) });
        Vector2[][][] lBand = { new[] { lBandRing.ToArray() } };
        Wall[] lWalls =
        {
            new Wall(V(0, 0), V(60, 0), V(0, -1)), new Wall(V(60, 0), V(60, 20), V(1, 0)), new Wall(V(60, 20), V(20, 20), V(0, 1)),
            new Wall(V(20, 20), V(20, 50), V(1, 0)), new Wall(V(0, w), V(0, 0), V(-1, 0)), new Wall(V(20, 50), V(20 - w, 50), V(0, 1)),
        };
        VerifyQuayMesh("concave L", BuildQuay(lBand, lWalls, top, seabed), lBand, lWalls, top, seabed);

        // 4. Clockwise input (faces still point upward and outward when the band ring and face sections are reversed)
        var cwRing = (Vector2[])rectBand[0][0].Clone();
        Array.Reverse(cwRing);
        Vector2[][][] cwBand = { new[] { cwRing } };
        var cwWalls = new Wall[rectWalls.Length];
        for (int i = 0; i < rectWalls.Length; i++) cwWalls[i] = new Wall(rectWalls[i].B, rectWalls[i].A, rectWalls[i].Normal);
        QuayMesh cw = BuildQuay(cwBand, cwWalls, top, seabed);
        VerifyQuayMesh("clockwise input", cw, cwBand, cwWalls, top, seabed);
        Require(Math.Abs(TopArea(cw) - TopArea(rect)) <= 1e-3 * TopArea(rect) && Math.Abs(WallArea(cw) - WallArea(rect)) <= 1e-3 * WallArea(rect),
            "Clockwise input changed the quay areas");

        // 5. Island surrounded by quays (the band has a hole)
        Vector2[][][] islandBand = { new[] { new[] { V(0, 0), V(40, 0), V(40, 30), V(0, 30) }, new[] { V(w, w), V(w, 30 - w), V(40 - w, 30 - w), V(40 - w, w) } } };
        Wall[] islandWalls = { new Wall(V(0, 0), V(40, 0), V(0, -1)), new Wall(V(40, 0), V(40, 30), V(1, 0)), new Wall(V(40, 30), V(0, 30), V(0, 1)), new Wall(V(0, 30), V(0, 0), V(-1, 0)) };
        VerifyQuayMesh("island with hole", BuildQuay(islandBand, islandWalls, top, seabed), islandBand, islandWalls, top, seabed);

        results.Add($"Quay mesh: rectangle/rotated/concave L/clockwise/island checked; rectangle top {rectTop:F2}m2 wall {WallArea(rect):F2}m2, " +
                    $"deterministic, translation error {moveError:E1}m");
    }

    static Vector2 V(float x, float z) => new Vector2(x, z);

    static Vector2 Rotate(Vector2 p, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad, cos = Mathf.Cos(r), sin = Mathf.Sin(r);
        return new Vector2(p.x * cos - p.y * sin, p.x * sin + p.y * cos);
    }

    static Vector2[][][] Transform(Vector2[][][] band, float degrees, Vector2 shift)
    {
        var result = new Vector2[band.Length][][];
        for (int p = 0; p < band.Length; p++)
        {
            result[p] = new Vector2[band[p].Length][];
            for (int r = 0; r < band[p].Length; r++)
            {
                result[p][r] = new Vector2[band[p][r].Length];
                for (int i = 0; i < band[p][r].Length; i++) result[p][r][i] = Rotate(band[p][r][i], degrees) + shift;
            }
        }
        return result;
    }

    static Wall[] Transform(Wall[] walls, float degrees, Vector2 shift)
    {
        var result = new Wall[walls.Length];
        for (int i = 0; i < walls.Length; i++)
            result[i] = new Wall(Rotate(walls[i].A, degrees) + shift, Rotate(walls[i].B, degrees) + shift, Rotate(walls[i].Normal, degrees));
        return result;
    }

    static QuayMesh BuildQuay(Vector2[][][] band, Wall[] walls, float top, Func<float, float, float> ground)
    {
        Type segmentType = T("ReclaimedLandSegment");
        Array segments = Array.CreateInstance(segmentType, walls.Length);
        for (int i = 0; i < walls.Length; i++)
            segments.SetValue(Activator.CreateInstance(segmentType, walls[i].A, walls[i].B, walls[i].Normal), i);
        object data = buildMethod.Invoke(null, new object[] { band, segments, top, ground });
        Type type = data.GetType();
        return new QuayMesh
        {
            Vertices = new List<Vector3>((List<Vector3>)type.GetField("Vertices").GetValue(data)),
            Normals = new List<Vector3>((List<Vector3>)type.GetField("Normals").GetValue(data)),
            Uvs = new List<Vector2>((List<Vector2>)type.GetField("Uvs").GetValue(data)),
            Top = new List<int>((List<int>)type.GetField("TopTriangles").GetValue(data)),
            Walls = new List<int>((List<int>)type.GetField("WallTriangles").GetValue(data)),
        };
    }

    static bool SameIndices(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static bool SameMesh(QuayMesh a, QuayMesh b)
    {
        if (a.Vertices.Count != b.Vertices.Count || !SameIndices(a.Top, b.Top) || !SameIndices(a.Walls, b.Walls)) return false;
        for (int i = 0; i < a.Vertices.Count; i++)
            if (a.Vertices[i] != b.Vertices[i] || a.Normals[i] != b.Normals[i] || a.Uvs[i] != b.Uvs[i]) return false;
        return true;
    }

    static double TriangleArea(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).magnitude * 0.5;

    static double TopArea(QuayMesh m)
    {
        double area = 0;
        for (int i = 0; i < m.Top.Count; i += 3) area += TriangleArea(m.Vertices[m.Top[i]], m.Vertices[m.Top[i + 1]], m.Vertices[m.Top[i + 2]]);
        return area;
    }

    static double WallArea(QuayMesh m)
    {
        double area = 0;
        for (int i = 0; i < m.Walls.Count; i += 3) area += TriangleArea(m.Vertices[m.Walls[i]], m.Vertices[m.Walls[i + 1]], m.Vertices[m.Walls[i + 2]]);
        return area;
    }

    static double RingArea(Vector2[] ring)
    {
        double area = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
            area += (double)a.x * b.y - (double)b.x * a.y;
        }
        return Math.Abs(area) * 0.5;
    }

    static bool InsideBand(Vector2[][][] band, Vector2 p)
    {
        foreach (Vector2[][] polygon in band)
        {
            bool inside = false;
            foreach (Vector2[] ring in polygon)
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                    if ((ring[i].y > p.y) != (ring[j].y > p.y) &&
                        p.x < (ring[j].x - ring[i].x) * (p.y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x)
                        inside = !inside;
            if (inside) return true;
        }
        return false;
    }

    static float DistanceToBandEdge(Vector2[][][] band, Vector2 p)
    {
        float best = float.PositiveInfinity;
        foreach (Vector2[][] polygon in band)
            foreach (Vector2[] ring in polygon)
                for (int i = 0; i < ring.Length; i++)
                    best = Mathf.Min(best, DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Length]));
        return best;
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        float t = d.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, d) / d.sqrMagnitude) : 0f;
        return (a + d * t - p).magnitude;
    }

    static void VerifyQuayMesh(string label, QuayMesh m, Vector2[][][] band, Wall[] walls, float top, Func<float, float, float> ground)
    {
        Require(m.Vertices.Count == m.Normals.Count && m.Vertices.Count == m.Uvs.Count, $"{label}: vertex attribute counts differ");
        foreach (Vector3 p in m.Vertices) Require(IsFinite(p.x) && IsFinite(p.y) && IsFinite(p.z), $"{label}: non-finite vertex");
        foreach (Vector3 n in m.Normals) Require(IsFinite(n.x) && IsFinite(n.y) && IsFinite(n.z), $"{label}: non-finite normal");
        Require(m.Top.Count >= 3 && m.Top.Count % 3 == 0 && m.Walls.Count % 3 == 0, $"{label}: triangle lists are malformed");

        // Top: a horizontal surface at height top that faces upward and covers the band polygon exactly.
        double expectedTop = 0;
        foreach (Vector2[][] polygon in band)
        {
            expectedTop += RingArea(polygon[0]);
            for (int r = 1; r < polygon.Length; r++) expectedTop -= RingArea(polygon[r]);
        }
        double topArea = TopArea(m);
        Require(Math.Abs(topArea - expectedTop) <= 1e-3 * expectedTop, $"{label}: top area {topArea:F3} differs from the band area {expectedTop:F3}");
        for (int i = 0; i < m.Top.Count; i += 3)
        {
            Vector3 a = m.Vertices[m.Top[i]], b = m.Vertices[m.Top[i + 1]], c = m.Vertices[m.Top[i + 2]];
            Require(Vector3.Cross(b - a, c - a).y > 0f, $"{label}: a top triangle faces down");
            foreach (int k in new[] { m.Top[i], m.Top[i + 1], m.Top[i + 2] })
            {
                Vector3 p = m.Vertices[k];
                Require(Mathf.Abs(p.y - top) <= 1e-4f, $"{label}: top vertex at {p.y} instead of {top}");
                Require((m.Normals[k] - Vector3.up).magnitude <= 1e-4f, $"{label}: top normal is not up");
                Require(InsideBand(band, V(p.x, p.z)) || DistanceToBandEdge(band, V(p.x, p.z)) <= 1e-3f, $"{label}: top vertex outside the band");
            }
        }

        // Faces: each triangle is a vertical face above one of the sections and faces outward. Its top is at top; its bottom is at the outside ground minus the foot depth (top - minimum height, clamped).
        var coveredArea = new double[walls.Length];
        for (int i = 0; i < m.Walls.Count; i += 3)
        {
            int[] corners = { m.Walls[i], m.Walls[i + 1], m.Walls[i + 2] };
            int owner = -1;
            for (int s = 0; s < walls.Length && owner < 0; s++)
            {
                bool onWall = true;
                foreach (int k in corners) onWall &= DistanceToSegment(V(m.Vertices[k].x, m.Vertices[k].z), walls[s].A, walls[s].B) <= 1e-3f;
                if (onWall) owner = s;
            }
            Require(owner >= 0, $"{label}: a wall triangle is off every wall segment");
            Vector2 outward = walls[owner].Normal.normalized;
            var outward3 = new Vector3(outward.x, 0f, outward.y);
            Vector3 a = m.Vertices[corners[0]], b = m.Vertices[corners[1]], c = m.Vertices[corners[2]];
            Vector3 faceNormal = Vector3.Cross(b - a, c - a);
            coveredArea[owner] += faceNormal.magnitude * 0.5;
            Require(Vector3.Dot(faceNormal.normalized, outward3) > 0.999f, $"{label}: a wall triangle does not face outward");
            foreach (int k in corners)
            {
                Vector3 p = m.Vertices[k];
                Require((m.Normals[k] - outward3).magnitude <= 1e-4f, $"{label}: wall normal is not the outward normal");
                if (Mathf.Abs(p.y - top) <= 1e-5f) continue;
                Vector2 probe = V(p.x, p.z) + outward * probeDistance;
                float bottom = Mathf.Min(ground(probe.x, probe.y) - wallFooting, top - wallMinHeight);
                Require(Mathf.Abs(p.y - bottom) <= 1e-4f, $"{label}: wall bottom {p.y} differs from {bottom}");
            }
        }
        // The faces cover the sections without gaps (matching the sum of the trapezoids in each row).
        for (int s = 0; s < walls.Length; s++)
        {
            Vector2 d = walls[s].B - walls[s].A;
            float length = d.magnitude;
            Vector2 outward = walls[s].Normal.normalized;
            int columns = Mathf.Max(1, Mathf.CeilToInt(length / wallSpacing));
            double expected = 0;
            float previous = 0f;
            for (int col = 0; col <= columns; col++)
            {
                Vector2 p = walls[s].A + d * (col / (float)columns);
                Vector2 probe = p + outward * probeDistance;
                float height = top - Mathf.Min(ground(probe.x, probe.y) - wallFooting, top - wallMinHeight);
                if (col > 0) expected += 0.5 * (previous + height) * length / columns;
                previous = height;
            }
            Require(Math.Abs(coveredArea[s] - expected) <= 1e-3 * expected + 1e-4, $"{label}: wall {s} covers {coveredArea[s]:F3}m2 instead of {expected:F3}m2");
        }

        // A ray from above hits the top only within the band (it does not hit a back-facing face).
        var go = new GameObject("QuayTop");
        var mesh = new Mesh();
        mesh.SetVertices(m.Vertices);
        mesh.SetTriangles(m.Top, 0);
        mesh.RecalculateBounds();
        var collider = go.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        Physics.SyncTransforms();
        int hits = 0;
        try
        {
            Bounds bounds = mesh.bounds;
            for (float x = bounds.min.x - 1f; x <= bounds.max.x + 1f; x += 0.5f)
                for (float z = bounds.min.z - 1f; z <= bounds.max.z + 1f; z += 0.5f)
                {
                    var p = V(x, z);
                    if (DistanceToBandEdge(band, p) <= 0.02f) continue;
                    bool inside = InsideBand(band, p);
                    bool hit = collider.Raycast(new Ray(new Vector3(x, top + 50f, z), Vector3.down), out RaycastHit info, 100f);
                    Require(hit == inside, $"{label}: ray at ({x:F1}, {z:F1}) {(inside ? "missed the top" : "hit outside the band")}");
                    if (!hit) continue;
                    hits++;
                    Require(Mathf.Abs(info.point.y - top) <= 1e-3f && info.normal.y > 0.99f, $"{label}: ray hit the top at {info.point.y} with normal {info.normal}");
                }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
        Require(hits > 0, $"{label}: no ray hit the top");
    }

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    // =====================================================================
    // C. Determine whether a port faces a quay on reclaimed land (PortBuildingMeshBuilder.FindFacingReclaimedQuays)
    // =====================================================================

    struct Facing
    {
        public int LandId;
        public Vector2 A, B, Normal;
        public float Top;
    }

    static void CheckPortFacing()
    {
        // Reclaimed land 1: [0,100]x[-50,0], with original land at z>0. The seaward edge and both side edges are quays.
        // Reclaimed land 2: a diamond-shaped island centered at (300,-100), with quays all around.
        var quays = new List<object>
        {
            Quay(1, V(0, -50), V(100, -50), V(0, -1), 3f),
            Quay(1, V(100, -50), V(100, 0), V(1, 0), 3f),
            Quay(1, V(0, 0), V(0, -50), V(-1, 0), 3f),
            Quay(2, V(300, -80), V(280, -100), V(-1, 1).normalized, 4f),
            Quay(2, V(280, -100), V(300, -120), V(-1, -1).normalized, 4f),
            Quay(2, V(300, -120), V(320, -100), V(1, -1).normalized, 4f),
            Quay(2, V(320, -100), V(300, -80), V(1, 1).normalized, 4f),
        };

        // Port on reclaimed land: the parcel crosses the quay, or its seaward edge overlaps the quay (the parcel is on the landward side of the quay).
        List<Facing> crossing = FindFacing(Rect(20, -70, 60, -30), quays);
        Require(crossing.Count == 1, $"crossing: expected 1 facing quay, got {crossing.Count}");
        Require(crossing[0].LandId == 1 && Mathf.Abs(crossing[0].Top - 3f) < 1e-6f && (crossing[0].Normal - V(0, -1)).magnitude < 1e-6f,
            "crossing: facing quay has the wrong land, top or normal");
        Require(Near(crossing[0].A, V(20, -50), 0.01f) && Near(crossing[0].B, V(60, -50), 0.01f),
            $"crossing: facing piece {crossing[0].A}-{crossing[0].B} is not the part inside the parcel");
        foreach ((string label, float shift) in new[] { ("on reclaimed land", 0f), ("on reclaimed land, 3cm inland", 0.03f), ("on reclaimed land, 3cm over the sea", -0.03f) })
        {
            List<Facing> south = FindFacing(Rect(20, -50 + shift, 60, -30), quays);
            Require(south.Count == 1 && south[0].LandId == 1 && (south[0].Normal - V(0, -1)).magnitude < 1e-6f,
                $"{label}: expected the south quay, got {south.Count}");
            Require(Near(south[0].A, V(20, -50), 0.01f) && Near(south[0].B, V(60, -50), 0.01f),
                $"{label}: facing piece {south[0].A}-{south[0].B} is not the seaward edge");
            List<Facing> east = FindFacing(Rect(70, -40, 100 - shift, -10), quays);
            Require(east.Count == 1 && east[0].LandId == 1 && (east[0].Normal - V(1, 0)).magnitude < 1e-6f,
                $"{label} (east quay): expected the east quay, got {east.Count}");
            Require(Near(east[0].A, V(100, -40), 0.01f) && Near(east[0].B, V(100, -10), 0.01f),
                $"{label} (east quay): facing piece {east[0].A}-{east[0].B} is not the seaward edge");
        }
        List<Facing> reversed = FindFacing(new[] { V(20, -30), V(60, -30), V(60, -70), V(20, -70) }, quays);
        Require(reversed.Count == 1 && Near(reversed[0].A, V(20, -50), 0.01f) && Near(reversed[0].B, V(60, -50), 0.01f),
            "clockwise parcel: the facing piece changed with the parcel orientation");

        // Reclaimed land beside an existing port: the parcel is on the seaward side of the quay, and the quay's outward normal faces the parcel's water surface. It does not face the quay.
        foreach ((string label, float gap) in new[] { ("beside the port", 0f), ("beside the port, 3cm gap", 0.03f), ("beside the port, 3cm overlap", -0.03f) })
        {
            Require(FindFacing(Rect(100 + gap, -40, 150, 20), quays).Count == 0, $"{label}: a port on the sea side of the east quay must not face it");
            Require(FindFacing(Rect(100 + gap, -40, 150, -10), quays).Count == 0, $"{label}: a sea-only port on the sea side of the east quay must not face it");
        }
        Require(FindFacing(Rect(101, -40, 150, 20), quays).Count == 0, "1m away: a parcel 1m from the quay must not face it");
        Require(FindFacing(Rect(100, -80, 150, -50), quays).Count == 0, "corner touch: a parcel touching the land at one corner must not face it");
        Require(FindFacing(CornerTouchPort, quays).Count == 0, "corner touch: a port whose corner touches the land's corner must not face it");
        Require(FindFacing(new[] { V(50, -50), V(60, -60), V(50, -70), V(40, -60) }, quays).Count == 0,
            "vertex on quay: a diamond parcel touching the quay at one vertex must not face it");
        Require(FindFacing(Rect(320, -100, 360, -60), quays).Count == 0, "island corner: a parcel touching the island at one vertex must not face it");
        results.Add("Port facing: crossing/on reclaimed land (exact, 3cm inland, 3cm over the sea; south and east quays)/clockwise parcel face the quay; " +
                    "beside the port (exact, 3cm gap, 3cm overlap)/1m away/corner touch/vertex on quay/island corner do not");

        CheckPortChord();
    }

    // A port touching reclaimed land 1 at its corner (100,-50). Its left edge runs diagonally upward from (100,-50) to (110,20), crossing the original coastline z=0.
    static readonly Vector2[] CornerTouchPort = { V(100, -50), V(150, -50), V(150, 20), V(110, 20) };

    /// <summary>
    /// Straight cut for a port that does not face the quay: the quay footprint is the bottom clipped to the landward side of the given
    /// berth_line (sea on the left of A'→B'), the seaward edge runs along that line, and a missing line leaves the bottom uncut.
    /// </summary>
    static void CheckPortChord()
    {
        Chord plain = QuayCut(Rect(100, -40, 150, 20), new[] { 150f, -20f, 100f, -20f });
        Require(plain.Valid && Mathf.Abs(plain.Dir.y) < 1e-4f && (plain.Normal - V(0, -1)).magnitude < 1e-4f,
            $"chord: the cut does not run along berth_line z=-20 (dir {plain.Dir}, normal {plain.Normal})");
        Require(SameRing(plain.Footprint, Rect(100, -20, 150, 20), 1e-3f),
            $"chord: the quay footprint of a plain port is {Describe(plain.Footprint)} instead of [100,150]x[-20,20]");
        Chord shallow = QuayCut(Rect(100, -12, 150, 20), new[] { 150f, -12f, 100f, -12f });
        Require(SameRing(shallow.Footprint, Rect(100, -12, 150, 20), 1e-3f),
            $"chord: the quay footprint on a line at the parcel's seaward edge is {Describe(shallow.Footprint)} instead of [100,150]x[-12,20]");
        Chord none = QuayCut(Rect(100, -40, 150, 20), null);
        Require(!none.Valid && SameRing(none.Footprint, Rect(100, -40, 150, 20), 1e-3f),
            $"chord: without berth_line the bottom must stay uncut ({Describe(none.Footprint)})");
        results.Add("Port chord: the straight cut follows berth_line (20m out / at the parcel's seaward edge); no line leaves the bottom uncut");
    }

    struct Chord
    {
        public bool Valid;
        public Vector2 Dir, Normal;
        public Vector2[] Footprint;
    }

    /// <summary>Straight-cut the port bottom (world coordinates) on berthLine (world [A'x, A'z, B'x, B'z]) with PortBuildingMeshBuilder.BuildQuayFootprint.</summary>
    static Chord QuayCut(Vector2[] portWorld, float[] berthLine)
    {
        Vector2 center = Vector2.zero;
        foreach (Vector2 p in portWorld) center += p;
        center /= portWorld.Length;
        var local = new Vector2[portWorld.Length];
        for (int i = 0; i < portWorld.Length; i++) local[i] = portWorld[i] - center;
        MethodInfo method = T("PortBuildingMeshBuilder").GetMethod("BuildQuayFootprint", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("PortBuildingMeshBuilder.BuildQuayFootprint not found");
        object[] args = { local, berthLine, new Vector3(center.x, 0f, center.y), null };
        var footprint = (Vector2[])method.Invoke(null, args);
        object seaward = args[3];
        Type edge = seaward.GetType();
        var world = new Vector2[footprint.Length];
        for (int i = 0; i < footprint.Length; i++) world[i] = footprint[i] + center;
        return new Chord
        {
            Valid = (bool)edge.GetField("Valid").GetValue(seaward),
            Dir = (Vector2)edge.GetField("Dir").GetValue(seaward),
            Normal = (Vector2)edge.GetField("Normal").GetValue(seaward),
            Footprint = world,
        };
    }

    /// <summary>Whether the vertex sets match within the tolerance (regardless of starting point or orientation).</summary>
    static bool SameRing(Vector2[] a, Vector2[] b, float tolerance)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        foreach (Vector2 p in a)
        {
            bool found = false;
            foreach (Vector2 q in b) found |= Near(p, q, tolerance);
            if (!found) return false;
        }
        return true;
    }

    static string Describe(Vector2[] ring) => ring == null ? "null" : string.Join(" ", Array.ConvertAll(ring, p => $"({p.x:F3},{p.y:F3})"));

    static Vector2[] Rect(float x0, float z0, float x1, float z1) => new[] { V(x0, z0), V(x1, z0), V(x1, z1), V(x0, z1) };

    static bool Near(Vector2 a, Vector2 b, float tolerance) => (a - b).magnitude <= tolerance;

    static object Quay(int landId, Vector2 a, Vector2 b, Vector2 normal, float top)
    {
        object segment = Activator.CreateInstance(T("ReclaimedLandSegment"), a, b, normal);
        return Activator.CreateInstance(T("ReclaimedQuay"), landId, segment, top);
    }

    static List<Facing> FindFacing(Vector2[] parcel, List<object> quays)
    {
        Type quayType = T("ReclaimedQuay");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(quayType));
        foreach (object quay in quays) list.Add(quay);
        MethodInfo method = T("PortBuildingMeshBuilder").GetMethod("FindFacingReclaimedQuays", BindingFlags.Public | BindingFlags.Static)
            ?? throw new Exception("PortBuildingMeshBuilder.FindFacingReclaimedQuays not found");
        var found = (IList)method.Invoke(null, new object[] { parcel, list });
        var result = new List<Facing>();
        Type segmentType = T("ReclaimedLandSegment");
        foreach (object item in found)
        {
            object segment = quayType.GetField("Segment").GetValue(item);
            result.Add(new Facing
            {
                LandId = (int)quayType.GetField("LandId").GetValue(item),
                Top = (float)quayType.GetField("TopHeight").GetValue(item),
                A = (Vector2)segmentType.GetField("A").GetValue(segment),
                B = (Vector2)segmentType.GetField("B").GetValue(segment),
                Normal = (Vector2)segmentType.GetField("Normal").GetValue(segment),
            });
        }
        return result;
    }
}
