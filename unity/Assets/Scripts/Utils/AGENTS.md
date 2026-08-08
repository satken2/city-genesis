# Utils フォルダ

汎用ユーティリティの置き場。**新しい汎用処理を書く前に、必ずこの一覧を確認すること。**(Scripts直下の`Utils.cs`にも雑多なユーティリティがあるので併せて確認)

| ファイル | 内容 |
| --- | --- |
| `FloatingOrigin.cs` | カメラが原点から閾値(5000)を超えて離れたら、全Transformを一括シフトして浮動小数点精度を保つ。**Unityのワールド座標はバックエンド座標と常に一致するとは限らない**ことの原因なので、座標を扱う処理は注意 |
| `SpatialHash.cs` | 汎用の空間ハッシュ`SpatialHash<T>`。「半径r以内のオブジェクト候補抽出」はこれを使う(自前で全件走査しない) |
| `ModelManager.cs` | モデルのロードとキャッシュ(シングルトン)。persistentDataPathのglTF/glbを優先し、なければ`Resources/Models`配下のプレハブにフォールバック。同じモデルを二度ロードしない。Resources側のプレハブは`Assets/Models`のFBXから自動生成される(unity直下AGENTS.mdの「モデルアセットの構成」参照) |
| `GeneratedModelUtils.cs` | 自動生成スキーマ(GeneratedModels)関連の補助 |
| `AgentIconFactory.cs` / `BuildingIconFactory.cs` / `PathIconFactory.cs` / `AlertIconFactory.cs` | 各種アイコンの生成。AlertIconFactory は alert ビット種別ごとのアイコンを agent/path 別に管理(Dictionary キーは `(entityType, bit)`)。詳細は `genesis-docs/docs/programs/アラートフラグ管理.md` 参照 |
| `InfraNetworkUtil.cs` | ノードの溶接互換(吸着可否)判定。バックエンドの溶接ルール(ビット積)をフロントで再現する。ノードの`type`はビットフラグ(NODE_ROAD/NODE_RAIL/…と、インフラ財ごとのビット`GetInfraNodeBit`のOR。バックエンド`MasterData.get_infra_node_bit`と一致)で、吸着は**「ノードのtypeマスク & パスのマスク(`GetRequiredNodeTypeForPath`=バックエンド`type_p2n`相当) が非0」**で判定する(`IsNodeSnappable` / `IsPathSnappable`)。網互換(電力vs水道)もこのビット積に畳み込まれており、旧`GetNodeInfraProducts`等の製品集合照合は廃止。マスクがインフラ財ビットを含むか(=インフラノード/インフラパスか)は`IsInfraMask` / `IsInfraPathVariant`で判定する。端子ノードの所属施設識別は依然`BuildingManager.TryGetInfraTerminalOwner`(建物側`infra_node_id`の逆引き)を使う(ノードAPIはownerを公開しない片方向参照)。TerrainInputHandlerのスナップとRoadPlanManagerのガイド描画・端子マーカーが使用 |
| `Billboard.cs` | 常にカメラを向くオブジェクト |
| `IntersectionPolygonUtil.cs` | 交差点ポリゴンの計算 |
| `TunnelSectionUtil.cs` | 地下パス(トンネル)の区間分類。地下判定(`IsUndergroundPath`=負の相対Yアンカーを持つか)、点列の描画区間分割(`SplitDrawableRuns`: 深さ=地形標高−道路Yがクリアランス高さ`height`を超える区間を除外し坑口位置を返す)、トンネル区間の抽出(`CollectTunnelRuns`: 道路エディタのプレビュー用)、**トンネル区間の有無判定(`HasTunnelSection`: 相対Y=0の非追従パスが丘に潜るかを安価に判定。地下メッシュ経路/Far非表示のゲート)**。**`SplitRuns`系と`HasTunnelSection`は`pathFollowsTerrain`引数を取り、falseのとき地表張り付きショートカット(`IsOnGround`)を無効化して常に深さ評価する**(地表近くを通る非追従デッキが誤って追従扱いされ丘の隆起を見逃すのを防ぐ)。バックエンド`geo_path_depth.py`(`path_uses_depth_classification`)と同一の分類定義。PathManager/RoadPath/RoadPlanManagerが使用 |
| `MultiValueTableBuilder.cs` | 複数値テーブルUIの構築 |
| ObjectIdInfo.cs | GameObjectのメタデータ(id/status/isFarObject/role)を保持するコンポーネント。名前の文字列パースに代わる安全なデータ参照手段として各Managerが使用する |
| OverlayMeshRenderer.cs | オーバーレイ用メッシュ描画。`Custom/OverlayLine` シェーダーを使用し、地形へのめり込みを防ぐためデフォルトで負のデプスオフセット（Offset -1, -20）が設定されている。`AddPreTriangulated` は単色版と**頂点色版**の2種(頂点色版はグラデーション表現用)。シェーダーのマテリアルプロパティで `_ZTest`(既定LEqual)、`_SrcBlend`/`_DstBlend`(既定はアルファブレンド。`_DstBlend=One` で加算=光の表現)、`_Tint`(頂点色に乗算。`SetTint` でメッシュを作り直さず明滅させる)を制御できる。 |
| StackedBreakdownMeter.cs | 積み上げメーター表示 |
| `UILineRenderer.cs` | UI上の線描画 |
| `UISoundEffectPlayer.cs` | 効果音再生 |
| `WindowUtil.cs` | ウィンドウ操作 |
| `ZonePolygonPreview.cs` | 地区ポリゴンのプレビュー表示 |

`Utils.cs`(Scripts直下)にも以下の汎用処理がある:
- `ApplyDepthOffsetShader(MeshRenderer, contextName)` ... DepthOffsetSimpleLitシェーダーを一括適用(キャッシュ付き)。PathManager / BuildingManager から使用。
- `DestroyObjectAfterFrames(MonoBehaviour, GameObject)` ... 2フレーム遅延でオブジェクト破棄(ポッピング防止)。各マネージャーから使用。
- `CalculateAverageWidth(List<float>)` ... 幅リストの平均値を計算。歩道コネクタ等で使用。
