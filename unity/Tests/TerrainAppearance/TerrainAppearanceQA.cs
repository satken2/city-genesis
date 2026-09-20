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
            foreach (bool dark in new[] { false, true })
            {
                RenderSettings.fog = dark;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = .002f;
                RenderSettings.fogColor = new Color(.6f, .5f, .4f);
                foreach (var material in new[] { near, far })
                    material.SetColor("_TerrainTint", dark ? new Color(.5f, .5f, .5f) : Color.white);
                farGO.SetActive(false); terrainGO.SetActive(true);
                var a = Capture(camera, root + (dark ? "/near-dark.png" : "/near.png"));
                farGO.SetActive(true); terrainGO.SetActive(false);
                var b = Capture(camera, root + (dark ? "/far-dark.png" : "/far.png"));
                double error = a.Zip(b, (x, y) => (Math.Abs(x.r - y.r) + Math.Abs(x.g - y.g) + Math.Abs(x.b - y.b)) / 3.0).Average();
                report += $"dark={dark}: mean RGB error={error:F3}/255\n";
                if (error > 3) throw new Exception(report);
                if (b.All(x => x.r > 240 && x.b > 240 && x.g < 10)) throw new Exception("Error shader rendered");
            }
            report += CheckForest(camera, terrainGO, farGO, near, far, root);
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

    static string CheckForest(Camera camera, GameObject terrainGO, GameObject farGO, Material near, Material far, string root)
    {
        RenderSettings.fog = false;
        near.SetColor("_TerrainTint",Color.white); far.SetColor("_TerrainTint",Color.white);
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
        terrain.materialTemplate.SetColor("_TerrainTint", Color.white);
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
        far.SetColor("_TerrainTint", Color.white);
        terrainGO.GetComponent<Terrain>().materialTemplate.SetColor("_TerrainTint", Color.white);
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
