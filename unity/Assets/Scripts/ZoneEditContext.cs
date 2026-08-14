using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 地区編集モードのツール。Ctrlラジアルメニュー(ZoneEditControlMenu)で切り替える。
/// - Create: 街路をクリックしてその街路を含む新規地区を作成する(色はツール選択時にUnityが決める)
/// - Expand: 右クリックで拡張先の地区を選び、左クリックした街路をその地区へ取り込む
/// - Merge:  右クリックで統合先の地区を選び、左クリックした地区を統合先へ統合する
/// </summary>
public enum ZoneEditTool
{
    Create,
    Expand,
    Merge
}

/// <summary>
/// 地区編集モードの状態を管理するシングルトン。
///
/// 操作モデルは3ツール共通で「右クリックで対象地区を選ぶ → 左クリックで適用」。
/// 選択中の地区(SelectedZoneId)とその色はカーソル脇のHUD(ZoneEditControlMenu)に常時表示され、
/// いま何に対して操作しているかが一目で分かるようにしてある。
///
/// 作成・拡張は3Dジェスチャで完結するためUnityが直接バックエンドへ送信する。
/// 統合だけは地区がひとつ消える不可逆操作なので、UI(Tauri)の確定専用ウィンドウ(zone_merge)に
/// 確定ボタンを出し、実際の送信はUI側が行う(Unityは統合対象をUIへ伝えて確定/取消の結果を受け取るだけ)。
/// </summary>
public class ZoneEditContext : MonoBehaviour
{
    public static ZoneEditContext Instance { get; private set; }

    /// <summary>
    /// 現在の編集ツール。ZoneEditControlMenu(Ctrlラジアル)で切り替える。
    /// 新規作成モードで入れば Create、既存編集モードで入れば Expand が初期値になる。
    /// </summary>
    public ZoneEditTool CurrentTool { get; private set; } = ZoneEditTool.Create;

    /// <summary>
    /// 新規地区プレビュー用のセンチネル値。
    /// 実際のzone_idの代わりにtempZoneIdに設定し、
    /// VisualModeObjectに「新規地区のプレビュー色」(PreviewColor)を適用させるために使う。
    /// </summary>
    public const int PREVIEW_SENTINEL = -1;

    /// <summary>
    /// 現在地区編集モードがアクティブかどうか。
    /// </summary>
    public bool IsActive => isActive;

    /// <summary>
    /// 拡張・統合の対象として選択中の地区ID。未選択は 0。
    /// 拡張ツールでは「取り込み先の地区」、統合ツールでは「統合先の地区」を意味する。
    /// </summary>
    public int SelectedZoneId => selectedZoneId;

    /// <summary>
    /// 新規地区プレビュー色。作成ツールを選んだ時点でUnityがランダム生成し、
    /// カーソル脇のHUDに提示したうえで、作成リクエストの color としてそのまま送る
    /// (提示した色と実際に作られる地区の色を一致させるため)。
    /// </summary>
    public Color PreviewColor => previewColor;

    /// <summary>
    /// UIの確定待ちになっている統合元の地区ID。0 は確定待ちなし。
    /// 確定パネルを閉じただけでは結果が返らないため、この状態で操作をブロックはしない
    /// (次の統合要求で上書きし、選択解除・モード終了でクリアする)。
    /// </summary>
    public int PendingMergeSourceZoneId => pendingMergeSourceZoneId;

    private bool isActive = false;
    private int selectedZoneId = 0;
    private Color previewColor = Color.cyan;
    private int pendingMergeSourceZoneId = 0;

    /// <summary>
    /// 適用直後の道路へ一時的に地区色を乗せた台帳(道路ID)。
    /// バックエンドの応答を待たずにクリックの手応えを出すための先行着色で、
    /// 送信結果(成功/失敗の両方)・モード終了時に ClearTempVisuals で元の色へ戻す。
    /// リロード任せにすると、送信失敗時は version が変わらず再描画されないため一時色が残る。
    /// </summary>
    private readonly HashSet<int> tempColoredPathIds = new HashSet<int>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// 編集ツールを設定する。ZoneEditControlMenu の項目選択から呼ばれる。
    /// 作成ツールへ切り替えたときは、これから作る地区の色をその場で決め直す
    /// (ツールを選び直すと色が変わる＝色を引き直せる、という操作感)。
    /// </summary>
    public void SetTool(ZoneEditTool tool)
    {
        CurrentTool = tool;
        if (tool == ZoneEditTool.Create)
        {
            previewColor = GenerateZoneColor();
        }
    }

    /// <summary>
    /// 新規作成モードに遷移する。
    /// パスをクリックした際に新規地区が作成される。
    /// </summary>
    public void EnterCreateMode()
    {
        isActive = true;
        selectedZoneId = 0;
        pendingMergeSourceZoneId = 0;
        CurrentTool = ZoneEditTool.Create;
        previewColor = GenerateZoneColor();
        ZoneManager.Instance?.SetEditOutlineMode(true);
    }

    /// <summary>
    /// 既存地区の編集(拡張)モードに遷移する。
    /// 地区詳細パネルから zone_id 付きで編集モードへ入った場合の初期状態。
    /// </summary>
    public void EnterEditMode(int zoneId)
    {
        isActive = true;
        pendingMergeSourceZoneId = 0;
        selectedZoneId = zoneId;
        // 既存地区を選択した=拡張ツールに切り替える(右クリック選択・zone_id付き遷移でも同じ)。
        CurrentTool = ZoneEditTool.Expand;
        ZoneManager.Instance?.SetEditOutlineMode(true);
        ZoneManager.Instance?.SetSelectedZone(zoneId);
    }

    /// <summary>
    /// 地区編集モードを終了する。
    /// </summary>
    public void Exit()
    {
        isActive = false;
        selectedZoneId = 0;
        pendingMergeSourceZoneId = 0;
        CurrentTool = ZoneEditTool.Create;
        ClearTempVisuals();
        ZoneManager.Instance?.ClearHoverFill();
        ZoneManager.Instance?.SetSelectedZone(0);
        ZoneManager.Instance?.SetEditOutlineMode(false);
        IpcClient.Instance?.EmitEditZoneSelected(0, "");
    }

    /// <summary>
    /// 指定された地区IDを選択中地区（拡張先・統合先）として設定し、UIに通知する。
    /// 右クリック(道路・区画・建物・地区の内部)から呼ばれる。zoneId &lt;= 0 の場合は選択解除。
    /// 拡張ツール・統合ツールのどちらで選んでも意味が変わるだけで選択の実体は同じなので、
    /// ツールは切り替えない(作成ツール中に選んだ場合のみ拡張ツールへ移る)。
    /// </summary>
    public void SelectZone(int zoneId)
    {
        pendingMergeSourceZoneId = 0;

        if (zoneId > 0)
        {
            isActive = true;
            selectedZoneId = zoneId;
            if (CurrentTool == ZoneEditTool.Create)
            {
                CurrentTool = ZoneEditTool.Expand;
            }
            var zoneManager = ZoneManager.Instance;
            string zoneName = zoneManager != null ? zoneManager.GetZoneName(zoneId) : "";
            zoneManager?.SetSelectedZone(zoneId);
            IpcClient.Instance?.EmitEditZoneSelected(zoneId, zoneName);
            Debug.Log($"ZoneEditContext: 地区 {zoneId} ({zoneName}) を選択しました");
        }
        else
        {
            selectedZoneId = 0;
            ZoneManager.Instance?.SetSelectedZone(0);
            IpcClient.Instance?.EmitEditZoneSelected(0, "");
            Debug.Log("ZoneEditContext: 選択を解除しました");
        }
    }

    /// <summary>
    /// 選択中地区の名前を返す(未選択・未ロードなら空文字)。カーソルHUDの表示に使う。
    /// </summary>
    public string GetSelectedZoneName()
    {
        if (selectedZoneId <= 0) return "";
        return ZoneManager.Instance != null ? ZoneManager.Instance.GetZoneName(selectedZoneId) : "";
    }

    /// <summary>
    /// 選択中地区の識別色を返す(未選択ならグレー)。カーソルHUDの色チップに使う。
    /// </summary>
    public Color GetSelectedZoneColor()
    {
        if (selectedZoneId <= 0) return new Color(0.5f, 0.5f, 0.5f, 1f);
        return ZoneManager.Instance != null
            ? ZoneManager.Instance.GetZoneColor(selectedZoneId)
            : Color.cyan;
    }

    /// <summary>
    /// パスをベースに新規地区を作成する（Createツールでの街路クリック）。
    /// 地区の識別色は、ツール選択時にカーソル脇へ提示した PreviewColor をそのまま使う。
    /// </summary>
    public void CreateZone(int pathId)
    {
        var zoneManager = ZoneManager.Instance;
        int count = zoneManager != null ? zoneManager.GetAllZones().Count + 1 : 1;
        string zoneName = $"Zone {count}";

        // 応答を待たずにクリックした街路をプレビュー色で塗り、操作の手応えを出す。
        ApplyTempZoneColor(pathId, PREVIEW_SENTINEL);

        StartCoroutine(CreateZoneCoroutine(new int[] { pathId }, zoneName));
    }

    /// <summary>
    /// クリックされた街路を選択中地区へ取り込む（Expandツール）。
    /// 1クリック＝1本の即時割当で、確定ボタンは持たない(3Dジェスチャ完結型)。
    /// </summary>
    public void ExpandZone(int pathId)
    {
        if (selectedZoneId <= 0) return;

        ApplyTempZoneColor(pathId, selectedZoneId);
        StartCoroutine(AssignPathsCoroutine(selectedZoneId, new int[] { pathId }));
    }

    /// <summary>
    /// クリックされた地区を選択中地区へ統合するようUIへ要求する（Mergeツール）。
    /// 地区がひとつ消える不可逆操作のため、Unityはここでは送信せず、
    /// UI(Tauri)の確定専用ウィンドウ(zone_merge)に確定ボタンを出させる。
    /// 実際の送信(POST /zones/{id}/merge)はUI側が行い、結果は OnMergeFinished で戻る。
    /// </summary>
    public void RequestMerge(int sourceZoneId)
    {
        if (selectedZoneId <= 0 || sourceZoneId <= 0) return;
        if (sourceZoneId == selectedZoneId)
        {
            TooltipManager.Instance?.Show("統合元と統合先が同じ地区です");
            return;
        }
        // 既に確定待ちがあっても上書きする。確定パネルはウィンドウごと開き直されて
        // 新しい統合内容に差し替わるため、古い要求を残す意味がない
        // (パネルを閉じただけでは結果が返らないので、ブロックすると詰む)。
        pendingMergeSourceZoneId = sourceZoneId;

        var zoneManager = ZoneManager.Instance;
        string targetName = zoneManager != null ? zoneManager.GetZoneName(selectedZoneId) : "";
        string sourceName = zoneManager != null ? zoneManager.GetZoneName(sourceZoneId) : "";
        IpcClient.Instance?.EmitEditZoneRequestMerge(selectedZoneId, targetName, sourceZoneId, sourceName);
        Debug.Log($"ZoneEditContext: 地区統合を要求 source={sourceZoneId} -> target={selectedZoneId}");
    }

    /// <summary>
    /// UI側の統合確定/取消の結果を受け取る(edit_zone:finish_merge)。
    /// merged=true ならバックエンドへの送信は完了しているので、地区・道路を再取得して描画へ反映する。
    /// </summary>
    public void OnMergeFinished(bool merged)
    {
        pendingMergeSourceZoneId = 0;
        if (merged)
        {
            ForceReloadZonesAndPaths();
        }
    }

    /// <summary>
    /// 適用直後の先行着色を元に戻す。送信結果(成功/失敗の両方)とモード終了時に呼ぶ。
    /// 送信失敗時は version が変わらずリロードが再描画しないため、明示的に戻すことが必須。
    /// </summary>
    public void ClearTempVisuals()
    {
        if (tempColoredPathIds.Count == 0) return;

        var pathManager = FindAnyObjectByType<PathManager>();
        if (pathManager != null)
        {
            foreach (int pathId in tempColoredPathIds)
            {
                GameObject go = pathManager.GetPathObject(pathId);
                go?.GetComponent<VisualModeObject>()?.ClearTempZoneId();
            }
        }
        tempColoredPathIds.Clear();
    }

    /// <summary>
    /// 指定した街路へ一時的な地区色(または新規地区のプレビュー色)を乗せ、台帳へ記録する。
    /// </summary>
    private void ApplyTempZoneColor(int pathId, int zoneId)
    {
        var pathManager = FindAnyObjectByType<PathManager>();
        GameObject go = pathManager != null ? pathManager.GetPathObject(pathId) : null;
        go?.GetComponent<VisualModeObject>()?.SetTempZoneId(zoneId);
        tempColoredPathIds.Add(pathId);
    }

    /// <summary>
    /// 地区の識別色をランダム生成する。
    /// 彩度・明度を固定して色相だけを振ることで、どの地区も同じトーンの鮮やかな色になる
    /// (バックエンド crud_zones._generate_zone_color と同じ配色ルール)。
    /// </summary>
    private static Color GenerateZoneColor()
    {
        return Color.HSVToRGB(Random.value, 0.65f, 0.90f);
    }

    /// <summary>
    /// 色を "#rrggbb" のHEX文字列へ変換する(バックエンドの color フィールド形式)。
    /// </summary>
    public static string ToHex(Color color)
    {
        return "#" + ColorUtility.ToHtmlStringRGB(color).ToLowerInvariant();
    }

    private IEnumerator AssignPathsCoroutine(int zoneId, int[] pathIds)
    {
        if (ZoneApiClient.Instance == null)
        {
            new GameObject("ZoneApiClient").AddComponent<ZoneApiClient>();
        }

        yield return ZoneApiClient.Instance.AssignPathsToZone(
            pathIds,
            zoneId,
            () => {
                Debug.Log($"ZoneEditContext: Paths assigned to zone {zoneId}: count={pathIds.Length}");
                // 先行着色を戻してからリロードで本来の地区色に置き換える。
                ClearTempVisuals();
                ForceReloadZonesAndPaths();
            },
            (error) => {
                Debug.LogError($"ZoneEditContext: Failed to assign paths to zone {zoneId}: {error}");
                TooltipManager.Instance?.Show("この道路は地区に追加できません");
                // 失敗時は version が変わらずリロードで再描画されないため、明示的に戻すことが必須。
                ClearTempVisuals();
                ForceReloadZonesAndPaths();
            }
        );
    }

    private IEnumerator CreateZoneCoroutine(int[] pathIds, string zoneName)
    {
        var request = new CreateZoneRequest
        {
            path_ids = pathIds,
            name = zoneName,
            color = ToHex(previewColor),
            allowed_bd_mask = null,
            limit_volume_ratio = null,
            limit_coverage_ratio = null,
            limit_height = null
        };

        if (ZoneApiClient.Instance == null)
        {
            new GameObject("ZoneApiClient").AddComponent<ZoneApiClient>();
        }

        yield return ZoneApiClient.Instance.CreateZone(request,
            (response) => {
                Debug.Log($"ZoneEditContext: Zone created: ID={response.zone.zone_id}");
                ClearTempVisuals();
                // 作成した新規地区を自動選択し、続けて拡張できるようにする
                SelectZone(response.zone.zone_id);
                ForceReloadZonesAndPaths();
            },
            (error, statusCode) => {
                Debug.LogError($"ZoneEditContext: Failed to create zone: {error}");
                TooltipManager.Instance?.Show("この道路では地区を作成できません");
                ClearTempVisuals();
            }
        );
    }

    private void ForceReloadZonesAndPaths()
    {
        // 道路・区画の差分更新(道路の zone_id 変更・区画の所属地区変更を反映)。
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }
        // 地区は version 差分に頼らず全地区を強制再描画する。
        // 拡張・統合では侵食された側の地区・消滅した地区も同時に変わるため、
        // 変更のあった地区だけを差分描画すると取りこぼす(ReloadAllData の地区更新も
        // isForce=false のため areZonesLoaded 済みだとスキップされる)。
        var zoneManager = ZoneManager.Instance ?? FindAnyObjectByType<ZoneManager>();
        if (zoneManager != null)
        {
            zoneManager.ReloadAllZonesForced();
        }
    }
}
