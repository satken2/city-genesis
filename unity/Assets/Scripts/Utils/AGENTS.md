# Utils

汎用ユーティリティの置き場。新しい汎用処理を書く前に、必ずこの一覧と Scripts 直下の `Utils.cs` を確認し、各 Manager やビルダーに再実装しない。docs パスは `city-genesis/docs/docs/` 起点。

## ファイル

| ファイル | 内容 |
| --- | --- |
| `PolylineUtils.cs` | 線分・曲線・点列の幾何。`ClosestPointOnSegment`(Vector2/3) / `ClosestPointOnSegmentXZ` / `ProjectOnSegment(XZ)` / `DistanceToSegment(Sq)` / `IntersectRaySegment` / `CubicBezierPoint` / `SmoothedDirection` / `RoundCorners` |
| `PolygonUtils.cs` | 閉じた2D多角形(x=X, y=Z)。`Contains`(偶奇則。`IReadOnlyList<Vector2>` / `float[][]` / `List<List<float>>` を直接受ける。境界上を含めるなら `boundaryTolerance` 付き) / `AppendRayCrossings`(走査線用) / `SignedArea`(正=CCW) / `DistanceToBoundary` |
| `SpatialHash.cs` | `SpatialHash<T>`。「半径r以内の候補抽出」はこれを使い、自前で全件走査しない。巨大半径の Query はフリーズする |
| `ModelManager.cs` | モデルのロードとキャッシュ(シングルトン)。persistentDataPath の glTF/glb を優先し、無ければ `Resources/Models` のミラープレハブ。同じモデルを二度ロードしない |
| `GeneratedModelUtils.cs` | `ParseRing(JToken)`(GeoJSON リング→`List<Vector2>`) / `PathSummaryExtensions.GetRelativeHeightAtPoint`(`station_grade_profile` があれば設計絶対標高−地形、無ければアンカー相対Yの補間) |
| `AgentIconFactory.cs` / `BuildingIconFactory.cs` / `PathIconFactory.cs` / `AlertIconFactory.cs` | アイコン生成。`AlertIconFactory` はアラートビット `(entityType, bit)` ごとの Sprite |
| `FireEffectFactory.cs` | 火災・失火・爆破のパーティクル一式を建物寸法に合わせて手続き生成(テクスチャ・材質はコード生成で共有)。呼び出しは `DataLoaders/FireEffectManager`。調整の勘所は `geometries/S_火災エフェクトの描画.md` |
| `VisualEffectManager.cs` | 完成/解体/爆破の遷移でパーティクルと単発3D音を鳴らすシングルトン(AppRoot が生成)。検証 `Tests/Explosion/run-unity.ps1` |
| `InfraNetworkUtil.cs` | ノード溶接互換(吸着可否)。`IsNodeSnappable` / `IsPathSnappable` / `IsInfraMask` / `IsInfraPathVariant` / `IsInfraPathType` |
| `IntersectionPolygonUtil.cs` | 交差点の路面ポリゴン。`PathEndSection` / `IntersectionCorner` / `AtDistance` / `Trim` / `SamplePlan` / `RequiresMoreSpace`。任意 `shapeLayout` は描画専用の外周調整で、保存パス・論理占有は変えない |
| `TunnelSectionUtil.cs` | 地下パスの区間分類(バックエンド `geo_path_depth.py` と同一定義)。`IsUndergroundPath` / `SplitDrawableRuns` / `CollectTunnelRuns` / `HasTunnelSection` |
| `OccupancyVolumeUtil.cs` | 道路・建物の占有警告箱の三角形列。`RoadPlanManager` と `HighlightManager` で共用し、既存の材質は変えない。検証 `Tests/Occupancy/run-unity.ps1` |
| `OverlayMeshRenderer.cs` | オーバーレイ用メッシュ描画(`Custom/OverlayLine`)。生成は静的ファクトリ `Create(name, alwaysOnTop, parent, persistent)` |
| `ObjectIdInfo.cs` | GameObject のメタデータ(id / status / isFarObject / role)。名前の文字列パースの代わり |
| `CameraCaptureUtil.cs` | `CapturePng(camera, width, height)`。カメラを直接レンダーテクスチャへ描いてPNGにする(画面に重ねたCanvas・OnGUIは写らない)。セーブデータの画像に使う |
| `Billboard.cs` / `UILineRenderer.cs` / `WindowUtil.cs` | 常にカメラを向く / UI 上の線描画 / ウィンドウ操作 |

## 知らないと事故ること

- `PolylineUtils` の引数は点・始点・終点の順。射影する座標系で線分長²が 1e-8 以下なら始点を返す。画面座標とワールド座標を混ぜない。`CubicBezierPoint` は道路プレビュー・実道路・線路・車両基地が共有する(別実装は曲線を食い違わせる)。
- `InfraNetworkUtil`: ノード `type` はビットフラグで、吸着は「ノードのマスク & パスの必要マスク(`GetRequiredNodeTypeForPath`=バックエンド `type_p2n`)が非0」。網互換(電力vs水道)もこのビット積に含まれる。
  - `IsInfraPathType` は `consts.PATH_TYPES_INFRA` と一致させる契約。利用側で `PATH_ELECTRICITY` / `PATH_WATER` を直接列挙しない(種別追加時に片方が取り残される)。端子ノードの所属施設は `BuildingManager.TryGetInfraTerminalOwner`。
- `TunnelSectionUtil` の `SplitRuns` 系と `HasTunnelSection` は `pathFollowsTerrain=false` で地表張り付きショートカットを無効化し、常に深さ評価する(非追従デッキが丘の隆起を見逃さないため)。
- `OverlayMeshRenderer`: 各 Manager で `new GameObject`+`Shader.Find` を書かない。`alwaysOnTop=true` は `_ZTest=Always`(丘の裏も透ける。枠線・ギズモ用)、`false` は LEqual+負のデプスオフセット。
  - 追加設定は `Material` プロパティ(`_DstBlend=One` で加算、`_Tint` でメッシュを作り直さず明滅)。`AddPreTriangulated` は単色版と頂点色版。次の `BeginFrame` まで前フレームのメッシュを保持するので、内容が変わったフレームだけ作り直す。
- `AlertIconFactory`: Sprite 未割当のビットは `GetIcon` が null を返し `AlertManager` が無言でスキップする。アラート定数を追加したらシーンでの割当まで行う(手順は `programs/アラートフラグ管理.md`)。
- `FireEffectFactory`: `AddComponent<ParticleSystem>` 直後は再生状態なので、`duration` / `prewarm` を触る前に `Stop`。
- `VisualEffectManager.ClearTransientEffects` は自分が生成した子(`ownedEffects`)だけを破棄する。同居する `CameraAmbience` 等の子を一括破棄すると環境音が毎フレーム `MissingReferenceException` を出す。

## `Utils.cs`(Scripts 直下)

- `GetGroundHeight(Vector3)`: 地面の高さ。実タイルの高さ(未常駐の位置は全景・背景へフォールバック)で、埋立地の帯の中は max(地形, 天端)(`ReclaimedLandStore.TryGetBandTop`。バックエンドの勾配・パス検証と同じ規則)。同一フレーム・同一XZだけキャッシュし、近傍座標へ丸めない(傾斜地で問い合わせ順に依存する)。地形の更新・登録・除去と埋立地のストアの更新時は `InvalidateHeightCache()`。
- `TryGetTerrainGrid(Vector3, out origin, out spacing)`: 実タイルの格子原点と間隔。
- `ApplyDepthOffsetShader(MeshRenderer, contextName, onInstantiated)`: `DepthOffsetLit` を適用。レンダラーごとに材質を複製するので大量の描画物には使わず、建物は `BuildingManager.GetSharedDepthOffsetMaterials`、道路遠景は `PathManager` の共有材質を使う。路面標示用 `Custom/RoadSurface` は保持する(置き換えると標示が消える)。
- `DestroyObjectAfterFrames(MonoBehaviour, GameObject)`: 2フレーム遅延の破棄(ポッピング防止)。
- `WaitTask(Task)`: Task をコルーチンで待ち、失敗時は例外をログ出力。
- `CalculateAverageWidth(List<float>)`、`TransformBounds(Bounds, Matrix4x4)`(変換後の8隅を包み直す外接AABB)、`XZDistance(Squared)`(水平距離)、`GetCameraHorizontalForward` / `IsInHorizontalView`(視野優先処理の水平視野判定)。
- `CreateOverlayCanvas(name, sortingOrder, persistent)`: ScreenSpaceOverlay の Canvas。sortingOrder が前後関係を決める(`programs/アラートフラグ管理.md` §Canvas sortingOrder 階層)。GraphicRaycaster 付きなので、クリックを受けない画像・文字は `raycastTarget = false` にする。
- `SyncOverlayCanvasClickable(GraphicRaycaster)`: 上の Canvas を入力モードが通常のときだけクリック判定させる(毎フレーム呼ぶ)。編集中も表示するクリック可能ラベル(地区名・資源名)が使う。
- `LoadUIIcon(name)`: `Resources/UIIcons/{name}` の Sprite。Sprite Mode が Multiple だと単体ロードが null になるためサブスプライト先頭を採用する(正しい設定は Single)。
