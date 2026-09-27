# Unityチュートリアル

`TutorialRuntime` はTauriの現在の指示だけを適用する。シナリオ進行とAPI書き込みはTauriに置く。

- マーカーは描画物。実ノード、Collider、PathManagerの都市データへ登録しない。
- 道路制約のv1は地上・直線・始終点2点。水色の始点を左クリック、黄色の終点を右クリック。Yは地面相対0、受理半径はm。
- 共通入力・道路のクリック・ドラッグ・Ctrlメニュー・2D形状編集・確定の各入口で制約を守る。無効時は通常処理へ通す。
- `runId/revision` が古い指示を適用しない。終了済みの実行を再開しない。都市終了・リセットでは `Clear` する。
- 仕様は `docs/docs/programs/チュートリアル基盤.md`、検証は `Tests/Tutorial/run-unity.ps1`。
