あなたは都市シミュレーションゲームを開発するプロジェクトのAI開発エージェントです。以下のルールを守って開発してください。

# レポジトリ概要

このリポジトリは、シミュレーションゲーム、"City Genesis"のUnity部分のグラフィックプログラムである。

## プログラムの構造

バックエンド(FastAPI)とはHTTPで連携する。
このUnityプログラムとバックエンドはともにユーザーのPCで稼働し、localhost内部でしか通信しない。

```
- Assets/Scripts
  - API                 ... バックエンドと通信するクライアント(ApiClient.cs等)とスキーマ。GeneratedModels.csはopenapi.jsonから自動生成される。HTTP通信は必ずApiClientを経由すること(単体取得=`Get`/大きな一括取得=`GetBulk`。例外は地形バイナリ等の非JSON応答のみ)。`GetBulk`はAcceptヘッダでMessagePackを要求し、対応エンドポイント(bbox一括取得系)ではバイナリで受信して`MsgPackDecoder.cs`でデコードする(Content-Typeで自動判別。呼び出し側は形式を意識しない)。
  - DataLoaders         ... バックエンドから取得したデータを元にUnity空間にオブジェクトを作成するプログラム。実際の描画には`MeshGenerators`のプログラムが使われる。(詳細はDataLoaders/AGENTS.md)
  - MeshGenerators      ... 建物や道路の具体的なメッシュ生成アルゴリズム。バリアント→タイプ→デフォルトの順にフォールバックする。(詳細はMeshGenerators/AGENTS.md)
  - Editors             ... 3D空間内で道路・建物・土地区画・地区を編集する際の状態管理や確定後のバックエンド送信。(詳細はEditors/AGENTS.md)
  - InputHandlers       ... ユーザーのクリックやキー入力イベントの処理。状況によってイベントハンドラを切り替えられる。(詳細はInputHandlers/AGENTS.md)
  - VisualHandlers      ... 3D空間内の建物・道路等の「見せ方」(表示/非表示/ハイライト等)の一括管理。(詳細はVisualHandlers/AGENTS.md)
  - IPC                 ... Tauri製UIとのMemoryMappedFile通信。(詳細はIPC/AGENTS.md)
  - Utils               ... 各種ユーティリティ。新しい汎用処理を書く前に必ず確認。(詳細はUtils/AGENTS.md)
  - UI                  ... 3D側のUI部品(ツールチップ、モード表示、汎用ラジアルメニュー`RadialMenu/RadialMenuController`=トリガーキー押下でカーソル位置から項目が円形展開しキー離しでトグル、汎用カーソル追従HUD`CursorStatusHud`=現在設定のアイコン列+テキストを常時表示、立体方位磁針`CompassPanel`+メッシュ生成`CompassRoseMesh`=専用カメラでレンダーテクスチャに描いてRawImageへ貼る方式。実体はワールド上空(0,100000,0)の専用レイヤー`Compass`(6)に置き本編カメラから隔離する。筐体は固定でカードだけが本編カメラのヨーの逆に回り、俯角も追従する。カメラ角が変わったフレームだけ描き直す)。※ゲームのメインUIはこのレポジトリではなくcity-genesis/tauri(Tauri)にある
  - Grid                ... グリッドオーバーレイ表示
  - GameStateManager.cs ... グローバルなゲーム状態の管理(シングルトン)。状態変化イベントの購読。
  - MasterDataStore.cs  ... バックエンドから取得したマスターデータの保持。道路幅・車線数などの描画パラメータはここを正とする。
  - AppRoot.cs          ... アプリ起動処理。
  - MainMenu.cs         ... メインメニューのボタンイベントなどを管理するプログラム。
  - Utils.cs            ... ApplyDepthOffsetShader / DestroyObjectAfterFrames / CalculateAverageWidth / GetCameraHorizontalForward / IsInHorizontalView(視野優先処理の水平視野判定) など各マネージャー間で共有する汎用処理。新しい汎用処理を書く前に必ず確認。
  - Constants.cs / LocalConstants.cs ... 定数定義。
  - CameraCellChecker.cs... カメラの視野中心がどのセルにいるのかを毎フレーム判定し、変化したらCellDataLoaderに通知する(ロードの起点)。
  - CameraController.cs ... カメラの移動などを担うプログラム。
  - ZoneEditContext.cs  ... 地区編集の状態保持。
```

# AGENTS.mdの運用ルール

各主要ディレクトリに、役割・既存ユーティリティ・注意点を書いた`AGENTS.md`がある(`CLAUDE.md`は`AGENTS.md`へのシンボリックリンク)。**作業対象ディレクトリのAGENTS.mdを必ず先に読むこと。**
開発によってAGENTS.mdの記載内容に影響が出た場合や、記載と実装の矛盾を発見した場合は、必ず該当するAGENTS.mdを更新すること。

# 編集してはいけないファイル

`GeneratedModels.cs`はプログラムによって自動生成されるスキーマファイルなので、編集しないように。
`Constants.cs`はバックエンドのソースコードから自動生成されるフロント・バック共通定数なので、編集しないように。
これらのファイルを編集する必要がある場合は、ユーザーに相談すること。 

# モデルアセットの構成(FBXとミラープレハブ)

- `Assets/Models` ... FBXソース(Buildings / Paths / Props)。
- `Assets/Resources/Models` ... 上記と同一相対パスのラッパープレハブ。ランタイム(`Utils/ModelManager`)が `"Models/Buildings/{型}/{variant}/{パーツ}"` の文字列パスで `Resources.Load` するために必要。
- **このミラーは手動で維持しない。** `Assets/Models` にFBXを追加・移動すると、エディタ拡張(`Assets/Editor/ModelPrefabGenerator.cs`)が対応プレハブを自動生成する。一括同期はメニュー `Tools/Model Prefabs/Sync All`、FBXの削除・移動で孤児化したプレハブは `Tools/Model Prefabs/Delete Orphans` で削除する(自動削除はしない)。
- 自動生成されるプレハブはFBXをオーバーライドなしで包む純粋なラッパー。プレハブ側にコンポーネント追加やマテリアル差し替え等の手動編集を行った場合、同期ツールはそのプレハブを上書きせずスキップして警告する。

# アーキテクチャの要点(まずこれを把握する)

## データロードとLODの全体像

マップはセル(一辺 `TerrainManager.CellSize`、バックエンド設定から取得)に区切られ、カメラからの距離でセルごとに **Dynamic / Near / Far / UltraFar** の4ティアが決まる(`DataLoaders/LodConfig.cs`)。

ロードの流れ(詳細は `DataLoaders/AGENTS.md`):

```
CameraCellChecker(視線中心セルの変化検知)
  → CellDataLoader.OnCameraCellChanged
    → LodConfig.CalculateCellSets(セルごとのティア計算)
    → LoadBatch発行(旧バッチをCancel。StopCoroutineで止まらない子コルーチンの協調キャンセル用)
    → Building/Path/Parcel/Zone 各Managerの LoadLodCells(マージBBOXの一括API取得 + version差分で再描画スキップ)
    → TerrainManager.LoadLodCells(地形データ取得。適用はHeightStreamWorkerが視野優先・
      フレーム予算制で部分SetHeightsストリーミング。適用と同期してCPU高さ配列へ行単位書き込み)
  → TerrainManager.OnAllTerrainLodLoaded(updatedCells)  // 実際にデータが適用されたセルのみ通知
    → CellDataLoader.TerrainFollowUpCoroutine (フレーム分散)
       → 更新セル∩nearCells(Near以上の全ティア)の建物・道路メッシュ/高さ再計算とプロップのY再設定(地形追従、視野内優先)
```

- **地形タイルは全ティア共通解像度(513)で、ティア変更はタイルの作り直しではなくデータのストリーミング適用で行う**。ティアダウンでは何もしない(高精細データを保持)。重い生成(SetHeights・コライダーベイク・建物/道路のメッシュ生成)は視野優先・フレーム予算制のワーカーで複数フレームに分散する。建物のフル詳細メッシュには個数上限(LRU破棄)がある。詳細は `DataLoaders/AGENTS.md` と `genesis-docs/docs/geometries/E_LOD処理.md`。
- **建物・道路・プロップは地形より先に生成され、地形のロード完了後に高さ・形状が追従更新される**(2段階)。追従対象は「実際に地形が更新されたセル ∩ Near以上のティア」であり、**Dynamicセルに限定してはいけない**(Nearセルの道路が地形に埋まったまま放置される)。
- **地形が実際に変化していないセルは追従更新しない**: `OnAllTerrainLodLoaded` は「実際にハイトマップが適用されたセル」だけを渡すため、カメラのセル跨ぎで地形不変なら建物・道路の再生成は走らない。追従更新は `PerformanceConfig.FrameBudgetMs` でフレーム分散され、カメラ移動中のフレームヒッチを防ぐ。
- Dynamicティアは「開放(acquire)済みセル」にしか割り当てられない(未開放セルにはdynamic地形データが存在しないため)。
- バックエンドのオブジェクトには `version` があり、各Managerはversionが変わったものだけ再描画する(HTTP取得自体は毎回全件)。

## 座標系とセルインデックス

- Unityワールドはマップ中心が原点で、**Unityセルインデックスは負値を取る**。バックエンドのセルは0-based(左下原点)。変換は必ず `TerrainManager.UnityCellToBackendCell` / `BackendCellToUnityCells` / `WorldPositionToBackendCell` を使うこと。
- `FloatingOrigin`(Utils/)によりUnityワールド座標がバックエンド座標とずれている可能性がある点にも注意。

## 地形高さの取得

- 原則 `HeightmapTextureManager.GetTerrainHeight(pos)` を使う(CPU高さ配列のO(1)ルックアップ。未構築時・サンプリング結果が0の場合は `Utils.GetGroundHeight` にフォールバック)。
- `Utils.GetGroundHeight` はフレーム単位キャッシュ付き。**Terrainタイル差し替え後は `Utils.InvalidateHeightCache()` を呼ばないと古い高さを返す。**

## オブジェクトの命名規則とタグ(変更禁止級の規約)

- GameObject名にIDを埋め込む: `Building_{agentId}` / `Path_{pathId}` / `Parcel_{parcelId}` / `Node_{nodeId}`。遠景の簡易オブジェクトも同じ命名で、`ObjectIdInfo`コンポーネントの `isFarObject` フィールドで区別する。
- 各GameObjectには `ObjectIdInfo` コンポーネントが付与され、`id` / `status` / `isFarObject` / `role` フィールドでメタデータを管理する。**名前の文字列パースは行わず、常に `GetComponent<ObjectIdInfo>()` でデータを取得すること。**
- マウス入力のタイプ判定は**タグ**で行う: `BuildingObject` / `ParcelObject` / `PathObject` / `PathNode` / `ZoneObject` / `SeaObject`(地形はTerrainコンポーネントで判定するが、海面は `Terrain` コンポーネントを持たないため `SeaObject` タグで代用し `InputObjectType.TERRAIN` に振り分ける)。コライダーとタグがないとクリックできない。

## モード系は2系統ある

- **入力モード**(`InputSystemManager` / `InputHandlers/`): クリック時に何が起きるか。
- **ビジュアルモード**(`VisualModeHandler` / `VisualHandlers/`): どう見えるか(非表示/ハイライト/データ可視化)。
- 編集モードに入るときは通常この2つをセットで切り替える(トリガーはUI(Tauri)からのIPCイベント)。

## ログ

冗長ログは `UnityLog.If(LocalConstants.UNITY_LOG_*, ...)` を使う。ホットパスで無条件に `Debug.Log` を呼ばないこと(ビルドでもコストがかかり、特にエディタではスタックトレース取得が重い)。用途別フラグ: 道路系は `UNITY_LOG_ROAD`、建物系は `UNITY_LOG_BUILDING`、地形系は `UNITY_LOG_TERRAIN`、ロード進行(CellDataLoader/各Managerのセル分類・リロード開始等)は `UNITY_LOG_LOADER`、`[TIMING]` 計測ログは `UNITY_LOG_TIMING`。文字列補間が重い場合は `Func<object>` オーバーロード(`() => $"..."`)を使い、無効時に補間自体を回避する。`LogWarning` / `LogError` は無条件で残してよい。

# パフォーマンス設計 (全件走査の回避)

開発時は、オブジェクト数が増えた際にもフレームレートやロード時間を維持するため、以下の全件走査回避パターンを厳守すること。

- **接続パスの逆引きインデックス (`PathManager.pathsByNodeIndex`)**:
  交差点を1つ更新するたびに全パスキャッシュをループ走査するのを避けるため、ノードIDから接続パスIDの `HashSet` を引くインデックスを維持している。パスのキャッシュ更新・削除時は必ず `SavePathToCache` / `RemovePathFromCache` を用いてこのインデックスを同期すること。
- **地形更新時のパス検索最適化 (`PathManager.OnTerrainTileReadyHandler`)**:
  地形タイル（セル）が準備できた際に、交差するパスを再生成する処理では、全パスのセグメント走査を避け、すでにセル通過情報を持っている `pathIdsByCell` から対象セルおよび隣接セルに含まれるパスIDのみを抽出して交差判定を行う。
- **静止時のレイキャスト/地区判定のスキップ (`InputHandler.Update`)**:
  毎フレーム `RaycastAll` や地区ポリゴン判定 `GetZoneIdAtPoint` を実行すると重いため、Rayの変化（マウスやカメラの移動）と入力イベントがないフレームは処理を即座にスキップする。
- **地区判定の限定とBBOXによる高速化 (`ZoneManager.GetZoneIdAtPoint`)**:
  通常プレイ中の無駄な毎フレーム地区内外判定を排除するため、地区判定は地区編集モード中（あるいは地形モードが `MODE_ZONE_EDIT` のとき）のみ実行する。また、多角形内外判定（`IsPointInPolygon`）の前に、`zoneBboxCache` を用いた AABB (BBOX) 判定による早期リターンフィルタリングを実行する。
- **建物のセル逆引きインデックス (`BuildingManager.buildingIdsByCell`)**:
  セルから建物IDの `HashSet` を引くインデックスにより、`UnloadCell` や `SyncBuildingsInCells` で全建物キャッシュの線形走査を避ける。建物の登録・削除時は必ず `RegisterBuildingCell` / `UnregisterBuildingCell` を通してこのインデックスを同期すること。
- **区画のセル逆引きインデックス (`ParcelManager.parcelIdsByCell`)**:
  マウス位置から区画を解決する `ParcelManager.GetParcelIdAtPoint`(起伏地形で区画コライダーが地形に埋まった場合の InputHandler のフォールバック)が、マウス移動フレームごとに全区画のポリゴン内外判定を線形走査しないための逆引きインデックス。区画の登録は `RegisterParcelDataForLines`(内部で `RegisterParcelSpatialIndex`)、削除は `RemoveParcel` / `ClearAllCells` を通して同期すること。
- **接道街路の逆引きインデックス (`ParcelManager.parcelIdsByFrontPath`)** と **区画の逆引きインデックス (`BuildingManager.buildingIdsByParcel`)**:
  地区拡張ドラッグ中に「なぞった街路→面する区画→その区画上の建物」をフロントだけで解決して地区色を波及させるための逆引きインデックス(全区画・全建物の線形走査を避ける)。前者は `RegisterParcelSpatialIndex` / `UnregisterParcelSpatialIndex` / `ClearAllCells`、後者は `RegisterBuildingDataToCache`(parcel_id変更時は旧マッピング除去) / `SyncBuildingsInCells` / `ClearAllCells` を通して同期すること。
- **カメラ非依存のオーバーレイメッシュは毎フレーム再構築しない (`TerrainManager.UpdateCellOverlayIfActive`)**:
  開放済みセルの境界点線(`OverlayMeshRenderer` によるワールド固定幅の点線)はカメラに依存しない静的ジオメトリなので、毎フレーム `BeginFrame`〜`EndFrame` でメッシュを作り直すと、カメラ静止・入力なしのアイドル時でも無駄なCPU/GPU(頂点構築＋メッシュ再アップロード)を消費する。境界の変化(`borderSegmentsDirty`)や表示切り替え(`ShowCellBordersAndLabels` の変化)があったフレームだけ再構築し、それ以外は既存メッシュをそのまま描画すること。カメラに追従してビルボードやスクリーン幅換算が必要なオーバーレイ(パーセル枠線等)はこの限りではなく、カメラ移動閾値でガードする。

# 重要な考え方

## Y座標の考え方

バックエンドのデータから受け取られるノードとかパスのＹ座標っていうのは、これは全て地面からの相対高さです。
地面がぐちゃぐちゃのボコボコなら、その上に引かれる道路もぐちゃぐちゃボコボコになりますが、データ上Y座標が0ならば、それは高さ0、つまり完全に地面に沿っている道路です。 その道路のどこの部分をとっても、高さは0とみなしてください。ポリゴンの粗さとか、計算誤差により表示上はもしかしたら地面から浮いているかもしれない。でもデータ上は、その道路は地面に完全に張り付いていると考えます。 Y座標0の道路を、3D上は若干浮いてるからと言って高架道路とみなすようなことは、してはいけない。  
つまり地形に影響されて道路が曲がったりするのは、完全に3Dの表示の話であって、内部の様々な計算上は、道路というのは仮想的にすべて完全に平らな平面上にあるとみなして処理すること。
