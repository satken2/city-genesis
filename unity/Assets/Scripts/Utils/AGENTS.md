# Utils フォルダ

汎用ユーティリティの置き場。**新しい汎用処理を書く前に、必ずこの一覧を確認すること。**(Scripts直下の`Utils.cs`にも雑多なユーティリティがあるので併せて確認)

`IntersectionPolygonUtil.ComputeIntersectionPolygon` の任意の `shapeLayout` は、入口断面の計算後に描画専用の外周を調整する。路面ポリゴンの組み立て・検証は共通のままとし、`MotorwayMergeLayout` が1車線ランプに使用する。保存パスや論理占有ポリゴンを変更する入口ではない。

| ファイル | 内容 |
| --- | --- |
| `PolylineUtils.cs` | 線分・曲線・点列の幾何計算の共通実装。引数は点・始点・終点の順。`ClosestPointOnSegment`はVector2/Vector3の各座標系、`ClosestPointOnSegmentXZ`は水平射影後にYを補間する。`ProjectOnSegment` / `ProjectOnSegmentXZ`は最近接点の位置パラメータt(0〜1)。`DistanceToSegmentSq` / `DistanceToSegment`はVector2の距離。射影する座標系で長さの二乗が1e-8以下なら始点を返す。画面座標とワールド座標を混ぜない。`CubicBezierPoint`(Vector2/Vector3)は三次ベジエの評価で、道路プレビュー(RoadPlanManager)・実道路(PathManager)・線路・車両基地が共有する(別実装を持つとプレビューと実道路の曲線が食い違う)。`SmoothedDirection`は点列の前後辺を平均した進行方向(帯メッシュの断面配置用)。`RoundCorners`は点列の角丸め |
| `PolygonUtils.cs` | 閉じた2D多角形(Vector2の x=X, y=Z)の共通実装。`Contains`は偶奇則(レイキャスティング)の内外判定で、`IReadOnlyList<Vector2>`のほか区画・地区が保持する`float[][]`、資源応答の`List<List<float>>`をそのまま受ける(変換配列を作らない)。境界上の点を確実に含めたいときは`boundaryTolerance`付きを使う(OpenSpaceLayout・車両基地)。`SignedArea`は符号付き面積(正=CCW。巻き順の正規化に使う)、`DistanceToBoundary`は全辺への最短距離。**多角形の内外判定・面積・線分距離を各Managerやビルダーに再実装しないこと** |
| `SpatialHash.cs` | 汎用の空間ハッシュ`SpatialHash<T>`。「半径r以内のオブジェクト候補抽出」はこれを使う(自前で全件走査しない) |
| `ModelManager.cs` | モデルのロードとキャッシュ(シングルトン)。persistentDataPathのglTF/glbを優先し、なければ`Resources/Models`配下のプレハブにフォールバック。同じモデルを二度ロードしない。Resources側のプレハブは`Assets/Models`のFBXから自動生成される(unity直下AGENTS.mdの「モデルアセットの構成」参照) |
| `GeneratedModelUtils.cs` | 自動生成スキーマ(GeneratedModels)関連の補助。`ParseRing(JToken)` は GeoJSON リング `[[x,z],...]` を `List<Vector2>`(x=X, y=Z)に変換する共通入口(配列でなければ空リスト。水面・効果範囲ポリゴンが使用。各Managerに再実装しないこと) |
| `AgentIconFactory.cs` / `BuildingIconFactory.cs` / `PathIconFactory.cs` / `AlertIconFactory.cs` | 各種アイコンの生成。AlertIconFactory は alert ビット種別ごとのアイコンを agent/path 別に管理(Dictionary キーは `(entityType, bit)`)。**`consts.py`の`ALERT_AGENT_*`・`ALERT_PATH_*`のうち、アイコンで表現するビットに`[SerializeField] Sprite`フィールドを持つ**(火災系2ビット=`ON_FIRE`/`ACCIDENTAL_FIRE`はパーティクル表現のためフィールドを持たない)。Sprite未割当のビットは`GetIcon`がnullを返し`AlertManager`が無言でスキップするため、アラート定数を追加したらフィールド追加・`InitializeAlertIcons`への登録・シーン(`GameScene.unity`)での割当まで行うこと。詳細は `genesis-docs/docs/programs/アラートフラグ管理.md` 参照 |
| `FireEffectFactory.cs` | 火災(炎+火の粉+黒煙+点光源)・失火(白煙のみ)のパーティクル一式を、建物の底面半径と高さに合わせて手続き的に組み立てる静的ファクトリ。**テクスチャ・マテリアルはコード生成で全エフェクト共有**(アートアセット不要。マテリアルは3種類しか存在しない)。描画は `Resources/Shaders/FireParticle.shader`(前乗算アルファ+ソフトパーティクル)。**`AddComponent<ParticleSystem>`直後は playOnAwake で再生状態のため、`duration`/`prewarm` を触る前に必ず `Stop` すること**(停止中しか変更できない)。煙の「ゆらゆら」は `SetSwayNoise` が担当し、通常の `SetNoise` と違って **`damping=false`(有効だと低周波でほぼ揺れない)+ `separateAxes` で水平だけ強く揺らす**(Yも同じ強度で揺らすと上昇が波打って痙攣して見える)。`prewarm` は `duration` ぶんしか空回ししないので **`duration` は寿命上限に合わせる**(足りないと煙柱が上まで伸びない)。呼び出しは `DataLoaders/FireEffectManager`。詳細は `genesis-docs/docs/geometries/S_火災エフェクトの描画.md` 参照 |
| `InfraNetworkUtil.cs` | ノードの溶接互換(吸着可否)判定。バックエンドの溶接ルール(ビット積)をフロントで再現する。ノードの`type`はビットフラグ(NODE_ROAD/NODE_RAIL/…と、インフラ財ごとのビット`GetInfraNodeBit`のOR。バックエンド`MasterData.get_infra_node_bit`と一致)で、吸着は**「ノードのtypeマスク & パスのマスク(`GetRequiredNodeTypeForPath`=バックエンド`type_p2n`相当) が非0」**で判定する(`IsNodeSnappable` / `IsPathSnappable`)。網互換(電力vs水道)もこのビット積に畳み込まれており、旧`GetNodeInfraProducts`等の製品集合照合は廃止。マスクがインフラ財ビットを含むか(=インフラノード/インフラパスか)は`IsInfraMask` / `IsInfraPathVariant`で判定する。**パス種別そのものがインフラかの判定は`IsInfraPathType`(送電`PATH_ELECTRICITY`/導水`PATH_WATER`/パイプライン`PATH_PIPELINE`の3種)で、バックエンドの共有定数`consts.PATH_TYPES_INFRA`と一致させる契約**(種別が増えたらこの3つ組と同時に直す。「財を区別しない共通のインフラ挙動」=交差点メッシュの対象外・川の地表横断判定の対象外などの判定にのみ使い、電力網と水網の物理的分離はノードビット側が受け持つ)。**利用側で`PATH_ELECTRICITY`・`PATH_WATER`を直接列挙しないこと**(種別追加時に片方だけ取り残される)。端子ノードの所属施設識別は依然`BuildingManager.TryGetInfraTerminalOwner`(建物側`infra_node_id`の逆引き)を使う(ノードAPIはownerを公開しない片方向参照)。TerrainInputHandlerのスナップとRoadPlanManagerのガイド描画・端子マーカーが使用 |
| `Billboard.cs` | 常にカメラを向くオブジェクト |
| `IntersectionPolygonUtil.cs` | 本線と共有する`PathEndSection`、外周`IntersectionCorner`、経路距離評価`AtDistance`、点列の切断`Trim`、保存制御点を使う`SamplePlan`。空間不足は`RequiresMoreSpace`で明示する |
| `TunnelSectionUtil.cs` | 地下パス(トンネル)の区間分類。地下判定(`IsUndergroundPath`=負の相対Yアンカーを持つか)、点列の描画区間分割(`SplitDrawableRuns`: 深さ=地形標高−道路Yがクリアランス高さ`height`を超える区間を除外し坑口位置を返す)、トンネル区間の抽出(`CollectTunnelRuns`: 道路エディタのプレビュー用)、**トンネル区間の有無判定(`HasTunnelSection`: 相対Y=0の非追従パスが丘に潜るかを安価に判定。地下メッシュ経路/Far非表示のゲート)**。**`SplitRuns`系と`HasTunnelSection`は`pathFollowsTerrain`引数を取り、falseのとき地表張り付きショートカット(`IsOnGround`)を無効化して常に深さ評価する**(地表近くを通る非追従デッキが誤って追従扱いされ丘の隆起を見逃すのを防ぐ)。バックエンド`geo_path_depth.py`(`path_uses_depth_classification`)と同一の分類定義。PathManager/RoadPath/RoadPlanManagerが使用 |
| `MultiValueTableBuilder.cs` | 複数値テーブルUIの構築 |
| ObjectIdInfo.cs | GameObjectのメタデータ(id/status/isFarObject/role)を保持するコンポーネント。名前の文字列パースに代わる安全なデータ参照手段として各Managerが使用する |
| OverlayMeshRenderer.cs | オーバーレイ用メッシュ描画。**生成は静的ファクトリ `Create(name, alwaysOnTop, parent, persistent)` を使う**(GameObject生成+`Custom/OverlayLine` マテリアル+`Initialize` を一括で行い、シェーダー参照をキャッシュする。各Managerで `new GameObject`+`Shader.Find` を書かないこと)。`alwaysOnTop=true` は `_ZTest=Always` で地形・建物に埋もれず常に手前に描く(丘の裏も透けるX線状。枠線・ギズモ・ハイライト用途で採用済みのトレードオフ)、`false` はシェーダー既定の LEqual+負のデプスオフセット(Offset -1, -20)で塗りが建物に自然に隠れる。描画キューや加算合成など追加設定は `Material` プロパティ(レンダラー専用マテリアル。複製しない)に対して行う。メッシュとマテリアルは `OnDestroy` で自動解放する。`AddPreTriangulated` は単色版と**頂点色版**の2種(頂点色版はグラデーション表現用)。シェーダーのマテリアルプロパティで `_ZTest`、`_SrcBlend`/`_DstBlend`(既定はアルファブレンド。`_DstBlend=One` で加算=光の表現)、`_Tint`(頂点色に乗算。`SetTint` でメッシュを作り直さず明滅させる)を制御できる。 |
| StackedBreakdownMeter.cs | 積み上げメーター表示 |
| `UILineRenderer.cs` | UI上の線描画 |
| `UISoundEffectPlayer.cs` | 効果音再生 |
| `VisualEffectManager.cs` | 建物・道路の完成/解体遷移でパーティクルと単発3D音を鳴らすシングルトン。**シーン配置ではなくAppRootが生成する**。音量は`ambience:volume`(master×sfx)購読×基準ゲイン0.8で、受信前は無音。クリップはインスペクタ未設定なら`Resources/Audio/Effects/construction-complete`・`demolition`を探す(現状どちらも未配置=無音)。呼び出しはBuildingManager/PathManager/ParcelManagerの`TriggerBuildingStatusEffect`等 |
| `WindowUtil.cs` | ウィンドウ操作 |

爆破操作は `FireEffectFactory.CreateExplosion`（共有の炎・煙素材で有限バースト）と `VisualEffectManager.PlayExplosion` を使う。音源は `Resources/Audio/Effects/building-explosion.ogg`。通常の完成/解体遷移には爆発を割り当てない。単発音は既存の `Play3DSound` で距離減衰と `ambience:volume` を反映し、再生中の音量変更にも追従する。建物と独立した子オブジェクトを実時間で後始末し、`ClearTransientEffects` は都市の全消去に連動する。AppRootのGameObjectには`CameraAmbience`の音源や天候の反射プローブ・雨粒など他コンポーネントの子も同居するため、`ClearTransientEffects`は自分が生成して登録した子(`ownedEffects`)だけを破棄する。`transform`の子を一括破棄すると環境音が`MissingReferenceException`を毎フレーム出す。検証は `Tests/Explosion/run-unity.ps1`。

`Utils.cs`(Scripts直下)にも以下の汎用処理がある:
- `GetGroundHeight(Vector3)` ... 同一フレーム・同一XZ座標だけをキャッシュする実タイル高さ取得。近傍座標への丸めは禁止（傾斜地で問い合わせ順に依存する高さになる）。地形の更新・登録・除去時は `InvalidateHeightCache()` を呼ぶ。
- `TryGetTerrainGrid(Vector3, out Vector2 origin, out Vector2 spacing)` ... 実タイルの格子原点と間隔。全タイル共通解像度の格子にfloorを合わせるために使う。
- `ApplyDepthOffsetShader(MeshRenderer, contextName)` ... DepthOffsetLitシェーダー(URP Litベース+Offset -1,-1のZ-fighting対策バリアント)を一括適用(キャッシュ付き)。PathManager / BuildingManager から使用。
- `DestroyObjectAfterFrames(MonoBehaviour, GameObject)` ... 2フレーム遅延でオブジェクト破棄(ポッピング防止)。各マネージャーから使用。
- `CalculateAverageWidth(List<float>)` ... 幅リストの平均値を計算。歩道コネクタ等で使用。
- `TransformBounds(Bounds, Matrix4x4)` ... ローカルAABBを行列で変換した後の外接AABB(8隅を包み直す)。プロップ・柱・環境車両の配置後バウンズ計算で共用。
- `WaitTask(Task)` ... Task の完了をコルーチンで待ち、失敗時は例外をログ出力する。ApiClient / BuildingManager / PathManager / TerrainManager のバックグラウンド処理待ちで共用(各Managerに再実装しないこと)。
- `XZDistanceSquared(Vector3, Vector3)` / `XZDistance(Vector3, Vector3)` ... 高さ差を無視した水平距離(の二乗)。車両・歩行者の描画半径判定、停留所の道路近接判定で使用。
- `CreateOverlayCanvas(name, sortingOrder, persistent)` ... ScreenSpaceOverlay の Canvas(CanvasScaler+GraphicRaycaster付き)を生成する。3Dオブジェクト追従ラベルの土台で、ZoneManager(60)・ResourceManager(55)・AlertManager(70)が共用。sortingOrder が前後関係を決める。
- `LoadUIIcon(name)` ... `Resources/UIIcons/{name}` の Sprite をロードする。Sprite Mode が Multiple でインポートされていると単体ロードが null になるため、その場合はサブスプライトの先頭を採用する(正しい設定は Single)。編集モードのラジアルメニュー/HUDが使用。

PathSummaryExtensions.GetRelativeHeightAtPointはstation_grade_profileがあればXZ射影した設計絶対標高からUtils.GetGroundHeightを引く。基準線なしでは既存のアンカー相対Y補間を使い、描画Yのオフセットを混入させない。

`OccupancyVolumeUtil` は道路幅×占有高さの区間箱と、建物フットプリントの外接矩形×高さの警告箱を三角形列へ追加する。RoadPlanManagerのローカル警告とHighlightManagerの権威警告で共用し、既存オブジェクトのマテリアルを変更しない。検証は `Tests/Occupancy/run-unity.ps1`（独立Unity、結果と画像はTemp/OccupancyQA）。
