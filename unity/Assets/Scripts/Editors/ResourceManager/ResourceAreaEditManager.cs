using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 資源エリア編集マネージャ。
///
/// 頂点配置・ドラッグ・オーバーレイ描画といった「任意ポリゴンの作り方」は共通基底
/// <see cref="PolygonAreaEditManager"/> に集約されており、本クラスはそれを継承して
/// 「完成したポリゴンを edit_resource:update_shape でUIへ通知する」ことと、
/// オーバーレイの見た目(暖色系)だけを差し替える。
///
/// 既存の区画・道路と異なり、確定(バックエンドへの POST /api/v1/resources 送信)は
/// UI(Tauri)側が直接行う。このマネージャはポリゴン形状の組み立てとUIへの逐次通知のみを担当する。
/// </summary>
public class ResourceAreaEditManager : PolygonAreaEditManager
{
    protected override string OverlayCanvasName => "ResourceAreaEditOverlayCanvas";

    // 道路(青系)・区画境界(青系)と区別しやすいよう、資源エリアは暖色系(オレンジ)で描画する。
    protected override Color EdgeColor => new Color(0.95f, 0.65f, 0.2f);
    protected override Color VertexColor => new Color(0.95f, 0.75f, 0.35f);

    /// <summary>
    /// 現在のポリゴンを edit_resource:update_shape でUIへ通知する。
    /// 資源エリアはfinalizedを使わない(UIは頂点数3以上で確定可能)ため polygon のみ送る。
    /// </summary>
    protected override void EmitShape(List<List<float>> polygon, bool finalized)
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.EmitEditResourceUpdateShape(polygon);
    }
}
