using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>
/// 火災エフェクトマネージャ。
///
/// 役割
/// ----
/// 建物(agent)の alert ビットのうち火災系の2つを、ビルボードアイコンではなく
/// 3D空間のパーティクル表現で描く。
///
/// - `ALERT_AGENT_ON_FIRE`(本格火災): 炎 + 火の粉 + 黒煙 + 明滅する点光源
/// - `ALERT_AGENT_ACCIDENTAL_FIRE`(失火): 薄い白煙のみ
///
/// 両ビットが同時に立っている建物は本格火災を優先する。ただし当エポックで焼け落ちた建物
/// (`STATUS_COLLAPSED`)は瓦礫がくすぶっている状態として白煙に落とす(瓦礫の上で炎が
/// 燃え盛るのは不自然なため)。
///
/// データ取得
/// ----------
/// 専用のHTTP取得は行わず、`AlertManager` が `GET /api/v1/alerts` で取った結果を
/// `ApplyAlerts` で受け取る(同じ情報のために2本リクエストを投げない)。
/// 表示フィルタ(表示パネルのアラートタブ)と全体表示トグルにも `AlertManager` 経由で追従する。
///
/// 配置と寿命
/// ----------
/// エフェクトは建物GameObjectの子にはせず、ワールド座標に置いた自前のルートの下に作る。
/// 建物はLODの個数上限で破棄され得るため、子にすると炎まで一緒に消える。
///
/// `ApplyAlerts` はカメラのセル跨ぎでも呼ばれるので、既に燃えている建物のエフェクトは
/// **作り直さず維持する**(作り直すと煙柱が根元から生え直して目立つ)。作り直すのは
/// 種別が変わったときと、建物のメッシュが後からロードされて寸法が大きく変わったときだけ。
///
/// パフォーマンス
/// --------------
/// - 火災件数は都市規模でも高々数十件だが、煙柱はパーティクル数が多いので
///   `maxEffectDistance` より遠いエフェクトは GameObject ごと非アクティブにする。
/// - 点光源は近い順に `maxFireLights` 件までしか点けない(URPはForward+のため多灯自体は
///   許容されるが、点光源は影響範囲が広く重なると加算で白飛びしやすい)。
/// - 距離判定と光源の割り当ては `cullIntervalSeconds` ごとにまとめて行う。
/// </summary>
public class FireEffectManager : MonoBehaviour
{
    public static FireEffectManager Instance { get; private set; }

    [Header("表示範囲")]
    [Tooltip("この距離(m)より遠い火災エフェクトは非アクティブにする。")]
    public float maxEffectDistance = 1400f;

    [Tooltip("距離判定と光源割り当てを行う間隔(秒)。")]
    public float cullIntervalSeconds = 0.25f;

    [Header("火災の照り返し")]
    [Tooltip("同時に点灯させる火災の点光源の最大数。カメラに近い順に割り当てる。")]
    public int maxFireLights = 6;

    [Tooltip("点光源を使うか。負荷や画作りの都合で切りたい場合にOFFにする。")]
    public bool enableFireLights = true;

    [Tooltip("光源の明滅の速さ。")]
    public float lightFlickerSpeed = 3f;

    [Tooltip("光源の明滅の深さ(0=明滅なし、1=消灯まで振れる)。")]
    [Range(0f, 1f)] public float lightFlickerDepth = 0.35f;

    /// <summary>建物メッシュも階数も分からない場合に使う底面半径(m)。</summary>
    private const float FALLBACK_RADIUS = 6f;

    /// <summary>建物メッシュも階数も分からない場合に使う高さ(m)。</summary>
    private const float FALLBACK_HEIGHT = 8f;

    /// <summary>寸法がこの比率を超えて変わったらエフェクトを作り直す。</summary>
    private const float REBUILD_SIZE_RATIO = 1.35f;

    /// <summary>建物1棟分の火災エフェクト。</summary>
    private class FireEffect
    {
        public GameObject Root;
        public FireEffectFactory.PlumeKind Kind;
        public float Radius;
        public float Height;
        public Light Light;
        public float LightBaseIntensity;
        public float FlickerPhase;
    }

    private readonly Dictionary<int, FireEffect> _effects = new();
    private readonly List<int> _staleIds = new();
    private readonly List<FireEffect> _lightCandidates = new();

    private Transform _root;
    private bool _isVisible = true;
    private float _lastCullTime = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null)
        {
            var go = new GameObject("FireEffectManager");
            go.AddComponent<FireEffectManager>();
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        var rootGO = new GameObject("FireEffects");
        rootGO.transform.SetParent(transform, false);
        _root = rootGO.transform;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// エフェクト全体の表示/非表示。`AlertManager.IsVisible` に追従する。
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            if (_root != null) _root.gameObject.SetActive(_isVisible);
        }
    }

    /// <summary>
    /// アラート一覧から火災エフェクトを同期する。`AlertManager` がAPI応答を受けた直後に呼ぶ。
    /// </summary>
    /// <param name="agents">GET /api/v1/alerts の agents 配列</param>
    /// <param name="agentFilterMask">表示パネルのアラートフィルタ(agent側の下位ビット)</param>
    public void ApplyAlerts(List<AlertItem> agents, int agentFilterMask)
    {
        if (_root == null) return;

        var alive = new HashSet<int>();

        if (agents != null)
        {
            foreach (var item in agents)
            {
                FireEffectFactory.PlumeKind? kind = ResolveKind(item, agentFilterMask);
                if (kind == null) continue;

                alive.Add(item.id);
                SyncEffect(item, kind.Value);
            }
        }

        _staleIds.Clear();
        foreach (var kvp in _effects)
        {
            if (!alive.Contains(kvp.Key)) _staleIds.Add(kvp.Key);
        }
        foreach (int id in _staleIds)
        {
            DestroyEffect(id);
        }

        _lastCullTime = -1f;
    }

    /// <summary>全エフェクトを破棄する。全量リロード時に呼ばれる。</summary>
    public void ClearAll()
    {
        foreach (var kvp in _effects)
        {
            if (kvp.Value.Root != null) Destroy(kvp.Value.Root);
        }
        _effects.Clear();
    }

    /// <summary>
    /// アラートビットと状態からエフェクト種別を決める。表示対象でなければ null。
    /// </summary>
    private FireEffectFactory.PlumeKind? ResolveKind(AlertItem item, int agentFilterMask)
    {
        bool onFire = (item.alert & Constants.ALERT_AGENT_ON_FIRE) != 0
                      && (agentFilterMask & Constants.ALERT_AGENT_ON_FIRE) != 0;
        bool accidental = (item.alert & Constants.ALERT_AGENT_ACCIDENTAL_FIRE) != 0
                          && (agentFilterMask & Constants.ALERT_AGENT_ACCIDENTAL_FIRE) != 0;

        if (onFire)
        {
            // 焼け落ちた建物は炎ではなく瓦礫のくすぶりとして描く。
            return item.status == Constants.STATUS_COLLAPSED
                ? FireEffectFactory.PlumeKind.Smolder
                : FireEffectFactory.PlumeKind.Blaze;
        }
        if (accidental)
        {
            return FireEffectFactory.PlumeKind.Smolder;
        }
        return null;
    }

    /// <summary>
    /// 1棟分のエフェクトを生成または更新する。種別も寸法も変わっていなければ何もしない
    /// (煙柱の連続性を保つため、無用な作り直しをしない)。
    /// </summary>
    private void SyncEffect(AlertItem item, FireEffectFactory.PlumeKind kind)
    {
        if (!TryResolvePlacement(item, out Vector3 worldPos, out float radius, out float height)) return;

        if (_effects.TryGetValue(item.id, out FireEffect existing) && existing.Root != null)
        {
            bool sizeChanged = Ratio(existing.Radius, radius) > REBUILD_SIZE_RATIO
                               || Ratio(existing.Height, height) > REBUILD_SIZE_RATIO;
            if (existing.Kind == kind && !sizeChanged)
            {
                existing.Root.transform.position = worldPos;
                return;
            }
            DestroyEffect(item.id);
        }

        var spec = new FireEffectFactory.PlumeSpec(radius, height, ResolveDrift(item.id));
        GameObject root = FireEffectFactory.Create(kind, spec, _root, $"Fire_{item.id}");
        root.transform.position = worldPos;

        Light fireLight = root.GetComponentInChildren<Light>(includeInactive: true);
        _effects[item.id] = new FireEffect
        {
            Root = root,
            Kind = kind,
            Radius = radius,
            Height = height,
            Light = fireLight,
            LightBaseIntensity = fireLight != null ? fireLight.intensity : 0f,
            FlickerPhase = (item.id % 97) * 0.37f,
        };
    }

    private void DestroyEffect(int id)
    {
        if (_effects.TryGetValue(id, out FireEffect effect))
        {
            if (effect.Root != null) Destroy(effect.Root);
            _effects.Remove(id);
        }
    }

    /// <summary>
    /// 建物の設置位置・底面半径・高さを解決する。
    /// 高さは実際に描かれているメッシュのAABBを最優先とし、建物が未ロード(LOD破棄・遠景)なら
    /// 階数×階高、それも無ければ既定値にフォールバックする。
    /// </summary>
    private bool TryResolvePlacement(AlertItem item, out Vector3 worldPos, out float radius, out float height)
    {
        worldPos = Vector3.zero;
        radius = FALLBACK_RADIUS;
        height = FALLBACK_HEIGHT;

        if (item.center_point == null || item.center_point.Count < 2) return false;

        float x = item.center_point[0];
        float z = item.center_point[1];
        float groundHeight = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
        worldPos = new Vector3(x, groundHeight, z);

        BuildingSummary summary = BuildingManager.Instance != null
            ? BuildingManager.Instance.GetBuildingSummary(item.id)
            : null;

        if (summary != null && summary.polygonArray != null && summary.polygonArray.Length >= 3)
        {
            radius = ResolveFootprintRadius(summary, x, z);
        }

        GameObject buildingGO = BuildingManager.Instance != null
            ? BuildingManager.Instance.GetBuildingObject(item.id)
            : null;

        if (buildingGO != null && TryResolveMeshHeight(buildingGO, groundHeight, out float meshHeight))
        {
            height = meshHeight;
        }
        else if (summary != null && summary.floors.HasValue && summary.floors.Value > 0)
        {
            height = summary.floors.Value * BuildingMeshUtils.GetFloorHeight(summary);
        }

        return true;
    }

    /// <summary>建物底面ポリゴンの外接矩形から、中心を基準にした半径を求める。</summary>
    public static float ResolveFootprintRadius(BuildingSummary summary, float centerX, float centerZ)
    {
        if (summary?.polygonArray == null) return FALLBACK_RADIUS;
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        foreach (float[] point in summary.polygonArray)
        {
            if (point == null || point.Length < 2) continue;
            minX = Mathf.Min(minX, point[0]);
            maxX = Mathf.Max(maxX, point[0]);
            minZ = Mathf.Min(minZ, point[1]);
            maxZ = Mathf.Max(maxZ, point[1]);
        }

        if (minX > maxX || minZ > maxZ) return FALLBACK_RADIUS;

        // 中心が外接矩形の中央とは限らない(L字の建物など)ため、中心から遠い側の辺で測る。
        float halfX = Mathf.Max(maxX - centerX, centerX - minX);
        float halfZ = Mathf.Max(maxZ - centerZ, centerZ - minZ);
        return Mathf.Max(halfX, halfZ);
    }

    /// <summary>
    /// 建物GameObjectの描画AABBの頂点から地表までを高さとみなす。
    /// マッシュアップ建物のように複数の子メッシュを持つケースがあるため子まで走査する。
    /// </summary>
    private static bool TryResolveMeshHeight(GameObject buildingGO, float groundHeight, out float height)
    {
        height = 0f;

        Renderer[] renderers = buildingGO.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return false;

        float top = float.MinValue;
        foreach (Renderer renderer in renderers)
        {
            top = Mathf.Max(top, renderer.bounds.max.y);
        }
        if (top <= float.MinValue) return false;

        height = top - groundHeight;
        return height > 1f;
    }

    /// <summary>
    /// 建物IDから決まる煙のドリフト方向。黄金角で回すことで、隣り合う建物の煙柱が
    /// 同じ向きに揃わないようにする。
    /// </summary>
    private static Vector2 ResolveDrift(int id)
    {
        float angle = id * 2.399963f;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private static float Ratio(float a, float b)
    {
        float lo = Mathf.Max(Mathf.Min(a, b), 1e-3f);
        float hi = Mathf.Max(a, b);
        return hi / lo;
    }

    private void Update()
    {
        if (!_isVisible || _effects.Count == 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        if (Time.time - _lastCullTime >= cullIntervalSeconds)
        {
            _lastCullTime = Time.time;
            UpdateCullingAndLights(cam.transform.position);
        }

        UpdateLightFlicker();
    }

    /// <summary>
    /// 距離による表示打ち切りと、点光源を点ける対象の選定をまとめて行う。
    /// </summary>
    private void UpdateCullingAndLights(Vector3 camPos)
    {
        float maxSqr = maxEffectDistance * maxEffectDistance;
        _lightCandidates.Clear();

        foreach (var kvp in _effects)
        {
            FireEffect effect = kvp.Value;
            if (effect.Root == null) continue;

            float sqrDist = (effect.Root.transform.position - camPos).sqrMagnitude;
            bool visible = sqrDist <= maxSqr;
            if (effect.Root.activeSelf != visible) effect.Root.SetActive(visible);

            if (effect.Light == null) continue;

            if (visible && enableFireLights && effect.Kind == FireEffectFactory.PlumeKind.Blaze)
            {
                _lightCandidates.Add(effect);
            }
            else if (effect.Light.gameObject.activeSelf)
            {
                effect.Light.gameObject.SetActive(false);
            }
        }

        if (_lightCandidates.Count > 1)
        {
            _lightCandidates.Sort((a, b) =>
                (a.Root.transform.position - camPos).sqrMagnitude
                    .CompareTo((b.Root.transform.position - camPos).sqrMagnitude));
        }

        for (int i = 0; i < _lightCandidates.Count; i++)
        {
            bool lit = i < maxFireLights;
            GameObject lightGO = _lightCandidates[i].Light.gameObject;
            if (lightGO.activeSelf != lit) lightGO.SetActive(lit);
        }
    }

    /// <summary>点灯中の光源の強さをゆらす。炎の揺らぎと光の揺らぎが合うと火災らしく見える。</summary>
    private void UpdateLightFlicker()
    {
        if (!enableFireLights || lightFlickerDepth <= 0f) return;

        for (int i = 0; i < _lightCandidates.Count && i < maxFireLights; i++)
        {
            FireEffect effect = _lightCandidates[i];
            if (effect.Light == null || !effect.Light.gameObject.activeSelf) continue;

            float noise = Mathf.PerlinNoise(effect.FlickerPhase, Time.time * lightFlickerSpeed);
            effect.Light.intensity = effect.LightBaseIntensity * (1f - lightFlickerDepth * (1f - noise));
        }
    }
}
