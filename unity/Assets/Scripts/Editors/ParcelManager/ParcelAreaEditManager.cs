using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 土地区画の「任意ポリゴン」作成マネージャ。
///
/// 土地区画作成モード(STATE_EDIT_PARCEL)で parcel_creation_mode が "manual" のときに
/// 使われる。既存の点選択・パス選択・区画分割(<see cref="ParcelEditorManager"/>)とは異なり、
/// 左クリックで頂点を打ち・右クリックで形状を仮確定し・頂点をドラッグして調整する
/// 「任意ポリゴンの作り方」で新規区画の輪郭を組み立てる。
///
/// 頂点配置・ドラッグ・オーバーレイ描画のロジックは天然資源エリア編集と完全に共通で、
/// 共通基底 <see cref="PolygonAreaEditManager"/> に集約されている。本クラスはそれを継承し、
/// 「完成したポリゴンを edit_parcel:update_creation_shape でUIへ通知する」ことと、区画境界と
/// 揃えた寒色系(青)の見た目だけを差し替える。
///
/// 確定(バックエンドへの POST /api/v1/parcels 送信。mode="manual")は UI(Tauri)側が
/// 直接行うため、このマネージャはポリゴン形状の組み立てとUIへの逐次通知のみを担当する。
/// </summary>
public class ParcelAreaEditManager : PolygonAreaEditManager
{
    protected override string OverlayCanvasName => "ParcelAreaEditOverlayCanvas";

    // 土地区画の境界表示(青系)に揃える。
    protected override Color EdgeColor => new Color(0.3f, 0.6f, 0.95f);
    protected override Color VertexColor => new Color(0.45f, 0.7f, 1f);

    /// <summary>
    /// Escは <see cref="ParcelEditorManager"/> が「作成モードは維持したまま選択をリセット」
    /// として処理する(描きかけのポリゴンを捨ててもう一度描き始める)。
    /// ここで基底の「編集キャンセル＝通常入力モードへ復帰」を走らせると、
    /// 作成モードのまま入力プリセットだけ通常に戻り、クリックが効かなくなる。
    /// </summary>
    protected override bool CancelOnEscape => false;

    /// <summary>
    /// 現在のポリゴンを edit_parcel:update_creation_shape でUIへ通知する。
    /// 土地区画作成パネルは頂点数3以上かつ仮確定済み(finalized)を確定可能条件とするため、
    /// polygon に加えて finalized も送る。
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditParcelUpdateCreationShape(polygon, finalized);
    }
}
