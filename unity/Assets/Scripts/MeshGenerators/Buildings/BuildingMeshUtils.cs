// Assets/Scripts/MeshGeneration/Generators/CarRoadMeshGenerator.cs
using UnityEngine;
using System.Collections.Generic;
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


    /// <summary>
    /// プレハブ配下の全メッシュを、プレハブルートのローカル空間へ変換した1つのメッシュに結合して返す。
    /// FBXが複数オブジェクトで構成されていても全パーツが取り込まれ、各ノードのTransform
    /// (位置・回転・スケール)も頂点に反映される。サブメッシュは元レンダラーのサブメッシュ単位で
    /// 維持され、materials には各サブメッシュに対応する共有マテリアルが同じ順序で返る。
    /// SkinnedMeshRenderer はバインドポーズのままベイクして取り込む(ボーン変形は行わない)。
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
        List<Mesh> bakedMeshes = new List<Mesh>();

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
            AddSubMeshInstances(mf.sharedMesh, localToRoot, rendererMaterials, instances, materialList);
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
                    bakedMeshes.Add(baked);

                    Matrix4x4 localToRoot = instanceRootInverse * smr.transform.localToWorldMatrix;
                    AddSubMeshInstances(baked, localToRoot, smr.sharedMaterials, instances, materialList);
                }
            }
            finally
            {
                Object.Destroy(tempInstance);
            }
        }

        if (instances.Count == 0)
        {
            foreach (Mesh baked in bakedMeshes)
            {
                Object.Destroy(baked);
            }
            return null;
        }

        Mesh combined = new Mesh();
        combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        combined.CombineMeshes(instances.ToArray(), false, true);
        combined.RecalculateBounds();

        foreach (Mesh baked in bakedMeshes)
        {
            Object.Destroy(baked);
        }

        materials = materialList.ToArray();
        return combined;
    }

    /// <summary>
    /// CombineModuleMeshes の内部処理。メッシュの全サブメッシュを CombineInstance として登録し、
    /// 対応するマテリアル(不足分は最後の要素、無ければ null)をマテリアルリストへ同期追加する。
    /// </summary>
    private static void AddSubMeshInstances(Mesh mesh, Matrix4x4 transform, Material[] rendererMaterials,
        List<CombineInstance> instances, List<Material> materialList)
    {
        for (int subIndex = 0; subIndex < mesh.subMeshCount; subIndex++)
        {
            instances.Add(new CombineInstance
            {
                mesh = mesh,
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

    public static float GetFloorHeight(BuildingSummary buildingData)
    {
        // MasterDataStoreから1階あたりの高さを取得
        float floorHeight = LocalConstants.FLOOR_HEIGHT;
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
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

        if (ModelManager.Instance != null)
        {
            return ModelManager.Instance.GetModel(path, suppressWarning);
        }
        return null;
    }

    /// <summary>
    /// 塔屋や屋上マテリアルなど、位置番号を持たないパーツを取得
    /// パス: Models/Buildings/{prefix}/{suffix}/{partName}
    /// </summary>
    public static GameObject GetPartNoPosition(string prefix, string partName, int suffix, bool suppressWarning = false)
    {
        string path = $"Models/Buildings/{prefix}/{suffix}/{partName}";

        if (ModelManager.Instance != null)
        {
            return ModelManager.Instance.GetModel(path, suppressWarning);
        }
        return null;
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
    /// 多角形の符号付き面積を計算
    /// </summary>
    public static float SignedArea(Vector2[] points)
    {
        float area = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            int j = (i + 1) % points.Length;
            area += points[i].x * points[j].y - points[j].x * points[i].y;
        }
        return area * 0.5f;
    }

    public static Vector2[] GetFootprint(BuildingSummary buildingData)
    {
        // 底面の座標をVector2配列へ格納
        Vector2[] footprint = new Vector2[buildingData.polygonArray.Length];
        for (int i = 0; i < buildingData.polygonArray.Length; i++)
        {
            footprint[i] = new Vector2(buildingData.polygonArray[i][0], buildingData.polygonArray[i][1]);
        }

        // 頂点の並びをCCW(反時計回り)に正規化
        if (BuildingMeshUtils.SignedArea(footprint) < 0f)
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

    private static string DeterminePrefix(BuildingSummary buildingData)
    {
        string variantPrefix = $"a{buildingData.variant}";
        if (HasVariantDefinition(buildingData.variant))
        {
            return variantPrefix;
        }

        int bdType = ResolveBdTypeFallback(buildingData.variant, buildingData.bd_type ?? 0);
        return $"b{bdType}";
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
                cy = HeightmapTextureManager.GetTerrainHeight(new Vector3(cx, 1000f, cz));
            }
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        if (parcel != null)
        {
            float[][] poly = parcel.GetPolygonArray();
            if (poly != null && poly.Length >= 3)
            {
                parcelFootprint = new Vector2[poly.Length];
                for (int i = 0; i < poly.Length; i++)
                {
                    parcelFootprint[i] = new Vector2(poly[i][0] - cx, poly[i][1] - cz);
                }
                if (SignedArea(parcelFootprint) < 0f)
                {
                    System.Array.Reverse(parcelFootprint);
                }
            }
        }

        return parcelFootprint;
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
        return ModelManager.Instance != null ? ModelManager.Instance.GetModel(commonPath, true) : null;
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