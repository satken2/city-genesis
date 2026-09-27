// Assets/Scripts/MeshGeneration/Generators/CarRoadMeshGenerator.cs
using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using GeneratedModels;

public static class BuildingMeshUtils
{
    /// <summary>
    /// 必要なキャッシュ（マテリアルなど）を初期化する
    /// </summary>
    public static void InitializeCache()
    {
    }

    public static bool HasModelAtPath(string resourcePath)
    {
        return ModelManager.Instance != null && ModelManager.Instance.HasModel(resourcePath);
    }

    // ================================================================
    // 描画LOD(接写パーツの解決)
    // ================================================================

    /// <summary>接写用パーツ名のサフィックス(例: Models/Buildings/a2000/0/mid-w1_close)。</summary>
    public const string CLOSE_SUFFIX = "_close";

    /// <summary>
    /// 現在生成中の建物の描画LOD。BuildingMeshBuilder.Generate が生成の前後で設定・復元する。
    /// 接写(Close)のとき、パーツ解決は同じ閾値フォルダの `{パーツ名}_close` を先に試し、無ければ無印を使う。
    /// 生成は同期的にメインスレッドで行われ、十数箇所あるパーツ取得の呼び出しへ引数を通すより
    /// この入口で一括して効かせる。セルのティア表を参照するわけではない。
    /// </summary>
    public static DrawLod CurrentDrawLod { get; set; } = DrawLod.Normal;

    /// <summary>描画LODに応じてリソースパスを解決する(接写なら `_close` 優先)。</summary>
    private static GameObject GetModelForDrawLod(string path, bool suppressWarning)
    {
        if (ModelManager.Instance == null) return null;
        if (CurrentDrawLod == DrawLod.Far)
        {
            // 遠景は専用モデルだけを使い、欠落時に高密度な通常モデルを読み込まない。
            return ModelManager.Instance.GetModel(path + "_far", true);
        }
        if (CurrentDrawLod == DrawLod.Close)
        {
            GameObject close = ModelManager.Instance.GetModel(path + CLOSE_SUFFIX, true);
            if (close != null) return close;
        }
        return ModelManager.Instance.GetModel(path, suppressWarning);
    }


    /// <summary>
    /// プレハブ配下の全メッシュを、プレハブルートのローカル空間へ変換した1つのメッシュに結合して返す。
    /// FBXが複数オブジェクトで構成されていても全パーツが取り込まれ、各ノードのTransform
    /// (位置・回転・スケール)も頂点に反映される。サブメッシュは元レンダラーのサブメッシュ単位で
    /// 維持され、materials には各サブメッシュに対応する共有マテリアルが同じ順序で返る。
    /// SkinnedMeshRenderer はバインドポーズのままベイクして取り込む(ボーン変形は行わない)。
    /// 頂点はサブメッシュごとの連続区画に並び、各区画はそのサブメッシュが参照する頂点だけを持つ
    /// (<see cref="CompactSubMeshVertices"/>)。このため結果をサブメッシュ単位で再び結合しても頂点は複製されない。
    /// メッシュが1つも見つからない場合は null。返されたメッシュは呼び出し側が破棄すること。
    /// </summary>
    public static Mesh CombineModuleMeshes(GameObject prefab, out Material[] materials)
    {
        materials = null;
        if (prefab == null)
        {
            return null;
        }

        List<CombineInstance> instances = new List<CombineInstance>();
        List<Material> materialList = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // プレハブルート基準のローカル座標へ変換する行列。
        // プレハブアセットのルートは通常 identity だが、ルート自体にTransformが付いている場合にも対応する。
        Matrix4x4 rootInverse = prefab.transform.localToWorldMatrix.inverse;

        MeshFilter[] meshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
        foreach (MeshFilter mf in meshFilters)
        {
            if (mf == null || mf.sharedMesh == null)
            {
                continue;
            }

            Matrix4x4 localToRoot = rootInverse * mf.transform.localToWorldMatrix;
            Material[] rendererMaterials = mf.GetComponent<Renderer>()?.sharedMaterials;
            AddSubMeshInstances(mf.sharedMesh, localToRoot, rendererMaterials, instances, materialList, temporaryMeshes);
        }

        // SkinnedMeshRenderer はプレハブアセット上では直接ベイクできないため、一時インスタンスを作ってベイクする。
        SkinnedMeshRenderer[] skinnedRenderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (skinnedRenderers.Length > 0)
        {
            GameObject tempInstance = Object.Instantiate(prefab);
            try
            {
                Matrix4x4 instanceRootInverse = tempInstance.transform.localToWorldMatrix.inverse;
                SkinnedMeshRenderer[] instanceSmrs = tempInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (SkinnedMeshRenderer smr in instanceSmrs)
                {
                    if (smr == null || smr.sharedMesh == null)
                    {
                        continue;
                    }

                    Mesh baked = new Mesh();
                    smr.BakeMesh(baked);
                    temporaryMeshes.Add(baked);

                    Matrix4x4 localToRoot = instanceRootInverse * smr.transform.localToWorldMatrix;
                    AddSubMeshInstances(baked, localToRoot, smr.sharedMaterials, instances, materialList, temporaryMeshes);
                }
            }
            finally
            {
                Object.Destroy(tempInstance);
            }
        }

        if (instances.Count == 0)
        {
            foreach (Mesh temporary in temporaryMeshes)
            {
                Object.Destroy(temporary);
            }
            return null;
        }

        Mesh combined = new Mesh();
        combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        combined.CombineMeshes(instances.ToArray(), false, true);
        combined.RecalculateBounds();

        foreach (Mesh temporary in temporaryMeshes)
        {
            Object.Destroy(temporary);
        }

        materials = materialList.ToArray();
        return combined;
    }

    /// <summary>
    /// CombineModuleMeshes の内部処理。メッシュの全サブメッシュを CombineInstance として登録し、
    /// 対応するマテリアル(不足分は最後の要素、無ければ null)をマテリアルリストへ同期追加する。
    /// 頂点を詰め直した一時メッシュを作った場合は temporaryMeshes へ積む(結合後に破棄する)。
    /// </summary>
    private static void AddSubMeshInstances(Mesh mesh, Matrix4x4 transform, Material[] rendererMaterials,
        List<CombineInstance> instances, List<Material> materialList, List<Mesh> temporaryMeshes)
    {
        Mesh source = CompactSubMeshVertices(mesh);
        if (source != mesh)
        {
            temporaryMeshes.Add(source);
        }

        for (int subIndex = 0; subIndex < source.subMeshCount; subIndex++)
        {
            instances.Add(new CombineInstance
            {
                mesh = source,
                subMeshIndex = subIndex,
                transform = transform
            });

            Material material = null;
            if (rendererMaterials != null && rendererMaterials.Length > 0)
            {
                material = rendererMaterials[Mathf.Min(subIndex, rendererMaterials.Length - 1)];
            }
            materialList.Add(material);
        }
    }

    /// <summary>
    /// 各サブメッシュが参照する頂点だけをサブメッシュごとの連続区画へ詰め直した一時メッシュを返す。
    /// インデックス範囲の中に未参照の頂点を持つサブメッシュが無ければ、元のメッシュをそのまま返す。
    ///
    /// Mesh.CombineMeshes はサブメッシュ単位の CombineInstance でも、そのサブメッシュのインデックスの
    /// 最小〜最大の範囲にある頂点を丸ごと写す。FBX取り込みの頂点溶接は位置・法線・UVが同じ頂点を材質を
    /// またいで1つにまとめる(実寸キューブ投影UVでは、同じ向きで面一に接する別材質の面の頂点が一致する)ため、
    /// 後のサブメッシュの範囲が前のサブメッシュの区画まで伸び、間にある未参照の頂点まで複製される。
    /// 区画内は元の頂点順を保ち、複数のサブメッシュが共有する頂点はサブメッシュごとに持たせる。
    /// どちらも CombineMeshes が元から写していたとおりなので、結合結果は未参照の頂点が消える以外は変わらない。
    /// 頂点はストリームのバイト列のまま写すので、属性の種類と形式も元のメッシュと同じになる。
    /// </summary>
    private static Mesh CompactSubMeshVertices(Mesh mesh)
    {
        int subMeshCount = mesh.subMeshCount;
        int vertexCount = mesh.vertexCount;
        if (!mesh.isReadable || subMeshCount == 0 || vertexCount == 0)
        {
            return mesh;
        }

        // mark[v] == s + 1 は「サブメッシュ s の走査で頂点 v を数え済み」の印。
        int[][] subMeshIndices = new int[subMeshCount][];
        int[] firstVertex = new int[subMeshCount];
        int[] usedCount = new int[subMeshCount];
        int[] mark = new int[vertexCount];
        int packedVertexCount = 0;
        int packedIndexCount = 0;
        bool hasUnreferenced = false;
        for (int s = 0; s < subMeshCount; s++)
        {
            int[] indices = mesh.GetIndices(s);
            int min = int.MaxValue;
            int max = -1;
            int used = 0;
            foreach (int index in indices)
            {
                if (mark[index] == s + 1)
                {
                    continue;
                }
                mark[index] = s + 1;
                used++;
                if (index < min) min = index;
                if (index > max) max = index;
            }
            subMeshIndices[s] = indices;
            firstVertex[s] = min;
            usedCount[s] = used;
            packedVertexCount += used;
            packedIndexCount += indices.Length;
            hasUnreferenced |= used > 0 && max - min + 1 > used;
        }
        if (!hasUnreferenced)
        {
            return mesh;
        }

        using (Mesh.MeshDataArray sourceArray = Mesh.AcquireReadOnlyMeshData(mesh))
        {
            Mesh.MeshData source = sourceArray[0];
            Mesh.MeshDataArray packedArray = Mesh.AllocateWritableMeshData(1);
            bool applied = false;
            try
            {
                Mesh.MeshData packed = packedArray[0];
                packed.SetVertexBufferParams(packedVertexCount, mesh.GetVertexAttributes());
                int streamCount = source.vertexBufferCount;
                if (packed.vertexBufferCount != streamCount)
                {
                    return mesh;
                }
                var sourceStreams = new NativeArray<byte>[streamCount];
                var packedStreams = new NativeArray<byte>[streamCount];
                int[] strides = new int[streamCount];
                for (int stream = 0; stream < streamCount; stream++)
                {
                    strides[stream] = source.GetVertexBufferStride(stream);
                    // 同じ属性記述なら同じレイアウトになる前提でバイト列を写す。崩れていれば詰め直さない。
                    if (packed.GetVertexBufferStride(stream) != strides[stream])
                    {
                        return mesh;
                    }
                    if (strides[stream] > 0)
                    {
                        sourceStreams[stream] = source.GetVertexData<byte>(stream);
                        packedStreams[stream] = packed.GetVertexData<byte>(stream);
                    }
                }

                packed.SetIndexBufferParams(packedIndexCount, UnityEngine.Rendering.IndexFormat.UInt32);
                NativeArray<uint> packedIndices = packed.GetIndexData<uint>();
                var descriptors = new UnityEngine.Rendering.SubMeshDescriptor[subMeshCount];
                int[] remap = new int[vertexCount];
                int nextVertex = 0;
                int nextIndex = 0;
                for (int s = 0; s < subMeshCount; s++)
                {
                    // 他のサブメッシュの印と区別できる負の印を、このサブメッシュの参照頂点に付け直してから走査する。
                    int marker = -(s + 1);
                    int[] indices = subMeshIndices[s];
                    foreach (int index in indices)
                    {
                        mark[index] = marker;
                    }

                    int blockStart = nextVertex;
                    int runSource = 0;
                    int runTarget = 0;
                    int runLength = 0;
                    for (int v = firstVertex[s], remaining = usedCount[s]; remaining > 0; v++)
                    {
                        if (mark[v] != marker)
                        {
                            continue;
                        }
                        remaining--;
                        remap[v] = nextVertex;
                        if (runLength > 0 && runSource + runLength == v)
                        {
                            runLength++;
                        }
                        else
                        {
                            CopyVertexRun(sourceStreams, packedStreams, strides, runSource, runTarget, runLength);
                            runSource = v;
                            runTarget = nextVertex;
                            runLength = 1;
                        }
                        nextVertex++;
                    }
                    CopyVertexRun(sourceStreams, packedStreams, strides, runSource, runTarget, runLength);

                    for (int i = 0; i < indices.Length; i++)
                    {
                        packedIndices[nextIndex + i] = (uint)remap[indices[i]];
                    }
                    descriptors[s] = new UnityEngine.Rendering.SubMeshDescriptor(nextIndex, indices.Length, mesh.GetTopology(s))
                    {
                        firstVertex = blockStart,
                        vertexCount = nextVertex - blockStart,
                    };
                    nextIndex += indices.Length;
                }

                packed.subMeshCount = subMeshCount;
                for (int s = 0; s < subMeshCount; s++)
                {
                    packed.SetSubMesh(s, descriptors[s]);
                }

                Mesh compacted = new Mesh { name = mesh.name };
                Mesh.ApplyAndDisposeWritableMeshData(packedArray, compacted);
                applied = true;
                return compacted;
            }
            finally
            {
                if (!applied)
                {
                    packedArray.Dispose();
                }
            }
        }
    }

    /// <summary>元の頂点番号 sourceVertex からの連続 count 頂点を、全ストリームについて targetVertex 以降へ写す。</summary>
    private static void CopyVertexRun(NativeArray<byte>[] source, NativeArray<byte>[] target, int[] strides,
        int sourceVertex, int targetVertex, int count)
    {
        if (count == 0)
        {
            return;
        }
        for (int stream = 0; stream < strides.Length; stream++)
        {
            int stride = strides[stream];
            if (stride > 0)
            {
                NativeArray<byte>.Copy(source[stream], sourceVertex * stride, target[stream], targetVertex * stride, count * stride);
            }
        }
    }

    /// <summary>
    /// モジュールに設定されている sharedMaterials を取得する
    /// </summary>
    /// <remarks>
    /// sharedMaterials をそのまま返すことで、同じマテリアルアセットを複数建物で共有しパフォーマンスを確保する。
    /// </remarks>
    public static Material[] GetModuleMaterials(GameObject module)
    {
        if (module == null)
        {
            return null;
        }

        Renderer renderer = module.GetComponentInChildren<Renderer>();
        if (renderer == null)
        {
            return null;
        }

        return renderer.sharedMaterials;
    }

    /// <summary>
    /// 1階あたりの高さ(m)を返す。解決順はパーツ prefix と同じく「名所 → バリアント」で、
    /// 観光名所(landmark > 0)は landmark_master の階高を優先する。
    /// バックエンドも建物高さを名所行の階高で確定するため、ここを揃えないと
    /// 3Dの高さ・カリング半径・炎エフェクトの高さがデータ上の高さとズレる。
    /// </summary>
    public static float GetFloorHeight(BuildingSummary buildingData)
    {
        float floorHeight = LocalConstants.FLOOR_HEIGHT;
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            if (buildingData.landmark > 0)
            {
                var landmarkMaster = MasterDataStore.Instance.GetLandmarkMaster(buildingData.landmark);
                if (landmarkMaster != null && landmarkMaster.height_per_floor.HasValue)
                {
                    return landmarkMaster.height_per_floor.Value;
                }
            }

            var agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingData.variant);
            if (agentMaster != null && agentMaster.height_per_floor.HasValue)
            {
                floorHeight = agentMaster.height_per_floor.Value;
            }
        }
        return floorHeight;
    }

    public static Material GetPartMaterialBySuffix(string prefix, string partName, int suffix)
    {
        GameObject module = GetPartNoPosition(prefix, partName, suffix);
        if (module == null)
        {
            return null;
        }

        Material[] materials = GetModuleMaterials(module);
        if (materials != null && materials.Length > 0)
        {
            return materials[0];
        }

        return null;
    }

    /// <summary>
    /// 屋根用のマテリアルを選択する。
    /// 指定されたプレフィックスと閾値に合致する天井パーツからマテリアルを取得し、
    /// 存在しない場合はフォールバック配列内の有効なマテリアル、
    /// それも存在しない場合はシステム既定のデフォルトマテリアルを使用する。
    /// </summary>
    public static Material SelectRoofMaterial(string prefix, int suffix, Material[] fallbackMaterials)
    {
        Material moduleMaterial = GetPartMaterialBySuffix(prefix, "ceil", suffix);
        if (moduleMaterial != null) return moduleMaterial;
        
        Material fallback = SelectFallbackMaterial(fallbackMaterials);
        if (fallback != null) return fallback;

        return BuildingMeshBuilder.GetDefaultBuildingMaterial();
    }

    /// <summary>
    /// 地面パネル用のマテリアルを選択する。
    /// 指定されたプレフィックスと閾値に合致する床パーツからマテリアルを取得し、
    /// 存在しない場合はフォールバック配列内の有効なマテリアル、
    /// それも存在しない場合はシステム既定のデフォルトマテリアルを使用する。
    /// </summary>
    public static Material SelectFloorMaterial(string prefix, int suffix, Material[] fallbackMaterials)
    {
        Material moduleMaterial = GetPartMaterialBySuffix(prefix, "floor", suffix);
        if (moduleMaterial != null) return moduleMaterial;

        Material fallback = SelectFallbackMaterial(fallbackMaterials);
        if (fallback != null) return fallback;

        return BuildingMeshBuilder.GetDefaultBuildingMaterial();
    }

    /// <summary>
    /// 窓や開口を持たない無地の壁面用のマテリアルを選択する。切妻屋根の妻壁のように、
    /// プロシージャルに張った壁面へ「普通の壁と同じ材質」を与えるために使う。
    /// `generic-wall` パーツはマテリアル参照専用（形状は使われない）。
    /// 存在しない場合はフォールバック配列内の有効なマテリアル（＝躯体の壁パーツのもの。
    /// 窓の意匠が乗るため見た目は妥協になる）、それも無ければ既定マテリアルを使う。
    /// </summary>
    public static Material SelectGenericWallMaterial(string prefix, int suffix, Material[] fallbackMaterials)
    {
        Material moduleMaterial = GetPartMaterialBySuffix(prefix, "generic-wall", suffix);
        if (moduleMaterial != null) return moduleMaterial;

        Material fallback = SelectFallbackMaterial(fallbackMaterials);
        if (fallback != null) return fallback;

        return BuildingMeshBuilder.GetDefaultBuildingMaterial();
    }

    /// <summary>
    /// 提供されたフォールバックマテリアル配列の中から、null でない最初のマテリアルを選択する。
    /// 全て null または配列が空の場合は null を返す。
    /// </summary>
    private static Material SelectFallbackMaterial(Material[] fallbackMaterials)
    {
        if (fallbackMaterials != null && fallbackMaterials.Length > 0)
        {
            foreach (var mat in fallbackMaterials)
            {
                if (mat != null)
                {
                    return mat;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 25パーツシステムのモジュールを取得
    /// パス: Models/Buildings/{prefix}/{suffix}/{level}-{type}{position}
    /// 例: Models/Buildings/b16/500/mid-w1
    /// </summary>
    public static GameObject GetPart(string prefix, string level, string type, int position, int suffix, bool suppressWarning = false)
    {
        string posStr = position > 0 ? position.ToString() : "";
        string partName = $"{level}-{type}{posStr}";
        string path = $"Models/Buildings/{prefix}/{suffix}/{partName}";

        return GetModelForDrawLod(path, suppressWarning);
    }

    /// <summary>
    /// 塔屋や屋上マテリアルなど、位置番号を持たないパーツを取得
    /// パス: Models/Buildings/{prefix}/{suffix}/{partName}
    /// </summary>
    public static GameObject GetPartNoPosition(string prefix, string partName, int suffix, bool suppressWarning = false)
    {
        string path = $"Models/Buildings/{prefix}/{suffix}/{partName}";

        return GetModelForDrawLod(path, suppressWarning);
    }

    /// <summary>
    /// visual_variant (0.0-1.0) をバリアント番号 (0-1000) に変換
    /// </summary>
    public static int ComputeVariantNumber(float visual_variant)
    {
        return Mathf.FloorToInt(Mathf.Clamp01(visual_variant) * 1000f);
    }

    /// <summary>
    /// 指定パーツが存在するか確認（位置付き）
    /// パス: Models/Buildings/{prefix}/{suffix}/{level}-{type}{position}
    /// </summary>
    public static bool HasPart(string prefix, string level, string type, int position, int suffix)
    {
        string posStr = position > 0 ? position.ToString() : "";
        string partName = $"{level}-{type}{posStr}";
        string path = $"Models/Buildings/{prefix}/{suffix}/{partName}";
        return HasModelAtPath(path);
    }


    /// <summary>
    /// 多角形（CCW）を外側へ一定距離オフセットする。各頂点を隣り合う2辺の外向き法線の
    /// 二等分方向へ移動し、鋭角の頂点で角が痩せないよう移動量を 1/cos で割り増しする。
    /// 屋根の軒の出（けらば・軒先の張り出し）の輪郭を作るために、各屋根ビルダーが共有する。
    /// </summary>
    public static Vector2[] OffsetPolygonOutward(Vector2[] polygon, float offset)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return null;
        }

        int count = polygon.Length;
        Vector2[] offsetPoints = new Vector2[count];

        for (int i = 0; i < count; i++)
        {
            Vector2 prev = polygon[(i - 1 + count) % count];
            Vector2 curr = polygon[i];
            Vector2 next = polygon[(i + 1) % count];

            Vector2 prevDir = curr - prev;
            Vector2 nextDir = next - curr;
            if (prevDir.sqrMagnitude < 1e-6f || nextDir.sqrMagnitude < 1e-6f)
            {
                offsetPoints[i] = curr;
                continue;
            }

            Vector2 prevNorm = prevDir.normalized;
            Vector2 nextNorm = nextDir.normalized;
            Vector2 prevOutward = new Vector2(prevNorm.y, -prevNorm.x);
            Vector2 nextOutward = new Vector2(nextNorm.y, -nextNorm.x);
            Vector2 bisector = prevOutward + nextOutward;

            if (bisector.sqrMagnitude < 1e-6f)
            {
                offsetPoints[i] = curr + nextOutward * offset;
                continue;
            }

            Vector2 offsetDir = bisector.normalized;
            float alignment = Mathf.Clamp(Vector2.Dot(offsetDir, prevOutward), 0.1f, 1f);
            offsetPoints[i] = curr + offsetDir * (offset / alignment);
        }

        return offsetPoints;
    }

    /// <summary>
    /// 多角形の最小面積外接矩形（回転キャリパー）を求め、長辺方向と短辺方向を返す。
    /// 切妻屋根は長辺方向を棟線に、片流れ屋根は短辺方向を傾斜方向に使う。
    /// </summary>
    /// <param name="polygon">対象の多角形</param>
    /// <param name="center">外接矩形の中心</param>
    /// <param name="longDir">長辺方向（正規化済み）</param>
    /// <param name="shortDir">短辺方向（長辺と直交、正規化済み）</param>
    /// <param name="halfLength">長辺方向の半長</param>
    /// <param name="halfWidth">短辺方向の半幅</param>
    /// <returns>方向を決められない退化ポリゴンでは false</returns>
    public static bool TryResolveMinAreaRect(Vector2[] polygon,
        out Vector2 center, out Vector2 longDir, out Vector2 shortDir,
        out float halfLength, out float halfWidth)
    {
        center = Vector2.zero;
        longDir = Vector2.right;
        shortDir = Vector2.up;
        halfLength = 0f;
        halfWidth = 0f;

        if (polygon == null || polygon.Length < 3)
        {
            return false;
        }

        int n = polygon.Length;
        float bestArea = float.MaxValue;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            Vector2 edge = polygon[(i + 1) % n] - polygon[i];
            if (edge.sqrMagnitude < 1e-6f)
            {
                continue;
            }

            Vector2 axisX = edge.normalized;
            Vector2 axisY = new Vector2(-axisX.y, axisX.x);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int j = 0; j < n; j++)
            {
                float x = Vector2.Dot(polygon[j], axisX);
                float y = Vector2.Dot(polygon[j], axisY);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            if (sizeX < 1e-4f || sizeY < 1e-4f)
            {
                continue;
            }

            float area = sizeX * sizeY;
            if (area >= bestArea)
            {
                continue;
            }

            bestArea = area;
            found = true;
            center = axisX * ((minX + maxX) * 0.5f) + axisY * ((minY + maxY) * 0.5f);

            if (sizeX >= sizeY)
            {
                longDir = axisX;
                shortDir = axisY;
                halfLength = sizeX * 0.5f;
                halfWidth = sizeY * 0.5f;
            }
            else
            {
                longDir = axisY;
                shortDir = axisX;
                halfLength = sizeY * 0.5f;
                halfWidth = sizeX * 0.5f;
            }
        }

        return found && halfWidth > 1e-3f && halfLength > 1e-3f;
    }

    public static Vector2[] GetFootprint(BuildingSummary buildingData)
    {
        // 底面の座標をVector2配列へ格納
        Vector2[] footprint = new Vector2[buildingData.polygonArray.Length];
        for (int i = 0; i < buildingData.polygonArray.Length; i++)
        {
            footprint[i] = new Vector2(buildingData.polygonArray[i][0], buildingData.polygonArray[i][1]);
        }

        // 区画全面を使う建物(農地など)はバックエンドが区画ポリゴンをそのまま底面に採用する
        // ため、建物ポリゴンにも区画由来の縮退頂点(共線・重複)が入り得る。壁・角の配置が
        // 破綻しないよう区画と同じ間引きを通す。間引きで3頂点未満になる場合は生データを使う。
        Vector2[] sanitized = SanitizeRingPolygon(footprint);
        if (sanitized != null)
        {
            footprint = sanitized;
        }

        // 頂点の並びをCCW(反時計回り)に正規化
        if (PolygonUtils.SignedArea(footprint) < 0f)
        {
            System.Array.Reverse(footprint);
        }

        return footprint;
    }


    public readonly struct BuildingModelNaming
    {
        public string Prefix { get; }
        public bool IsPrebuilt { get; }

        public BuildingModelNaming(string prefix, bool isPrebuilt)
        {
            Prefix = prefix;
            IsPrebuilt = isPrebuilt;
        }
    }

    public static BuildingModelNaming ResolveModelNaming(BuildingSummary buildingData)
    {
        if (buildingData == null)
        {
            return new BuildingModelNaming(string.Empty, false);
        }

        InitializeCache();
        string prefix = DeterminePrefix(buildingData);
        bool prebuilt = DetermineIsPrebuilt(buildingData);

        return new BuildingModelNaming(prefix, prebuilt);
    }

    public static string GetGlbPrefix1(BuildingSummary buildingData) => ResolveModelNaming(buildingData).Prefix;

    /// <summary>
    /// パーツフォルダの prefix を決める。構造種別(structure > 0)を持つ建物は、構造つきフォルダ
    /// (a{variant}s{structure} / b{bd_type}s{structure})があればそれを優先し、無ければ従来どおり
    /// a{variant} → b{bd_type} へ落ちる。構造の差は木造の板壁 / RC の打放しのようにマテリアルや
    /// 壁パーツの差し替えで表現し、ジオメトリの生成方式(model_type)は変えない。
    /// 解決順: landmark-{model_prefix} → a{v}s{s} → a{v} → b{bd}s{s} → b{bd}
    ///
    /// 観光名所(landmark > 0)が先頭なのは、名所が一点物でありながら用途バリアントを
    /// 1つ(観光名所)しか持たないため、a{variant} では名所同士を区別できないからである。
    /// </summary>
    private static string DeterminePrefix(BuildingSummary buildingData)
    {
        string landmarkPrefix = DetermineLandmarkPrefix(buildingData.landmark);
        if (landmarkPrefix != null)
        {
            return landmarkPrefix;
        }

        int structure = buildingData.structure;
        string variantPrefix = $"a{buildingData.variant}";
        if (structure > 0 && AgentModelConfig.HasStructurePrefix($"{variantPrefix}s{structure}"))
        {
            return $"{variantPrefix}s{structure}";
        }
        if (HasVariantDefinition(buildingData.variant))
        {
            return variantPrefix;
        }

        int bdType = ResolveBdTypeFallback(buildingData.variant, buildingData.bd_type ?? 0);
        if (structure > 0 && AgentModelConfig.HasStructurePrefix($"b{bdType}s{structure}"))
        {
            return $"b{bdType}s{structure}";
        }
        return $"b{bdType}";
    }

    /// <summary>
    /// 観光名所のパーツフォルダ名(landmark-{model_prefix})を返す。名所でない場合、
    /// マスターから model_prefix を解決できない場合、実在フォルダが無い場合は null。
    /// 実在判定は agent_model_config.yaml の landmark_prefixes による(構造つきフォルダと同じ方式)。
    /// </summary>
    private static string DetermineLandmarkPrefix(int landmark)
    {
        if (landmark <= 0 || MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized)
        {
            return null;
        }

        string modelPrefix = MasterDataStore.Instance.GetLandmarkModelPrefix(landmark);
        if (string.IsNullOrEmpty(modelPrefix))
        {
            return null;
        }

        string prefix = $"landmark-{modelPrefix}";
        return AgentModelConfig.HasLandmarkPrefix(prefix) ? prefix : null;
    }

    private static bool DetermineIsPrebuilt(BuildingSummary buildingData)
    {
        if (buildingData == null || buildingData.variant == 0)
        {
            return false;
        }

        return AgentModelConfig.TryGetAgentDefinition(buildingData.variant, out var definition) && definition?.prebuilt == true;
    }

    private static int ResolveBdTypeFallback(int variant, int originalBdType)
    {
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            try
            {
                var agentMaster = MasterDataStore.Instance.GetAgentMaster(variant);
                if (agentMaster?.bd_type != null)
                {
                    return agentMaster.bd_type.Value;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"BuildingMeshUtils: bd_type を取得できませんでした (variant={variant}): {ex.Message}");
            }
        }

        return originalBdType;
    }

    private static bool HasVariantDefinition(int variant)
    {
        if (variant <= 0)
        {
            return false;
        }

        return AgentModelConfig.TryGetAgentDefinition(variant, out var definition) && definition != null;
    }

    /// <summary>
    /// 建物が属する土地区画(parcel)のポリゴンと、建物の配置基準点(centerPointWorld)を解決する。
    /// ModularBuildingMeshBuilder / PreBuiltBuildingMeshBuilder / ConstructingBuildingMeshBuilder が
    /// 共通で使う「敷地コンテキスト」の取得処理。
    ///
    /// - 返り値はCCWに正規化された区画ポリゴンで、座標は建物の center_point 基準の
    ///   ローカルXZ座標(建物 footprint と同じ規約)。区画が取得できない場合は null。
    ///   ParcelManager の区画ポリゴンはワールド座標だが、メッシュ生成側は建物GameObject
    ///   (center_point 位置に配置される)のローカル空間で頂点を組み立てるため、ここで変換して返す。
    /// - centerPointWorld のYは、区画が取得できて useParcelAltitude が true なら parcel.altitude、
    ///   それ以外は建物中心位置の地形標高になる。
    /// </summary>
    public static Vector2[] ResolveParcelContext(BuildingSummary buildingData, bool useParcelAltitude,
        out ParcelSummary parcel, out Vector3 centerPointWorld)
    {
        parcel = null;
        centerPointWorld = Vector3.zero;
        Vector2[] parcelFootprint = null;

        if (buildingData.parcel_id > 0)
        {
            ParcelManager parcelManager = Object.FindAnyObjectByType<ParcelManager>();
            if (parcelManager != null)
            {
                parcel = parcelManager.GetParcelSummary(buildingData.parcel_id);
                if (parcel == null)
                {
                    Debug.LogWarning($"[BMU] ResolveParcelContext: parcel_id={buildingData.parcel_id} が ParcelManager のキャッシュに存在しません。");
                }
            }
            else
            {
                Debug.LogWarning("[BMU] ResolveParcelContext: ParcelManager がシーンに存在しません。");
            }
        }

        float cx = 0f, cz = 0f;
        if (buildingData.centerPointArray != null && buildingData.centerPointArray.Length >= 2)
        {
            cx = buildingData.centerPointArray[0];
            cz = buildingData.centerPointArray[1];
            float cy;
            if (parcel != null && useParcelAltitude)
            {
                cy = parcel.altitude;
            }
            else
            {
                cy = Utils.GetGroundHeight(new Vector3(cx, 1000f, cz));
            }
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        if (parcel != null)
        {
            float[][] poly = parcel.GetPolygonArray();
            if (poly != null && poly.Length >= 3)
            {
                Vector2[] raw = new Vector2[poly.Length];
                for (int i = 0; i < poly.Length; i++)
                {
                    raw[i] = new Vector2(poly[i][0] - cx, poly[i][1] - cz);
                }
                parcelFootprint = SanitizeRingPolygon(raw);
                if (parcelFootprint != null && PolygonUtils.SignedArea(parcelFootprint) < 0f)
                {
                    System.Array.Reverse(parcelFootprint);
                }
            }
        }

        return parcelFootprint;
    }

    /// <summary>縮退辺とみなす辺長(m)。これ未満しか離れていない頂点は同一点として除去する。</summary>
    private const float RING_MIN_EDGE_LENGTH = 0.01f;

    /// <summary>ほぼ一直線とみなす方向変化角(度)。これ未満の中間頂点は共線として除去する。</summary>
    private const float RING_COLLINEAR_ANGLE_DEG = 5f;

    /// <summary>
    /// 輪ポリゴン(建物底面・土地区画)をメッシュ生成用に正規化する。バックエンドのポリゴンは
    /// 「閉じた輪」の契約(B_土地形状.md)であり、shapely 由来の閉環重複(末尾=先頭)や、
    /// 道路用地の切り取り・区画整形で入る「ほぼ一直線上の中間頂点」を含むことがある
    /// (区画全面を使う建物では底面=区画ポリゴンなので建物側にも同じ縮退が入る)。
    /// これらをそのまま壁・角の配置(1頂点=1角パーツ)に使うと、ゼロ長辺では内角が
    /// 計算不能になって角パーツが閉じた X 字に折れ、内角180°付近では角パーツが開ききって
    /// 壁の線を横切る形に暴れる。①縮退辺(閉環重複を含む)の頂点、②方向変化が閾値未満の
    /// 共線頂点を除去し、除去後3頂点未満なら null を返す(呼び出し側でフォールバックする)。
    /// </summary>
    private static Vector2[] SanitizeRingPolygon(Vector2[] ring)
    {
        if (ring == null || ring.Length < 3) return null;

        List<Vector2> dedup = new List<Vector2>(ring.Length);
        foreach (Vector2 p in ring)
        {
            if (dedup.Count == 0 || (p - dedup[dedup.Count - 1]).magnitude >= RING_MIN_EDGE_LENGTH)
            {
                dedup.Add(p);
            }
        }
        // 輪の綴じ目(末尾→先頭)側の縮退辺。shapely の閉環重複はここで落ちる。
        while (dedup.Count >= 2 && (dedup[0] - dedup[dedup.Count - 1]).magnitude < RING_MIN_EDGE_LENGTH)
        {
            dedup.RemoveAt(dedup.Count - 1);
        }
        if (dedup.Count < 3) return null;

        // 共線頂点の判定は「最後に残した頂点からの方向」と「次辺の方向」の差で行う。
        // 各頂点単独の方向変化で判定すると、緩いカーブ上の頂点(1個ずつは閾値未満でも
        // 累積すると大きく曲がる)が全滅して弧が弦に潰れるため、累積変化が閾値を超えた
        // 時点で頂点を残す方式にしている。走査の開始点(アンカー)は方向変化が最大の
        // 頂点=確実な角にして、カーブの途中から走査が始まるのを防ぐ。
        int n = dedup.Count;
        int anchor = 0;
        float maxTurn = -1f;
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = dedup[(i + n - 1) % n];
            Vector2 curr = dedup[i];
            Vector2 next = dedup[(i + 1) % n];
            float turn = Vector2.Angle(curr - prev, next - curr);
            if (turn > maxTurn)
            {
                maxTurn = turn;
                anchor = i;
            }
        }

        List<int> keptIndices = new List<int>(n) { anchor };
        Vector2 lastKept = dedup[anchor];
        for (int k = 1; k < n; k++)
        {
            int idx = (anchor + k) % n;
            Vector2 curr = dedup[idx];
            Vector2 next = dedup[(anchor + k + 1) % n];
            if (Vector2.Angle(curr - lastKept, next - curr) >= RING_COLLINEAR_ANGLE_DEG)
            {
                keptIndices.Add(idx);
                lastKept = curr;
            }
        }
        if (keptIndices.Count < 3) return null;

        // アンカーは走査の開始点にすぎないので、出力は元の頂点順で返す。
        // 頂点0=前面の辺(エントランス・塔屋の向き、壁・角の位置番号)の規約を保つため。
        keptIndices.Sort();
        Vector2[] result = new Vector2[keptIndices.Count];
        for (int i = 0; i < keptIndices.Count; i++)
        {
            result[i] = dedup[keptIndices[i]];
        }
        return result;
    }

    /// <summary>
    /// 位置番号を持たないパーツを「バリアント/建物タイプ → 標準パーツ(common)」の順で解決する。
    /// scaffold-wall / scaffold-corner / crane-base / crane-mid / crane-top など、
    /// 全建物共通のフォールバック先を持つ工事系パーツの取得に使う。
    ///
    /// 解決順:
    /// 1. Models/Buildings/{prefix}/{suffix}/{partName}
    ///    (suffix は標準閾値候補 0,100,...,1000 のうち variantNumber 以下でパーツが存在する最大値。
    ///     閾値0は常に候補に含まれるため「{prefix}/0」へのフォールバックもここでカバーされる)
    /// 2. Models/Buildings/common/{partName} (標準パーツ。common は外観バリアント閾値の概念を
    ///     持たないため suffix フォルダを挟まず直接置く)
    /// 3. どちらも無ければ null (呼び出し側はそのパーツをスキップする)
    /// </summary>
    public static GameObject GetPartWithCommonFallback(string prefix, string partName, float visualVariant)
    {
        GameObject part = GetPartBySuffixScan(prefix, partName, ComputeVariantNumber(visualVariant));
        if (part != null)
        {
            return part;
        }
        return GetCommonPart(partName);
    }

    /// <summary>
    /// 指定プレフィックス配下で partName を suffix 走査して解決する(common へのフォールバックはしない)。
    /// suffix は標準閾値候補 0,100,...,1000 のうち variantNumber 以下でパーツが存在する最大値。
    /// 見つからなければ null。マッシュアップ建物のパーツ解決(パーツごとの独立スキャン)にも使う。
    /// </summary>
    public static GameObject GetPartBySuffixScan(string prefix, string partName, int variantNumber)
    {
        int bestSuffix = -1;
        for (int suffix = 0; suffix <= 1000; suffix += 100)
        {
            if (suffix > variantNumber) continue;
            if (HasModelAtPath($"Models/Buildings/{prefix}/{suffix}/{partName}") && suffix > bestSuffix)
            {
                bestSuffix = suffix;
            }
        }
        return bestSuffix >= 0 ? GetPartNoPosition(prefix, partName, bestSuffix, true) : null;
    }

    /// <summary>
    /// 標準パーツ(common)を取得する。common は外観バリアント閾値の概念を持たないため
    /// suffix フォルダを挟まず Models/Buildings/common/{partName} へ直接置く。
    /// </summary>
    private static GameObject GetCommonPart(string partName)
    {
        string commonPath = $"Models/Buildings/{LocalConstants.CONSTRUCTION_COMMON_PREFIX}/{partName}";
        return GetModelForDrawLod(commonPath, true);
    }

    /// <summary>
    /// 地面パネル(floor)のマテリアルを a{variant} → b{bd_type} → common の順でフォールバック解決する。
    /// draw_floor=yes の建物でのみ呼ばれ、どこにも floor パーツが無ければ null を返す
    /// (既定建物マテリアルへはフォールバックしない=不要なスラブを描かない)。
    /// aPrefix と bPrefix が同一(a{variant} 定義が無く両方 b{bd_type} の場合)は b 段をスキップする。
    /// </summary>
    public static Material SelectFloorMaterialWithFallback(string aPrefix, string bPrefix, float visualVariant)
    {
        int variantNumber = ComputeVariantNumber(visualVariant);

        GameObject part = GetPartBySuffixScan(aPrefix, "floor", variantNumber);
        if (part == null && !string.IsNullOrEmpty(bPrefix) && bPrefix != aPrefix)
        {
            part = GetPartBySuffixScan(bPrefix, "floor", variantNumber);
        }
        if (part == null)
        {
            part = GetCommonPart("floor");
        }
        if (part == null)
        {
            return null;
        }

        Material[] materials = GetModuleMaterials(part);
        return (materials != null && materials.Length > 0) ? materials[0] : null;
    }

    /// <summary>
    /// master の draw_floor フラグに従って地面パネル(floor)のマテリアルを解決する。
    /// draw_floor=yes の建物だけ地面パネルを描画する仕様のため、未設定(null)/false や
    /// master 未初期化・バリアント未登録では null を返す（＝描画しない）。
    /// マテリアルは floor パーツを a{variant}(=prefix) → b{bd_type} → common の順にフォールバック解決し、
    /// どこにも floor パーツが無ければ null（既定建物マテリアルにはフォールバックしない）。
    /// prefix は呼び出し側で解決済みの建物プレフィックス（a{variant} または b{bd_type}）。
    /// モジュラー・既成建物の両方から共通で使う。
    /// </summary>
    public static Material ResolveFloorMaterialFromMaster(BuildingSummary buildingData, string prefix)
    {
        if (buildingData == null || MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized)
        {
            return null;
        }

        MasterDataStore.AgentMasterItem agentMaster;
        try
        {
            agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingData.variant);
        }
        catch (System.Exception)
        {
            return null;
        }

        if (agentMaster?.draw_floor != true)
        {
            return null;
        }

        int bdType = agentMaster.bd_type ?? buildingData.bd_type ?? 0;
        return SelectFloorMaterialWithFallback(prefix, $"b{bdType}", buildingData.visual_variant);
    }

    /// <summary>Y方向スケール計算時のモジュール高さの下限。ゼロ除算と極端な拡大を防ぐ。</summary>
    private const float MIN_STACKED_MODULE_HEIGHT = 0.01f;

    /// <summary>
    /// プレハブモジュールを指定のY位置へ配置し、CombineInstance リストへ追加する。
    /// prebuilt建物(base/mid/top-prebuilt)とクレーン(crane-base/mid/top)の積み上げに共通で使う。
    ///
    /// - fitHeight を指定した場合はモジュール高さがその値になるようY方向のみスケールする(XZは実寸のまま)。
    ///   null の場合は一切スケールしない。prebuilt建物は mid だけ階高へ合わせ(base/top は null)、
    ///   マッシュアップのサブ建物は全パーツ null(実寸)、工事中のクレーンは base/mid を階高へ合わせる。
    /// - 高さ方向の基準は alignByOrigin で切り替える:
    ///   - false(既定・prebuilt): FBXのピボット位置に依存せず積むため、メッシュ下端(bounds.min.y)を
    ///     baseY に揃える。fitHeight はメッシュ全高(bounds.size.y)に対して合わせる。
    ///   - true(クレーン等): モデルの原点(y=0)が接続点(下端)である前提で、原点を baseY に置く。
    ///     fitHeight は原点より上の高さ(bounds.max.y)に対して合わせる。原点より下に張り出しがある
    ///     パーツでも接続点がぴたりと合い、段どうしの隙間が出ない。
    /// - XZはモデルの原点(ピボット)を xzOffset だけ平行移動した位置に重ねる。
    ///   rotation はY軸回転(原点基準)を頂点に適用する。
    /// </summary>
    /// <returns>配置したモジュールが消費する高さ(m。次段の baseY 加算に使う)。配置に失敗した場合は 0。</returns>
    public static float AddStackedModule(GameObject prefab, float baseY, float? fitHeight,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes,
        Vector2 xzOffset = default, Quaternion? rotation = null, bool alignByOrigin = false)
    {
        Mesh moduleMesh = CombineModuleMeshes(prefab, out Material[] moduleMaterials);
        if (moduleMesh == null || moduleMesh.subMeshCount == 0)
        {
            if (moduleMesh != null)
            {
                Object.Destroy(moduleMesh);
            }
            return 0f;
        }

        Bounds bounds = moduleMesh.bounds;
        // fitHeight を合わせる基準高さ。alignByOrigin=true は原点より上の高さ、false はメッシュ全高。
        float refHeight = alignByOrigin
            ? Mathf.Max(bounds.max.y, MIN_STACKED_MODULE_HEIGHT)
            : Mathf.Max(bounds.size.y, MIN_STACKED_MODULE_HEIGHT);
        float scaleY = fitHeight.HasValue ? fitHeight.Value / refHeight : 1f;
        Quaternion rot = rotation ?? Quaternion.identity;
        bool hasRotation = rotation.HasValue;

        Vector3[] vertices = moduleMesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            // alignByOrigin=true は原点(y=0)基準でスケール、false はメッシュ下端を baseY に揃える
            v.y = alignByOrigin ? v.y * scaleY : (v.y - bounds.min.y) * scaleY;
            if (hasRotation)
            {
                v = rot * v;
            }
            v.x += xzOffset.x;
            v.y += baseY;
            v.z += xzOffset.y;
            vertices[i] = v;
        }
        moduleMesh.vertices = vertices;
        moduleMesh.RecalculateNormals();
        moduleMesh.RecalculateBounds();

        for (int subIndex = 0; subIndex < moduleMesh.subMeshCount; subIndex++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = moduleMesh,
                subMeshIndex = subIndex,
                transform = Matrix4x4.identity,
            });

            Material material = null;
            if (moduleMaterials != null && subIndex < moduleMaterials.Length)
            {
                material = moduleMaterials[subIndex];
            }
            materials.Add(material);
        }

        temporaryMeshes.Add(moduleMesh);

        return fitHeight ?? refHeight;
    }

}
