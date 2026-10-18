using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 火災の3Dエフェクト一式を、建物の底面半径と高さに合わせて手続き的に組み立てるファクトリ。
///
/// 種別
/// ----
/// - Blaze(燃えている建物): 炎 + 火の粉 + 黒煙 + 明滅する点光源
/// - Smolder(焼け落ちた瓦礫): 薄い白煙のみ(炎・光源なし)
///
/// アセット非依存
/// --------------
/// テクスチャ(炎・煙・火の粉)は起動時に1度だけコード生成し、マテリアルとともに静的に共有する。
/// アートアセットの追加なしで成立させるための設計で、パーティクル数が増えても
/// マテリアルは3種類しか存在しないためドローコールはバッチされる。
/// 描画は `Custom/FireParticle`(前乗算アルファ + ソフトパーティクル)。
///
/// パーティクルはローカル空間シミュレーション(`ParticleSystemSimulationSpace.Local`)で組む。
/// 発生源の建物は動かないため World 空間にする利点がなく、Local なら親のスケールが
/// そのまま煙柱全体に効く。FloatingOrigin がワールドをずらしても、煙が元の位置に尾を引かない。
/// </summary>
public static class FireEffectFactory
{
    /// <summary>エフェクトの種別。</summary>
    public enum PlumeKind
    {
        Blaze,
        Smolder,
    }

    /// <summary>建物1棟分のエフェクト寸法。</summary>
    public readonly struct PlumeSpec
    {
        /// <summary>建物底面の外接半径(m)。炎の幅・煙の太さ・発生量の基準。</summary>
        public readonly float Radius;

        /// <summary>建物の高さ(m)。炎の吹き出し口と煙の発生高さの基準。</summary>
        public readonly float Height;

        /// <summary>煙の水平ドリフト方向(単位ベクトル)。建物ごとに変えて煙柱の均一化を避ける。</summary>
        public readonly Vector2 Drift;

        public PlumeSpec(float radius, float height, Vector2 drift)
        {
            Radius = Mathf.Clamp(radius, 2.5f, 45f);
            Height = Mathf.Clamp(height, 3f, 220f);
            Drift = drift.sqrMagnitude > 1e-4f ? drift.normalized : Vector2.right;
        }
    }

    private const int FLAME_TEXTURE_SIZE = 128;
    private const int SMOKE_TEXTURE_SIZE = 128;
    private const int SPARK_TEXTURE_SIZE = 32;

    // 煙は通常合成、炎と火の粉は加算合成。加算は煙の上に重ねたいのでキューを後ろに置く。
    private const int SMOKE_RENDER_QUEUE = 3000;
    private const int FLAME_RENDER_QUEUE = 3010;
    private const int SPARK_RENDER_QUEUE = 3020;

    private static Material _flameMaterial;
    private static Material _smokeMaterial;
    private static Material _sparkMaterial;

    /// <summary>
    /// 指定種別のエフェクト一式を生成し、ルートGameObjectを返す。
    /// ルートはワールド座標で建物の設置面(足元)に置かれる前提で、内部の高さは全て相対。
    /// </summary>
    public static GameObject Create(PlumeKind kind, in PlumeSpec spec, Transform parent, string name)
    {
        EnsureMaterials();

        var root = new GameObject(name);
        root.transform.SetParent(parent, false);

        if (kind == PlumeKind.Blaze)
        {
            BuildFlames(root.transform, spec);
            BuildEmbers(root.transform, spec);
            BuildBlazeSmoke(root.transform, spec);
            BuildFireLight(root.transform, spec);
        }
        else
        {
            BuildSmolderSmoke(root.transform, spec);
        }

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
        {
            ps.Play();
        }
        return root;
    }

    /// <summary>爆破操作用の単発噴煙。通常火災の素材だけを共有し、燃え続ける煙柱は作らない。</summary>
    public static GameObject CreateExplosion(float radius, Transform parent)
    {
        EnsureMaterials();
        var root = new GameObject("BuildingExplosion");
        root.transform.SetParent(parent, false);
        float r = Mathf.Clamp(radius, 2.5f, 35f);

        var flames = AddExplosionSystem(root.transform, "BlastFlames", _flameMaterial, 30, r);
        var main = flames.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.65f);
        main.startSize = new ParticleSystem.MinMaxCurve(r * 0.3f, r * 0.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.5f, r * 1.1f);
        SetColorOverLifetime(flames,
            new[] {
                new GradientColorKey(new Color(1f, 0.42f, 0.08f), 0f),
                new GradientColorKey(new Color(0.95f, 0.09f, 0.015f), 0.3f),
                new GradientColorKey(new Color(0.32f, 0.025f, 0.01f), 1f),
            },
            new[] {
                new GradientAlphaKey(0.9f, 0f),
                new GradientAlphaKey(0.7f, 0.35f),
                new GradientAlphaKey(0f, 1f),
            });
        SetSizeOverLifetime(flames, new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0.2f)));

        var smoke = AddExplosionSystem(root.transform, "BlastSmoke", _smokeMaterial, 45, r);
        main = smoke.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(r * 0.35f, r * 0.65f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(r * 0.15f, r * 0.38f);
        main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        var velocity = smoke.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.Local;
        velocity.y = Mathf.Max(1.5f, r * 0.3f);
        SetColorOverLifetime(smoke,
            new[] {
                new GradientColorKey(new Color(0.15f, 0.13f, 0.12f), 0f),
                new GradientColorKey(new Color(0.38f, 0.36f, 0.34f), 0.4f),
                new GradientColorKey(new Color(0.6f, 0.58f, 0.55f), 1f),
            },
            new[] {
                new GradientAlphaKey(0.15f, 0f),
                new GradientAlphaKey(0.65f, 0.08f),
                new GradientAlphaKey(0.35f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            });
        SetSizeOverLifetime(smoke, AnimationCurve.Linear(0f, 0.45f, 1f, 1.8f));
        SetSwayNoise(smoke, Mathf.Max(0.3f, r * 0.08f), 0.35f, 0.5f);
        SetRotationOverLifetime(smoke, 18f);

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>()) ps.Play();
        return root;
    }

    private static ParticleSystem AddExplosionSystem(Transform root, string name, Material material, short count, float radius)
    {
        var ps = AddSystem(root, name, material, count);
        var main = ps.main;
        main.prewarm = false;
        main.loop = false;
        main.duration = 0.15f;
        main.useUnscaledTime = true;
        // 画面外へ出ても寿命を進め、戻ったときに爆発が再開しないようにする。
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 55f;
        shape.radius = radius * 0.55f;
        shape.rotation = new Vector3(-90f, 0f, 0f);
        return ps;
    }

    // ------------------------------------------------------------------
    // パーティクルシステムの構築
    // ------------------------------------------------------------------

    /// <summary>炎本体。建物上部の箱状の範囲から吹き上がる。</summary>
    private static void BuildFlames(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Flames", _flameMaterial, 220);

        var main = ps.main;
        main.duration = 6f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.7f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(Mathf.Max(1f, r * 0.26f), Mathf.Max(1.9f, r * 0.55f));
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(1.1f, r * 0.42f), Mathf.Max(2.1f, r * 0.8f));
        main.startRotation = new ParticleSystem.MinMaxCurve(-0.22f, 0.22f);
        main.gravityModifier = -0.04f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * r * 0.85f, 24f, 150f);

        // 屋根だけでなく上層階の開口部からも噴き出している見え方にするため、
        // 発生源は建物上部を覆う厚みのある箱にする。底面いっぱいに広げると
        // 建物より炎の方が大きく見えてしまうので、XZは底面より内側に収める。
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.85f, h * 0.3f, r * 0.85f);
        shape.position = new Vector3(0f, h * 0.8f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(1.00f, 0.96f, 0.80f), 0.00f),
                new GradientColorKey(new Color(1.00f, 0.82f, 0.38f), 0.18f),
                new GradientColorKey(new Color(1.00f, 0.48f, 0.10f), 0.45f),
                new GradientColorKey(new Color(0.72f, 0.16f, 0.03f), 0.78f),
                new GradientColorKey(new Color(0.25f, 0.05f, 0.01f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(1.00f, 0.10f),
                new GradientAlphaKey(0.85f, 0.42f),
                new GradientAlphaKey(0.35f, 0.75f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetSizeOverLifetime(ps, new AnimationCurve(
            new Keyframe(0f, 0.45f),
            new Keyframe(0.28f, 1.00f),
            new Keyframe(1f, 0.20f)));

        SetNoise(ps, strength: Mathf.Max(0.4f, r * 0.18f), frequency: 0.4f, scrollSpeed: 0.55f, octaves: 2);
    }

    /// <summary>火の粉。炎から舞い上がる細かい光点。</summary>
    private static void BuildEmbers(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Embers", _sparkMaterial, 140);

        var main = ps.main;
        main.duration = 6f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 4.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(Mathf.Max(1.3f, r * 0.45f), Mathf.Max(2.6f, r * 0.9f));
        main.startSize = new ParticleSystem.MinMaxCurve(0.09f, 0.28f);
        main.gravityModifier = -0.1f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * 1.2f, 4f, 20f);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.75f, h * 0.24f, r * 0.75f);
        shape.position = new Vector3(0f, h * 0.84f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(1.00f, 0.93f, 0.62f), 0.00f),
                new GradientColorKey(new Color(1.00f, 0.58f, 0.16f), 0.45f),
                new GradientColorKey(new Color(0.65f, 0.12f, 0.02f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(1.00f, 0.12f),
                new GradientAlphaKey(0.55f, 0.65f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetNoise(ps, strength: Mathf.Max(0.45f, r * 0.45f), frequency: 0.22f, scrollSpeed: 0.8f, octaves: 2);

        // 上昇の軌跡を持たせると点ではなく「飛んでいる火の粉」に見える。
        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.06f;
        renderer.lengthScale = 2.2f;
    }

    /// <summary>火災の黒煙。上昇しながら広がり、冷えて薄灰色になる。</summary>
    private static void BuildBlazeSmoke(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Smoke", _smokeMaterial, 180);

        var main = ps.main;
        // prewarm は duration ぶんだけ空回しするため、寿命の上限に合わせておかないと
        // 表示された瞬間に煙柱が上まで伸びきらない。
        main.duration = 11f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 11f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(Mathf.Max(1.1f, r * 0.24f), Mathf.Max(2f, r * 0.45f));
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(1.6f, r * 0.5f), Mathf.Max(2.8f, r * 0.85f));
        main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        main.gravityModifier = -0.03f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * 0.9f, 5f, 26f);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.5f, h * 0.06f, r * 0.5f);
        shape.position = new Vector3(0f, h * 1.0f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(0.10f, 0.09f, 0.09f), 0.00f),
                new GradientColorKey(new Color(0.30f, 0.29f, 0.28f), 0.45f),
                new GradientColorKey(new Color(0.55f, 0.54f, 0.52f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(0.85f, 0.15f),
                new GradientAlphaKey(0.55f, 0.60f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetSizeOverLifetime(ps, new AnimationCurve(
            new Keyframe(0f, 0.50f),
            new Keyframe(0.45f, 1.10f),
            new Keyframe(1f, 1.70f)));

        SetRotationOverLifetime(ps, degreesPerSecond: 8f);
        SetSwayNoise(ps, strength: Mathf.Max(0.6f, r * 0.35f), frequency: 0.12f, scrollSpeed: 0.16f);
        SetDrift(ps, spec.Drift, speed: 0.6f);
    }

    /// <summary>失火の白煙。炎は伴わず、細く立ち上るだけ。</summary>
    private static void BuildSmolderSmoke(Transform root, in PlumeSpec spec)
    {
        float r = spec.Radius;
        float h = spec.Height;

        ParticleSystem ps = AddSystem(root, "Smoke", _smokeMaterial, 90);

        var main = ps.main;
        main.duration = 9f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(Mathf.Max(0.8f, r * 0.26f), Mathf.Max(1.4f, r * 0.45f));
        main.startRotation = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        main.gravityModifier = -0.02f;

        var emission = ps.emission;
        emission.rateOverTime = Mathf.Clamp(r * 0.5f, 2.5f, 12f);

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(r * 0.22f, h * 0.04f, r * 0.22f);
        shape.position = new Vector3(0f, h * 0.98f, 0f);

        SetColorOverLifetime(ps,
            new[]
            {
                new GradientColorKey(new Color(0.62f, 0.61f, 0.60f), 0.00f),
                new GradientColorKey(new Color(0.80f, 0.79f, 0.78f), 1.00f),
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(0.42f, 0.20f),
                new GradientAlphaKey(0.28f, 0.60f),
                new GradientAlphaKey(0.00f, 1.00f),
            });

        SetSizeOverLifetime(ps, new AnimationCurve(
            new Keyframe(0f, 0.35f),
            new Keyframe(0.5f, 1.00f),
            new Keyframe(1f, 1.60f)));

        SetRotationOverLifetime(ps, degreesPerSecond: 6f);
        SetSwayNoise(ps, strength: Mathf.Max(0.35f, r * 0.2f), frequency: 0.13f, scrollSpeed: 0.14f);
        SetDrift(ps, spec.Drift, speed: 0.4f);
    }

    /// <summary>
    /// 火災の照り返し用の点光源。夜間や日陰で炎が「発光している」ことを周囲に伝える。
    /// 明滅と点灯本数の制限は FireEffectManager が行うため、ここでは消灯状態で置くだけ。
    /// </summary>
    private static void BuildFireLight(Transform root, in PlumeSpec spec)
    {
        var go = new GameObject("FireLight");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, spec.Height * 0.7f, 0f);

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.62f, 0.28f);
        light.range = Mathf.Clamp(spec.Radius * 7f, 25f, 140f);
        light.intensity = Mathf.Clamp(spec.Radius * 0.55f, 2.5f, 10f);
        light.shadows = LightShadows.None;
        go.SetActive(false);
    }

    // ------------------------------------------------------------------
    // パーティクルモジュールの設定ヘルパー
    // ------------------------------------------------------------------

    private static ParticleSystem AddSystem(Transform parent, string name, Material material, int maxParticles)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var ps = go.AddComponent<ParticleSystem>();
        // AddComponent 直後は playOnAwake で再生状態になっている。duration や prewarm は
        // 停止中しか変更できないため、モジュールを触る前に必ず止める(再生は Create の末尾)。
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        // カメラのセル跨ぎでエフェクトを作り直したとき、煙柱が根元から生え直すのを防ぐ。
        main.prewarm = true;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = maxParticles;
        main.cullingMode = ParticleSystemCullingMode.Pause;

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        // カメラをロールさせても炎が横倒しにならないようにする。
        renderer.allowRoll = false;

        return ps;
    }

    private static void SetColorOverLifetime(ParticleSystem ps, GradientColorKey[] colorKeys, GradientAlphaKey[] alphaKeys)
    {
        var gradient = new Gradient();
        gradient.SetKeys(colorKeys, alphaKeys);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(gradient);
    }

    private static void SetSizeOverLifetime(ParticleSystem ps, AnimationCurve curve)
    {
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);
    }

    /// <summary>
    /// 回転速度を設定する。スクリプトから設定する角度はラジアン単位のため度から変換する
    /// (Inspector の表示は度で、この差はUnityの既知の非対称)。
    /// </summary>
    private static void SetRotationOverLifetime(ParticleSystem ps, float degreesPerSecond)
    {
        float rad = degreesPerSecond * Mathf.Deg2Rad;

        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-rad, rad);
    }

    private static void SetNoise(ParticleSystem ps, float strength, float frequency, float scrollSpeed, int octaves)
    {
        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = strength;
        noise.frequency = frequency;
        noise.scrollSpeed = scrollSpeed;
        noise.octaveCount = octaves;
        // 周波数に応じて強度を抑え、細かい揺らぎが暴れないようにする。
        noise.damping = true;
    }

    /// <summary>
    /// 煙が「ゆらゆらと立ち上る」揺らぎを設定する。
    ///
    /// 低周波・低スクロール速度で大きくゆっくり揺らすため、周波数に強度を比例させる
    /// `damping` は切る(有効のままだと低周波では揺れがほとんど出ない)。
    /// さらに軸を分離して**水平方向だけ強く揺らす**。Y方向まで同じ強度で揺らすと
    /// 上昇速度が波打ち、ゆらゆらではなく上下に痙攣する動きになる。
    /// </summary>
    private static void SetSwayNoise(ParticleSystem ps, float strength, float frequency, float scrollSpeed)
    {
        var noise = ps.noise;
        noise.enabled = true;
        noise.frequency = frequency;
        noise.scrollSpeed = scrollSpeed;
        noise.octaveCount = 1;
        noise.damping = false;
        noise.separateAxes = true;
        noise.strengthX = strength;
        noise.strengthY = strength * 0.2f;
        noise.strengthZ = strength;
    }

    private static void SetDrift(ParticleSystem ps, Vector2 drift, float speed)
    {
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        vel.x = new ParticleSystem.MinMaxCurve(drift.x * speed);
        vel.z = new ParticleSystem.MinMaxCurve(drift.y * speed);
    }

    // ------------------------------------------------------------------
    // マテリアルとテクスチャの生成(全エフェクトで共有)
    // ------------------------------------------------------------------

    private static void EnsureMaterials()
    {
        if (_flameMaterial != null) return;

        Shader shader = Shader.Find("Custom/FireParticle");
        if (shader == null)
        {
            Debug.LogWarning("FireEffectFactory: Custom/FireParticle が見つからないため Sprites/Default にフォールバックします。");
            shader = Shader.Find("Sprites/Default");
        }

        _flameMaterial = CreateMaterial(shader, BuildFlameTexture(), additive: true, renderQueue: FLAME_RENDER_QUEUE, softFade: 2.5f);
        _sparkMaterial = CreateMaterial(shader, BuildSparkTexture(), additive: true, renderQueue: SPARK_RENDER_QUEUE, softFade: 1.5f);
        _smokeMaterial = CreateMaterial(shader, BuildSmokeTexture(), additive: false, renderQueue: SMOKE_RENDER_QUEUE, softFade: 6f);
    }

    private static Material CreateMaterial(Shader shader, Texture2D texture, bool additive, int renderQueue, float softFade)
    {
        var mat = new Material(shader)
        {
            name = additive ? "FireParticleAdditive" : "FireParticleAlpha",
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = renderQueue,
        };

        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texture);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", texture);
        if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_SoftFadeDistance")) mat.SetFloat("_SoftFadeDistance", softFade);

        return mat;
    }

    /// <summary>
    /// 炎の粒テクスチャ。上に向かって細くなる涙型で、縁をノイズで崩して炎の舌に見せる。
    /// パーティクルはビューアラインかつロール禁止なので、この向き(下が根元)が保たれる。
    /// </summary>
    private static Texture2D BuildFlameTexture()
    {
        int size = FLAME_TEXTURE_SIZE;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            float halfWidth = (1f - Mathf.Pow(v, 1.6f)) * SmoothStep01(0f, 0.20f, v);

            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float ax = Mathf.Abs(u - 0.5f) * 2f;

                float wobble = (Fbm(u * 4f + 3.1f, v * 3f + 7.7f, 3) - 0.5f) * 0.22f;
                float w = Mathf.Max(halfWidth + wobble, 0f);
                float alpha = 1f - SmoothStep01(w * 0.35f, w, ax);

                float core = (1f - ax) * (1f - SmoothStep01(0.15f, 0.75f, v));
                Color rgb = Color.Lerp(new Color(1f, 0.78f, 0.45f), Color.white, Mathf.Clamp01(core));

                pixels[y * size + x] = new Color(rgb.r, rgb.g, rgb.b, Mathf.Clamp01(alpha));
            }
        }

        return FinalizeTexture(pixels, size, "FireFlameTexture");
    }

    /// <summary>煙の粒テクスチャ。放射状の減衰にfbmノイズを掛けた雲状の塊。</summary>
    private static Texture2D BuildSmokeTexture()
    {
        int size = SMOKE_TEXTURE_SIZE;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float radius = new Vector2((u - 0.5f) * 2f, (v - 0.5f) * 2f).magnitude;

                float n = Fbm(u * 3.3f + 13.7f, v * 3.3f + 5.2f, 4);
                float edge = 1f - SmoothStep01(0.25f, 1f, radius + (n - 0.5f) * 0.75f);
                float alpha = Mathf.Pow(Mathf.Clamp01(edge), 1.35f) * Mathf.Lerp(0.7f, 1f, n);

                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        return FinalizeTexture(pixels, size, "FireSmokeTexture");
    }

    /// <summary>火の粉テクスチャ。中心が白く飛んだ小さな光点。</summary>
    private static Texture2D BuildSparkTexture()
    {
        int size = SPARK_TEXTURE_SIZE;
        var pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float radius = new Vector2((u - 0.5f) * 2f, (v - 0.5f) * 2f).magnitude;
                float alpha = Mathf.Pow(Mathf.Clamp01(1f - radius), 3f);

                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        return FinalizeTexture(pixels, size, "FireSparkTexture");
    }

    private static Texture2D FinalizeTexture(Color32[] pixels, int size, string name)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        tex.SetPixels32(pixels);
        tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        return tex;
    }

    /// <summary>
    /// GLSL 相当の smoothstep。`Mathf.SmoothStep` は from→to を補間する別物なので使えない。
    /// </summary>
    private static float SmoothStep01(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-5f));
        return t * t * (3f - 2f * t);
    }

    private static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f;
        float amplitude = 0.5f;
        float frequency = 1f;
        float normalizer = 0f;

        for (int i = 0; i < octaves; i++)
        {
            sum += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
            normalizer += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return sum / Mathf.Max(normalizer, 1e-5f);
    }
}
