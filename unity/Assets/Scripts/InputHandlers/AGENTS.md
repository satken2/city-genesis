# InputHandlers フォルダ

ユーザーのクリック・ホバー・ドラッグ等のマウス入力を、3D空間のオブジェクトに振り分けるシステム。

## 基本フロー

1. **イベント取得**: `InputHandler.cs`(Scripts直下ではなくこのフォルダ。シングルトン)が毎フレーム、カメラからマウス位置へRayを飛ばす。`Physics.RaycastAll` を距離順にソートし、入力モードが `MODE_NONE` のオブジェクトは**貫通**して次のヒットを採用する。
2. **対象判定**: ヒットした GameObject の**タグ**でタイプを判定する(`BuildingObject` / `ParcelObject` / `PathObject` / `PathNode` / `ZoneObject` / `SeaObject`)。地形は `Terrain` コンポーネントの有無で判定するが、**海面(Sea)は `Terrain` コンポーネントを持たないため `SeaObject` タグで代用する**(詳細は後述)。※名前プレフィックスではない。
3. **地形ヒットの区画への差し替え(フォールバック)**: 最初の有効ヒットが地形(TERRAIN)で、かつ区画(PARCEL)の入力モードが `MODE_NONE` でない場合、`ParcelManager.GetParcelIdAtPoint(ヒット点)` でヒット点XZを含む区画ポリゴンを2D内外判定で探し、見つかればディスパッチ先をその区画GameObjectに差し替える。**起伏地形では区画のMeshCollider(上面はポリゴン角の頂点でしか地形をサンプルしない)が地形の下に埋まり、レイキャストでは手前の地形が必ず勝って区画を選択できないため。** 区画コライダーが地形より上に出ている平地では従来どおりレイキャストが先に区画を拾う。判定はセル逆引きインデックス(`ParcelManager.parcelIdsByCell`)＋BBOXで絞り込むため全区画走査は発生しない。
4. **ハンドラ呼び出し**: 対象オブジェクトから `GetComponent<IInputHandler>()` でハンドラを取得し、`onClick / onRightClick / onHoverStart / onHover / onHoverEnd` を呼ぶ。引数には「そのオブジェクトタイプの現在の入力モード」が渡される。クリック/右クリック候補は `RaycastHit` ではなく「対象GameObject＋ヒット点」で保持される(上記の差し替えに対応するため。マウスアップまでに対象が破棄された場合は発火しない)。

## クリックとドラッグの区別(知らないとわからない)

- 左クリックは**マウスダウン時には発火しない**。クリック候補(`hasPendingClick`)として記録し、マウスアップまでの移動が5ピクセル未満なら `onClick`、5ピクセル以上動いたらドラッグ(`StartDrag` / `StartZoneDrag` / `StartTerrainDrag`)として確定する。カメラドラッグ(`CameraController.IsMouseDragging`)が始まったら候補は破棄される。
- **右クリックも同様にマウスダウン時には発火しない**。右クリック候補(`hasPendingRightClick` / `hasPendingTerrainRightClick`)として記録し、マウスアップまでの移動が5ピクセル未満なら `onRightClick` を発火、5ピクセル以上動いたら**カメラ回転(右ドラッグ)**とみなして発火しない。これにより「右クリック＝確定等」「右ドラッグ＝カメラ回転」を両立させる。※`CameraController` 側も右回転を「閾値超で開始する保留方式」(`isPendingRotate`)にしてあり、両者の閾値判定が整合する。`CameraController` は `[DefaultExecutionOrder(-100)]` で先に走るため、押下フレームで即 `IsMouseRotating` を見ると右クリックが必ず回転に食われる(過去のバグ)。判定は移動量で行うこと。
- ドラッグの行き先はモード依存: ノードドラッグ→`NodeInputHandler.onDragStart/Update/End`、地形ドラッグ→道路編集(`RoadPlanManager`)・区画編集(`ParcelEditorManager`/`ParcelShapeEditManager`)。**地区編集はドラッグを持たない**(左ドラッグは素直にカメラパン)。
- **地区編集(MODE_ZONE_EDIT)の操作はCtrlラジアルメニュー(`Editors/ZoneEditor/ZoneEditControlMenu`)で3ツールを切り替える**: 「新規地区の作成(Create)」「既存地区の拡張(Expand)」「地区の統合(Merge)」。**3ツール共通で「右クリックで対象地区を選ぶ→左クリックで適用」**という同じ操作モデルにしてあり、選択中の地区はカーソルHUDに色チップ+名前で、3Dビューでは太い枠線で示される。旧仕様のShift+ドラッグ拡張・ドラッグ中のフロント色波及・`ZonePolygonPreview`は廃止した。
  - **左クリック(適用)**: 道路上は`PathInputHandler.HandleZoneEditClick`。Create=`ZoneEditContext.CreateZone`(その街路で新規地区を作成)、Expand=`ExpandZone`(選択中地区へ`POST /paths/batch-zone`で1本ずつ即時割当)、Merge=`RequestMerge`(その道路の地区を統合元としてUIへ要求)。地区の面(地形)上は`TerrainInputHandler.HandleZoneEditTerrainClick`で、Mergeツールのときだけカーソル下の地区を統合元にする(Create/Expandは対象街路を指す必要があるため無反応)。
  - **右クリック(対象地区の選択)**: 道路上は`PathInputHandler.onRightClick`(その道路の`zone_id`)、地区の面上は`TerrainInputHandler.HandleZoneEditTerrainRightClick`(`InputHandler.HoveredZoneId`)。どちらも`ZoneEditContext.SelectZone`へ流れ、Createツール中に選ぶとExpandツールへ自動切替する。地区外での右クリック・**Esc押下**は選択解除(モード自体の終了は従来どおりUI起点の`STATE_NORMAL`)。
  - **拡張の連続性チェックはフロントで行う**: バックエンドの`POST /paths/batch-zone`は選択が1本だけだと連続性を検証しない(`_validate_contiguity`が即return)ため、そのままだと離れた道路が地区に混ざる。`PathInputHandler.IsConnectedToZone`が`PathManager.GetZonePathIds`→`IsPathConnectedToSet`で選択中地区の道路と繋がっているかを見てから送る。
  - **適用直後の先行着色**: 応答を待たずにクリックした街路へ`VisualModeObject.SetTempZoneId`で適用先の色(新規作成は`ZoneEditContext.PREVIEW_SENTINEL`=`PreviewColor`)を乗せる。台帳は`ZoneEditContext.tempColoredPathIds`で、送信結果(成功/失敗の両方)・モード終了で`ClearTempVisuals`が明示的に戻す(**失敗時はversion不変でリロードが再描画しないため、リロード任せにせず明示クリアが必須**)。
  - **既存地区へのホバーハイライト**: 地区編集モード中にマウスが既存地区の領域内に入ると`ZoneManager.SetHoverFillZone`が半透明フィル(ポリゴンを`BuildingMeshBuilder.TriangulateOrFan`で三角形化・`_ZTest=Always`の専用`OverlayMeshRenderer`)を描く。ホバー地区が変わったフレームのみ再生成する。`InputHandler.HoveredZoneId`はこの判定結果の公開版で、地形クリックからカーソル下の地区を解決するのに使う。
- **土地区画作成(MODE_PARCEL_CREATION_*)の作成方法もCtrlラジアルメニュー(`Editors/ParcelManager/ParcelEditControlMenu`)で切り替える**: 6種(道路沿い/ブロック一括/ランダム/単一区画/矩形分割/ランダム分割)。真実源は`ParcelEditorManager.CurrentMode`で、切り替えると`InputModePreset`(POINT/PATH/SPLIT)と選択状態がその場でリセットされる。**Escは選択のリセットのみでモードからは抜けず、確定してもモードを抜けない(連続作成)**。`TerrainInputHandler.Update`のEsc処理はマーカーを消すだけで、リセット自体は`ParcelEditorManager.Update`が行う(このコンポーネントは地形タイルごとに付くため条件は軽く保つこと)。
- **`StartTerrainDrag()` はレイキャストが何にもヒットしなかった場合しか呼ばれない(重要な罠)**: 地形コライダー(Terrain/SeaObject)にヒットした通常のケースでは、ドラッグは `hasPendingClick`(オブジェクトクリック候補、type=TERRAIN)→`StartDrag()`→`UpdateDrag()` と流れ、**各エディタマネージャの `StartDrag`(掴み開始処理)は一切呼ばれないまま `ProcessMouseDrag(worldPos)` だけが毎フレーム呼ばれる**。そのため地形ドラッグを受けるマネージャは、`RoadPlanManager.ProcessMouseDrag` / `ConnectorEditManager.ProcessMouseDrag` のように**初回呼び出しで現在のマウス位置から掴み対象を決定する(遅延グラブ)実装にする必要がある**。`StartTerrainDrag` 内の `IsMouseOverStop`/`IsMouseOverVertex` 分岐だけに掴み開始を書くと、実プレイでは地形が常にヒットするため一度も通らず「全くドラッグできない」バグになる(接続点編集で実際に起きた)。
- UI(uGUI)上にポインタがある場合・カメラドラッグ中・**カメラのフライモード中(`CameraController.IsFlyMode`)**・**ラジアルメニュー表示中(`RadialMenuController.IsOpen`)**は入力処理全体がスキップされる(フライモード/メニュー表示中はクリック・ホバー・ツールチップを含む一切のマウス入力を無効化する。入力プリセットは変更せず `InputHandler.Update` 先頭のゲートで非破壊的に止めるため、抜ければ元の入力状態がそのまま復帰する。`CameraController.Update` にも同じ `IsOpen` ゲートがあり、メニュー操作のマウス移動でカメラが動かない。メニューのCanvasには意図的にGraphicRaycasterが無いため、EventSystemのUI判定では止まらないことに注意)。

## 各ハンドラ

| ファイル | 付与する側 | 説明 |
| --- | --- | --- |
| `BuildingInputHandler.cs` | BuildingManager が建物生成時に AddComponent | `buildingId` を保持。クリックでIPC経由の詳細ウィンドウ表示など |
| `ParcelInputHandler.cs` | ParcelManager | `parcelId` を保持 |
| `PathInputHandler.cs` | PathManager (駅舎パスの建屋コライダーには `StationHousePath` が同じ三点セットを付与) | `pathId` を保持。地区編集モードのドラッグ割当(`onZoneDragHover/End`)も担当 |
| `NodeInputHandler.cs` | 道路編集系 | ドラッグイベント(`onDragStart/Update/End`)を持つ |
| `TerrainInputHandler.cs` | TerrainManager が各Terrainタイル生成時に AddComponent / `InputHandler.Start` が `SeaObject` タグの海面に AddComponent | 何もない地面のクリック。道路の点配置などはここ経由。海面(Sea)も `SeaObject` タグを付与の上このハンドラで同じく処理する。**建物配置**: Ctrlメニューで区画/自由/道路沿いを明示選択する。区画配置中の地形クリックは無視し、直接配置中はBuildingPlanManagerがPARCEL入力をMODE_NONEへ変えて区画上も地形へ貫通させる。地形クリック・ホバーでは共通吸着をスキップし、道路外縁への吸着をBuildingPlanManagerに委譲する。位置固定は`edit_building:placement_state`でUIへ通知する |

- マウス位置のワールド座標は `InputHandler.posX / posZ`(地形ヒットがなければ y=0 平面との交点)。`InputSystemManager.GetMousePosition()` でも取れる。
- **吸着フラグの真実源は `TerrainInputHandler` の static 変数群**(`EnableNodeSnapping`/`EnablePathSnapping`/`EnableGridSnapping`/`EnableRightAngleSnapping`/`EnableStraightLineSnapping`): 切り替えは3Dビュー側のCtrlラジアルメニュー(`Editors/RoadEditor/RoadEditControlMenu`)が行い、UIパネルからの`view:set_snapping`イベントは廃止済み。グリッド吸着はグリッド表示と連動(`GridOverlay`が表示切替時に自動でON/OFF)。グリッド角度設定モード中は`GridSetManager`が全フラグを退避して「ノード吸着のみON」にし、終了時に復元する。static初期値が既定値(Node=T/Path=T/他=F)で、敷設モードの出入りを跨いで値が保持される仕様。**駅舎パス(type=`PATH_STATION_HOUSE`)の敷設中だけは、`RoadPlanManager.SnappingSuppressed`が全フラグを実効OFFにする**(フラグ自体は書き換えないので他バリアントへ戻せばプレイヤーの設定が復帰する。グリッド吸着のみ有効)。
- **吸着(スナップ)の適用場所は2段構えで、道路/鉄道建設モードだけ例外**: `TerrainInputHandler.onClick/onRightClick/onHover` は冒頭で全モード共通の `ApplySnapping`(インスタンスの `snapThreshold` 既定10m・バリアントフィルタ無し=variant -1)を掛けてから各マネージャへ委譲する。ただし `MODE_ROAD_CONSTRUCTION` と `MODE_EDIT_CONNECTOR` はこの共通吸着をスキップする。道路/鉄道は `RoadPlanManager` が自前の `snapThreshold`(道路専用の吸着半径)と `PathSubtype`(インフラ網互換フィルタ)で一度だけ吸着するため。**ここで共通吸着も掛けると二重吸着になり、`RoadPlanManager.snapThreshold` を変えても吸着範囲が変わらない**(共通側の10mが先に効いてしまう)過去のバグの原因になる。道路の吸着半径を変えたいときは `RoadPlanManager.snapThreshold`(シーンのシリアライズ値が優先)を触ること。`RoadPlanManager` → `TerrainInputHandler.ApplySnapping(..., thresholdOverride)` に半径を渡して共有ハンドラの既定半径から切り離している。
- **吸着の標高(相対高さ)引き継ぎは、バックエンドが保存している相対Yから取ること**: `ApplySnapping` が返す `snapRelHeight` は、ノード吸着=`NodeData.GetRelativeHeight()`、パスセグメント吸着=`PathSummaryExtensions.GetRelativeHeightAtPoint`(アンカー相対YのXZ最近傍区間での線形補間)、建設中端点吸着=端点の相対Y要素、で決める。**描画点列の絶対Yからの逆算は禁止**。保存設計標高がある線路だけは、その基準線の絶対標高から同じUtils地形サンプルを引いて相対Yを求める。GetNodeSnapPositionも接続線路の設計標高を優先する。吸着対象セグメント(`segmentGrid`)は描画用点列で、折れ線パスはアンカー間の生チャード(数百mになりうる)のため中間の地形起伏がそのまま残差になり、さらに描画用オフセット(`ROAD_SPLINE_OFFSET`)と高さ源の不一致(CPU配列 vs `Terrain.SampleHeight`)も乗る。この残差を標高として端点に継承すると、地面のパス(相対Y=0)へ吸着しただけで端点が地下/空中扱いになり、インフラのT字タップがバックエンドの高さ判定で無言不成立になる(見た目は繋がっているのに網が分断される幽霊接続)。軌道複線のオフセット考慮吸着(`RoadPlanManager.TryOffsetAwareRailSnap`系)も同じ規約。
- **入力負荷の最適化 (毎フレーム処理の回避)**: 
  - マウスやカメラが動いておらず、かつクリック等の入力イベントも発生していないフレームでは、重い `RaycastAll` やホバー判定等の処理全体を即座にスキップする (`lastRay` キャッシュを利用)。
  - 地区のホバー判定 (`ZoneManager.GetZoneIdAtPoint`) は通常プレイ時の毎フレームオーバーヘッドを避けるため、「地区編集モード中」のみに制限して実行する。

## 道路の並行補助との共通吸着の連携

道路の並行・終端揃え設定は`RoadPlanManager`のstaticプロパティにあり、共通`Enable*Snapping`とは別。
並行有効時だけ`ApplySnapping(..., skipGrid: true)`で接続吸着を先に解決し、並行候補が不成立なら
道路編集側がグリッドへ戻す。共有フラグは書き換えない。他の入力モードでは`skipGrid`の既定falseを使う。
接続吸着の戻り値`snappedToElevatable`は相対高さ0でもtrueなので、並行モードではこの結果を再射影しない。
Ctrlメニューの2設定と候補・ガイド・XYZの役割分担は`Editors/AGENTS.md`を参照。

## 入力モード

沿道区画生成の道路選択は `PathInputHandler.IsPathSelectableForParcelCreation` が稼働中・街路・マスターの `frontage_allowed` を確認する。道路橋223は通行可能な街路でも選択しない。地区編集の街路選択は別条件のため、橋も対象のままとする。

モード定数・プリセット定数は `InputHandler.cs` 内の `InputMode` / `InputModePreset` / `InputObjectType` に定義されている。
オブジェクトタイプごとにモードを設定できる(`InputSystemManager.SetInputMode(objectType, mode)`)。一括設定は `InputSystemManager.SetModePreset(preset)`。
※`InputSystemManager` は `InputHandler` への薄いラッパー(シングルトン)。実体は `InputHandler.SetMode / SetModePreset`。

入力モード(`InputMode`):
- MODE_NONE = 0 (レイキャストを貫通する)
- MODE_NORMAL = 1
- MODE_ROAD_CONSTRUCTION = 2
- MODE_BUILDING_CONSTRUCTION = 3
- MODE_PARCEL_CREATION_POINT = 4
- MODE_PARCEL_CREATION_PATH = 5
- MODE_PARCEL_CREATION_SPLIT = 6
- MODE_EDIT = 7
- MODE_ZONE_EDIT = 8
- MODE_PARCEL_SHAPE_EDIT = 10
- MODE_SET_GRID = 11
- MODE_EDIT_RESOURCE = 12
- MODE_EDIT_TRANSIT = 13 (公共交通の停留所配置。`TerrainInputHandler` が `TransitPlanManager` へ委譲。ドラッグ移動は `InputHandler.StartTerrainDrag/UpdateDrag/EndDrag` が資源編集と同様に `IsMouseOverStop`/`ProcessMouseDrag` へ振り分ける)
- MODE_EDIT_PROP = 14 (プロップ配置。`TerrainInputHandler` が `PropPlacementManager` へ委譲。クリック・ホバーとも共通吸着を通さず、他オブジェクトを貫通して任意XZへ配置する。高さは配置マネージャが地形標高へ加算)
- MODE_REMOVE = 17 (ブルドーザ=オブジェクト削除。空き地区画・計画中の建物/道路・地形プロップをクリックで削除。実処理は `RemovalManager`。**地形プロップはColliderを持たない**ので、`InputHandler` がクリック候補の発火前に `TryRemovePropAtScreenPoint`(→`RemovalManager.TryRemovePropAt`→`PropManager.TryPick` のAABB判定。ヒット物までの距離より手前の最近傍1本)を試し、拾えたら建物・区画へは配送しない。個別配置は即 `DELETE /props/instances/{id}`、範囲配置は `remove_object:request_confirm` でUIの確認ダイアログを待ち、`remove_object:confirm_result` の承諾で `DELETE /props/scatters/{id}`。ホバー枠は `UpdatePropHover`+`LateUpdate` の `OverlayMeshRenderer`)
- MODE_EDIT_STATION = 21 (駅パッケージ配置=軌道建設パネルの簡易モードの駅タブ。`TerrainInputHandler` が `StationPlanManager` へ委譲し、左クリックで配置地点を固定・右クリックで固定解除する。**クリック・ホバーとも共通吸着 `ApplySnapping` を通さない**——駅の中心線は複線の「間」を通るため、既存の線路やノードへ吸着すると意図した位置から半間隔ぶんずれるため)
- MODE_PATH_MERGE = 18 (パス結合の相手選択。`PathInputHandler.onClick` が `PathTopologyEditManager.OnPathClickedForMerge` へ委譲し、候補パス以外のクリックは弾かれる。ホバーハイライトも候補のみ)
- MODE_PATH_SPLIT = 19 (パス分割の分割点選択。PATH と TERRAIN の両方をこのモードにし、どちらのクリックも `PathTopologyEditManager.OnClickForSplit` へ委譲する〔パス脇の地形クリックでも中心線への垂線の足が分割点になる〕。**`TerrainInputHandler` の共通吸着 `ApplySnapping` はこのモードで適用しない**——ノード吸着で端点へ引き寄せられると常に「端点に近すぎる」分割になりバックエンドに拒否されるため)
- MODE_EDIT_CONNECTOR = 15 (施設の接続端子ノード=インフラ/走行網接続点のドラッグ移動編集。マーカー=逆四角錐はノード実体の上空に浮くオーバーレイのためコライダーへのレイキャストではなく地形ドラッグとして受け、**掴み開始は `ConnectorEditManager.ProcessMouseDrag` の初回呼び出しの遅延グラブ**(上記の罠参照。`StartTerrainDrag` 経由は地形コライダーが無い場合のフォールバックのみ)。当たり判定 `IsMouseOverConnector` は「ノード地表点〜マーカー天面のスクリーン線分」との距離で行い、半径はズームに応じて拡大(最低45px)。NODEは`MODE_NONE`でレイキャスト貫通させる。`TerrainInputHandler.onHover` はこのモードで吸着(`ApplySnapping`)を適用しない(ドラッグ中の端子が自分自身の旧キャッシュ位置へ吸着し続けるため)。**`CameraController.IsMouseOverEditHandle` にもマーカー判定を足さないと、先に走る(`DefaultExecutionOrder(-100)`)カメラドラッグが掴みを奪う**)

プリセット(`InputModePreset`): ALL_NONE / ALL_NORMAL / ROAD_CONSTRUCTION / BUILDING_CONSTRUCTION / PARCEL_CREATION_POINT / PARCEL_CREATION_PATH / PARCEL_CREATION_SPLIT / ZONE_EDIT / PARCEL_SHAPE_EDIT / SET_GRID / EDIT_RESOURCE / TRANSIT_CONSTRUCTION / PROP_PLACEMENT / CONNECTOR_EDIT / REMOVE_OBJECTS / PATH_MERGE(=パスのみ`MODE_PATH_MERGE`) / PATH_SPLIT(=パスと地形が`MODE_PATH_SPLIT`) / STATION_PLACEMENT(=地形のみ`MODE_EDIT_STATION`)

**クリックのみでドラッグを持たないモードは `StartDrag` / `StartTerrainDrag` の冒頭で明示的に return させること**(MODE_REMOVE / MODE_PATH_MERGE / MODE_PATH_SPLIT / MODE_ZONE_EDIT)。しないと既定経路でドラッグ状態に入り、`RoadPlanManager` 等のドラッグ処理へフォールバックする。

編集モードに入るときは通常、入力モード(ここ)とビジュアルモード(`VisualHandlers/VisualModeHandler`)をセットで切り替える。

## 新規ハンドラ実装のテンプレート

ハンドラは必ず `IInputHandler` を実装すること(`InputHandler` が `GetComponent<IInputHandler>()` でディスパッチするため、実装しないと一切呼ばれない)。

```csharp
using UnityEngine;

public class MyObjectInputHandler : MonoBehaviour, IInputHandler
{
    public int ObjectTypeId => InputObjectType.BUILDING; // 対応するタイプ

    public void onClick(int mode)
    {
        switch (mode)
        {
            case InputMode.MODE_NORMAL:
                break;
            default:
                break;
        }
    }

    public void onRightClick(int mode) { }
    public void onHoverStart(int mode) { }
    public void onHoverEnd(int mode) { }
    // onHover は毎フレーム呼ばれるので重い処理を書かない
    public void onHover(int mode) { }
}
```

また、レイキャストで検出されるためには、オブジェクトに**コライダー**と上記の**タグ**が設定されている必要がある。

## 海面(Sea)のクリック扱い(知らないと事故る)

- 海面は `TerrainManager` が管理するTerrainタイルとは別物で、シーンに静的配置された板ポリゴン(`Sea` GameObject, Layer=Water, `Custom/DepthOffsetLit` シェーダー)。`Terrain` コンポーネントを持たないため、素の状態では `GetObjectType` が `-1` を返し、クリックが無反応になる。
- これを解決するため、Sea には **`SeaObject` タグ**を付与し、`InputHandler.Start` で **`TerrainInputHandler` を AddComponent** する。`GetObjectType` は `SeaObject` タグを `InputObjectType.TERRAIN` に振り分けるため、海面クリックは地形クリックと同じ経路(`TerrainInputHandler.onClick`)に流れる。
- これにより土地区画作成の点選択(`MODE_PARCEL_CREATION_POINT`)等が海面上でも有効になる。海上区画(`PARCEL_WATER`)として作成できる。
- Sea には既に `VisualSeaObject`(`VisualHandlers/`)が付与されており、`InputObjectType.TERRAIN` として VisualModeHandler に登録済み。入力系もこれと同じタイプに揃える形。
- `InputHandler.terrainLayerMask`(Layer 10 Groundのみ)には Water レイヤーが含まれないため、`UpdateTerrainPosition` での `posX/posZ` 更新には影響しない。海域では y=0 平面との交点フォールバックが効くため、`TerrainInputHandler.onClick` に渡る `clickPosition` は正しく算出される。

パス吸着の位置射影は`PolylineUtils.ClosestPointOnSegmentXZ(point, start, end)`を使う。カーソルの地形Yで勾配上のXZ位置をずらさず、標高は別途保存基準から解決する。線分の幾何計算は入力ハンドラに持たせない。

## 公共交通の選択

通常のPATH/TERRAIN入力中は`TransitSelection.ProcessPointer`を物理レイキャストより先に呼ぶ。車体は3D境界（遮蔽物を考慮）、帯・停留所・名前ラベルは前面表示と同じスクリーン座標で判定する。マウスダウン時の路線IDを保持し、移動5px未満のリリースでtransit_detailを開く。重なる帯は路線候補を表示する。編集・UI・カメラドラッグ・フライ・ラジアルメニュー・セッション切替では候補を破棄する。車両クリックはハイライトOFFでも有効。
