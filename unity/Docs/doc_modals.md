# 各種モーダルの位置について

モーダルを配置する箇所は以下の7種類
あるモーダルを表示するとき、場所が競合するモーダルは非表示にする。
場所が異なるモーダルは同時に表示できる。
dep.と書かれているモーダルは右に書かれているモーダルの子モーダル。親モーダルが閉じたら子も閉じる。

## 左端1
- 土地区画の詳細 (ParcelInfoPanel)
- パスの詳細 (PathInfoPanel)

## 左端2
- 建物の詳細 (BuildingInfoPanel) dep. ParcelInfoPanel

## 右端1
- 建設プロジェクト (PojectInfoPanel)
- 土地区画の作成 (ParcelPlanPanel)

## 右端2
- 建物の建設 (BuildingPlanPanel) dep. PojectInfoPanel
- 道路の敷設 (RoadPlanPanel) dep. PojectInfoPanel
- 線路の敷設 (TrackPlanPanel) dep. PojectInfoPanel

## 左下
- ビジュアル変更 (VisualPanel)

## 中央
- 予算の設定 (BudgetPanel)
- 税金の設定 (BudgetPanel)
- 金融政策の設定 (BudgetPanel)
- 統計 (BudgetPanel)
- 地区の設定 (BudgetPanel)
- ログ (LogPanel)
- セーブ/ロード (SaveDataPanel)

## オーバーレイ
- 確認ダイアログ (ConfirmDialog)

