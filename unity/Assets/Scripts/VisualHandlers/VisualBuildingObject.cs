using UnityEngine;

/// <summary>
/// 建物の表示モードに応じたオーバーレイ色・透明度の計算を担当するコンポーネント。
/// VisualBuildingStats から耐久度や家賃、容量（capacity）などのステータス値を読み取り、
/// ビジュアルモードに応じた色を VisualModeObject に提供する。
/// マテリアルの管理や描画の切り替えは VisualModeObject が一元して行うため、
/// このコンポーネントは IVisualModeHandler を実装せず、
/// VisualModeHandler への登録も行わない。
/// </summary>
public class VisualBuildingObject : MonoBehaviour
{
    private VisualBuildingStats stats;

    private void Start()
    {
        stats = GetComponent<VisualBuildingStats>();
    }

    /// <summary>
    /// 耐久度0.0の色（#ff4444）。UI凡例の meterStartColor と一致させる。
    /// </summary>
    private static readonly Color DurabilityMinColor = new Color(1f, 0.267f, 0.267f);

    /// <summary>
    /// 指定されたビジュアルモードに基づいてオーバーレイ色を計算して返す。
    /// 純色オーバーレイが不要なモードの場合は null を返す。
    /// </summary>
    public Color? GetOverlayColor(int mode)
    {
        if (stats == null) stats = GetComponent<VisualBuildingStats>();
        if (stats == null) return null;

        switch (mode)
        {
            case VisualModeBuilding.RENT:
                // 家賃: 0→緑, 10000→赤
                return Color.Lerp(Color.green, Color.red, Mathf.Clamp01(NullableToZero(stats.rent) / 10000f));
            case VisualModeBuilding.DURABILITY:
                // 耐久度: 0→赤(#ff4444), 1→白(#ffffff)
                return Color.Lerp(DurabilityMinColor, Color.white, Mathf.Clamp01(NullableToZero(stats.durability)));
            default:
                return null;
        }
    }

    /// <summary>
    /// 指定されたスペース種別モードにおいて、この建物がその種別のフロアを持っているかを返す。
    /// VisualBuildingStats の spaceTypes ビットマスクで判定する。
    /// スペース種別定数は2の累乗（SPACE_RESIDENCE=2, SPACE_RETAIL=4, ...）なので
    /// AND演算で直接ビットの有無を確認できる。
    /// </summary>
    public bool HasSpaceType(int spaceType)
    {
        if (stats == null) stats = GetComponent<VisualBuildingStats>();
        if (stats == null) return false;

        return (stats.spaceTypes & spaceType) != 0;
    }

    private float NullableToZero(float? v) => v.HasValue ? v.Value : 0f;
}