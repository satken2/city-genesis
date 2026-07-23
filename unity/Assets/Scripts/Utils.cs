// Assets/Scripts/MeshGeneration/Generators/CarRoadMeshGenerator.cs
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

public static class Utils
{
    /// <summary>
    /// 地形高さのキャッシュ。キーは (x, z) を整数メートルグリッドに丸めた値。
    /// 同一フレーム内で同じ座標付近への問い合わせを高速化する。
    /// </summary>
    private static Dictionary<long, float> heightCache = new Dictionary<long, float>();
    private static int cacheFrame = -1;
    private const float CacheResolution = 1f;

    /// <summary>
    /// TerrainManager のキャッシュ参照。
    /// FindAnyObjectByType はシーン内全オブジェクトを走査する O(n) 操作であり、
    /// GetGroundHeight から頻繁に呼ばれるため、毎回の検索を避けて静的フィールドに保持する。
    /// TerrainManager の Awake で RegisterTerrainManager を呼んで設定する。
    /// OnDestroy 時のクリア判定のため、内部アクセス用のプロパティも用意する。
    /// </summary>
    private static TerrainManager cachedTerrainManager;

    /// <summary>
    /// OnDestroy 等でキャッシュ参照の比較に使用するための内部プロパティ。
    /// </summary>
    internal static TerrainManager CachedTerrainManager => cachedTerrainManager;

    /// <summary>
    /// TerrainManager のインスタンスをキャッシュに登録する。
    /// TerrainManager.Awake から呼び出すことで、以降の GetGroundHeight で
    /// FindAnyObjectByType を実行せずにインスタンスを参照できるようになる。
    /// </summary>
    public static void RegisterTerrainManager(TerrainManager manager)
    {
        cachedTerrainManager = manager;
    }

    /// <summary>
    /// キャッシュを強制的にクリアする。TerrainのLOD差し替え後などに呼び出す。
    /// </summary>
    public static void InvalidateHeightCache()
    {
        cacheFrame = -1;
        heightCache.Clear();
    }

    /// <summary>
    /// 指定位置の地形高さを返す。同一フレームかつ近傍座標の場合はキャッシュを利用する。
    /// キャッシュの解像度は CacheResolution（メートル）単位で丸められるため、
    /// LOD差し替え直後など正確な高さが必要な場面では InvalidateHeightCache() を呼ぶこと。
    /// </summary>
    public static float GetGroundHeight(Vector3 position)
    {
        int frame = Time.frameCount;
        if (frame != cacheFrame)
        {
            heightCache.Clear();
            cacheFrame = frame;
        }

        long key = PackGridKey(position.x, position.z);
        if (heightCache.TryGetValue(key, out float cached))
        {
            return cached;
        }

        float groundHeight = ComputeGroundHeight(position);
        heightCache[key] = groundHeight;
        return groundHeight;
    }

    private static float ComputeGroundHeight(Vector3 position)
    {
        Terrain terrain = GetTerrainAtPosition(position);
        if (terrain != null)
        {
            // Terrain.SampleHeight はタイルのローカル高さ(ベースY除外)を返すため、
            // ワールドYにするにはタイルの transform.position.y を加える。地形タイルは
            // 海底の水深表現のためベースYが下げられている(TerrainManager.SeaFloorDepthSpan)ので、
            // これを足さないと海面付近の高さが一定量ずれる。
            return terrain.SampleHeight(position) + terrain.transform.position.y;
        }

        int groundLayerMask = LayerMask.GetMask("Ground");
        Ray ray = new Ray(new Vector3(position.x, 1000f, position.z), Vector3.down);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, groundLayerMask))
        {
            return hit.point.y;
        }

        return 0f;
    }

    /// <summary>
    /// Task の完了をコルーチン上で待機するラッパー。
    /// 完了後に例外を再スロー／ログ出力する。
    /// </summary>
    public static IEnumerator WaitTask(Task task)
    {
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted && task.Exception != null)
        {
            Debug.LogError($"Background task failed: {task.Exception.Flatten()}");
        }
    }

    /// <summary>
    /// (x, z) をグリッド座標に丸めて long 型キーにパックする。
    /// </summary>
    private static long PackGridKey(float x, float z)
    {
        int gx = (int)Mathf.Round(x / CacheResolution);
        int gz = (int)Mathf.Round(z / CacheResolution);
        return ((long)gx << 32) | (uint)gz;
    }

    private static Terrain GetTerrainAtPosition(Vector3 position)
    {
        // キャッシュされた TerrainManager を優先的に参照する。
        // TerrainManager の terrainTiles 辞書は LOD 切り替え時に最新の Terrain で上書きされるため、
        // 同じセル内に複数の LOD 地形（例：近景 257 と超近景 513）が存在しても、
        // 必ず最も詳細なアクティブな Terrain が取得できる。
        // 同じセルサイズの Terrain が複数存在する場合に Terrain.activeTerrains の配列順に
        // 依存して低解像度の地形が選ばれてしまう問題を防ぐため、この順序としている。
        if (cachedTerrainManager != null)
        {
            Terrain managedTerrain = cachedTerrainManager.GetTerrainAtPosition(position);
            if (managedTerrain != null)
            {
                return managedTerrain;
            }
        }

        // TerrainManager から取得できなかった場合（マップ外など）のフォールバック。
        // アクティブな Terrain のうち当該座標を含むものをすべて候補にし、
        // 水平面積が最小のものを優先する。
        Terrain[] terrains = Terrain.activeTerrains;
        Terrain best = null;
        float bestArea = float.MaxValue;

        foreach (Terrain terrain in terrains)
        {
            if (terrain == null || terrain.terrainData == null) continue;
            if (!terrain.gameObject.activeInHierarchy) continue;

            Vector3 terrainPosition = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;

            if (position.x >= terrainPosition.x &&
                position.x <= terrainPosition.x + terrainSize.x &&
                position.z >= terrainPosition.z &&
                position.z <= terrainPosition.z + terrainSize.z)
            {
                float area = terrainSize.x * terrainSize.z;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = terrain;
                }
            }
        }

        return best;
    }

    private static Shader cachedDepthOffsetShader;

    public static void ApplyDepthOffsetShader(MeshRenderer renderer, string contextName = "")
    {
        if (renderer == null) return;

        if (cachedDepthOffsetShader == null)
        {
            cachedDepthOffsetShader = Shader.Find("Custom/DepthOffsetSimpleLit");
        }
        Shader offsetShader = cachedDepthOffsetShader;
        if (offsetShader == null)
        {
            string prefix = string.IsNullOrEmpty(contextName) ? "" : contextName + ": ";
            Debug.LogWarning($"{prefix}Custom/DepthOffsetSimpleLit shader not found");
            return;
        }

        Material[] mats = renderer.materials;
        bool changed = false;
        for (int i = 0; i < mats.Length; i++)
        {
            if (mats[i] != null && mats[i].shader != offsetShader)
            {
                mats[i].shader = offsetShader;
                changed = true;
            }
        }
        if (changed)
        {
            renderer.materials = mats;
        }
    }

    public static IEnumerator DestroyObjectAfterFrames(MonoBehaviour host, GameObject obj)
    {
        yield return null;
        yield return null;

        if (obj != null)
        {
            Object.Destroy(obj);
        }
    }

    // ================================================================
    // 視野優先処理(生成・再構築の処理順決定)の共通ヘルパー
    // ================================================================

    /// <summary>
    /// カメラ前方ベクトルの水平成分(正規化済み)を返す。真上・真下向きのフォールバックは+Z。
    /// 重い生成・再構築処理を「視野内を先に」処理するための水平角判定に使う
    /// (BuildingManager の詳細メッシュ生成、PathManager の地形追従再構築など)。
    /// </summary>
    public static Vector3 GetCameraHorizontalForward(Camera cam)
    {
        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) return Vector3.forward;
        return fwd.normalized;
    }

    /// <summary>
    /// 指定位置が「重い生成処理を即時に行うべき視野内」かどうかの水平判定。
    /// カメラ近傍(<paramref name="nearDistance"/>以内)は向きに関わらず視野内とみなす。
    /// 視錐台は生成可否のフィルタではなく処理順の重み付けにのみ使うこと
    /// (視野外を永久に生成しないと、カメラが振り向いたときに欠けたままになる)。
    /// </summary>
    public static bool IsInHorizontalView(Vector3 position, Vector3 camPos, Vector3 camForwardHorizontal, float nearDistance, float halfAngleDeg)
    {
        Vector3 to = position - camPos;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist <= nearDistance) return true;
        return Vector3.Angle(camForwardHorizontal, to) <= halfAngleDeg;
    }

    public static float CalculateAverageWidth(List<float> widths)
    {
        if (widths == null || widths.Count == 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < widths.Count; i++) sum += widths[i];
        return sum / widths.Count;
    }
}