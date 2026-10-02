# 鉄道敷設・用地買収編

MetroCityの道路・区画・民間建物と既設の公共施設を使う。ホーム、駅舎、線路、車両基地はプレイヤーが作る。発電所・変電所は既設。買収区間の工期は代行しない。

## 1. ホームを置く

ID: `scenario4.platforms`

案内の定義は `tutorial.scenario4.step1`。完了条件は checks.pf_town, checks.pf_new。西は「高規格高架PF(相対式)」(462)、東は「複線鉄道PF(相対式)」(471)。西の完成後は東の開く・種別選択・敷設を案内し、パネルを戻る・閉じる操作で西に戻らない。再開時も保存済みの西ホームを確認して東から案内する。

## 2. 駅舎を置く

ID: `scenario4.stations`

案内の定義は `tutorial.scenario4.step2`。完了条件は checks.station_town, checks.station_new。

## 3. 買収区間を計画

ID: `scenario4.acquisition_plan`

案内の定義は `tutorial.scenario4.step3`。完了条件は checks.track。

## 4. 買収と病院による停止を確認

ID: `scenario4.negotiate`

案内の定義は `tutorial.scenario4.step4`。完了条件は checks.negotiating と checks.blocked。

## 5. 病院の撤去を指示

ID: `scenario4.demolish`

案内の定義は `tutorial.scenario4.step5`。完了条件は checks.demolition。

## 6. 買収区間を完成

ID: `scenario4.acquisition_complete`

案内の定義は `tutorial.scenario4.step6`。完了条件は checks.acquired_rail。民間用地の買収・病院の撤去・通常工事をエポックで進めて稼働中の線路を確認する。

## 7. 幹線道路の真上を通す

ID: `scenario4.road_rail`

案内の定義は `tutorial.scenario4.step7`。完了条件は checks.rail。高さ6mで幹線道路の中心上を通す。道路の用地の上空を使う区間では、民間区画の買収を避けられる。道路から外れて建物に掛かる区間には同じ免除はない。

## 8. 車両基地を建設

ID: `scenario4.depot`

案内の定義は `tutorial.scenario4.step8`。完了条件は checks.depot。

## 9. 曲線勾配の引き込み線

ID: `scenario4.branch`

案内の定義は `tutorial.scenario4.step9`。完了条件は checks.depot_rail。通常の編集ツールで曲線・中間点・高さ・制御点を調整する。原本の接続端子と本線合流点を結び、勾配と曲率の通常の検証に合格した曲線経路だけを認める。短い直線で急に高さを変えず、線形表示の違反箇所を見ながら距離と接線を調整する。

## 10. 既設発電所から給電

ID: `scenario4.power`

案内の定義は `tutorial.scenario4.step10`。完了条件は checks.depot_power。

## 11. 路線を開業

ID: `scenario4.line`

案内の定義は `tutorial.scenario4.step11`。完了条件は checks.line。

## 12. 車両調達と運行を確認

ID: `scenario4.operating`

案内の定義は `tutorial.scenario4.step12`。完了条件は checks.operating。両ホームに停車する運休していない路線の輸送力が正になれば合格。短い駅間では徒歩が選ばれるため、乗客数を必須条件にしない。

## 13. まとめ

ID: `scenario4.summary`

案内の定義は `tutorial.scenario4.step13`。
