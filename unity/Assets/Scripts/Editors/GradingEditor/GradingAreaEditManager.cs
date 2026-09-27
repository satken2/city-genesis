using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 造成(陸地整地・埋立)の範囲を作図するマネージャ(STATE_EDIT_GRADING)。
///
/// 操作は資源エリアの作図と同じで、Placing(左クリックで頂点を追加)から始め、
/// 右クリックで形を閉じると Editing(頂点ドラッグ・辺クリックで挿入・右クリックで削除)に移る。
/// 形が変わるたびに edit_grading:update_shape で UI へ送る。種類(整地/埋立)・目標の高さ・
/// 見積もりの判定はバックエンドが行い、確定(POST /api/v1/grading)は UI が直接送る。
/// </summary>
public class GradingAreaEditManager : PolygonAreaEditManager
{
    protected override string OverlayCanvasName => "GradingAreaEditOverlayCanvas";

    // 道路・区画(青系)・資源エリア(オレンジ)・選択頂点(緑)・ホバー辺(黄)と見分けられるよう紫系で描く。
    protected override Color EdgeColor => new Color(0.8f, 0.4f, 0.95f);
    protected override Color VertexColor => new Color(0.88f, 0.62f, 0.98f);

    /// <summary>
    /// 共通吸着は既定で道路の中心線へ吸い付く。範囲がパスに掛かると発注で必ず拒否されるため掛けない
    /// (頂点は道路の縁の外側に置く必要がある)。
    /// </summary>
    protected override bool UseDragSnapping => false;

    /// <summary>
    /// Esc では抜けない。Unity だけが通常モードへ戻ると、ウィンドウは開いたまま表示プリセットも戻らない。
    /// 抜けるのはウィンドウを閉じたとき(Rust が STATE_NORMAL を送る)と確定時の edit_grading:finish だけ。
    /// </summary>
    protected override bool CancelOnEscape => false;

    /// <summary>
    /// 現在のポリゴンを edit_grading:update_shape で UI へ通知する。
    /// UI は finalized(形を閉じた後)のときだけ見積もりを取る。
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        IpcClient.Instance?.EmitEditGradingUpdateShape(polygon, finalized);
    }
}
