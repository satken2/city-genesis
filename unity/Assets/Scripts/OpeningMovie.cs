using System.Collections;
using System.Collections.Generic;
using GeneratedModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>本編の一覧 API と同じ形式の同梱データを、ページ順に本編の描画マネージャーへ渡す。</summary>
public sealed class OpeningMovie : MonoBehaviour
{
    private sealed class City
    {
        public Dictionary<int, MasterDataStore.AgentMasterItem> agents;
        public Dictionary<int, PathMasterItem> paths;
        public JObject[] rivers;
        public Heightmap terrain;
        public Stage[] stages;
    }

    private sealed class Heightmap
    {
        public int resolution;
        public float size, belowSea, span;
    }

    private sealed class Stage
    {
        public NodeData[] nodes;
        public PathSummary[] paths;
        public BuildingSummary[] buildings;
    }

    private static OpeningMovie instance;
    public static bool IsVisible => instance != null;
    private City city;
    private PathManager paths;
    private Camera movieCamera;
    private int shownStage, targetStage;
    private float angle;
    private bool buildingStage;

    public static IEnumerator Show(int stage)
    {
        if (instance == null)
        {
            instance = new GameObject("Opening Movie").AddComponent<OpeningMovie>();
            instance.Prepare();
        }
        var movie = instance;
        movie.targetStage = stage;
        // 同じページが再送された場合も、既に進行中の生成を待つ。
        if (movie.buildingStage)
        {
            while (ReferenceEquals(movie, instance) && movie.buildingStage && movie.shownStage < stage) yield return null;
            yield break;
        }
        movie.buildingStage = true;
        try
        {
            while (ReferenceEquals(movie, instance) && movie.shownStage < movie.targetStage)
            {
                var shot = movie.city.stages[movie.shownStage + 1];
                var masters = MasterDataStore.Instance;
                foreach (var node in shot.nodes) movie.paths.CreateOrUpdateNode(node);
                foreach (var path in shot.paths)
                {
                    masters.WithRenderingMasters(movie.city.agents, movie.city.paths, () => movie.paths.CreateOrUpdatePath(path));
                }
                // 配置順はデータ側で指定する。一軒ずつ本編に渡すことで、画面の端から順に街が立ち上がる。
                foreach (var building in shot.buildings)
                {
                    masters.WithRenderingMasters(movie.city.agents, movie.city.paths,
                        () => BuildingManager.Instance.CreateOrUpdateBuildingObject(building, false));
                    var house = BuildingManager.Instance.GetBuildingObject(building.agent_id);
                    movie.StartCoroutine(Rise(house.transform));
                    yield return new WaitForSecondsRealtime(0.045f);
                    if (!ReferenceEquals(movie, instance)) yield break;
                }
                movie.shownStage++;
            }
        }
        finally
        {
            if (movie != null) movie.buildingStage = false;
        }
    }

    private static IEnumerator Rise(Transform house)
    {
        const float duration = 0.26f;
        var scale = house.localScale;
        float start = Time.unscaledTime;
        while (house != null)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - start) / duration) - 1f;
            float y = 1f + t * t * (2.5f * t + 1.5f);
            house.localScale = new Vector3(scale.x, scale.y * Mathf.Max(0.01f, y), scale.z);
            if (t >= 0f) yield break;
            yield return null;
        }
    }

    private void Prepare()
    {
        city = JsonConvert.DeserializeObject<City>(Resources.Load<TextAsset>("Config/opening_city").text);
        paths = FindFirstObjectByType<PathManager>();
        var cameraObject = new GameObject("Opening Camera", typeof(Camera));
        cameraObject.transform.SetParent(transform, false);
        cameraObject.tag = "MainCamera";
        movieCamera = cameraObject.GetComponent<Camera>();
        movieCamera.fieldOfView = 43f;
        movieCamera.farClipPlane = 1400f;
        movieCamera.depth = 5;
        PositionCamera();

        var map = city.terrain;
        var bytes = Resources.Load<TextAsset>("Config/opening_heightmap").bytes;
        var heights = TerrainManager.ConvertFloat16BinaryToHeights(bytes, map.resolution, map.resolution, map.span, map.belowSea);
        var data = new TerrainData { heightmapResolution = map.resolution, alphamapResolution = 16,
            size = new Vector3(map.size, map.span, map.size) };
        gameObject.AddComponent<TerrainTileResources>().Own(data);
        data.SetHeights(0, 0, heights);
        var terrain = Utils.CachedTerrainManager.CreateRenderedTile(data, transform,
            Vector3.down * map.belowSea, "Opening terrain");
        terrain.gameObject.layer = LayerMask.NameToLayer("Ground");
        foreach (var river in city.rivers)
            RiverSurfaceManager.Instance.CreateSurface(RiverStore.Parse(river), transform);
    }

    private void LateUpdate()
    {
        angle = Mathf.MoveTowards(angle, targetStage * 5f, Time.unscaledDeltaTime * 3f);
        PositionCamera();
    }

    private void PositionCamera()
    {
        var focus = new Vector3(512f, 21f, 534f);
        movieCamera.transform.position = focus + Quaternion.Euler(0f, angle, 0f) * new Vector3(45f, 105f, -176f);
        movieCamera.transform.LookAt(focus);
    }

    public static void Close()
    {
        if (instance == null) return;
        instance.gameObject.SetActive(false);
        BuildingManager.Instance?.ClearAllCells();
        instance.paths?.ClearAllCells();
        Destroy(instance.gameObject);
        instance = null;
    }
}
