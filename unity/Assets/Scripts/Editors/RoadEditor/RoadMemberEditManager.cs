using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GeneratedModels;

/// <summary>
/// 道路(名前付き道路)の区間編集モード(STATE_EDIT_ROAD_MEMBERS)の3D側を司るシングルトン。
///
/// 道路詳細パネルの「区間を編集」から開始し、所属区間を道路色の帯、直進方向の自動延長で
/// 追加候補になった区間を薄い帯で示す(描画は RoadFocusOverlay)。街路・高速道路の区間が
/// クリックされるたびに edit_road:toggle_path を UI へ送るだけで、所属の付け外しそのものは
/// UI が API(POST /roads/{id}/members)で行い、結果の全集合を edit_road:set_members で送り返す
/// (パスの結合相手の選択と同じ責務分担)。帯はその応答で更新するので、クリック直後には変わらない。
/// </summary>
public class RoadMemberEditManager : MonoBehaviour
{
    public static RoadMemberEditManager Instance { get; private set; }

    private readonly HashSet<int> members = new HashSet<int>();
    private readonly HashSet<int> preview = new HashSet<int>();
    private Color color = Color.white;
    private PathManager pathManager;

    public bool IsActive { get; private set; }
    public int RoadId { get; private set; } = Constants.VALUE_NONE;

    public static RoadMemberEditManager Ensure()
    {
        if (Instance == null) new GameObject("RoadMemberEditManager").AddComponent<RoadMemberEditManager>();
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>区間編集モードを開始する。pathIds は開始時点の所属区間。</summary>
    public void Begin(int roadId, Color roadColor, IEnumerable<int> pathIds)
    {
        IsActive = true;
        RoadId = roadId;
        color = roadColor;
        Replace(members, pathIds);
        preview.Clear();
        RoadFocusOverlay.Ensure().SetEditLayers(color, members, preview);
        Debug.Log($"RoadMemberEditManager: 区間編集モード開始 road={roadId}, members={members.Count}");
    }

    /// <summary>
    /// edit_road:set_members。所属区間と追加候補の全集合を置き換える。
    /// モード中でない・別の道路宛ての通知は、閉じかけたパネルからの遅れた応答なので無視する。
    /// </summary>
    public void SetMembers(int roadId, IEnumerable<int> pathIds, IEnumerable<int> previewIds)
    {
        if (!IsActive || roadId != RoadId) return;
        Replace(members, pathIds);
        Replace(preview, previewIds);
        RoadFocusOverlay.Ensure().SetEditLayers(color, members, preview);
    }

    /// <summary>モードを終了し、区間編集の帯を消す(道路詳細パネルの強調は残す)。</summary>
    public void End()
    {
        if (!IsActive) return;
        IsActive = false;
        RoadId = Constants.VALUE_NONE;
        members.Clear();
        preview.Clear();
        RoadFocusOverlay.Instance?.ClearEditLayers();
        TooltipManager.Instance?.Hide();
        Debug.Log("RoadMemberEditManager: 区間編集モードを終了しました");
    }

    private static void Replace(HashSet<int> target, IEnumerable<int> source)
    {
        target.Clear();
        if (source == null) return;
        foreach (int id in source) if (id > 0) target.Add(id);
    }

    /// <summary>道路に属せる種別(街路・高速道路)かどうか。</summary>
    public static bool IsNamedRoadType(int pathType)
    {
        return System.Array.IndexOf(Constants.PATH_TYPES_NAMED_ROAD, pathType) >= 0;
    }

    /// <summary>区間がクリックされた。街路・高速道路なら UI へ付け外しを依頼する。</summary>
    public void OnPathClicked(int pathId)
    {
        if (!IsActive) return;
        PathSummary path = GetPathManager()?.GetPathData(pathId);
        if (path == null) return;
        if (!IsNamedRoadType(path.path_type))
        {
            TooltipManager.Instance?.Show("道路に含められるのは街路と高速道路だけです");
            return;
        }
        bool shift = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
        IpcClient.Instance?.EmitRoadTogglePath(pathId, path.name ?? "", path.path_type, shift);
    }

    /// <summary>
    /// 区間にカーソルが乗った。操作の案内を出し、クリックできる区間なら true(呼び出し側がホバー色を付ける)。
    /// </summary>
    public bool OnPathHoverStart(int pathId)
    {
        if (!IsActive) return false;
        PathSummary path = GetPathManager()?.GetPathData(pathId);
        if (path == null) return false;
        if (!IsNamedRoadType(path.path_type))
        {
            TooltipManager.Instance?.Show("道路に含められるのは街路と高速道路だけです");
            return false;
        }
        TooltipManager.Instance?.Show(BuildHoverTooltip(pathId));
        RoadFocusOverlay.Instance?.SetHoverPath(pathId);
        return true;
    }

    public void OnPathHoverEnd(int pathId)
    {
        TooltipManager.Instance?.Hide();
        RoadFocusOverlay.Instance?.ClearHoverPath(pathId);
    }

    private string BuildHoverTooltip(int pathId)
    {
        if (members.Contains(pathId)) return "クリックでこの道路から外す";
        const string shiftHint = "\nShift+クリックで直進方向へまとめて追加";
        int otherRoadId = RoadManager.Instance != null ? RoadManager.Instance.GetRoadIdOfPath(pathId) : Constants.VALUE_NONE;
        if (otherRoadId > 0 && otherRoadId != RoadId && RoadManager.Instance.TryGetRoad(otherRoadId, out var other))
        {
            return $"クリックでこの道路へ付け替え(現在: {RoadManager.GetDisplayName(other)})" + shiftHint;
        }
        return "クリックでこの道路に追加" + shiftHint;
    }

    private PathManager GetPathManager()
    {
        if (pathManager == null) pathManager = FindAnyObjectByType<PathManager>();
        return pathManager;
    }
}
