using System;
using UnityEngine;

/// <summary>
/// Ground appearance profiles for each ground cover type (Constants.GROUND_COVER_*).
/// TerrainManager selects the profile matching city ground_cover (GET /api/v1/city)
/// and passes identical values to terrain tiles, overview terrain, and outside-city background materials (shader: TerrainSurfaceAppearance.hlsl).
/// Adjust in Resources/Terrain/GroundCoverPalette.asset; if the asset or type is missing,
/// render with <see cref="GroundCoverProfile.CreateDefaults"/> values.
/// </summary>
[CreateAssetMenu(fileName = "GroundCoverPalette", menuName = "City Genesis/Ground Cover Palette")]
public class GroundCoverPalette : ScriptableObject
{
    public const string ResourcePath = "Terrain/GroundCoverPalette";

    /// <summary>Notification when Inspector values change, used to update terrain materials immediately during Play.</summary>
    public static event Action Changed;

    public GroundCoverProfile[] profiles = GroundCoverProfile.CreateDefaults();

    /// <summary>Profile for the specified type, or null if absent.</summary>
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
/// Appearance for one ground cover type. Colors use gamma-space values as in the Inspector; Material.SetColor converts them to linear.
/// Includes both pre-lighting surface properties (textures, procedural colors, relief, rock) and post-lighting overlays (large-scale shading and color correction).
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
    [Tooltip("岩が混ざり始める斜面の角度(度)")]
    [Range(0f, 90f)] public float rockStartAngle = 56.633f;
    [Tooltip("完全に岩になる斜面の角度(度)")]
    [Range(0f, 90f)] public float rockFullAngle = 68.284f;
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
    private static readonly int RockSlopeThresholdId = Shader.PropertyToID("_CliffSlopeThreshold");
    private static readonly int RockSlopeBlendId = Shader.PropertyToID("_CliffSlopeBlend");
    private static readonly int ShoreSandColorId = Shader.PropertyToID("_ShoreSandColor");

    /// <summary>
    /// Set type-specific values on terrain shader materials
    /// (Custom/DepthOffsetTerrainLit and Custom/DepthOffsetSimpleLitFar). TerrainLayer controls ground textures and smoothness
    /// (_BaseMap/_Smoothness for Far), so callers set these. Shared by TerrainManager and Tests/TerrainAppearance.
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
        // The shader measures slope as 1 - normalWS.y, so convert angles to that measure
        float rockStart = 1f - Mathf.Cos(rockStartAngle * Mathf.Deg2Rad);
        float rockFull = 1f - Mathf.Cos(rockFullAngle * Mathf.Deg2Rad);
        material.SetFloat(RockSlopeThresholdId, rockStart);
        material.SetFloat(RockSlopeBlendId, rockFull - rockStart);
        // Pass the sand replacement fraction in alpha (SetColor does not convert alpha to linear)
        Color shore = shoreSandColor;
        shore.a = shoreSand;
        material.SetColor(ShoreSandColorId, shore);
    }

    /// <summary>
    /// Defaults for five types. Grassland uses the values already tuned in the scene (GameScene.unity), preserving its prior appearance.
    /// The other four start from brightness values compatible with the same lighting and tone mapping, intended for refinement in the running game.
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
                rockStartAngle = 56.633f, rockFullAngle = 68.284f,
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
                rockStartAngle = 56.633f, rockFullAngle = 68.284f,
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
                rockStartAngle = 56.633f, rockFullAngle = 68.284f,
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
                rockStartAngle = 25f, rockFullAngle = 30f, // Snow slides off slopes steeper than about 30°, exposing dark rock
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
                rockStartAngle = 56.633f, rockFullAngle = 68.284f,
            },
        };
    }

    /// <summary>Defaults for the specified type. Null for unknown types.</summary>
    public static GroundCoverProfile CreateDefault(int groundCover)
    {
        foreach (GroundCoverProfile profile in CreateDefaults())
        {
            if (profile.groundCover == groundCover) return profile;
        }
        return null;
    }
}
