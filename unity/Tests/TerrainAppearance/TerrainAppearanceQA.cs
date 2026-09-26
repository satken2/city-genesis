using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class TerrainAppearanceQA
{
    public static void Run()
    {
        string root = Path.GetDirectoryName(Application.dataPath);
        try
        {
            GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.gray;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50, 25, 0);
            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 220, 0);
            camera.transform.rotation = Quaternion.Euler(90, 0, 0);
            camera.orthographic = true;
            camera.orthographicSize = 90;
            camera.farClipPlane = 1000;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.magenta;
            camera.targetTexture = new RenderTexture(256, 256, 24);

            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(200, 100, 200) };
            var layer = new TerrainLayer { diffuseTexture = Texture2D.whiteTexture, tileSize = Vector2.one, smoothness = .15f };
            data.terrainLayers = new[] { layer };
            var terrainGO = Terrain.CreateTerrainGameObject(data);
            terrainGO.transform.position = new Vector3(-100, 20, -100);
            var terrain = terrainGO.GetComponent<Terrain>();
            terrain.basemapDistance = 10000;
            var near = new Material(Shader.Find("Custom/DepthOffsetTerrainLit"));
            terrain.materialTemplate = near;
            var farGO = GameObject.CreatePrimitive(PrimitiveType.Plane);
            farGO.transform.position = new Vector3(0, 20, 0);
            farGO.transform.localScale = new Vector3(20, 1, 20);
            var far = new Material(Shader.Find("Custom/DepthOffsetSimpleLitFar"));
            farGO.GetComponent<Renderer>().sharedMaterial = far;
            far.SetFloat("_Smoothness", .15f);
            foreach (var material in new[] { near, far })
            {
                material.SetFloat("_ProceduralGroundBlend", .85f);
                material.SetFloat("_ProceduralGroundScale", 40);
                material.SetColor("_TerrainGroundColorA", new Color(.38f, .47f, .26f));
                material.SetColor("_TerrainGroundColorB", new Color(.55f, .48f, .34f));
                material.SetColor("_TerrainGroundColorC", new Color(.62f, .66f, .45f));
                material.SetFloat("_TerrainDetailNormalStrength", .25f);
                material.SetFloat("_TerrainDetailNormalScale", 1 / 3.5f);
            }
            var report = "";
            foreach (bool fog in new[] { false, true })
            {
                RenderSettings.fog = fog;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = .002f;
                RenderSettings.fogColor = new Color(.6f, .5f, .4f);
                farGO.SetActive(false); terrainGO.SetActive(true);
                var a = Capture(camera, root + (fog ? "/near-fog.png" : "/near.png"));
                farGO.SetActive(true); terrainGO.SetActive(false);
                var b = Capture(camera, root + (fog ? "/far-fog.png" : "/far.png"));
                double error = a.Zip(b, (x, y) => (Math.Abs(x.r - y.r) + Math.Abs(x.g - y.g) + Math.Abs(x.b - y.b)) / 3.0).Average();
                report += $"fog={fog}: mean RGB error={error:F3}/255\n";
                if (error > 3) throw new Exception(report);
                if (b.All(x => x.r > 240 && x.b > 240 && x.g < 10)) throw new Exception("Error shader rendered");
            }
            report += CheckGroundCovers(camera, terrain, farGO, near, far, layer, root);
            report += CheckForest(camera, terrainGO, farGO, root);
            report += CheckContours(camera, terrainGO, farGO, root);
            report += CheckLoadedBoundary(camera, terrainGO, farGO, far, root);
            foreach (var shader in new[] { near.shader, far.shader })
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    if (message.severity.ToString() == "Error") throw new Exception(shader.name + ": " + message.message);
            File.WriteAllText(root + "/result.txt", "PASS\n" + report);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(root + "/result.txt", "FAIL " + e);
            EditorApplication.Exit(1);
        }
    }

    /// <summary>
    /// 地表種別5種を本編と同じ GroundCoverProfile.ApplyTo で描き、Terrain と遠景メッシュの平均色の一致、
    /// 種別どうしの見分け、テクスチャの欠落を検査する。起伏と海岸を付けた近景の比較画像
    /// ground-cover-sheet.png(左から GROUND_COVER_TYPES の順)も出力する。
    /// 遠景メッシュは Plane の UV で貼るため模様の位置は Terrain と揃わず、平均色だけを比べる。
    /// マテリアルは複製して使い、終了時に元のマテリアル・レイヤー・地形へ戻す。
    /// </summary>
    static string CheckGroundCovers(Camera camera, Terrain terrain, GameObject farGO, Material near, Material far, TerrainLayer layer, string root)
    {
        RenderSettings.fog = false;
        var farRenderer = farGO.GetComponent<Renderer>();
        var nearGround = new Material(near);
        var farGround = new Material(far);
        terrain.materialTemplate = nearGround;
        farRenderer.sharedMaterial = farGround;
        var baseTexture = layer.diffuseTexture;
        var baseTileSize = layer.tileSize;
        float baseSmoothness = layer.smoothness;
        var palette = Resources.Load<GroundCoverPalette>(GroundCoverPalette.ResourcePath);
        var report = palette != null ? "ground cover: palette asset\n" : "ground cover: code defaults (palette asset missing)\n";
        var means = new System.Collections.Generic.List<Vector3>();
        var sheet = new Texture2D(256 * Constants.GROUND_COVER_TYPES.Length, 256, TextureFormat.RGB24, false);
        for (int i = 0; i < Constants.GROUND_COVER_TYPES.Length; i++)
        {
            int type = Constants.GROUND_COVER_TYPES[i];
            GroundCoverProfile profile = (palette != null ? palette.Find(type) : null) ?? GroundCoverProfile.CreateDefault(type);
            var texture = string.IsNullOrEmpty(profile.textureResource) ? null : Resources.Load<Texture2D>(profile.textureResource);
            if (!string.IsNullOrEmpty(profile.textureResource) && texture == null)
                throw new Exception("Ground texture missing: Resources/" + profile.textureResource);
            layer.diffuseTexture = texture != null ? texture : Texture2D.whiteTexture;
            layer.tileSize = profile.textureTileSize;
            layer.smoothness = profile.smoothness;
            farGround.SetTexture("_BaseMap", layer.diffuseTexture);
            farGround.SetVector("_BaseMap_ST", new Vector4(1 / profile.textureTileSize.x, 1 / profile.textureTileSize.y, 0, 0));
            farGround.SetFloat("_Smoothness", profile.smoothness);
            profile.ApplyTo(nearGround);
            profile.ApplyTo(farGround);

            farGO.SetActive(false); terrain.gameObject.SetActive(true);
            var a = Capture(camera, root + $"/ground-{type}-near.png");
            farGO.SetActive(true); terrain.gameObject.SetActive(false);
            var b = Capture(camera, root + $"/ground-{type}-far.png");
            if (b.All(x => x.r > 240 && x.b > 240 && x.g < 10)) throw new Exception("Error shader rendered for ground cover " + type);
            Vector3 meanA = Mean(a), meanB = Mean(b);
            float difference = Mathf.Max(Mathf.Abs(meanA.x - meanB.x), Mathf.Max(Mathf.Abs(meanA.y - meanB.y), Mathf.Abs(meanA.z - meanB.z)));
            report += $"ground cover {type}: mean RGB={meanA.x:F0},{meanA.y:F0},{meanA.z:F0}; Terrain/UltraFar max channel difference={difference:F1}/255\n";
            if (difference > 6) throw new Exception(report);
            means.Add(meanA);

            farGO.SetActive(false); terrain.gameObject.SetActive(true);
            sheet.SetPixels32(256 * i, 0, 256, 256, CaptureRelief(camera, terrain, root + $"/ground-{type}-relief.png"));
        }
        for (int i = 0; i < means.Count; i++)
            for (int j = i + 1; j < means.Count; j++)
                if (Vector3.Distance(means[i], means[j]) < 12)
                    throw new Exception(report + $"Ground covers {Constants.GROUND_COVER_TYPES[i]} and {Constants.GROUND_COVER_TYPES[j]} look alike");
        sheet.Apply();
        File.WriteAllBytes(root + "/ground-cover-sheet.png", sheet.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(sheet);

        layer.diffuseTexture = baseTexture;
        layer.tileSize = baseTileSize;
        layer.smoothness = baseSmoothness;
        terrain.materialTemplate = near;
        farRenderer.sharedMaterial = far;
        UnityEngine.Object.DestroyImmediate(nearGround);
        UnityEngine.Object.DestroyImmediate(farGround);
        return report;
    }

    /// <summary>
    /// 海(標高0m未満)から急斜面の丘へ上がる起伏を付けて近景を撮り、元の平らな地形へ戻す。
    /// 波打ち際の砂浜・崖の岩・標高による色補正を目視するための画像で、合否判定には使わない。
    /// </summary>
    static Color32[] CaptureRelief(Camera camera, Terrain terrain, string path)
    {
        var data = terrain.terrainData;
        var position = terrain.transform.position;
        var heights = new float[33, 33];
        for (int z = 0; z < 33; z++)
            for (int x = 0; x < 33; x++)
            {
                float shore = x / 32f * .18f;
                float hill = Mathf.Clamp01(1 - Vector2.Distance(new Vector2(x, z), new Vector2(24, 16)) / 7f) * .45f;
                heights[z, x] = shore + hill;
            }
        data.SetHeights(0, 0, heights);
        terrain.transform.position = new Vector3(position.x, -6, position.z);
        var pixels = Capture(camera, path);
        data.SetHeights(0, 0, new float[33, 33]);
        terrain.transform.position = position;
        return pixels;
    }

    static Vector3 Mean(Color32[] pixels)
    {
        return new Vector3(
            (float)pixels.Average(p => (double)p.r),
            (float)pixels.Average(p => (double)p.g),
            (float)pixels.Average(p => (double)p.b));
    }

    static string CheckForest(Camera camera, GameObject terrainGO, GameObject farGO, string root)
    {
        RenderSettings.fog = false;
        Shader.SetGlobalTexture("_ForestCoverage",Texture2D.whiteTexture);
        Shader.SetGlobalVector("_ForestCoverageRect",new Vector4(-10000,-10000,1/20000f,1/20000f));
        Shader.SetGlobalVector("_ForestDetailRect",new Vector4(-10000,-10000,1/20000f,1/20000f));
        Shader.SetGlobalVector("_ForestPineColor",new Vector4(.10f,.17f,.045f,1));
        Shader.SetGlobalVector("_ForestLeafColor",new Vector4(.12f,.20f,.055f,1));
        Shader.SetGlobalFloat("_ForestOverviewEnabled",1);
        Shader.SetGlobalTexture("_ForestDetail",Texture2D.blackTexture);
        farGO.SetActive(false); terrainGO.SetActive(true);
        var a=Capture(camera,root+"/forest-near.png");
        farGO.SetActive(true); terrainGO.SetActive(false);
        var b=Capture(camera,root+"/forest-far.png");
        double error=a.Zip(b,(x,y)=>(Math.Abs(x.r-y.r)+Math.Abs(x.g-y.g)+Math.Abs(x.b-y.b))/3.0).Average();
        // 地形とメッシュの微小な投影差で樹冠の縁はずれるため、色の平均も独立に比較する。
        double meanDifference=Math.Abs(a.Average(p=>(double)p.g)-b.Average(p=>(double)p.g));
        if(error>6 || meanDifference>2) throw new Exception("Forest Terrain/UltraFar mismatch: "+error+"; mean green="+meanDifference);
        Shader.SetGlobalTexture("_ForestDetail",Texture2D.whiteTexture);
        var ground=Capture(camera,root+"/forest-detail-ready.png");
        double change=b.Zip(ground,(x,y)=>Math.Abs(x.g-y.g)).Average();
        if(change<10) throw new Exception("Forest replacement had no visible effect: "+change);
        camera.farClipPlane=3000;
        var averages=new double[4];
        int index=0;
        foreach(float height in new[]{800f,950f,1120f,1520f})
        {
            camera.transform.position=new Vector3(0,height,0);
            var pixels=Capture(camera,root+$"/forest-transition-{index}.png");
            averages[index++]=pixels.Average(p=>(double)p.g);
        }
        if(Math.Abs(averages[0]-averages[3])<10 || averages[1]>averages[0]+2 || averages[2]>averages[1]+2)
            throw new Exception("Forest transition discontinuity: "+string.Join(",",averages));
        if(Math.Abs(averages[2]-averages[3])>2)
            throw new Exception("Forest backdrop changed while models fade: "+string.Join(",",averages));
        Shader.SetGlobalFloat("_ForestOverviewEnabled",0);
        camera.transform.position=new Vector3(0,220,0);
        return $"forest: Terrain/UltraFar mean error={error:F3}/255; replacement contrast={change:F1}; transition green={string.Join(",",averages.Select(x=>x.ToString("F1")))}\n";
    }

    static string CheckContours(Camera camera, GameObject terrainGO, GameObject farGO, string root)
    {
        RenderSettings.fog = false;
        farGO.SetActive(false);
        terrainGO.SetActive(true);
        var terrain = terrainGO.GetComponent<Terrain>();
        var heights = new float[33, 33];
        for (int z = 0; z < 33; z++)
            for (int x = 0; x < 33; x++) heights[z, x] = x / 32f * .4f;
        terrain.terrainData.SetHeights(0, 0, heights);
        camera.farClipPlane = 2000;
        var counts = new int[2];
        var contrasts = new double[2];
        for (int i = 0; i < 2; i++)
        {
            camera.transform.position = new Vector3(0, i == 0 ? 140 : 1000, 0);
            Shader.SetGlobalFloat("_ShowContour", 0);
            var off = Capture(camera, root + $"/contour-{i}-off.png");
            Shader.SetGlobalFloat("_ShowContour", 1);
            var on = Capture(camera, root + $"/contour-{i}-on.png");
            bool previous = false;
            // 同じ斜面・画面範囲で線の本数と暗さを比較し、距離による密度とコントラストを検証する。
            for (int x = 10; x < 246; x++)
            {
                int p = 128 * 256 + x;
                double darkening = (off[p].r + off[p].g + off[p].b - on[p].r - on[p].g - on[p].b) / 3.0;
                bool line = darkening > 12;
                if (line && !previous) counts[i]++;
                contrasts[i] = Math.Max(contrasts[i], darkening);
                previous = line;
            }
        }
        string report = $"contours: near/far lines={counts[0]}/{counts[1]}, peak contrast={contrasts[0]:F1}/{contrasts[1]:F1}\n";
        if (counts[1] < 1 || counts[0] < counts[1] * 3 || contrasts[0] <= contrasts[1]) throw new Exception(report);
        terrain.terrainData.SetHeights(0, 0, new float[33, 33]);
        camera.transform.position = new Vector3(0, 80, 0);
        var flatOn = Capture(camera, root + "/contour-flat-on.png");
        Shader.SetGlobalFloat("_ShowContour", 0);
        var flatOff = Capture(camera, root + "/contour-flat-off.png");
        if (!flatOn.SequenceEqual(flatOff)) throw new Exception("Contour lines tinted a flat plateau");
        return report;
    }

    static string CheckLoadedBoundary(Camera camera, GameObject terrainGO, GameObject farGO, Material far, string root)
    {
        RenderSettings.fog = false;
        terrainGO.transform.position = new Vector3(0, 20, 0);
        camera.transform.position = new Vector3(100, 400, 100);
        camera.orthographicSize = 250;
        var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
        sea.transform.position = new Vector3(100, 0, 100);
        sea.transform.localScale = Vector3.one * 100;
        var seaMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        seaMaterial.SetColor("_BaseColor", Color.blue);
        sea.GetComponent<Renderer>().sharedMaterial = seaMaterial;

        // セル幅200mに対して遠景格子80mとし、三角形がロード境界をまたぐ条件を再現する。
        const int side = 9;
        var vertices = new Vector3[side * side];
        var triangles = new int[(side - 1) * (side - 1) * 6];
        int t = 0;
        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                vertices[i] = new Vector3(-220 + 80 * x, 20, -220 + 80 * z);
                if (x == side - 1 || z == side - 1) continue;
                triangles[t++] = i; triangles[t++] = i + side; triangles[t++] = i + 1;
                triangles[t++] = i + 1; triangles[t++] = i + side; triangles[t++] = i + side + 1;
            }
        var mesh = new Mesh { vertices = vertices, triangles = triangles };
        mesh.RecalculateNormals();
        farGO.GetComponent<MeshFilter>().sharedMesh = mesh;
        farGO.transform.position = Vector3.zero;
        farGO.transform.localScale = Vector3.one;
        farGO.SetActive(true);
        var mask = new Texture2D(3, 3, TextureFormat.R8, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        far.SetTexture("_LoadedCellMask", mask);
        far.SetVector("_LoadedCellMaskParams", new Vector4(1 / 200f, 1, 1, 0));
        far.SetVector("_LoadedCellMaskTexel", new Vector4(1 / 3f, 1 / 3f, 0, 0));
        far.SetFloat("_FarGridStep", 80);
        far.SetFloat("_FarSinkAmount", 100);
        var report = "";
        foreach (int state in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var bytes = new byte[9];
            if (state != 1) bytes[state == 3 ? 5 : state == 5 ? 3 : 4] = 255;
            mask.SetPixelData(bytes, 0); mask.Apply();
            terrainGO.SetActive(state != 1 && state != 2);
            terrainGO.transform.position = new Vector3(state == 3 ? 200 : state == 5 ? -200 : 0, 20, 0);
            far.SetFloat("_FarGridStep", state == 4 ? 1 : 80);
            var pixels = Capture(camera, root + $"/boundary-{state}.png");
            int seaPixels = pixels.Count(c => c.b > 150 && c.r < 30 && c.g < 30);
            report += $"boundary state={state}: exposed sea pixels={seaPixels}\n";
            // 状態2は内部の沈み込み確認。状態4は余白を縮め、境界の裂け目を再現する対照。
            if (state == 2 || state == 4 ? seaPixels == 0 : seaPixels != 0) throw new Exception(report);
        }

        // 都市境界: 遠景は都市の中にしか面が無いので、全セルがロード済みなら境界から1格子の帯まで沈める。
        // 都市範囲を渡す本編の設定と、渡さない対照(境界の帯が沈まずに残る)を比べる。
        mask.SetPixelData(Enumerable.Repeat((byte)255, 9).ToArray(), 0); mask.Apply();
        terrainGO.SetActive(false);
        far.SetFloat("_FarGridStep", 80);
        foreach (bool cityRect in new[] { true, false })
        {
            far.SetVector("_FarCityRect", cityRect ? new Vector4(-200, -200, 400, 400) : Vector4.zero);
            var pixels = Capture(camera, root + $"/city-edge-{(cityRect ? "rect" : "none")}.png");
            int landPixels = pixels.Count(c => !(c.b > 150 && c.r < 30 && c.g < 30));
            report += $"city edge rect={cityRect}: unsunk far pixels={landPixels}\n";
            if (cityRect ? landPixels != 0 : landPixels == 0) throw new Exception(report);
        }
        far.SetVector("_FarCityRect", Vector4.zero);
        return report;
    }

    static Color32[] Capture(Camera camera, string path)
    {
        camera.Render();
        RenderTexture.active = camera.targetTexture;
        var texture = new Texture2D(256, 256, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        var pixels = texture.GetPixels32();
        UnityEngine.Object.DestroyImmediate(texture);
        RenderTexture.active = null;
        return pixels;
    }
}
