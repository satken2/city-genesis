// Assets/Scripts/MeshGeneration/Generators/CarRoadMeshGenerator.cs
using UnityEngine;
using System.Collections.Generic;
using Unity.Collections;
using GeneratedModels;

public static class BuildingMeshUtils
{
    /// <summary>
    /// Initialize required caches (materials, etc.).
    /// </summary>
    public static void InitializeCache()
    {
    }

    public static bool HasModelAtPath(string resourcePath)
    {
        return ModelManager.Instance != null && ModelManager.Instance.HasModel(resourcePath);
    }

    // ================================================================
    // Rendering LOD (close-up part resolution).
    // ================================================================

    /// <summary>Suffix for close-up part names (e.g. Models/Buildings/a2000/0/mid-w1_close).</summary>
    public const string CLOSE_SUFFIX = "_close";

    /// <summary>
    /// Rendering LOD of the building currently being generated. BuildingMeshBuilder.Generate sets/restores it around generation.
    /// For close-up (Close), part lookup first tries `{partName}_close` in the same model folder, then the standard part.
    /// Generation is synchronous on the main thread; applying this at the entry point avoids passing an argument through
    /// the many part-lookup calls. This does not refer to the cell tier table.
    /// </summary>
    public static DrawLod CurrentDrawLod { get; set; } = DrawLod.Normal;

    /// <summary>Resolve a resource path for the rendering LOD (prefer `_close` for close-up).</summary>
    private static GameObject GetModelForDrawLod(string path, bool suppressWarning)
    {
        if (ModelManager.Instance == null) return null;
        if (CurrentDrawLod == DrawLod.Close)
        {
            GameObject close = ModelManager.Instance.GetModel(path + CLOSE_SUFFIX, true);
            if (close != null) return close;
        }
        return ModelManager.Instance.GetModel(path, suppressWarning);
    }


    /// <summary>
    /// Combine all meshes under a prefab into one mesh transformed to prefab-root local space.
    /// Includes all parts even when an FBX contains multiple objects, applying each node's Transform
    /// (position, rotation, scale) to its vertices. Preserve source-renderer submeshes;
    /// return their corresponding shared materials in the same order in materials.
    /// Bake SkinnedMeshRenderer in its bind pose (without bone deformation).
    /// Vertices occupy contiguous ranges per submesh, each containing only vertices referenced by that submesh
    /// (<see cref="CompactSubMeshVertices"/>). Recombining the result by submesh therefore does not duplicate vertices.
    /// Return null if no mesh is found. The caller must destroy the returned mesh.
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

        // Matrix transforming into prefab-root local coordinates.
        // Prefab asset roots are normally identity, but also support a Transform on the root itself.
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

        // SkinnedMeshRenderer cannot be baked directly on a prefab asset; create a temporary instance and bake it.
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
    /// Internal operation for CombineModuleMeshes. Register every submesh as a CombineInstance,
    /// adding corresponding materials in sync (use the last entry if short, or null if none).
    /// Add temporary meshes with repacked vertices to temporaryMeshes (destroyed after combining).
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
    /// Return a temporary mesh with only referenced vertices packed into contiguous ranges per submesh.
    /// Return the original mesh if no submesh has unreferenced vertices within its index range.
    ///
    /// Even for a submesh CombineInstance, Mesh.CombineMeshes copies all vertices between that submesh's
    /// minimum and maximum index. FBX import welds vertices with identical position, normal, and UV across materials
    /// (with actual-size cube-projection UVs, coplanar faces of different materials can share identical vertices),
    /// so later submesh ranges extend into earlier ranges, duplicating unreferenced vertices between them.
    /// Preserve original vertex order within each range; duplicate shared vertices for each submesh.
    /// Both match the ordering and duplication without repacking, so the combined result differs only by having no unreferenced vertices.
    /// Copy vertex streams as raw bytes, preserving the source mesh's attribute types and formats.
    /// </summary>
    private static Mesh CompactSubMeshVertices(Mesh mesh)
    {
        int subMeshCount = mesh.subMeshCount;
        int vertexCount = mesh.vertexCount;
        if (!mesh.isReadable || subMeshCount == 0 || vertexCount == 0)
        {
            return mesh;
        }

        // mark[v] == s + 1 means vertex v has already been counted while scanning submesh s.
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
                    // Copy bytes assuming identical attribute descriptors imply identical layouts. Skip repacking if that assumption fails.
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

                uint[] packedIndices = new uint[packedIndexCount];
                var descriptors = new UnityEngine.Rendering.SubMeshDescriptor[subMeshCount];
                int[] remap = new int[vertexCount];
                int nextVertex = 0;
                int nextIndex = 0;
                for (int s = 0; s < subMeshCount; s++)
                {
                    // Remark this submesh's referenced vertices with a negative mark distinct from other submeshes, then scan.
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

                packed.SetIndexBufferParams(packedIndexCount, UnityEngine.Rendering.IndexFormat.UInt32);
                packed.GetIndexData<uint>().CopyFrom(packedIndices);
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

    /// <summary>Copy count consecutive vertices from sourceVertex to targetVertex onward across all streams.</summary>
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
    /// Get sharedMaterials assigned to the module.
    /// </summary>
    /// <remarks>
    /// Return sharedMaterials directly so multiple buildings share material assets for performance.
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
    /// Return height per floor (m). Resolve in the same order as part prefixes: landmark → variant;
    /// landmarks (landmark > 0) prefer the floor height in landmark_master.
    /// The backend also determines building height from the landmark row's floor height, so consistency is required
    /// to align 3D height, culling radius, and flame-effect height with the data.
    /// </summary>
    public static float GetFloorHeight(BuildingSummary buildingData) => GetFloorHeight(buildingData.variant, buildingData.landmark);

    public static float GetFloorHeight(int variant, int landmark)
    {
        float floorHeight = LocalConstants.FLOOR_HEIGHT;
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized)
        {
            if (landmark > 0)
            {
                var landmarkMaster = MasterDataStore.Instance.GetLandmarkMaster(landmark);
                if (landmarkMaster != null && landmarkMaster.height_per_floor.HasValue)
                {
                    return landmarkMaster.height_per_floor.Value;
                }
            }

            var agentMaster = MasterDataStore.Instance.GetAgentMaster(variant);
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
    /// Select a roof material.
    /// Get it from the ceiling part in the specified prefix/model-number folder;
    /// if absent, use a valid material from the fallback array;
    /// if none exists, use the system default material.
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
    /// Select a ground-panel material.
    /// Get it from the floor part in the specified prefix/model-number folder;
    /// if absent, use a valid material from the fallback array;
    /// if none exists, use the system default material.
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
    /// Select a plain-wall material without windows or openings. Used to give procedurally generated walls,
    /// such as gable ends, the same material as regular walls.
    /// The `generic-wall` part supplies only a material reference (its geometry is unused).
    /// If absent, use a valid material from the fallback array (the body's wall-part material,
    /// a visual compromise because it includes window designs), or the default material if none exists.
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
    /// Select the first non-null material in the fallback material array.
    /// Return null if all entries are null or the array is empty.
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
    /// Get a module from the 25-part system.
    /// Path: Models/Buildings/{prefix}/{suffix}/{level}-{type}{position}
    /// Example: Models/Buildings/b16/500/mid-w1
    /// </summary>
    public static GameObject GetPart(string prefix, string level, string type, int position, int suffix, bool suppressWarning = false)
    {
        string posStr = position > 0 ? position.ToString() : "";
        string partName = $"{level}-{type}{posStr}";
        string path = $"Models/Buildings/{prefix}/{suffix}/{partName}";

        return GetModelForDrawLod(path, suppressWarning);
    }

    /// <summary>
    /// Get a part without a position number, such as a rooftop prop or roof material.
    /// Path: Models/Buildings/{prefix}/{suffix}/{partName}
    /// </summary>
    public static GameObject GetPartNoPosition(string prefix, string partName, int suffix, bool suppressWarning = false)
    {
        return GetModelForDrawLod(PartPath(prefix, partName, suffix), suppressWarning);
    }

    /// <summary>Resource path of a part without a position number, before the render-LOD suffix.</summary>
    public static string PartPath(string prefix, string partName, int suffix) => $"Models/Buildings/{prefix}/{suffix}/{partName}";

    /// <summary>
    /// Select one exterior model number (numbered folder under the prefix folder) using visual_variant (0–1) and prefix-folder weights
    /// (ModelWeights). Return -1 if no model exists. Look up parts in the highest folder at or below this number that contains them;
    /// use the same number for parts shared by building type (b{bd_type}).
    /// </summary>
    public static int ResolveModelId(string prefix, float visualVariant)
        => ModelWeights.PickByFraction(BUILDINGS_ROOT + prefix, visualVariant);

    /// <summary>
    /// Check whether a part with a position number exists.
    /// Path: Models/Buildings/{prefix}/{suffix}/{level}-{type}{position}
    /// </summary>
    public static bool HasPart(string prefix, string level, string type, int position, int suffix)
    {
        string posStr = position > 0 ? position.ToString() : "";
        string partName = $"{level}-{type}{posStr}";
        string path = $"Models/Buildings/{prefix}/{suffix}/{partName}";
        return HasModelAtPath(path);
    }


    /// <summary>
    /// Offset a CCW polygon outward by a constant distance. Move each vertex along the bisector
    /// of its adjacent edges' outward normals, scaling by 1/cos so acute corners do not become too narrow.
    /// Shared by roof builders to create the outline of eaves and verge overhangs.
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
    /// Find the minimum-area bounding rectangle (rotating calipers) and return its long and short directions.
    /// Gable roofs use the long direction for the ridge; shed roofs use the short direction for the slope.
    /// </summary>
    /// <param name="polygon">Target polygon.</param>
    /// <param name="center">Center of the bounding rectangle.</param>
    /// <param name="longDir">Long direction (normalized).</param>
    /// <param name="shortDir">Short direction (perpendicular to longDir, normalized).</param>
    /// <param name="halfLength">Half-length along the long direction.</param>
    /// <param name="halfWidth">Half-width along the short direction.</param>
    /// <returns>False for a degenerate polygon whose directions cannot be determined.</returns>
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
        // Store footprint coordinates in a Vector2 array.
        Vector2[] footprint = new Vector2[buildingData.polygonArray.Length];
        for (int i = 0; i < buildingData.polygonArray.Length; i++)
        {
            footprint[i] = new Vector2(buildingData.polygonArray[i][0], buildingData.polygonArray[i][1]);
        }
        return NormalizeFootprint(footprint);
    }

    /// <summary>Remove degenerate vertices from the center-relative footprint vertices and normalize to counterclockwise winding. May modify the supplied array.</summary>
    public static Vector2[] NormalizeFootprint(Vector2[] footprint)
    {
        // For buildings using the full parcel (such as farmland), the backend uses the parcel polygon directly as the footprint.
        // Building polygons can therefore contain degenerate parcel vertices (collinear or duplicate). Apply the same simplification as parcels
        // to keep wall/corner placement valid. Use raw data if simplification leaves fewer than three vertices.
        Vector2[] sanitized = SanitizeRingPolygon(footprint);
        if (sanitized != null)
        {
            footprint = sanitized;
        }

        // Normalize vertex order to CCW (counterclockwise).
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
    /// Determine the part-folder prefix.
    /// Resolution order: landmark-{model_prefix} → a{variant} → a{model_source_variant} → b{bd_type}. Search the asset pack in this order first;
    /// search standard assets only if none exist in the pack (even if the pack has only b{bd_type}, do not mix with standard a{variant}).
    /// If the chosen prefix folder belongs to the pack, read its parts exclusively from the pack (AssetPackStore).
    ///
    /// Landmarks (landmark > 0) come first because each is unique while all share
    /// one usage variant (landmark), so a{variant} cannot distinguish them.
    /// </summary>
    private static string DeterminePrefix(BuildingSummary buildingData)
    {
        string landmarkPrefix = LandmarkFolder(buildingData.landmark);
        string variantPrefix = buildingData.variant > 0 ? $"a{buildingData.variant}" : null;
        int sourceVariant = 0;
        if (MasterDataStore.Instance != null && MasterDataStore.Instance.IsInitialized &&
            MasterDataStore.Instance.GetAgentMasterDictionary().TryGetValue(buildingData.variant, out var agentMaster))
        {
            sourceVariant = agentMaster.model_source_variant ?? 0;
        }
        string sourcePrefix = sourceVariant > 0 ? $"a{sourceVariant}" : null;
        string bdTypePrefix = $"b{ResolveBdTypeFallback(buildingData.variant, buildingData.bd_type ?? 0)}";
        foreach (string prefix in new[] { landmarkPrefix, variantPrefix, sourcePrefix, bdTypePrefix })
        {
            if (prefix != null && AssetPackStore.HasUnit(BUILDINGS_ROOT + prefix)) return prefix;
        }

        if (landmarkPrefix != null && AgentModelConfig.HasLandmarkPrefix(landmarkPrefix))
        {
            return landmarkPrefix;
        }
        if (HasVariantDefinition(buildingData.variant))
        {
            return variantPrefix;
        }
        if (HasVariantDefinition(sourceVariant))
        {
            return sourcePrefix;
        }
        return bdTypePrefix;
    }

    /// <summary>
    /// Return the landmark part-folder name (landmark-{model_prefix}). Return null for non-landmarks or
    /// when model_prefix cannot be resolved from master data. Callers check folder existence in each search location
    /// (landmark_prefixes in agent_model_config.yaml for standard assets; the pack's list for packs).
    /// </summary>
    private static string LandmarkFolder(int landmark)
    {
        if (landmark <= 0 || MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized)
        {
            return null;
        }

        string modelPrefix = MasterDataStore.Instance.GetLandmarkModelPrefix(landmark);
        return string.IsNullOrEmpty(modelPrefix) ? null : $"landmark-{modelPrefix}";
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
    /// Resolve the building's parcel polygon and placement reference point (centerPointWorld).
    /// Shared site-context lookup for ModularBuildingMeshBuilder / PreBuiltBuildingMeshBuilder / ConstructingBuildingMeshBuilder,
    /// using the same context across builders.
    ///
    /// - Return a parcel polygon normalized to CCW, using local XZ coordinates relative to the building's center_point
    ///   (same convention as the building footprint). Return null if the parcel is unavailable.
    ///   ParcelManager polygons use world coordinates, but mesh vertices are assembled in building GameObject
    ///   local space (placed at center_point), so convert here.
    /// - centerPointWorld Y is terrain elevation at the building center. Terrain-following site decorations, foundations, and dirt mounds
    ///   determine local Y relative to it. Sea-parcel building GameObjects are placed at sea level (parcel.altitude),
    ///   but using sea level here would sink terrain-following vertices to the seabed; always retain terrain elevation.
    /// </summary>
    public static Vector2[] ResolveParcelContext(BuildingSummary buildingData,
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
            centerPointWorld = new Vector3(cx, Utils.GetGroundHeight(new Vector3(cx, 1000f, cz)), cz);
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

    /// <summary>Edge length (m) considered degenerate. Remove vertices closer than this as coincident points.</summary>
    private const float RING_MIN_EDGE_LENGTH = 0.01f;

    /// <summary>Direction-change angle (degrees) considered nearly straight. Remove intermediate vertices below this as collinear.</summary>
    private const float RING_COLLINEAR_ANGLE_DEG = 5f;

    /// <summary>
    /// Normalize polygon rings (building footprints/parcels) for mesh generation. Backend polygons follow
    /// the closed-ring contract (B_土地形状.md) and may contain shapely's closing duplicate (last=first),
    /// or nearly collinear intermediate vertices introduced by road cutouts and parcel shaping
    /// (buildings covering entire parcels inherit the same degeneracy because footprint=parcel polygon).
    /// Using these directly for wall/corner placement (one corner part per vertex) makes interior angles undefined
    /// on zero-length edges, folding corners into closed X shapes; near 180°, corner parts spread out
    /// and cross the wall line. Remove (1) vertices on degenerate edges (including the closing duplicate), and (2) collinear vertices
    /// whose direction change is below the threshold. Return null if fewer than three remain (callers fall back).
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
        // Degenerate edge at the ring seam (last→first). Removes shapely's closing duplicate here.
        while (dedup.Count >= 2 && (dedup[0] - dedup[dedup.Count - 1]).magnitude < RING_MIN_EDGE_LENGTH)
        {
            dedup.RemoveAt(dedup.Count - 1);
        }
        if (dedup.Count < 3) return null;

        // Determine collinearity from the difference between the direction from the last retained vertex and the next edge's direction.
        // Checking each vertex's direction change alone would remove all vertices on gentle curves (each below the threshold,
        // but with substantial cumulative curvature), flattening arcs into chords. Retain a vertex once cumulative change
        // exceeds the threshold. Start scanning at the vertex with the largest direction change
        // (a definite corner) so scanning cannot start in the middle of a curve.
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

        // The anchor only determines where scanning starts; return vertices in their original order.
        // This preserves the vertex-0/front-edge convention (entrance/rooftop-prop orientation and wall/corner position numbers).
        keptIndices.Sort();
        Vector2[] result = new Vector2[keptIndices.Count];
        for (int i = 0; i < keptIndices.Count; i++)
        {
            result[i] = dedup[keptIndices[i]];
        }
        return result;
    }

    /// <summary>
    /// Resolve parts without position numbers in variant/building-type → standard (common) order.
    /// Used for construction parts with shared fallbacks, such as
    /// scaffold-wall / scaffold-corner / crane-base / crane-mid / crane-top.
    ///
    /// Resolution order:
    /// 1. Models/Buildings/{prefix}/{suffix}/{partName}
    ///    (suffix is the largest ModelIds model number at or below the selected model number containing the part)
    /// 2. Models/Buildings/common/{partName} (standard parts; common has no exterior models,
    ///     so parts sit directly here without a suffix folder)
    /// 3. Null if neither exists (callers skip the part).
    /// </summary>
    public static GameObject GetPartWithCommonFallback(string prefix, string partName, float visualVariant)
    {
        GameObject part = GetPartBySuffixScan(prefix, partName, ResolveModelId(prefix, visualVariant));
        if (part != null)
        {
            return part;
        }
        return GetCommonPart(partName);
    }

    /// <summary>
    /// Resolve partName by scanning suffixes under the given prefix (no common fallback).
    /// suffix is the largest ModelIds model number at or below modelId containing the part.
    /// Return null if absent. Also used for mashup parts (independent scan per part).
    /// </summary>
    public static GameObject GetPartBySuffixScan(string prefix, string partName, int modelId)
    {
        int bestSuffix = FindPartSuffix(prefix, partName, modelId);
        return bestSuffix >= 0 ? GetPartNoPosition(prefix, partName, bestSuffix, true) : null;
    }

    /// <summary>
    /// Folder selected by <see cref="GetPartBySuffixScan"/>. -1 if absent.
    /// Check existence using standard names, so the value is identical across rendering LODs.
    /// </summary>
    public static int FindPartSuffix(string prefix, string partName, int modelId)
    {
        int bestSuffix = -1;
        foreach (int suffix in ModelIds(prefix))
        {
            if (suffix > modelId) continue;
            if (HasModelAtPath($"Models/Buildings/{prefix}/{suffix}/{partName}") && suffix > bestSuffix)
            {
                bestSuffix = suffix;
            }
        }
        return bestSuffix;
    }

    private const string BUILDINGS_ROOT = ModelWeights.BUILDING_ROOT;

    /// <summary>Model numbers under the prefix folder (ascending). The bundled ModelWeights table holds the list.</summary>
    public static IReadOnlyList<int> ModelIds(string prefix) => ModelWeights.Ids(BUILDINGS_ROOT + prefix);

    /// <summary>
    /// Get a standard part (common). As common has no exterior models, place parts directly
    /// at Models/Buildings/common/{partName} without a suffix folder.
    /// </summary>
    private static GameObject GetCommonPart(string partName)
    {
        string commonPath = $"Models/Buildings/{LocalConstants.CONSTRUCTION_COMMON_PREFIX}/{partName}";
        return GetModelForDrawLod(commonPath, true);
    }

    /// <summary>
    /// Resolve the ground-panel (floor) material with a{variant} → b{bd_type} → common fallbacks.
    /// Called only for buildings with draw_floor=yes. Return null if no floor part exists anywhere
    /// (no default-building-material fallback, avoiding unwanted slabs).
    /// Skip the b stage if aPrefix and bPrefix are identical (both b{bd_type} because a{variant} is undefined).
    /// </summary>
    public static Material SelectFloorMaterialWithFallback(string aPrefix, string bPrefix, float visualVariant)
    {
        int modelId = ResolveModelId(aPrefix, visualVariant);

        GameObject part = GetPartBySuffixScan(aPrefix, "floor", modelId);
        if (part == null && !string.IsNullOrEmpty(bPrefix) && bPrefix != aPrefix)
        {
            part = GetPartBySuffixScan(bPrefix, "floor", modelId);
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
    /// Resolve the ground-panel (floor) material according to master draw_floor.
    /// Only buildings with draw_floor=yes draw ground panels; return null for unset (null)/false,
    /// uninitialized master data, or an unregistered variant (= do not draw).
    /// Resolve floor parts via a{variant} (=prefix) → b{bd_type} → common;
    /// return null if none exists (no fallback to the default building material).
    /// prefix is the building prefix already resolved by the caller (a{variant} or b{bd_type}).
    /// Shared by modular and prebuilt buildings.
    /// ownPartOnly uses only prefix's floor part, without b{bd_type}/common fallbacks
    /// (for depots, preventing shared pavement beneath tracks).
    /// </summary>
    public static Material ResolveFloorMaterialFromMaster(BuildingSummary buildingData, string prefix, bool ownPartOnly = false)
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

        if (ownPartOnly)
        {
            GameObject part = GetPartBySuffixScan(prefix, "floor", ResolveModelId(prefix, buildingData.visual_variant));
            Material[] materials = part != null ? GetModuleMaterials(part) : null;
            return (materials != null && materials.Length > 0) ? materials[0] : null;
        }

        int bdType = agentMaster.bd_type ?? buildingData.bd_type ?? 0;
        return SelectFloorMaterialWithFallback(prefix, $"b{bdType}", buildingData.visual_variant);
    }

    /// <summary>Minimum module height for Y-scale calculations. Prevents division by zero and extreme scaling.</summary>
    private const float MIN_STACKED_MODULE_HEIGHT = 0.01f;

    /// <summary>
    /// Place a prefab module at the given Y and add it to the CombineInstance list.
    /// Shared by prebuilt building stacks (base/mid/top-prebuilt) and cranes (crane-base/mid/top).
    ///
    /// - If fitHeight is specified, scale only Y to that height (XZ remain actual size).
    ///   With null, apply no scaling. Prebuilt buildings fit only mid to floor height (base/top use null);
    ///   mashup sub-buildings use null throughout (actual size); construction cranes fit base/mid to floor height.
    /// - alignByOrigin chooses the vertical reference:
    ///   - false (default, prebuilt): align the mesh bottom (bounds.min.y) to baseY
    ///     to stack independently of the FBX pivot. Fit full mesh height (bounds.size.y) to fitHeight.
    ///   - true (cranes, etc.): assume model origin (y=0) is the connection point (bottom) and place it at baseY.
    ///     Fit the height above the origin (bounds.max.y) to fitHeight. Even parts projecting below
    ///     the origin connect precisely without gaps between levels.
    /// - Translate the XZ model origin (pivot) by xzOffset.
    ///   Apply rotation about Y around the origin to vertices.
    /// </summary>
    /// <returns>Height consumed by the placed module (m, added to the next level's baseY). Zero on placement failure.</returns>
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
        // Reference height for fitHeight: height above origin for alignByOrigin=true; full mesh height otherwise.
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
            // alignByOrigin=true scales about origin (y=0); false aligns the mesh bottom to baseY.
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
