using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

public partial class TerrainManager
{
    private float[] backgroundHeights;
    private int backgroundRows, backgroundCols;
    private float backgroundX, backgroundZ, backgroundSpacing;
    private bool backgroundLoaded;
    private float backgroundMinHeight, backgroundMaxHeight;
    private Material backgroundMaterial;
    private readonly List<GameObject> backgroundObjects = new List<GameObject>();
    private readonly List<Mesh> backgroundMeshes = new List<Mesh>();

    public bool IsCityCell(Vector2Int cell) =>
        cell.x * CellSize >= -GetMapWidth() * .5f && (cell.x + 1) * CellSize <= GetMapWidth() * .5f &&
        cell.y * CellSize >= -GetMapHeight() * .5f && (cell.y + 1) * CellSize <= GetMapHeight() * .5f;

    public Rect BackgroundBounds => backgroundHeights == null
        ? new Rect(-GetMapWidth()*.5f, -GetMapHeight()*.5f, GetMapWidth(), GetMapHeight())
        : new Rect(backgroundX, backgroundZ, (backgroundCols-1)*backgroundSpacing, (backgroundRows-1)*backgroundSpacing);

    private bool TrySampleBackground(Vector3 p, out float height)
    {
        height = 0;
        if (backgroundHeights == null) return false;
        float x = (p.x-backgroundX)/backgroundSpacing, z = (p.z-backgroundZ)/backgroundSpacing;
        if (x < 0 || z < 0 || x > backgroundCols-1 || z > backgroundRows-1) return false;
        int ix = Mathf.Min((int)x, backgroundCols-2), iz = Mathf.Min((int)z, backgroundRows-2);
        float fx = x-ix, fz = z-iz;
        int i = iz*backgroundCols+ix;
        height = Mathf.Lerp(Mathf.Lerp(backgroundHeights[i], backgroundHeights[i+1], fx),
            Mathf.Lerp(backgroundHeights[i+backgroundCols], backgroundHeights[i+backgroundCols+1], fx), fz);
        return true;
    }

    private bool TryGetBackgroundHeightRange(Vector2Int cell, out float min, out float max)
    {
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        if (backgroundHeights == null) return false;
        int x0 = Mathf.Clamp(Mathf.FloorToInt((cell.x*CellSize-backgroundX)/backgroundSpacing), 0, backgroundCols-1);
        int z0 = Mathf.Clamp(Mathf.FloorToInt((cell.y*CellSize-backgroundZ)/backgroundSpacing), 0, backgroundRows-1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(((cell.x+1)*CellSize-backgroundX)/backgroundSpacing), 0, backgroundCols-1);
        int z1 = Mathf.Clamp(Mathf.CeilToInt(((cell.y+1)*CellSize-backgroundZ)/backgroundSpacing), 0, backgroundRows-1);
        for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
        {
            float h = backgroundHeights[z*backgroundCols+x];
            min = Mathf.Min(min, h); max = Mathf.Max(max, h);
        }
        return true;
    }

    private void ClearBackground()
    {
        foreach (var go in backgroundObjects) if (go != null) Destroy(go);
        foreach (var mesh in backgroundMeshes) if (mesh != null) Destroy(mesh);
        backgroundObjects.Clear();
        backgroundMeshes.Clear();
        if (backgroundMaterial != null) Destroy(backgroundMaterial);
        backgroundMaterial = null;
        backgroundHeights = null;
        backgroundLoaded = false;
    }

    private IEnumerator EnsureBackground(LoadBatch batch)
    {
        if (backgroundLoaded || batch.IsCanceled) yield break;
        int epoch = clearEpoch;
        using (var request = UnityWebRequest.Get($"{ApiClient.BASE_URL}/terrain/background"))
        {
            yield return request.SendWebRequest();
            if (batch.IsCanceled || epoch != clearEpoch) yield break;
            if (request.responseCode == 204) { backgroundLoaded = true; yield break; }
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"背景地形の取得失敗: {request.error}");
                yield break;
            }
            backgroundRows = ParseIntHeader(request, "X-Grid-Rows", 0);
            backgroundCols = ParseIntHeader(request, "X-Grid-Cols", 0);
            float Header(string name) => float.Parse(request.GetResponseHeader(name), CultureInfo.InvariantCulture);
            backgroundSpacing = Header("X-Grid-Spacing");
            backgroundX = Header("X-Origin-X");
            backgroundZ = Header("X-Origin-Z");
            byte[] bytes = request.downloadHandler.data;
            if (backgroundRows < 2 || backgroundCols < 2 || bytes.LongLength != (long)backgroundRows*backgroundCols*4)
            { Debug.LogError("背景地形の格子サイズが不正です"); yield break; }
            backgroundHeights = new float[backgroundRows*backgroundCols];
            Buffer.BlockCopy(bytes, 0, backgroundHeights, 0, bytes.Length);
            backgroundMinHeight = float.PositiveInfinity;
            backgroundMaxHeight = float.NegativeInfinity;
            foreach (float h in backgroundHeights)
            {
                backgroundMinHeight = Mathf.Min(backgroundMinHeight, h);
                backgroundMaxHeight = Mathf.Max(backgroundMaxHeight, h);
            }
        }
        EnsureUltraFarLandMaterials();
        backgroundMaterial = new Material(ultraFarLandMat) { name = "BackgroundTerrain" };
        backgroundMaterial.SetFloat(FarSinkAmountId, 0f);
        // 大きな背景も一枚の巨大メッシュにせず、描画のカリングと生成の中断を可能にする。
        const int chunk = 64;
        for (int z0 = 0; z0 < backgroundRows-1; z0 += chunk)
        for (int x0 = 0; x0 < backgroundCols-1; x0 += chunk)
        {
            if (epoch != clearEpoch) yield break;
            if (batch.IsCanceled) { ClearBackground(); yield break; }
            int width = Mathf.Min(chunk, backgroundCols-1-x0), depth = Mathf.Min(chunk, backgroundRows-1-z0);
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            for (int z = 0; z <= depth; z++) for (int x = 0; x <= width; x++)
            {
                float wx = backgroundX+(x0+x)*backgroundSpacing, wz = backgroundZ+(z0+z)*backgroundSpacing;
                vertices.Add(new Vector3(wx, backgroundHeights[(z0+z)*backgroundCols+x0+x], wz));
                uv.Add(new Vector2(wx, wz));
            }
            for (int z = 0; z < depth; z++) for (int x = 0; x < width; x++)
            {
                float wx = backgroundX+(x0+x+.5f)*backgroundSpacing, wz = backgroundZ+(z0+z+.5f)*backgroundSpacing;
                if (Mathf.Abs(wx) < GetMapWidth()*.5f && Mathf.Abs(wz) < GetMapHeight()*.5f) continue;
                int i = z*(width+1)+x;
                triangles.Add(i); triangles.Add(i+width+1); triangles.Add(i+1);
                triangles.Add(i+1); triangles.Add(i+width+1); triangles.Add(i+width+2);
            }
            if (triangles.Count > 0)
            {
                var mesh = new Mesh { name = "BackgroundTerrainChunk" };
                mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                var go = new GameObject("BackgroundTerrain");
                go.transform.SetParent(GetContainer("Terrains"));
                go.layer = LayerMask.NameToLayer("Ground");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = backgroundMaterial;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.AddComponent<VisualTerrainObject>();
                backgroundMeshes.Add(mesh); backgroundObjects.Add(go);
            }
            yield return null;
        }
        backgroundLoaded = true;
        UpdateCameraClipDistance();
    }
}
