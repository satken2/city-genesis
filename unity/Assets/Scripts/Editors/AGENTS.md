# Editors フォルダ

3Dビュー内で道路・建物・土地区画・地区・資源エリア・造成範囲・公共交通・プロップを編集するときの状態管理(形状の組み立て、プレビュー描画、UIへの逐次通知、確定後のリセット)を担う。マウス入力は`InputHandlers/`が受け、入力モードに応じてここへ委譲される。

## 確定送信の契約

- `RoadEditor/RoadPlanManager.Tutorial.cs` はチュートリアルの直線制約との接点(次に置く点の高さもここで揃える)。マーカー・吸着・許可判定は `Tutorial/TutorialRuntime.cs` にまとめる。3D入力、Ctrlメニュー、2D形状変更と確定通知に同じ制約を適用し、シナリオIDを道路エディタで分岐しない。

- パネル確定型(道路・建物・区画・資源・造成・公共交通・プロップ・結合/分割・区間編集)の確定はUI(Tauri)が直接バックエンドへ送る。ここは`edit_*:update_*`で形状を逐次通知し、成功後の`edit_*:finish`系(または`session:reload`)でリセットするだけ。
- 3Dジェスチャ完結型(地区の作成・拡張、接続端子のドラッグ移動)だけはUnityが直接送信する。地区の統合は不可逆なのでUIの確認ウィンドウを経由する。
- ローカル判定(交差・川/海・湖の横断・線形・干渉)は表示用の推定で、権威はバックエンド(`/paths/validate`等)。ローカル線形推定で`can_confirm`を拒否しない。例外はロープウェイの折れ角(地形に依存せず確定的なので直接禁止)。
- 詳細は`docs/programs/IPC連携の整理.md`の「確定送信の責務分担」「グラフィックプログラムのステート」。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `RoadEditor/RoadPlanManager*.cs` | 道路・線路・インフラの敷設と既存パスの形状編集(Inactive→Placing→Editing)。partial: `ParallelSnap`・`Earthwork`・`ShapeSync`(2Dパネル同期)・`RouteProposal`・`AlignmentLabels`/`NodeLabels` |
| `RoadEditor/RoadRenderer*.cs`、`RoadVertex.cs`、`RoadOverlayHitDetector.cs` | プレビューメッシュ(詳細区間+操作中の帯=`Sections`)、頂点表示、既存道路へのヒット判定 |
| `RoadEditor/RoadParallelSnapGeometry.cs` | 並行吸着のXZ射影計算 |
| `RoadEditor/RoadEarthworkPreview.cs` | `/paths/earthwork-preview`の`path_profiles`(造成後の絶対標高)を取得するだけ。切土・盛土の体積は表示しない |
| `RoadEditor/RoadEditControlMenu.cs` | 道路編集のCtrlラジアルメニュー+カーソルHUD。曲線/直線と吸着フラグの切替はここだけ |
| `RoadEditor/PathTopologyEditManager.cs` | 既存パスの結合相手・分割点の選択。候補の前段フィルタだけ持ち、残りの条件はバックエンドのdry_run |
| `RoadEditor/RoadMemberEditManager.cs` | 名前付き道路の区間編集。クリックを`edit_road:toggle_path`で知らせるだけで所属は書き換えない |
| `BuildingEditor/` | 建物建設。`BuildingPlanManager`(区画配置)、`.Placement`(自由/道路沿い配置)、`BuildingPlacementGeometry`、`BuildingEditControlMenu`(基底に乗らない独自実装)、`EffectRangeOverlay` |
| `ParcelManager/` | 土地区画の作成・分割・形状編集。作成方法の真実源は`ParcelEditorManager.CurrentMode`(モード表`Modes`)。他に`ParcelEditControlMenu`・`ParcelPlanRenderer`・`ParcelShapeEditManager` |
| `ResourceManager/ResourceAreaEditManager.cs` | 天然資源エリアの新規作成(テンプレート作成専用) |
| `GradingEditor/GradingAreaEditManager.cs` | 造成(陸地整地・埋立)の範囲の作図(`STATE_EDIT_GRADING`)。`edit_grading:update_shape`で`{polygon, finalized}`を送るだけで、種類・目標の高さ・見積もりはバックエンドが決める |
| `TransitEditor/TransitPlanManager.cs` | 公共交通の路線設置・経路編集。巡回点を並べるだけで制御点・標高は持たない。経路プレビューは`/transit/lines/route-preview` |
| `ZoneEditor/ZoneEditControlMenu.cs` | 地区編集の3ツール切替。状態本体は`Scripts/ZoneEditContext.cs` |
| `PropEditor/PropPlacementManager.cs` | プロップの個別配置・範囲散布プレビュー(`PropGeometry`/`PropManager.CanPreview`を永続描画と共用) |
| `ConnectorEditManager.cs` | 接続端子ノードのマーカー表示とドラッグ移動(`STATE_EDIT_CONNECTOR`)。実移動とPATCHは`NodeInputHandler`へ委譲 |
| `PolygonAreaEditManager.cs` | 任意ポリゴン編集の共通基底(資源エリア・造成範囲・区画形状編集)。Placing中は次の左クリックで頂点が置かれる位置にポインタマーカー(`DrawPlacingMarker`)を出す |
| `EditControlMenuBase.cs` | Ctrlラジアルメニュー+カーソルHUDドライバの共通基底(道路・地区・区画) |
| `PlacementInput.cs` | 配置エディタ共用の入力部品(`IsBlocked`・中ボタン回転・キー長押しリピート) |

## 必ず守る規約

- `RoadEditor/RailJunctionGeometry.cs` は線路の元のベジエへの射影と解析接線を担当し、`RoadPlanManager.RailJunctions.cs` が単線/複線のハンドル整列で使用する。自動端点ハンドルは直線延長吸着の設定によらず整列し、手動ハンドルは保持する。分岐・合流は完成済み本線だけ、単純延長は未完成でも許可する。接線とポイントの共通平面は `/paths/validate` の干渉結果で検証する(N文書「敷設時の分岐・合流制約」)。

- 新しいポリゴン編集・Ctrlメニュー・配置キー操作は上の共通基底/部品を使い、各マネージャに複製しない。
- `PolygonAreaEditManager`の頂点ドラッグは`InputHandler`が地形との交点から求めた`worldPos`のXZを使い、地形高さを適用する。固定深度の`ScreenToWorldPoint`で逆投影すると斜め視点でカーソルからずれるため使わない。共通吸着は`UseDragSnapping`で制御し、`ParcelShapeEditManager`と`GradingAreaEditManager`は常にfalse。区画形状編集と造成範囲の作図は頂点の配置・移動・挿入とも吸着なし固定で、共有吸着フラグやグリッド表示には依存しない(造成は頂点が道路の中心線に吸い付くと範囲がパスに掛かり、発注で必ず拒否される)。
- `PolygonAreaEditManager`のPlacingマーカーは、`TerrainInputHandler`が渡す`OnTerrainHover`の位置を`UpdateOverlay`でスクリーン座標の円(`pointCircleRadius`×1.2、`VertexColor`)として描く(`RoadPlanManager`のようなワールド描画ではない)。位置は`Utils.GetGroundHeight`へ射影するので海上は海底側に出る(頂点の円と同じ)。`InputHandler`は入力ゲート(UI上・カメラドラッグ・ラジアルメニュー等)で`onHoverEnd`を呼び、`TerrainInputHandler`がそれを`OnTerrainHoverEnd`へ中継して消す。`onHover`はレイが変わらない静止フレームには届かないので、フレーム番号での失効判定は使えない。Editingでは出さない。
- 曲線/直線・吸着フラグ・区画の作成方法の真実源はUnity側(ラジアルメニュー)。ホーム設置側・複線のオフセット/進行方向・駅舎の`HouseEnds`/`PassageMode`・矩形区画の寸法はUIパネルが真実源。標高・盛土だけは双方向同期(`edit_path:change_node`⇔`select_node`)。
- 頂点の移動・標高変更は必ず`RoadPlanManager.MoveAnchorPosition`を、制御点(ハンドル)の移動は`MoveControlXZ`を通す(手動制御点の平行移動・高さ固定の解除・制御点の再計算を一箇所で行う)。2Dパネルの`shape_command`も同じ経路に委譲し、吸着は通さない。
- 制御点の手動フラグ`control_*_manual`はXZだけを守る。高さは`RecomputeAllControlPoints`がアンカーの自動接線の縦断勾配×水平距離で毎回決め直す。高さを保つのは`control_height_manual`(経路提案の結果と、再開時に保存値が規則から外れた制御点)だけで、そのアンカーか両隣を動かすと解除する。ハンドルの高さを固定すると、ノードの高さをどう変えてもそのノードの勾配が変わらず、左右のハンドル長の差で縦断が折れる。
- 制御点はエディタ内では絶対標高、保存・IPCでは所属アンカー直下の地盤基準の相対Y(`ControlToRelativeList`⇔`RestoreControl`)。
- 造成後の縦断(`path_profiles`)は操作停止後の3Dモデルにだけ使い、操作中はローカル近似。応答は要求キー一致時のみ採用し、生成器へ`follow_terrain=0`+一時の`station_grade_profile`を渡して生地形へ再投影させない。
- 線分への射影・距離は`Utils/PolylineUtils`に委譲する。
- `edit_parcel:select_path`は`ParcelEditorManager.OnPathSelected`だけが送る(射影済みアンカーを載せるため。`PathInputHandler`からも送ると二重発火)。

## 知らないと壊れる罠

- `RoadPlanManager` は `TerrainInputHandler` を吸着時に遅延取得する。起動時は地形タイルが未生成でも正常であり、キャッシュした参照もタイル破棄後には再取得する。
- 地形ドラッグでは各マネージャの`StartDrag`は呼ばれず`ProcessMouseDrag`だけが毎フレーム届く。掴む対象は初回呼び出しで決める(遅延グラブ)。`CameraController.IsMouseOverEditHandle`にも当たり判定を足さないとカメラドラッグに掴みを奪われる(`InputHandlers/AGENTS.md`)。
- `PolygonAreaEditManager.CancelOnEscape`(既定true)のEscは入力プリセットを`ALL_NORMAL`に戻して表示を再適用するだけで、`IpcStateCoordinator`のステート遷移もUIへの通知も通らない(ウィンドウは開いたまま、編集用の表示プリセットも残る)。造成範囲の作図はfalseにして、抜け道をウィンドウを閉じたときの`STATE_NORMAL`と確定の`edit_grading:finish`だけにしている。
- `RoadPlanManager`のオーバーレイは2本。`overlayRenderer`は毎フレーム`BeginFrame`でクリアされる(配置点・ハンドル・端子マーカー)。`backgroundRenderer`はカメラ5m移動/`bgDirty`/`PathManager.NodeDataVersion`変化のときだけ作り直すキャッシュ(既存の辺・ノード)。既存物を`overlayRenderer`へ描くと同フレームで消えて何も表示されない。
- ギズモのマテリアルだけ`_ZTest=Always`で常に手前。道路本体のプレビューには適用しない(地形をX線状に貫通して見える)。埋没は地形追従で防ぐ。
- 曲線モードの`GetRenderPoints`はベジエを直接サンプリングするため、地上区間(相対高さ≒0)のYを地形+盛土へ追従させる処理(`PathManager.SampleBezierSegmentWithTerrain`と同一)を内に持つ。省くとアンカー間の起伏を無視して道路が丘に埋まる。
- プレビューの点列は設計空間(地面+設計相対高さ+盛土)。`base_height`(`PathManager.GetPathBaseHeight`)を持つパス(送電線)は、生成器へ渡す直前(`RoadRenderer.ToDrawPoints`)と帯の頂点(`UpdateStrip`)で引く。引き忘れると鉄塔ごと`base_height`だけ浮く。地形追従区間の判定(`SamplePreviewSpans`・曲線の`GetRenderPoints`)も、基準は相対高さ0でなく`base_height`。
- プレビューのウェイポイントは`{position:[x,相対Y,z]}`の辞書でパス要約へ詰める(`RoadRenderer.GenerateComponentPreview`)。`PathSummary.GetWaypointList`(`GeneratedModelUtils.ToWaypointList`)は辞書しか読まず、素の配列は黙って捨てられる。アンカーから経路を組む送電線(`PowerLinePath`)が始点→終点の直線になり、置いたウェイポイントを通らない。
- 駅舎パス(type=`PATH_STATION_HOUSE`)の敷設中は`SnappingSuppressed`で既存地物への吸着を全て止める(グリッドのみ有効)。フラグ自体は書き換えない。
- 複線敷設中の吸着はオフセット考慮版`TryOffsetAwareRailSnap`。中心線を素朴に吸着させると全線路がずれて1本も接続できない。
- 線路のプレビューは、既存線路の描画(つなぎ目のS字・並走の縫い合わせ・車止め)に参加する仮の相手として`RoadRenderer.RegisterPreviewTracks`でPathManagerへ登録する(`RoadPlanManager.RegisterPreviewTracks`。N§8.5)。登録はプレビューのメッシュを作る前に呼ぶ(プレビューの線どうしも互いを相手にするため。登録が変わると詳細区間を作り直させる)。配置中は仮終点を除いた確定済みの点まで、ドラッグ中は前の登録を保って離したときに更新する(既存線路の作り直しを操作の区切りに抑える)。線ごとの仮の`path_id`は負(線 i は −(i+1))で、形状を編集中の計画の`pathId`を除外として渡す。登録は`ResetPreview`(`OnDestroy`も経由)で消える。
- インフラパス敷設中の建物干渉推定は同一網の需給施設を除外する(端子は区画内にあり、引き込みで建物と重なるのが正常)。端子未ロード時は除外しない=安全側で赤。

## 参照先

- 公開docs: `programs/Unity入力制御.md`、`programs/IPC連携の整理.md`(ステートとイベント契約)、`geometries/A_パス形状.md`・`J_パスの交差処理.md`、`geometries/G_道路メッシュの生成.md`「敷設・形状編集時のプレビュー」、`geometries/N_線路メッシュの生成.md`§8.4・§8.5、`geometries/M_地形プロップ.md`§M.7、`programs/経路提案.md`、`programs/道路の詳細と道路名.md`。
- Skill: `genesis-input-mode`、`genesis-path-geometry`。表示モードは別系統で`genesis-visual-mode`。
- 回帰検証: `unity/Tests/RoadSnapping`、`Tests/RoadPreview`、`Tests/BuildingPlacement`。
