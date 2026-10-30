using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Build resident terrain tiles locally from the core's base grid (/terrain/near) and edited cells (/terrain/dynamic),
/// using the same rule as the core's TerrainSurface: bicubic-interpolate the base grid onto the edited-cell node grid,
/// overwrite nodes inside edited cells, then bilinearly sample the tile points. Tiles therefore need no request,
/// so the camera can move anywhere (including while an epoch runs) without asking the core.
/// Keep this rule identical to genesis-core TerrainSurface; any difference shows as seams and grounding mismatches.
/// Immutable snapshot: an update creates a new instance, so background tile generation can read it without locks.
/// Coordinates are map-local (origin at the map's lower-left corner) and cells are backend cells.
/// </summary>
public sealed class TerrainSurfaceSampler
{
    private readonly float[] near;            // Base grid rows (z) × cols (x), never changed during a session.
    private readonly int nearRows, nearCols;
    private readonly double nearSpacing, dynamicSpacing, cellSize, mapWidth, mapHeight;
    private readonly int cellsX, cellsY;
    private readonly Dictionary<Vector2Int, float[]> editedCells;   // Edited backend cell → node grid (rows × cols of cellSize/dynamicSpacing+1).
    private readonly int editedSize;
    private readonly Dictionary<Vector2Int, int> versions;           // Unity cell → /terrain/cell-versions revision this snapshot reflects.

    public TerrainSurfaceSampler(float[] near, int nearRows, int nearCols, TerrainSettingsData settings,
        Dictionary<Vector2Int, float[]> editedCells, Dictionary<Vector2Int, int> versions)
    {
        this.near = near;
        this.nearRows = nearRows;
        this.nearCols = nearCols;
        nearSpacing = settings.near_map_spacing;
        dynamicSpacing = settings.dynamic_map_spacing;
        cellSize = settings.cell_size;
        mapWidth = settings.map_width;
        mapHeight = settings.map_height;
        cellsX = settings.map_width / settings.cell_size;
        cellsY = settings.map_height / settings.cell_size;
        editedSize = settings.cell_size / settings.dynamic_map_spacing + 1;
        this.editedCells = editedCells;
        this.versions = versions;
    }

    public Dictionary<Vector2Int, float[]> EditedCells => editedCells;
    public int EditedCellSize => editedSize;
    public int Version(Vector2Int unityCell) => versions.TryGetValue(unityCell, out int version) ? version : 0;

    /// <summary>Elevations of one backend cell on a resolution×resolution grid sharing its borders with neighbors, as [z, x]. Thread-safe.</summary>
    public void Tile(Vector2Int backendCell, int resolution, float[,] output)
    {
        var xs = Linspace(backendCell.x * cellSize, (backendCell.x + 1) * cellSize, resolution);
        var zs = Linspace(backendCell.y * cellSize, (backendCell.y + 1) * cellSize, resolution);
        Sample(xs, zs, output);
    }

    private static double[] Linspace(double start, double stop, int count)
    {
        var values = new double[count];
        double step = (stop - start) / (count - 1);
        for (int i = 0; i < count; i++) values[i] = start + i * step;
        values[count - 1] = stop;
        return values;
    }

    private void Sample(double[] xs, double[] zs, float[,] output)
    {
        int[] ix = new int[xs.Length], iz = new int[zs.Length];
        double[] fx = new double[xs.Length], fz = new double[zs.Length];
        for (int i = 0; i < xs.Length; i++) { double g = xs[i] / dynamicSpacing; ix[i] = (int)Math.Floor(g); fx[i] = g - ix[i]; }
        for (int i = 0; i < zs.Length; i++) { double g = zs[i] / dynamicSpacing; iz[i] = (int)Math.Floor(g); fz[i] = g - iz[i]; }
        int x0 = ix[0], z0 = iz[0];
        int nodeCols = ix[ix.Length - 1] - x0 + 2, nodeRows = iz[iz.Length - 1] - z0 + 2;
        var nx = new double[nodeCols];
        var nz = new double[nodeRows];
        for (int i = 0; i < nodeCols; i++) nx[i] = Math.Min((x0 + i) * dynamicSpacing, mapWidth);
        for (int i = 0; i < nodeRows; i++) nz[i] = Math.Min((z0 + i) * dynamicSpacing, mapHeight);
        float[,] nodes = BaseNodes(nx, nz);
        ApplyEdits(nx, nz, nodes);
        for (int r = 0; r < zs.Length; r++)
        {
            int row = iz[r] - z0;
            double wz0 = 1 - fz[r], wz1 = fz[r];
            for (int c = 0; c < xs.Length; c++)
            {
                int col = ix[c] - x0;
                double wx0 = 1 - fx[c], wx1 = fx[c];
                double value = (double)nodes[row, col] * wz0 * wx0;
                value += (double)nodes[row, col + 1] * wz0 * wx1;
                value += (double)nodes[row + 1, col] * wz1 * wx0;
                value += (double)nodes[row + 1, col + 1] * wz1 * wx1;
                output[r, c] = (float)value;
            }
        }
    }

    private static void CubicWeights(double t, out double w0, out double w1, out double w2, out double w3)
    {
        w0 = -0.5 * t + t * t - 0.5 * t * t * t;
        w1 = 1 - 2.5 * t * t + 1.5 * t * t * t;
        w2 = 0.5 * t + 2 * t * t - 1.5 * t * t * t;
        w3 = -0.5 * t * t + 0.5 * t * t * t;
    }

    /// <summary>Separable bicubic interpolation of the base grid: convolve the used rows along X first, then along Z.</summary>
    private float[,] BaseNodes(double[] nx, double[] nz)
    {
        int cols = nx.Length, rows = nz.Length;
        var tapX = new int[cols, 4];
        var weightX = new double[cols, 4];
        for (int c = 0; c < cols; c++)
        {
            double x = nx[c] / nearSpacing;
            int i0 = (int)Math.Floor(x);
            CubicWeights(x - i0, out weightX[c, 0], out weightX[c, 1], out weightX[c, 2], out weightX[c, 3]);
            for (int i = 0; i < 4; i++) tapX[c, i] = Mathf.Clamp(i0 + i - 1, 0, nearCols - 1);
        }
        var band = new Dictionary<int, double[]>();
        double[] Band(int sourceRow)
        {
            if (band.TryGetValue(sourceRow, out double[] values)) return values;
            values = new double[cols];
            int offset = sourceRow * nearCols;
            for (int i = 0; i < 4; i++)
                for (int c = 0; c < cols; c++)
                    values[c] += near[offset + tapX[c, i]] * weightX[c, i];
            band[sourceRow] = values;
            return values;
        }
        var result = new float[rows, cols];
        var weightZ = new double[4];
        for (int r = 0; r < rows; r++)
        {
            double z = nz[r] / nearSpacing;
            int j0 = (int)Math.Floor(z);
            CubicWeights(z - j0, out weightZ[0], out weightZ[1], out weightZ[2], out weightZ[3]);
            var sum = new double[cols];
            for (int j = 0; j < 4; j++)
            {
                double[] source = Band(Mathf.Clamp(j0 + j - 1, 0, nearRows - 1));
                for (int c = 0; c < cols; c++) sum[c] += source[c] * weightZ[j];
            }
            for (int c = 0; c < cols; c++) result[r, c] = (float)sum[c];
        }
        return result;
    }

    private void ApplyEdits(double[] nx, double[] nz, float[,] nodes)
    {
        if (editedCells.Count == 0) return;
        for (int r = 0; r < nz.Length; r++)
        {
            int cellZ = Math.Min((int)Math.Floor(nz[r] / cellSize), cellsY - 1);
            int localZ = (int)((nz[r] - cellZ * cellSize) / dynamicSpacing);
            for (int c = 0; c < nx.Length; c++)
            {
                int cellX = Math.Min((int)Math.Floor(nx[c] / cellSize), cellsX - 1);
                if (!editedCells.TryGetValue(new Vector2Int(cellX, cellZ), out float[] data)) continue;
                int localX = (int)((nx[c] - cellX * cellSize) / dynamicSpacing);
                nodes[r, c] = data[localZ * editedSize + localX];
            }
        }
    }
}
