using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 建物・道路を「オブジェクト単位」で非表示にするフィルタ。
///
/// レイヤー単位の表示制御(VisualModeHandler の色モード・表示フラグ)とは直交する別系統で、
/// 非表示にする agent_id / path_id の集合をそのまま保持する。UI(Tauri)の建設プロジェクト
/// パネル・各詳細パネルが `view:set_object_filter` で集合ごと送ってくるものが唯一の入力。
///
/// **状態はこのセッション(Unityのプロセス)にしか存在しない。** バックエンドは表示・非表示を
/// 一切保持せず、セーブにも含めない(タグは保存されるが、そのタグを隠しているかは保存しない)。
/// UIから送られるのはタグ名ではなくIDの集合なので、グラフィック側はタグを知らなくてよい。
///
/// ストリーミング(bbox差分ロード)で後から生成されたオブジェクトにも効くよう、
/// 生成側(BuildingManager / PathManager)が生成直後に `IsHidden` を引いて初期状態を決める。
/// </summary>
public static class ObjectVisibilityFilter
{
    private static readonly HashSet<int> hiddenBuildings = new HashSet<int>();
    private static readonly HashSet<int> hiddenPaths = new HashSet<int>();

    /// <summary>非表示にしている建物の数。</summary>
    public static int HiddenBuildingCount => hiddenBuildings.Count;

    /// <summary>非表示にしている道路の数。</summary>
    public static int HiddenPathCount => hiddenPaths.Count;

    /// <summary>指定したオブジェクトがフィルタで非表示になっているか。</summary>
    public static bool IsHidden(int objectType, int id)
    {
        if (objectType == InputObjectType.BUILDING) return hiddenBuildings.Contains(id);
        if (objectType == InputObjectType.PATH) return hiddenPaths.Contains(id);
        return false;
    }

    /// <summary>
    /// 非表示集合を丸ごと置き換え、既に生成済みのオブジェクトへ反映する。
    /// 差分ではなく全置換なのは、UI側が「現在隠しているものの全集合」を常に計算できるため
    /// (追加・削除の取りこぼしで表示が食い違う余地をなくす)。
    /// </summary>
    public static void SetHidden(IEnumerable<int> buildingIds, IEnumerable<int> pathIds)
    {
        hiddenBuildings.Clear();
        hiddenPaths.Clear();
        if (buildingIds != null) foreach (int id in buildingIds) hiddenBuildings.Add(id);
        if (pathIds != null) foreach (int id in pathIds) hiddenPaths.Add(id);
        ApplyToExistingObjects();
    }

    /// <summary>非表示指定をすべて解除する(セッションのリセット時など)。</summary>
    public static void Clear()
    {
        if (hiddenBuildings.Count == 0 && hiddenPaths.Count == 0) return;
        hiddenBuildings.Clear();
        hiddenPaths.Clear();
        ApplyToExistingObjects();
    }

    /// <summary>現在の非表示集合をコピーして返す(UIへの状態通知用)。</summary>
    public static List<int> GetHiddenBuildings() => new List<int>(hiddenBuildings);

    /// <summary>現在の非表示集合をコピーして返す(UIへの状態通知用)。</summary>
    public static List<int> GetHiddenPaths() => new List<int>(hiddenPaths);

    /// <summary>
    /// 生成済みオブジェクトへ現在のフィルタを反映する。
    /// 対象の列挙は生成元のキャッシュ(BuildingManager / PathManager)が持っているため、
    /// シーン全体の走査はしない。
    /// </summary>
    private static void ApplyToExistingObjects()
    {
        if (BuildingManager.Instance != null) BuildingManager.Instance.RefreshVisibilityFilter();
        var pathManager = Object.FindAnyObjectByType<PathManager>();
        if (pathManager != null) pathManager.RefreshVisibilityFilter();
    }

    /// <summary>
    /// 生成直後のオブジェクトへフィルタを適用する。
    /// 遠景LODの建物は VisualModeObject を持たない(FarBuildingBatcher が結合するため)ので、
    /// FarBuildingMember がレイヤー設定とフィルタを合わせて適用し、結合バッチへ通知する。
    /// メンバーがまだない生成途中のオブジェクトは MeshRenderer を直接切り替える。
    /// </summary>
    public static void ApplyToObject(GameObject target, int objectType, int id)
    {
        if (target == null) return;
        bool hidden = IsHidden(objectType, id);
        var visual = target.GetComponent<VisualModeObject>();
        if (visual != null)
        {
            visual.SetFilterHidden(hidden);
            return;
        }
        var far = target.GetComponent<FarBuildingMember>();
        if (far != null)
        {
            far.ApplyVisualMode(VisualModeHandler.Instance?.GetMode(objectType) ?? VisualMode.MODE_NORMAL);
            return;
        }
        var renderer = target.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.enabled = !hidden;
        var collider = target.GetComponent<Collider>();
        if (collider != null) collider.enabled = !hidden;
    }
}
