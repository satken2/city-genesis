using UnityEngine;

/// <summary>
/// 区画の統計値(VisualParcelStats)への参照を ParcelManager のデータ可視化ループへ渡すための軽量コンポーネント。
/// 区画の見た目(枠線・ハイライト・データ可視化の塗り)はすべて ParcelManager の OverlayMeshRenderer が一括で描くため、
/// 区画個体は VisualModeHandler に登録しない(区画数ぶんの登録・定期走査・モード切替の通知を避ける)。
/// 区画の MeshRenderer は生成時に無効化したままにする。
/// </summary>
public class VisualParcelObject : MonoBehaviour
{
    private VisualParcelStats stats;

    /// <summary>
    /// VisualParcelStats を返す。ParcelManager のデータ可視化ループ内で使用する。
    /// 初回アクセス時に GetComponent で取得してキャッシュする。
    /// </summary>
    public VisualParcelStats Stats
    {
        get
        {
            if (stats == null) stats = GetComponent<VisualParcelStats>();
            return stats;
        }
    }
}
