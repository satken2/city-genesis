# Vehicles

車両は描画専用で、経済シミュレーションや永続エンティティへ書き戻さない。仕様は `city-genesis/docs/docs/geometries/O_車両の走行描画.md`。

- `Vehicle` と `VehiclePolyline` は移動状態と距離補間。交差点は車線オフセット済みの点列なので二重に横へずらさない。端を越えたフレームの残り時間を捨てない。
- `VehicleManager` は道路スナップショット・分岐・密度調整・プール。ノード遷移で台数超過を理由にデスポーンしてはいけない。到着予約は交差点を跨ぎ、路肩寄せは道路内だけで行う。
- 全体枠は `VehiclePopulationBudget`。道路別の端数を切り上げず、合算後に整数化する。実在台数をObserveで返し、到着・補充の遅れを端数へ繰り越す。
- 台帳は0.25秒ごとに実体一覧から構築する。交差点中は移動先へ1回だけ数え、到着予約・フェード中も消えるまでは枠を占める。
- 車線と区画線は `RoadLaneLayout`（MeshGenerators/Paths/RoadPath.cs）を共有する。通行方向は `CityInfoStore`。交通量は型付き `PathSummary.path_traffic` を読み、utilizationで代用しない。
- 範囲候補は `GetPathsNearCircle` のセル逆引き。円内区間・発生・デスポーンはすべてrenderRadius+despawnMarginを使う。
- 回帰検証は `unity/Tests/Vehicles/run.ps1`。独立Unityで本編アセンブリを実行し、本編シーンを変更しない。

- 到着時は `BeginRoadsideExit` でその場の90度旋回→実寸の横移動を行い、歩道込みの道路外縁から3m先へ抜けた後だけフェードする。退出中は道路スナップショット更新で位置を戻さず、管理円外でも退出を完了させる。
