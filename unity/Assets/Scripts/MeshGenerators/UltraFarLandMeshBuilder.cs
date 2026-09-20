using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 超遠景(UltraFar)の陸地シルエットメッシュを生成するビルダー。
///
/// UltraFar に求められるのは「遠くに見える陸地のシルエット」だけ(建物・道路は非表示、
/// 地形は固定)であり、マップ全体を1枚の粗いハイトマップ(解像度33)で表す Unity Terrain は
/// この用途に不向きだった。標高0m付近の地形がうねって平らな海面板(ワールドY=0)を突き抜け、
/// 海岸線も粗くしか出ない。
///
/// 代わりに、バックエンドが near_map(10m間隔)から精密に抽出した海岸線ポリゴン
/// (<see cref="CoastlineStore"/>)を直接メッシュ化する。海(陸地外)はメッシュを作らない
/// ため、海は既存の海面板がそのまま担い、突き抜けや Z-fighting が原理的に発生しない。
///
/// 手法は <c>ResourceManager</c> のグリッド細分化と同系統で、陸地のAABBを <c>gridStep</c>
/// 間隔のグリッドに切り、各グリッド頂点で海岸線への符号付き距離(<see cref="CoastlineStore.SignedDistance"/>)
/// と far_map 高さをサンプリングする。各グリッドセルは「辺をたどり、陸側コーナーと海岸線交差点を
/// 順に拾う」マーチングスクエア的クリップで陸地ポリゴンに切り出すため、海岸線はグリッド解像度より
/// 細かく(交差点を線形補間して)滑らかに出る。高さで頂点を持ち上げることで粗い起伏も表現する。
///
/// Mesh 自体の生成はメインスレッド専用なので、本ビルダーは純粋なデータ(頂点・三角形配列)のみを
/// 構築する。重い符号付き距離計算を含むため、バックグラウンドスレッドから呼んでよい
/// (<see cref="CoastlineStore"/> と渡されるデリゲートがビルド中に変更されないこと)。
/// </summary>
public static class UltraFarLandMeshBuilder
{
    /// <summary>ビルド結果。メインスレッドで Mesh に組み立てる。</summary>
    public struct MeshData
    {
        public List<Vector3> vertices;
        public List<Vector2> uvs;             // UV 座標 (ワールド X, Z)
        public List<Vector3> normals;         // 位置溶接済みのスムース法線
        public List<int> triangles;
    }

    /// <summary>
    /// 陸地メッシュデータを構築する。
    /// </summary>
    /// <param name="mapWidth">マップ幅(X、メートル)。</param>
    /// <param name="mapHeight">マップ高さ(Z、メートル)。</param>
    /// <param name="gridStep">グリッド間隔(メートル)。小さいほど海岸線・起伏が細かいが頂点が増える。</param>
    /// <param name="heightOffset">陸地を海面板の上に保つため全頂点Yに足す微小オフセット(メートル)。</param>
    /// <param name="sampleHeight">ワールド(x,z)→地表ワールドY(メートル)。far_map 由来の粗い高さ。</param>
    public static MeshData Build(
        float mapWidth, float mapHeight, float gridStep, float heightOffset,
        Func<float, float, float> sampleHeight)
    {
        var data = new MeshData
        {
            vertices = new List<Vector3>(4096),
            uvs = new List<Vector2>(4096),
            normals = null, // 末尾でまとめて構築する
            triangles = new List<int>(8192),
        };

        if (gridStep < 1f) gridStep = 1f;

        // 海岸線が無いマップ(全島=全陸)では、海岸線判定を使わずマップ全体を陸として張る。
        bool hasCoastline = CoastlineStore.HasCoastline;

        // 走査範囲: 海岸線のAABB(あれば)をマップ範囲にクランプし、1グリッド分の余白を付ける。
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

        // グリッド頂点ごとに、座標・符号付き距離・高さを事前計算する(セル間で共有)。
        var gx = new float[nx];
        var gz = new float[nz];
        for (int i = 0; i < nx; i++) gx[i] = minX + i * gridStep;
        for (int j = 0; j < nz; j++) gz[j] = minZ + j * gridStep;

        var sGrid = new float[nx, nz]; // 符号付き距離(陸+ / 海-)
        var yGrid = new float[nx, nz]; // 地表ワールドY(オフセット込み)
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                float x = gx[i], z = gz[j];
                sGrid[i, j] = hasCoastline ? CoastlineStore.SignedDistance(x, z) : 1f;
                yGrid[i, j] = sampleHeight(x, z) + heightOffset;
            }
        }

        // セルのコーナーを「上から見て表(+Y)になる」順(c00→c01→c11→c10)で巡回する。
        // 各辺について、始点コーナーが陸なら採用し、辺が陸/海をまたぐなら交差点を補間して採用する。
        // 得られた陸地ポリゴン(3〜5頂点)を扇状に三角形分割する。
        // コーナー添字オフセット(i,j基準)。
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
                        // 符号反転位置 t (sa>0, sb<=0 または逆)。sa==sb は上の aIn!=bIn で除外済み。
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
                    continue; // 陸が無い(全コーナー海)、または退化セル。
                }

                // ポリゴン頂点を追加し、扇状(0, k, k+1)に分割する。
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
    /// 位置溶接済みのスムース法線を構築する。
    ///
    /// 頂点はグリッドセルごとに個別追加されるため、セル境界の同一ワールドXZ位置に重複頂点ができる。
    /// Mesh.RecalculateNormals(面法線を頂点で平均するが、頂点は重複したまま溶接されない)だと
    /// セル境界で法線が不連続になり、遠景陸地がファセット(多面体)状にのっぺり見える。
    /// そこで面積重み付きの面法線を「同一XZ位置」へ集約してから正規化し、セルをまたいで
    /// 連続した滑らかな法線を得る(頂点自体は溶接せず、法線値だけを共有させる)。
    /// </summary>
    private static List<Vector3> BuildSmoothNormals(MeshData data)
    {
        int vcount = data.vertices.Count;
        var normals = new List<Vector3>(vcount);
        for (int i = 0; i < vcount; i++) normals.Add(Vector3.zero);

        // 面積重み付きの面法線(=正規化しない外積)を各頂点へ加算する。
        AccumulateFaceNormals(data.vertices, data.triangles, normals);

        // 同一XZ位置(量子化キー)へ法線を集約する。交差点・コーナー頂点は隣接セルで
        // 同一座標が決定論的に再現されるため、量子化で確実に一致する。
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
            Vector3 fn = Vector3.Cross(vb - va, vc - va); // 面積重み付き(正規化しない)
            normals[ia] += fn;
            normals[ib] += fn;
            normals[ic] += fn;
        }
    }

    /// <summary>ワールドXZを量子化して衝突しない64bitキーにパックする(法線溶接用)。</summary>
    private static long WeldKey(Vector3 v, float quantum)
    {
        int qx = Mathf.RoundToInt(v.x / quantum);
        int qz = Mathf.RoundToInt(v.z / quantum);
        return ((long)qx << 32) | (uint)qz;
    }
}
