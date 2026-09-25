using System;
using UnityEngine;

/// <summary>
/// 地表種別(Constants.GROUND_COVER_*)ごとの地表の見た目の一覧。
/// TerrainManager が都市の地表種別(GET /api/v1/city の ground_cover)に対応するプロファイルを選び、
/// 地形タイル・超遠景・都市外背景のマテリアルへ同じ値を渡す(シェーダーは TerrainSurfaceAppearance.hlsl)。
/// 調整は Resources/Terrain/GroundCoverPalette.asset で行い、アセットが無い・種別が欠けている場合は
/// <see cref="GroundCoverProfile.CreateDefaults"/> の値で描く。
/// </summary>
[CreateAssetMenu(fileName = "GroundCoverPalette", menuName = "City Genesis/Ground Cover Palette")]
public class GroundCoverPalette : ScriptableObject
{
    public const string ResourcePath = "Terrain/GroundCoverPalette";

    /// <summary>Inspector で値を変えたときの通知。Play中の地形マテリアルへ即座に反映させるために使う。</summary>
    public static event Action Changed;

    public GroundCoverProfile[] profiles = GroundCoverProfile.CreateDefaults();

    /// <summary>指定種別のプロファイル。無ければ null。</summary>
    public GroundCoverProfile Find(int groundCover)
    {
        if (profiles == null) return null;
        foreach (GroundCoverProfile profile in profiles)
        {
            if (profile != null && profile.groundCover == groundCover) return profile;
        }
        return null;
    }

    private void Reset()
    {
        profiles = GroundCoverProfile.CreateDefaults();
    }

    private void OnValidate()
    {
        Changed?.Invoke();
    }
}

/// <summary>
/// 1つの地表種別の見た目。色は Inspector と同じガンマ空間の値で持ち、Material.SetColor がリニアへ変換する。
/// 照明前の下地(テクスチャ・手続きの色・凹凸・岩)と、照明後の色被せ(大きな濃淡・色補正)の両方を持つ。
/// </summary>
[Serializable]
public class GroundCoverProfile
{
    [Tooltip("地表種別(Constants.GROUND_COVER_*)")]
    public int groundCover = Constants.GROUND_COVER_GRASSLAND;

    [Header("Ground Texture")]
    [Tooltip("Resources 基準の地面テクスチャのパス。見つからなければ下地色Aの単色で描く")]
    public string textureResource = "";
    [Tooltip("テクスチャ1枚が覆う大きさ(m)")]
    public Vector2 textureTileSize = new Vector2(10f, 10f);
    [Tooltip("テクスチャの色をそのまま混ぜる割合。0 なら色を持ち込まず、明るさの濃淡だけを下地色へ掛ける")]
    [Range(0f, 1f)] public float textureColorWeight = 1f;
    [Tooltip("テクスチャの平均輝度(リニア)。濃淡を掛けるときの基準で、これより明るい画素が下地色を明るくする")]
    public float textureLuminance = 0.25f;
    [Tooltip("濃淡を掛けるときの強調倍率。砂のように色の差が小さいテクスチャで上げる")]
    public float textureContrast = 1f;
    [Tooltip("地面のスムースネス")]
    [Range(0f, 1f)] public float smoothness = 0.15f;

    [Header("Procedural Ground")]
    [Tooltip("手続きの下地色とテクスチャの混合率。1 でテクスチャの寄与が無くなる")]
    [Range(0f, 1f)] public float proceduralBlend = 0.85f;
    [Tooltip("下地色AとBを切り替える大きなパッチの周期(m)")]
    public float proceduralScale = 40f;
    [Tooltip("下地色A(ベース)")]
    public Color groundColorA = Color.white;
    [Tooltip("下地色B(大きなパッチ)")]
    public Color groundColorB = Color.white;
    [Tooltip("下地色C(細かい斑)")]
    public Color groundColorC = Color.white;

    [Header("Surface Relief")]
    [Tooltip("平地の細かい凹凸の法線の強度")]
    [Range(0f, 1f)] public float detailNormalStrength = 0.25f;
    [Tooltip("風紋(一方向に並ぶ細かい波)の法線の強度。0 で無効。カメラから約90mより遠くでは描かない")]
    [Range(0f, 1f)] public float rippleStrength = 0f;
    [Tooltip("風紋の波長(m)")]
    public float rippleWavelength = 0.6f;

    [Header("Macro Variation (after lighting)")]
    [Range(0f, 0.5f)] public float macroVariationStrength = 0.12f;
    public Color macroVariationTint = Color.white;

    [Header("Color Grade (after lighting)")]
    [Range(0f, 1f)] public float colorGradeStrength = 0.2f;
    [Tooltip("低地のティント(乗算。1付近で明度を保つ)")]
    public Color lowColor = Color.white;
    [Tooltip("高地のティント")]
    public Color highColor = Color.white;
    [Tooltip("急斜面のティント")]
    public Color slopeColor = Color.white;

    [Header("Rock And Shore")]
    [Tooltip("崖・波打ち際の岩の色に掛ける色。白で岩テクスチャのまま")]
    [ColorUsage(false, true)] public Color rockTint = Color.white;
    [Tooltip("波打ち際の砂の色")]
    public Color shoreSandColor = new Color(0.80f, 0.72f, 0.56f, 1f);
    [Tooltip("低地(波打ち際・浅い海底)の岩を砂へ置き換える割合。0 で岩のまま、1 で砂浜")]
    [Range(0f, 1f)] public float shoreSand = 0f;

    private static readonly int MacroVariationStrengthId = Shader.PropertyToID("_TerrainMacroVariationStrength");
    private static readonly int MacroVariationTintId = Shader.PropertyToID("_TerrainMacroVariationTint");
    private static readonly int ColorGradeStrengthId = Shader.PropertyToID("_TerrainColorGradeStrength");
    private static readonly int LowColorId = Shader.PropertyToID("_TerrainLowColor");
    private static readonly int HighColorId = Shader.PropertyToID("_TerrainHighColor");
    private static readonly int SlopeColorId = Shader.PropertyToID("_TerrainSlopeColor");
    private static readonly int DetailNormalStrengthId = Shader.PropertyToID("_TerrainDetailNormalStrength");
    private static readonly int RippleStrengthId = Shader.PropertyToID("_GroundRippleStrength");
    private static readonly int RippleWavelengthId = Shader.PropertyToID("_GroundRippleWavelength");
    private static readonly int ProceduralBlendId = Shader.PropertyToID("_ProceduralGroundBlend");
    private static readonly int ProceduralScaleId = Shader.PropertyToID("_ProceduralGroundScale");
    private static readonly int GroundColorAId = Shader.PropertyToID("_TerrainGroundColorA");
    private static readonly int GroundColorBId = Shader.PropertyToID("_TerrainGroundColorB");
    private static readonly int GroundColorCId = Shader.PropertyToID("_TerrainGroundColorC");
    private static readonly int TextureColorWeightId = Shader.PropertyToID("_GroundTextureColorWeight");
    private static readonly int TextureLuminanceId = Shader.PropertyToID("_GroundTextureLuminance");
    private static readonly int TextureContrastId = Shader.PropertyToID("_GroundTextureContrast");
    private static readonly int RockTintId = Shader.PropertyToID("_CliffRockTint");
    private static readonly int ShoreSandColorId = Shader.PropertyToID("_ShoreSandColor");

    /// <summary>
    /// 地形シェーダー(Custom/DepthOffsetTerrainLit と Custom/DepthOffsetSimpleLitFar)のマテリアルへ、
    /// この種別で変わる値を設定する。地面テクスチャとスムースネスは TerrainLayer(遠景は _BaseMap/_Smoothness)が
    /// 受け持つので、呼び出し側が設定する。本編の TerrainManager と Tests/TerrainAppearance が共用する。
    /// </summary>
    public void ApplyTo(Material material)
    {
        material.SetFloat(MacroVariationStrengthId, macroVariationStrength);
        material.SetColor(MacroVariationTintId, macroVariationTint);
        material.SetFloat(ColorGradeStrengthId, colorGradeStrength);
        material.SetColor(LowColorId, lowColor);
        material.SetColor(HighColorId, highColor);
        material.SetColor(SlopeColorId, slopeColor);
        material.SetFloat(DetailNormalStrengthId, detailNormalStrength);
        material.SetFloat(RippleStrengthId, rippleStrength);
        material.SetFloat(RippleWavelengthId, Mathf.Max(0.05f, rippleWavelength));
        material.SetFloat(ProceduralBlendId, proceduralBlend);
        material.SetFloat(ProceduralScaleId, Mathf.Max(1f, proceduralScale));
        material.SetColor(GroundColorAId, groundColorA);
        material.SetColor(GroundColorBId, groundColorB);
        material.SetColor(GroundColorCId, groundColorC);
        material.SetFloat(TextureColorWeightId, textureColorWeight);
        material.SetFloat(TextureLuminanceId, Mathf.Max(0.001f, textureLuminance));
        material.SetFloat(TextureContrastId, textureContrast);
        material.SetColor(RockTintId, rockTint);
        // 砂へ置き換える割合はαで渡す(SetColor はαをリニア変換しない)
        Color shore = shoreSandColor;
        shore.a = shoreSand;
        material.SetColor(ShoreSandColorId, shore);
    }

    /// <summary>
    /// 5種の既定値。草原はシーン(GameScene.unity)で調整済みだった値そのもので、変更前の見た目を保つ。
    /// ほかの4種は草原と同じ照明・トーンマップの下で破綻しない明るさを起点にした初期値で、実機で詰める前提。
    /// </summary>
    public static GroundCoverProfile[] CreateDefaults()
    {
        const string grassTexture = "grass-2500-mm-architextures";
        const string sandTexture = "Textures/ground-sand-albedo";
        const string dryGrassTexture = "Textures/ground-dry-grass-albedo";
        return new[]
        {
            new GroundCoverProfile
            {
                groundCover = Constants.GROUND_COVER_GRASSLAND,
                textureResource = grassTexture, textureTileSize = new Vector2(10f, 10f),
                textureColorWeight = 1f, textureLuminance = 0.264f, textureContrast = 1f, smoothness = 0.15f,
                proceduralBlend = 0.84957576f, proceduralScale = 40f,
                groundColorA = new Color(0.20203808f, 0.3301887f, 0.03893734f),
                groundColorB = new Color(0.15074529f, 0.26415092f, 0.051085785f),
                groundColorC = new Color(0.3477889f, 0.4056604f, 0.109069064f),
                detailNormalStrength = 0.25f, rippleStrength = 0f, rippleWavelength = 0.6f,
                macroVariationStrength = 0.28f, macroVariationTint = new Color(0.45633167f, 0.5754717f, 0.33388218f),
                colorGradeStrength = 0.22f,
                lowColor = new Color(0.3660866f, 0.49056602f, 0.26148093f),
                highColor = new Color(0.90f, 0.92f, 0.98f),
                slopeColor = new Color(0.55f, 0.40f, 0.25f),
                rockTint = Color.white, shoreSandColor = new Color(0.80f, 0.72f, 0.56f), shoreSand = 0f,
            },
            new GroundCoverProfile
            {
                groundCover = Constants.GROUND_COVER_DESERT,
                textureResource = sandTexture, textureTileSize = new Vector2(10f, 10f),
                textureColorWeight = 0f, textureLuminance = 0.244f, textureContrast = 3f, smoothness = 0.10f,
                proceduralBlend = 0.75f, proceduralScale = 60f,
                groundColorA = new Color(0.66f, 0.51f, 0.31f),
                groundColorB = new Color(0.57f, 0.41f, 0.25f),
                groundColorC = new Color(0.76f, 0.63f, 0.44f),
                detailNormalStrength = 0.12f, rippleStrength = 0.35f, rippleWavelength = 0.6f,
                macroVariationStrength = 0.18f, macroVariationTint = new Color(1.00f, 0.88f, 0.72f),
                colorGradeStrength = 0.15f,
                lowColor = new Color(1.00f, 0.95f, 0.88f),
                highColor = new Color(0.96f, 0.92f, 0.88f),
                slopeColor = new Color(0.86f, 0.66f, 0.50f),
                rockTint = new Color(1.12f, 0.92f, 0.74f), shoreSandColor = new Color(0.78f, 0.68f, 0.50f), shoreSand = 1f,
            },
            new GroundCoverProfile
            {
                groundCover = Constants.GROUND_COVER_LUSH,
                textureResource = grassTexture, textureTileSize = new Vector2(10f, 10f),
                textureColorWeight = 1f, textureLuminance = 0.264f, textureContrast = 1f, smoothness = 0.12f,
                proceduralBlend = 0.85f, proceduralScale = 30f,
                groundColorA = new Color(0.09f, 0.22f, 0.05f),
                groundColorB = new Color(0.06f, 0.16f, 0.05f),
                groundColorC = new Color(0.16f, 0.30f, 0.08f),
                detailNormalStrength = 0.35f, rippleStrength = 0f, rippleWavelength = 0.6f,
                macroVariationStrength = 0.25f, macroVariationTint = new Color(0.40f, 0.62f, 0.40f),
                colorGradeStrength = 0.22f,
                lowColor = new Color(0.34f, 0.50f, 0.32f),
                highColor = new Color(0.86f, 0.92f, 0.96f),
                slopeColor = new Color(0.50f, 0.42f, 0.30f),
                rockTint = new Color(0.86f, 0.94f, 0.82f), shoreSandColor = new Color(0.80f, 0.72f, 0.56f), shoreSand = 0f,
            },
            new GroundCoverProfile
            {
                groundCover = Constants.GROUND_COVER_SNOW,
                textureResource = sandTexture, textureTileSize = new Vector2(16f, 16f),
                textureColorWeight = 0f, textureLuminance = 0.244f, textureContrast = 1.5f, smoothness = 0.35f,
                proceduralBlend = 0.88f, proceduralScale = 50f,
                groundColorA = new Color(0.83f, 0.87f, 0.92f),
                groundColorB = new Color(0.70f, 0.77f, 0.87f),
                groundColorC = new Color(0.90f, 0.92f, 0.95f),
                detailNormalStrength = 0.10f, rippleStrength = 0.10f, rippleWavelength = 1.2f,
                macroVariationStrength = 0.10f, macroVariationTint = new Color(0.90f, 0.94f, 1.00f),
                colorGradeStrength = 0.18f,
                lowColor = new Color(0.97f, 0.98f, 1.00f),
                highColor = new Color(0.92f, 0.95f, 1.00f),
                slopeColor = new Color(0.72f, 0.74f, 0.80f),
                rockTint = new Color(0.78f, 0.80f, 0.85f), shoreSandColor = new Color(0.80f, 0.72f, 0.56f), shoreSand = 0f,
            },
            new GroundCoverProfile
            {
                groundCover = Constants.GROUND_COVER_SEMI_ARID,
                textureResource = dryGrassTexture, textureTileSize = new Vector2(3f, 3f),
                textureColorWeight = 0.4f, textureLuminance = 0.327f, textureContrast = 1f, smoothness = 0.12f,
                proceduralBlend = 0.70f, proceduralScale = 25f,
                groundColorA = new Color(0.62f, 0.53f, 0.30f),
                groundColorB = new Color(0.30f, 0.33f, 0.17f),
                groundColorC = new Color(0.72f, 0.64f, 0.48f),
                detailNormalStrength = 0.25f, rippleStrength = 0f, rippleWavelength = 0.6f,
                macroVariationStrength = 0.22f, macroVariationTint = new Color(0.95f, 0.84f, 0.62f),
                colorGradeStrength = 0.18f,
                lowColor = new Color(0.98f, 0.92f, 0.80f),
                highColor = new Color(0.92f, 0.92f, 0.95f),
                slopeColor = new Color(0.80f, 0.66f, 0.52f),
                rockTint = new Color(1.05f, 0.99f, 0.90f), shoreSandColor = new Color(0.80f, 0.72f, 0.56f), shoreSand = 1f,
            },
        };
    }

    /// <summary>指定種別の既定値。未知の種別は null。</summary>
    public static GroundCoverProfile CreateDefault(int groundCover)
    {
        foreach (GroundCoverProfile profile in CreateDefaults())
        {
            if (profile.groundCover == groundCover) return profile;
        }
        return null;
    }
}
