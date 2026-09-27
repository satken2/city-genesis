using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 土地区画形状編集マネージャ。既存の土地区画ポリゴンの頂点を編集する。
///
/// 頂点ドラッグ・辺クリックで頂点挿入・右クリック削除・オーバーレイ描画といった
/// 「ポリゴンの頂点編集」は共通基底 <see cref="PolygonAreaEditManager"/> に集約されており、
/// 本クラスは保存済みポリゴンを読み込んでいきなりEditingフェーズから始める(頂点配置のPlacingは通らない)
/// ことと、「edit_parcel:update_shape でUIへ通知する」こと、編集中の元区画オブジェクトの表示制御だけを担う。
///
/// 確定(PATCH /parcels/{id})はUI(Tauri)側が直接行い、保存成功後に届く
/// `edit_parcel:finish_shape_edit` で CompleteEditMode が呼ばれて編集を終了する。
/// </summary>
public class ParcelShapeEditManager : PolygonAreaEditManager
{
    [Header("Parcel")]
    public ParcelManager parcelManager;

    /// <summary>編集対象の区画ID。編集中でなければ -1。</summary>
    private int editingParcelId = -1;

    protected override string OverlayCanvasName => "ParcelShapeEditOverlayCanvas";

    // 区画境界は道路際でも自由に調整できるよう、共有の吸着設定にかかわらず吸着しない。
    protected override bool UseDragSnapping => false;

    // 区画境界は道路と同系の青で描く(資源エリアの暖色と区別する)
    protected override Color EdgeColor => new Color(0.3f, 0.6f, 1f);
    protected override Color VertexColor => new Color(0.54f, 0.71f, 0.98f);

    protected override void Start()
    {
        base.Start();
        if (parcelManager == null)
        {
            parcelManager = FindAnyObjectByType<ParcelManager>();
        }
    }

    // =====================================================================
    // パブリックAPI: モード遷移
    // =====================================================================

    /// <summary>
    /// 形状編集モードを初期化する。
    /// 指定されたparcel_idとpolygon([x, z]列)から編集状態を構築し、Editingフェーズで開始する。
    /// </summary>
    public void InitializeEditMode(int parcelId, float[][] polygon)
    {
        Debug.Log($"ParcelShapeEditManager: 形状編集モード開始 parcel={parcelId}");

        var points = new List<Vector3>();
        if (polygon != null)
        {
            foreach (var pt in polygon)
            {
                if (pt == null || pt.Length < 2) continue;
                float x = pt[0];
                float z = pt[1];
                float y = Utils.GetGroundHeight(new Vector3(x, 1000f, z));
                points.Add(new Vector3(x, y, z));
            }
        }

        editingParcelId = parcelId;

        // 既存の区画オブジェクトを非表示にして、編集中のオーバーレイだけを見せる
        HideOriginalParcelObject(parcelId);

        InitializeEditingWithPolygon(points);
    }

    /// <summary>
    /// 編集モードを終了し、状態をクリアする。非表示にしていた元の区画オブジェクトも再表示する。
    /// </summary>
    public override void FinishEditMode()
    {
        base.FinishEditMode();
        ShowOriginalParcelObject(editingParcelId);
        editingParcelId = -1;
    }

    /// <summary>
    /// UI側でのポリゴン保存(PATCH /parcels/{id})成功後に呼ばれる編集終了処理。
    /// バックエンドへの送信自体はUI(Tauri)側が直接行うため、ここでは編集状態のクリアと
    /// 最新データの再取得・区画枠線の再描画のみを行う。
    /// </summary>
    public void CompleteEditMode()
    {
        FinishEditMode();

        // 差分再描画。version比較により変更のないオブジェクトの再描画はスキップされる。
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ReloadAllData();
        }

        // カメラが動いていなくても区画枠線が即座に更新されるよう、強制再描画を要求する。
        if (parcelManager != null)
        {
            parcelManager.ForceOutlineRedraw();
        }
    }

    // =====================================================================
    // IPC
    // =====================================================================

    /// <summary>
    /// 現在のポリゴンを edit_parcel:update_shape でUIへ通知する。
    /// 常にEditingフェーズなので finalized は使わない。
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditParcelUpdateShape(editingParcelId, polygon);
    }

    // =====================================================================
    // 既存区画オブジェクトの表示制御
    // =====================================================================

    private void HideOriginalParcelObject(int parcelId)
    {
        if (parcelManager == null) return;
        GameObject obj = parcelManager.GetParcelObject(parcelId);
        if (obj != null)
        {
            obj.SetActive(false);
        }
    }

    private void ShowOriginalParcelObject(int parcelId)
    {
        if (parcelManager == null) return;
        GameObject obj = parcelManager.GetParcelObject(parcelId);
        if (obj != null)
        {
            obj.SetActive(true);
        }
    }
}
