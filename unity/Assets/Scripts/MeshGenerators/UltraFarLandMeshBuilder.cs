using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ultra-far land-silhouette mesh builder.
///
/// UltraFar needs only distant land silhouettes (buildings/roads hidden,
/// terrain fixed). One coarse Unity Terrain heightmap (resolution 33) across the map
/// was unsuitable: terrain near zero elevation undulated through the flat sea plane (world Y=0),
/// and coastlines were coarse.
///
/// Instead mesh precise coastline polygons extracted by backend near_map (10m spacing)
/// from <see cref="CoastlineStore"/>. No sea/outside-land mesh means
/// the existing sea plane handles water without intersections or Z-fighting.
///
/// Like <c>ResourceManager</c> grid subdivision, divide land AABB into <c>gridStep</c>
/// cells; sample signed coastline distance (<see cref="CoastlineStore.SampleSignedDistanceGrid"/>)
/// and far_map height at vertices. Marching-squares-style clipping traverses each cell's edges, collecting land corners
/// and interpolated coastline intersections into a land polygon;
/// coastlines therefore remain smoother than grid resolution. Raise vertices to represent coarse relief.
///
/// Mesh creation is main-thread-only, so this builder outputs only pure vertex/triangle arrays.
/// Heavy signed-distance calculations may run in background threads,
/// provided <see cref="CoastlineStore"/> and supplied delegates remain unchanged during the build.
/// </summary>
public static class UltraFarLandMeshBuilder
{
    /// <summary>Build result, assembled into Mesh on the main thread.</summary>
    public struct MeshData
    {
        public List<Vector3> vertices;
        public List<Vector2> uvs;             // UV coordinates (world X,Z).
        public List<Vector3> normals;         // Position-welded smooth normals.
        public List<int> triangles;
    }

    /// <summary>
    /// Build land mesh data.
    /// </summary>
    /// <param name="mapWidth">Map width (X, meters).</param>
    /// <param name="mapHeight">Map height (Z, meters).</param>
    /// <param name="gridStep">Grid spacing (meters). Smaller gives finer coastlines/relief but more vertices.</param>
    /// <param name="heightOffset">Tiny Y offset added to all vertices to keep land above sea plane (meters).</param>
    /// <param name="sampleHeight">World (x,z)→ground world Y (meters), coarse far_map height.</param>
    public static MeshData Build(
        float mapWidth, float mapHeight, float gridStep, float heightOffset,
        Func<float, float, float> sampleHeight)
    {
        var data = new MeshData
        {
            vertices = new List<Vector3>(4096),
            uvs = new List<Vector2>(4096),
            normals = null, // Build together at the end.
            triangles = new List<int>(8192),
        };

        if (gridStep < 1f) gridStep = 1f;

        // Maps without coastlines (entire map land) mesh the whole map without coastline tests.
        bool hasCoastline = CoastlineStore.HasCoastline;

        // Scan range: coastline AABB if present, clamped to map, with one-grid margin.
        float halfW = mapWidth * 0.5f;
        float halfH = mapHeight * 0.5f;
        float minX = -halfW, minZ = -halfH, maxX = halfW, maxZ = halfH;
        if (hasCoastline && CoastlineStore.TryGetBounds(out float bMinX, out float bMinZ, out float bMaxX, out float bMaxZ))
        {
            minX = Mathf.Max(-halfW, bMinX - gridStep);
            minZ = Mathf.Max(-halfH, bMinZ - gridStep);
            maxX = Mathf.Min(halfW, bMaxX + gridStep);
            maxZ = Mathf.Min(halfH, bMaxZ + gridStep);
        }

        int nx = Mathf.Max(2, Mathf.CeilToInt((maxX - minX) / gridStep) + 1);
        int nz = Mathf.Max(2, Mathf.CeilToInt((maxZ - minZ) / gridStep) + 1);

        // Precompute coordinates/signed distances/heights at grid vertices, shared by cells.
        var gx = new float[nx];
        var gz = new float[nz];
        for (int i = 0; i < nx; i++) gx[i] = minX + i * gridStep;
        for (int j = 0; j < nz; j++) gz[j] = minZ + j * gridStep;

        // Signed distance (positive land, negative sea). Interpolate only endpoints of sign-changing edges; these cross coastline,
        // so both endpoints lie within gridStep of it. Farther distances may use capped values.
        var sGrid = hasCoastline ? CoastlineStore.SampleSignedDistanceGrid(gx, gz, gridStep) : new float[nx, nz];
        var yGrid = new float[nx, nz]; // Ground world Y including offset.
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                if (!hasCoastline) sGrid[i, j] = 1f;
                yGrid[i, j] = sampleHeight(gx[i], gz[j]) + heightOffset;
            }
        }

        // Traverse cell corners c00→c01→c11→c10 in upward-facing order.
        // For each edge, retain land start corners and interpolate intersections across land/sea transitions.
        // Fan-triangulate resulting land polygons (3–5 vertices).
        // Corner index offsets relative to (i,j).
        int[] cornerDi = { 0, 0, 1, 1 };
        int[] cornerDj = { 0, 1, 1, 0 };

        var polyX = new float[8];
        var polyZ = new float[8];
        var polyY = new float[8];

        for (int i = 0; i < nx - 1; i++)
        {
            for (int j = 0; j < nz - 1; j++)
            {
                int polyCount = 0;
                for (int e = 0; e < 4; e++)
                {
                    int ai = i + cornerDi[e], aj = j + cornerDj[e];
                    int e2 = (e + 1) & 3;
                    int bi = i + cornerDi[e2], bj = j + cornerDj[e2];

                    float sa = sGrid[ai, aj];
                    float sb = sGrid[bi, bj];
                    bool aIn = sa > 0f;
                    bool bIn = sb > 0f;

                    if (aIn)
                    {
                        polyX[polyCount] = gx[ai];
                        polyZ[polyCount] = gz[aj];
                        polyY[polyCount] = yGrid[ai, aj];
                        polyCount++;
                    }
                    if (aIn != bIn)
                    {
                        // Sign-change t (sa>0,sb<=0 or reverse). aIn!=bIn above already excludes sa==sb.
                        float denom = sa - sb;
                        float t = Mathf.Abs(denom) > 1e-6f ? sa / denom : 0.5f;
                        t = Mathf.Clamp01(t);
                        polyX[polyCount] = Mathf.Lerp(gx[ai], gx[bi], t);
                        polyZ[polyCount] = Mathf.Lerp(gz[aj], gz[bj], t);
                        polyY[polyCount] = Mathf.Lerp(yGrid[ai, aj], yGrid[bi, bj], t);
                        polyCount++;
                    }
                }

                if (polyCount < 3)
                {
                    continue; // No land (all corners sea), or degenerate cell.
                }

                // Append polygon vertices and fan-triangulate (0,k,k+1).
                int baseIdx = data.vertices.Count;
                for (int k = 0; k < polyCount; k++)
                {
                    data.vertices.Add(new Vector3(polyX[k], polyY[k], polyZ[k]));
                    data.uvs.Add(new Vector2(polyX[k], polyZ[k]));
                }
                for (int k = 1; k < polyCount - 1; k++)
                {
                    data.triangles.Add(baseIdx);
                    data.triangles.Add(baseIdx + k);
                    data.triangles.Add(baseIdx + k + 1);
                }
            }
        }

        data.normals = BuildSmoothNormals(data);
        return data;
    }

    /// <summary>
    /// Build position-welded smooth normals.
    ///
    /// Vertices are appended per cell, duplicating world-XZ positions along boundaries.
    /// Mesh.RecalculateNormals averages faces per vertex but does not weld duplicates,
    /// leaving discontinuous boundary normals and faceted distant land.
    /// Aggregate area-weighted face normals by identical XZ, then normalize for
    /// smooth continuity across cells (share normal values without welding vertices).
    /// </summary>
    private static List<Vector3> BuildSmoothNormals(MeshData data)
    {
        int vcount = data.vertices.Count;
        var normals = new List<Vector3>(vcount);
        for (int i = 0; i < vcount; i++) normals.Add(Vector3.zero);

        // Add area-weighted face normals (unnormalized cross products) to each vertex.
        AccumulateFaceNormals(data.vertices, data.triangles, normals);

        // Aggregate normals by quantized XZ keys. Intersections/corners reproduce identical coordinates
        // deterministically in neighboring cells, ensuring quantized matches.
        const float weldQuantum = 0.05f;
        var welded = new Dictionary<long, Vector3>(vcount);
        for (int i = 0; i < vcount; i++)
        {
            long key = WeldKey(data.vertices[i], weldQuantum);
            welded.TryGetValue(key, out Vector3 acc);
            welded[key] = acc + normals[i];
        }
        for (int i = 0; i < vcount; i++)
        {
            long key = WeldKey(data.vertices[i], weldQuantum);
            Vector3 n = welded[key];
            normals[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
        }
        return normals;
    }

    private static void AccumulateFaceNormals(List<Vector3> verts, List<int> tris, List<Vector3> normals)
    {
        if (tris == null) return;
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
            Vector3 va = verts[ia], vb = verts[ib], vc = verts[ic];
            Vector3 fn = Vector3.Cross(vb - va, vc - va); // Area-weighted (unnormalized).
            normals[ia] += fn;
            normals[ib] += fn;
            normals[ic] += fn;
        }
    }

    /// <summary>Quantize world XZ into collision-free 64-bit keys for normal welding.</summary>
    private static long WeldKey(Vector3 v, float quantum)
    {
        int qx = Mathf.RoundToInt(v.x / quantum);
        int qz = Mathf.RoundToInt(v.z / quantum);
        return ((long)qx << 32) | (uint)qz;
    }
}
