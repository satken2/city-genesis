# InputHandlers フォルダ

マウス入力(クリック・右クリック・ホバー・ドラッグ)を3D空間のオブジェクトへ振り分ける。`InputHandler`(シングルトン)が毎フレームレイキャストし、ヒットしたオブジェクトの`IInputHandler`に「そのオブジェクト種別の現在の入力モード」を渡して呼ぶ。編集の状態管理は`Editors/`の各マネージャが持ち、ここは委譲するだけ。

## ファイル一覧

| ファイル | 内容 |
| --- | --- |
| `InputHandler.cs` | 中核。レイキャスト、タグによる種別判定、クリック/ドラッグ判定、モード定数(`InputMode`/`InputModePreset`/`InputObjectType`)、入力ゲート |
| `InputSystemManager.cs` | `InputHandler.SetMode/SetModePreset`への薄いラッパー |
| `IInputHandler.cs` | `onClick/onRightClick/onHoverStart/onHover/onHoverEnd`。実装しないと一切呼ばれない。レイキャストに載るにはコライダーとタグが必要。`onHover`は毎フレーム呼ばれる |
| `BuildingInputHandler.cs` | `BuildingManager`が建物生成時に付与。詳細表示、コピー元の選択(`MODE_PICK_BUILDING`) |
| `ParcelInputHandler.cs` | `ParcelManager`が付与。建設モードでは区画データを添えて`BuildingPlanManager.OnParcelClick`へ渡す(`parcel_type`の一致判定に必要) |
| `PathInputHandler.cs` | `PathManager`が付与(駅舎の建屋コライダーには`StationHousePath`が付与)。地区編集・区画作成の道路選択・結合/分割・区間編集の振り分け |
| `NodeInputHandler.cs` | `PathManager`がノード生成時に付与(端子ノードには`ConnectorEditManager`が後付け)。ドラッグ移動と`PATCH /nodes/{id}`、失敗時のロールバック |
| `TerrainInputHandler.cs` | `TerrainManager`が地形タイルと都市外の背景地形のチャンクごとに付与し、`InputHandler.Start`が海面(`SeaObject`タグ)にも付与。地形クリックの委譲先分岐、共通吸着`ApplySnapping`、吸着フラグ(static) |
| `RemovalManager.cs` | ブルドーザ(`MODE_REMOVE`)の実処理。プロップはコライダーが無いので`InputHandler`がクリック配送前に`TryRemovePropAt`で判定する |
| `TransitSelection.cs` | 通常モードで公共交通の帯・停留所・車両をスクリーン座標で選択。物理レイキャストより先に呼ぶ |

モードとプリセットの一覧・各ハンドラのモード別挙動は`docs/programs/Unity入力制御.md`が正。編集モードに入るときは入力モードと表示モード(`VisualHandlers/`)をセットで切り替える(2系統は別物。`unity/AGENTS.md`「モード系は2系統ある」)。

## 知らないと壊れる罠

- 左右クリックはマウスダウンで発火しない。5px未満の移動でアップしたときだけ`onClick`/`onRightClick`、以上なら左はドラッグ、右はカメラ回転。`CameraController`は`[DefaultExecutionOrder(-100)]`で先に走るので、押下フレームで`IsMouseRotating`を見ると右クリックが必ず回転に食われる。判定は移動量で行う。
- `StartTerrainDrag()`はレイキャストが何にもヒットしなかったときしか呼ばれない。地形コライダーにヒットする通常ケースではクリック候補(type=TERRAIN)→`StartDrag`→`UpdateDrag`と流れ、マネージャの掴み開始は呼ばれず`ProcessMouseDrag(worldPos)`だけが毎フレーム届く。地形ドラッグを受けるマネージャは初回呼び出しで掴む(遅延グラブ)。`StartTerrainDrag`内の分岐だけに掴み開始を書くと実プレイで一度も通らない。
- クリックのみでドラッグを持たないモード(`MODE_REMOVE`/`MODE_PATH_MERGE`/`MODE_PATH_SPLIT`/`MODE_EDIT_ROAD_MEMBERS`/`MODE_ZONE_EDIT`)は`StartDrag`/`StartTerrainDrag`の冒頭で明示的にreturnする。しないと既定経路で`RoadPlanManager`等のドラッグ処理へフォールバックする。
- 地形ヒット時、区画の入力モードが`MODE_NONE`でなければ`ParcelManager.GetParcelIdAtPoint`で配送先を区画へ差し替える(起伏地では区画コライダーが地形に埋まりレイキャストで拾えない)。クリック候補は`RaycastHit`ではなく「対象GameObject+ヒット点」で持つ。
- 共通吸着`ApplySnapping`(既定半径10m)は`TerrainInputHandler`の各イベント冒頭で掛かるが、`MODE_ROAD_CONSTRUCTION`/`MODE_EDIT_CONNECTOR`/`MODE_PATH_SPLIT`/`MODE_EDIT_STATION`/`MODE_EDIT_PROP`は通さない。道路は`RoadPlanManager.snapThreshold`(シーンのシリアライズ値が優先)で一度だけ吸着するため、共通側も掛けると二重吸着になり半径を変えても効かない。
- 吸着で引き継ぐ相対高さはバックエンド保存の相対Y(ノード/アンカー補間/建設中端点)から取り、描画点列の絶対Yから逆算しない。残差を継承すると地面のパスへ吸着しただけで端点が地下/空中扱いになり、インフラのT字タップが無言で不成立になる(幽霊接続)。
- `MODE_PARCEL_SHAPE_EDIT`はクリック・右クリック・ホバーの共通吸着をすべて通さない。ホバーで`OverrideMousePosition`に吸着座標を渡すと後続のドラッグにも混入するため、頂点ドラッグ側だけで無効化してはならない。区画形状編集の吸着は常に無効で、切り替え操作は持たない。
- 吸着フラグの真実源は`TerrainInputHandler`のstatic変数群(`Enable*Snapping`。既定Node=T/Path=T/他=F。敷設モードを跨いで保持)。切替はCtrlラジアルメニューだけで、UIからの設定イベントは無い。グリッド吸着は`GridOverlay`の表示と連動し、`GridSetManager`は角度設定中に退避・復元する。
- `TerrainInputHandler`は地形タイルごとに付くコンポーネント。`Update`の条件は軽く保つ(Escの区画選択リセットは`ParcelEditorManager.Update`側で行う)。
- 入力ゲート: UI上のポインタ・カメラドラッグ中・フライ/撮影モード(`CameraController.BlocksWorldInput`)・ドライブ(`DriveMode.IsOpen`)・ラジアルメニュー表示中(`RadialMenuController.IsOpen`)は`InputHandler.Update`先頭で非破壊的に止める。撮影終了フレームも遮断し、Escを背後の操作へ流さない。メニューのCanvasにはGraphicRaycasterが無いのでEventSystemのUI判定では止まらない。
- `IsNormalMode`は全オブジェクトの入力モードが`MODE_NORMAL`であることを返す。撮影とドライブの開始可否で共用する(各編集マネージャ自身もキーを読むため、入力ゲートだけで編集操作を止めようとしない)。
- マウス・カメラが動かず入力も無いフレームは`lastRay`キャッシュで`RaycastAll`ごと省略する。地区のホバー判定(`ZoneManager.GetZoneIdAtPoint`)は地区編集中だけ行う。
- 地区拡張の連続性はフロント(`PathInputHandler.IsConnectedToZone`)で確認する。`POST /paths/batch-zone`は1本だけの選択では連続性を検証しない。適用直後の先行着色は`ZoneEditContext.ClearTempVisuals`で明示的に戻す(失敗時は版が変わらずリロードが再描画しない)。
- 沿道区画作成の道路選択(`IsPathSelectableForParcelCreation`)は稼働中・街路・`frontage_allowed`が条件で、道路橋223は不可。地区編集の街路選択は別条件で橋も対象。

## 海面(Sea)のクリック

- 海面はシーン静置の板(`Sea`、Y=0.001、Layer=Water)で`Terrain`コンポーネントを持たない。`SeaObject`タグで`TERRAIN`に分類し`TerrainInputHandler`を後付けするので、地形と同じ経路を流れる。区画種別はUnityもUIも送らず、バックエンドが位置から沿岸/海上/陸地を判定する。
- 都市外(マップの外)の背景地形のチャンク(`TerrainManager.Background.cs`)も`Terrain`コンポーネントを持たない板ポリゴン。`TerrainManager.IsBackgroundTerrain`で`TERRAIN`に分類し、カーソル位置(`UpdateTerrainPosition`)も背景の起伏上で解決する。都市外のコンテナ貨物PFに国際貨物輸送路線の停留所を置く操作や、テンプレート作成モードで都市外にパス・建物を置く操作がこの経路を通る。
- 海上では`Utils.GetGroundHeight`が海底(負のY)を返す。海上へ出す表示物は海面板より上へクランプする(地形マーカー`SeaMarkerMinY`=0.05m、区画プレビュー枠`ParcelPlanRenderer.SeaSurfaceY`=0.01m)。
- `terrainLayerMask`にWaterは含まれないが、海域ではy=0平面との交点フォールバックで`posX/posZ`が正しく出る。

## 参照先

- 公開docs: `programs/Unity入力制御.md`(最重要)、`programs/IPC連携の整理.md`「グラフィックプログラムのステート」、`programs/公共交通ハイライト.md`(TransitSelection)、`programs/道路の詳細と道路名.md`「区間編集モード」、`geometries/M_地形プロップ.md`§M.7.3(ブルドーザ)。
- Skill: `genesis-input-mode`。表示モード側は`genesis-visual-mode`。
- 委譲先の契約と罠は`Editors/AGENTS.md`。
