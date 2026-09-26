---
name: genesis-path-parts
description: City Genesisの道路・線路パーツ3Dモデル(body/line/sidewalk/wall/tree/pillar/track/platform等のFBX)をヘッドレスBlenderで制作・検証するときに使う。「道路モデルを作って」「線路パーツを作りたい」「歩道やガードレールを追加」など、Assets/Models/Paths配下のFBXを新規作成・修正する作業では必ずこのSkillに従うこと。Unity側のメッシュ生成コードの話は genesis-path-geometry を使うこと。建物パーツの制作は genesis-building-parts を使うこと。
---

> **パス表記の基準**: 本文中のパスはワークディレクトリ `S:/city/genesis`(`city-genesis` と `genesis-core` を並べた作業ツリー)を起点とする。`city-genesis` 単体で作業している場合は先頭の `city-genesis/` を読み替えること。`genesis-core/` 配下は非公開のコアシミュレータなので、手元に無ければその項目は読み飛ばしてよい。

# 道路・線路パーツの制作・検証

制作基盤(ヘッドレスBlender・ライブラリ.blend・バリデータ・ギャラリー)は
建物パーツと共通なので、**まず [[genesis-building-parts]] の「実行環境」と「鉄則」を読むこと**
(Blender の実体と起動方法は Linux サーバーと Windows で異なる。Windows は Microsoft Store 版で
stdout が返らない等の罠がある)。本Skillはパス(道路・線路)固有の差分を記述する。

## 鉄則(パス固有)

1. **規約の正本**は次のドキュメント。制作前に必ず読む:
   - `city-genesis/docs/docs/geometries/G_道路メッシュの生成.md`
     §5(マスターデータと寸法)・§6(命名とモデリング規約)・§7(フォールバック)・§8(各パーツの生成詳細)
   - 線路・駅は `N_線路メッシュの生成.md` §1(パーツ構成)・§1.2(ピボット規約)・§2(電化)・§7(駅パス)
2. **最小構成から始める**(下記)。パーツ種類は多いが全部作る前提ではない。
3. **マテリアルは既存を流用する。** 道路系の接頭辞は `PATH_*`
   (`pipeline/materials.py --category Paths` で一覧・検索)。
4. **寸法はマスターデータに従う。** `master.yaml` の `path_master[variant]` の
   `width` / `sidewalk_width` / `side_width` / `wall_width` / `center_width` /
   `tree_spacing` / `pillar_spacing` / `wire_spacing` / `platform_width` が
   配置の基準になる。**`width` は道路全体の幅で、歩道はその内側に含まれる**
   (歩道幅を足すのではない)。
5. モデルの編集は `city-genesis/blender/paths.blend`。書き戻しは
   `pipeline/library.py export --category paths --only p1/sidewalk`。

---

## 通常・接写モデルの作り分け（制作時に必ず確認）

**道路・線路の制作では、通常と接写で何を分けるかを必ず決める。** 既存モデルと組み立て外観を確認し、通常の形状・面数を基準にする。LOD追加だけを理由に道路の規格、軌間、ホーム形状、構造形式や設備配置を変更しない。規約の正本はG文書§6〜7、線路の適用対象はN文書§1。

| 詳細度 | 命名と制作方針 |
|---|---|
| Normal（無印、必須） | `p{variant}/{part}.fbx`。走行面、縁、軌道、構造の輪郭を保ち、細かな金物・溝・面取りを抑える。大量反復されるので単品の面数だけで判断しない |
| Close（必要な部品のみ） | 同じフォルダの `{part}_close.fbx`。留め具、レール締結部、手すりの細い桟、厚みや面取りなど、近距離で見える構造上妥当な細部を加える |
| Far | Unityの遠景生成を使う。パス用の `_far` は著作しない |

ホーム451/461・462・502と乗り場602のFarは`FarPlatformPath.cs`の簡易断面生成。通常と同じ共有材質で大きな屋根・壁・床を保つ。通常モデルの外形・材質を変えた場合は、この遠景断面との整合も確認する。通常・接写の8m反復とは別に、間引き済みのパス点列を使う。

### `_close` が実際に使われる対象

- モノレール502のホームドアはplatform本体に組み込む。通常・接写とも上屋と同じ8m周期で、独立パーツに分けない。非公開制作ツールのplatform_doors.pyが共通形状、build_monorail.pyとbuild_platform_close.pyが各LODを著作する。閉状態の外観モデル。

- ロープウェイ602の`station_shell`も`CreateSplineMesh`を通り、`station_shell_close`へ切り替わる。ホーム451/461・462・502と602の接写設備は、非公開の制作ツール`genesis-gallery/pipeline/jobs/build_platform_close.py`が通常モデルから著作する。通常の各ジョブを再実行したら接写ジョブも後に実行する。8m周期・原点・bbox・材質を保持する。再生成・実Unity検証・ミラー同期は同ツールの`pipeline/jobs/platform_close.md`を参照。

- スプラインに沿って敷く `body / line / sidewalk / wall / center`、線路の `track / wire / platform / platform_wall` が対象。高架用も `body_elevated_close` / `track_elevated_close` の命名で扱う。
- 送電線1101/1102の`pillar_cap`は専用生成器がCloseへ切り替える。それ以外の`tree / pillar / pillar_cap / portal / pole / beam`などのInstantiate配置、道路の点設備、ノード内のコネクタ・終端部品は無印を使う。**`_close` を書き出すだけでは切り替わらない。** 対象外パーツの接写対応を求められた場合は `genesis-path-geometry` で読み込み経路を確認し、アセットだけで対応済みとしない。
- 対象パーツのClose解決順は、バリアントの `_close` → バリアントの無印 → タイプの `_close` → タイプの無印。共通タイプにCloseを追加しても、専用バリアントの無印が優先される場合がある。

### 造形・軽量化・検証

- **無印と原点・bbox・マテリアル構成を一致させる。** 路面高、横幅、タイル長、端の接続断面を変えずに差し替えられる形にする。建物の「前方へ1.5m追加可」の例外はパスに適用しない。
- 舗装目地・路面模様・広告文字は画像や法線マップを優先し、石や文字を1個ずつメッシュ化しない。画像で十分な部品は通常と接写で同じ簡素な形状を使い、同形の `_close` を量産しない。画像の追加は `asset-credits` に従う。
- **連続部材と点設備を混同しない。** 短く圧縮・反復される壁や歩道に、一定間隔で置くべき設備を焼き込まない。軌道・ホームなど著作周期を保つパーツは、下記のタイル長・曲線用分割規約を維持する。左右ミラーで文字が反転しないかも確認する。
- Normalで過剰な金物や細分割は省き、Closeへ寄せる。ただし走行面、レール頭部、ホーム縁、支持構造の連続性を壊す間引きはしない。通常の意匠を保ったまま、実際の反復数を含めて軽量化する。
- ギャラリーを更新し、変更前／通常／接写を同じ長さ・視点で比較する。単品に加えて、直線・曲線・左右配置・接続端を確認し、対象なら高架や駅でも確認する。通常・接写の三角形数とUnity推定頂点数を記録し、バリデータと対象のUnity検証を実行する。パーツの見た目だけでなく、実際のClose解決経路で切り替わることまで確認する。

## 最小構成の原則

**必須は `body` ただ1つ**(線路は `body`=道床 と `track`=軌道)。
これが無いとスプラインに沿った白い板にフォールバックする。
他はすべてオプションで、無ければそのパーツをスキップするだけで破綻しない。

| ユーザーの要求に含まれる特徴 | 足すパーツ | 必要なマスター値 |
|---|---|---|
| (必須) 路面・道床 | `body` | `width` |
| 区画線・センターライン | `line` | `side_width` |
| 歩道 | `sidewalk` | `sidewalk_width` |
| 中央分離帯 | `center` | `center_width`(無ければ `line` で代用される) |
| 防音壁・ガードレール | `wall` | `wall_width` |
| 街路樹 | `tree` | `tree_spacing` |
| 高架にできる道路の柱 | `pillar` (+ `pillar_cap`) | `height_editable=true`, `pillar_spacing` |
| 地下道の坑口 | `portal` | — |
| **線路**: 軌道(レール+枕木) | `track` | `lanes` |
| 分岐器のレールコネクタ | `rail` (無ければ `track` で代用) | — |
| 電化(架線柱・門型梁) | `pole` / `beam` | `wire_spacing` |
| 駅のホーム | `platform` / `platform_wall` / `platform_panel` | `platform_width` |
| LRT停留所のホーム | `lrt_platform`(+ `lrt_platform_end` / `lrt_shelter`) | transit_master のLRT停留所リスト(下記「LRTの併用軌道と専用軌道」) |

### 既存アセットの実例

```
p201/p2001/p2011   1個  body                      (最小構成)
p1   4個  body line sidewalk tree                 (街路=PATH_STREET のタイプ既定)
p2   8個  body line sidewalk tree wall center pillar portal   (高速道路=PATH_MOTORWAY のタイプ既定)
p401 3個  body track portal                       (最小の線路)
p403 3個  body track wall                          (鉄橋。wallが主構トラス)
p4   8個  body track platform pole beam pillar wall portal    (電化+駅)
p411 5個  body track wire pole beam                (電化地上線路)
p413 6個  body track wall wire pole beam           (電化鉄橋。poleはトラス上弦取付)
p461 8個  body track platform platform_wall platform_panel wire pole beam (電化駅+上屋)
```

電化バリアント(411系/461系)のパーツは `pipeline/jobs/build_electric_rail.py` の
コード著作。寸法調整はジョブを直して再実行する(手編集すると再実行で消える)。

まず最小構成で作ってギャラリーの組み立てプレビューを見せ、ユーザーの追加指示
(「歩道をつけて」「防音壁を」)を受けてから足す。

---

## ピボット規約(最重要・ここを間違えると必ず破綻する)

配置側はパーツの**原点**を高さオフセットに置き、横位置だけ bounds 中心で補正する。
つまり**高さは原点、横は bounds** が基準になる。

| パーツ | 高さ方向のピボット | 横方向・向き |
|---|---|---|
| `body` | **路面の上面**(厚みは下方向へ伸ばす) | 中心線に配置。X幅は `width` にスケールされる |
| `line` / `sidewalk` / `wall` | **下端**(上方向へ伸ばす) | 左右2つずつ配置 |
| `center` | 下端 | 中心線に配置 |
| `tree` | **接地点**(原点=地面) | Instantiate配置。スケールなし |
| `pillar` | **天端=原点**(柱は下方向へ長めに伸ばす) | 中心線。地下パスには配置されない |
| `pillar_cap` | 路面原点(下方向へ厚みを持つ) | 中心線。Zスケールされない |
| `portal` | 接地点 | **ローカル +Z がトンネル内部**を向く |
| `track` / `rail` | **枕木の下面** | 軌道スロットに実寸配置(スケールなし) |
| `pole` | 設置面(柱は上方向) | 「右側用」。腕金は -X(軌道中心)向き。左側はY軸180°回転 |
| `beam` | スプライン中心の設置面 | X幅が柱間に引き伸ばされる |
| `platform` / `platform_wall` | **原点=線路中心線・線路面** | 「右側用」。縁は X=+1.5m 固定、上面造形は原点から1.5m高さ |
| `lrt_platform` | **原点=軌道中心線・レール踏面**(上面は踏面+0.30m) | 「右側用」。乗降縁は X=+1.35m 固定。左側はXミラー |
| `lrt_platform_end` | デッキ端の断面中央・レール踏面 | +Z がデッキから外向き。左右対称でミラーしない |
| `lrt_shelter` | デッキ上面・足跡中心 | 開放面が -X(軌道側)。左側はY軸180°回転 |

- **Z方向(進行方向)が1タイル**の長さ。道路の連続面は10m程度でよい(生成側が1m以下へ圧縮して反復する)。
  支柱・ベンチを含むホームは8mの造形周期を保ち、曲線用の分割をモデル内に持つ。
- **歩道は左側用（Unityの道路中心側が+X）**で右側をXミラーにする。
  **壁・ホームは右側用（道路中心側が-X）**で左側をXミラーにする。
  **架線柱poleは右側用**で左側をY軸180°回転する。
  **標準FBX経由では Blender(x,y,z)→Unity(-x,z,-y)** になる。
  ホームの右側用はBlenderで **-X側**、UnityのsharedMeshで+X側に置く。
  Blenderの再インポートだけではUnityの符号を検証できないため、実Unityでも確認する。
- **ホームの8mタイルを道路の1m上限で圧縮しない。** `TrackPath` は
  platform / platform_wall を `maxTileLength: 0` で配置し、曲線追従のため
  モデル内を長手方向1m以下で分割する。上屋柱4m・柵柱1.6mの著作周期を保つ。
- **`pillar` は Unity が「天端から地面までの高さ」を丸ごとZスケール倍率として掛ける。**
  天端を原点より下に著作すると、そのオフセットにも倍率が掛かって沈み、
  **どんな高さの高架でも橋脚が地中に埋もれて一切見えなくなる**(バリデータの
  `pillar-top-offset` で検出)。下端は倍率で伸びすぎても地中に隠れるだけなので、
  柱は一様断面で長めに(2〜8m)著作してよい。橋脚梁・ハンチ・門型ラーメンの横梁など
  **スケールさせたくない造形は `pillar_cap` へ分ける**(キャップは実寸のまま置かれ、
  その深さぶん柱が下げられるので天端と自動的に噛み合う)。
- **高架駅(`pillar` と `platform` を両方持つバリアント)では支柱を落とす地面が無い。**
  地上駅のホームを流用すると支柱・リブが空中で途切れる。桁から張り出した床版
  (線路面と同じ高さの連続スラブ)と受け梁を `platform` プレハブに持たせ、
  ホームの支柱はその上に立てる。橋脚は中心線に左右対称に置かれるため、
  片側ホームに合わせて横梁をホーム外縁まで伸ばすと反対側で梁が宙に突き出す。
- **同一バリアント内で高さの基準を必ず統一する。** 既存の p1/p2 は body の上面が
  原点より 0.5m 上にある著作で、上の規約とはずれているが、`line`(0.50〜0.52)・
  `sidewalk`(0.49〜0.74)も同じ基準で作られているため成立している。
  **新規バリアントは上表の規約で作る。既存バリアントにパーツを足す場合は、
  そのバリアントの既存パーツに基準を合わせる**(混ぜると歩道が路面に埋まる)。

---

## 制作の標準手順

1. `master.yaml` の `path_master[variant]` で寸法・電化・柱間隔・ホーム幅を確認する
2. **最小構成のパーツ種類を決める**。既存の似たバリアント(`p1`/`p2`/`p4`)の構成を
   `ls Assets/Models/Paths/p1/` で見て参考にする
3. 既存バリアントに足す場合は、そのバリアントの既存パーツの寸法・高さ基準を
   `pipeline/materials.py --category Paths` と inspect JSON
   (`genesis-gallery/out/inspect/Paths/<variant>/<part>.json`。`genesis-gallery` は作業ツリーに
   並置される**非公開**の制作・検証ツールで、手元に無ければ既存FBXの実測で代用する)で確認する
4. `city-genesis/blender/paths.blend` を開いてコレクション(例 `p2/wall`)を追加・編集
   (`p{数字}` はバリアントIDだが、`p1`/`p2`/`p3`/`p4`/`p12` のような小さい番号は
   `path_type` のタイプ既定パーツ。`p1`=街路・`p2`=高速道路)
5. `pipeline/library.py export --category paths --only p2/wall` でFBX化
6. `validator/validate.py <FBX>` (パスはマテリアル命名検査が対象)
7. `pipeline/build_gallery.py` → ギャラリーの組み立てプレビュー(直線30m敷設)で目視確認
   - `pillar` を持つバリアントは高架状態でプレビューされる
   - `platform` を持つ駅バリアント(`platform_width`設定)は片側ホーム
     (淵+動的デッキ+外縁)付きでプレビューされる(デッキがモデルと食い違えば
     著作寸法が規約とずれているサイン)
   - 電化バリアント(`wire_spacing`設定)は架線・架線柱・門型梁付きでプレビュー
     される(柱間隔は見やすさのため最大15mに詰められる)
   - 線路の軌道・架線は `lanes` 本を TrackPath と同じ横位置(幅を lanes 等分した各中心)に並べる
   - `lrt_platform` を持つLRTバリアントは、直線56mの中央に2両編成相当の相対式ホーム
     (両側・両端スロープ・中央の上屋)を置き、`pillar` があっても地上で見せる
   - 交差点・曲線・分岐器・近接線路の結合描画はプレビュー対象外(実機でしか確認できない)
8. **マスターデータの更新が必要か判定し、必要なら反映する**(下記)

---

## モデルを作った後: マスターデータの更新

**パスは特に、パーツを作っただけでは一切描画されないものが多い。**
対応する寸法・間隔がマスターに入っていないと、そのパーツはスキップされる。
モデル作業の最後に必ず下表を確認し、更新が必要なら
[[master-data]] Skill に従ってGoogleスプレッドシートを更新すること。
`master.yaml` は編集禁止の自動生成物なので**yamlを直接書き換えてはいけない**。
シート更新 → `create_path_master.py` で再生成 → `git diff` 検証、の順で進める。
書き込み権限が無ければ、変更するセルと値を表にしてユーザーに依頼する。

| 作ったパーツ | 入れる必要があるマスター項目(タブ: path_master) |
|---|---|
| `body` | `width`(道路全体の幅。歩道はこの内側に含まれる) |
| `sidewalk` | `sidewalk_width`(0だと歩道の幅が無く実質描かれない) |
| `line` | `side_width` |
| `wall` | **`wall_width`。null だと防音壁は一切描画されない** |
| `center` | **`center_width`。null だと中央分離帯は描画されない** |
| `tree` | `street_tree_type_allowed` / `default_street_tree_type`。個別Pathの `street_tree_type` で有無を選ぶ。`tree_spacing` がnull/0なら20m |
| `streetlight` / `fence` / `utility_pole` / `utility_box` | `streetlight_type_allowed` / `fence_type_allowed` / `utility_mode_allowed` と各 `default_*`。描画は個別Pathの設備設定に従う(G §8.6) |
| `pillar` / `pillar_cap` | `height_editable=true` と `pillar_spacing`(高架にできないバリアントでは柱は出ない) |
| `pole` / `beam` | **`wire_spacing`。null は非電化を意味し架線・柱・梁を一切描画しない** |
| `platform` / `platform_wall` / `platform_panel` | **`platform_width`。null はホームなし** |
| `track` | `lanes`(軌道の本数)・`track_type` |
| 新しいバリアントを作った | `type`(フォールバック先のタイプ)・`model_type`・`body_material`・`curve_allowed`・`max_span` 等の行一式 |

パーツの造形寸法とマスター値は**一致させる**こと(特にホームは
`width` / `platform_width` とずれるとデッキと食い違う。N doc §7.3)。

## 知らないと事故る仕様

- **フォールバックは バリアント → タイプ の2段**(`p{path_variant}` → `p{path_type}`)。
  `path_master.type` が同じバリアント同士でパーツを共有できる。
- `center` が無い場合は `line` が代用される。道路設備(tree/streetlight/fence/utility_pole/utility_box)はモデルが無くても簡易形状で描画し、それ以外の付属パーツは無ければスキップ。
- 道路本体(body)が無いと白い板。線路は **body と track が両方無い場合のみ**白い板
  (道床だけ・軌道だけでも描画は成立する)。
- `platform_panel` は**マテリアル参照専用**でメッシュは描画されない(ホーム上面は
  コードが動的生成する)。建物の `generic-wall` と同じ扱い。
- 高架/地下は同じパーツを高さ違いで使う。`pillar` は `height_editable=true` の
  バリアントのみ、かつ地下(負のY)には配置されない。
- 道路設備は切断済み区間の端から3mを空け、設備ごとの開始位相と間隔で配置する(G §8.6)。
  点設備の接地原点はZ=0、街灯は右側配置を基準に-Xへ向ける。柵モデルに支柱を含めない。
  電柱の電線取付位置は接地面+7.6m。柱・鉄道架線柱の配置規則とは区別する。

## LRTの併用軌道と専用軌道

LRTの線路は1本のパスで複線(上下線)を表す。222（幅24m・4車線）／242（幅32m・6車線）は中央寄りの左右1車線を自動車と共用し、軌道中心は最内車線中心（±2.5625m／±2.541667m）。303（幅12m）は中心±1.75mの2軌道、431（幅7m・lanes 2）はTrackPathが±1.75mへ2軌道を敷く（軌道中心間隔3.5m）。道路の`lanes`を道路上の軌道数として扱わない。詳細は公開G文書「LRT付き道路222・242・歩行者道303」、専用軌道431はN文書を参照。

- 道路用は`lrt_track`／`lrt_wire`／`lrt_pole`。RoadPathがLrtRoadPartsで追加配置する。通常鉄道のtrack/pole/beamと混ぜない。
- lrt_trackは幅3m、レール中心±0.7175m、踏面Z=0.025m。lrt_wireの中心はZ=5.30m。原点はどちらも路面で、UnityはBODY_ABOVE_SPLINEを加える。
- lrt_poleは道路中心・路面原点の実寸配置。222/242は中央T字柱、303は両側X=±5.4mの2本柱で、各腕が近い方の軌道（±1.75m）へ出る。bounds中心へ再配置しない。架線系はwire_spacingが正の場合だけ描く。
- 303のbodyは幅12mの段差のない舗装。石畳|x|<3.25m（軌道敷）・白い境界帯3.25〜3.45m（PATH_CONCRETE）・暖色レンガ3.45〜5.8m・縁5.8〜6.0m。設備列は±5.1m。
- 431のbody/body_closeは幅7.0mの複線道床（411の断面意匠。肩±3.2m・下端-0.55m・軌道間に+0.09mの盛り）。軌道の道床天端層（外端は軌道中心から1.6m）を法面から浮かせないための専用形状。それ以外（track/wire/pole/beam/高架系/portal）は411とメッシュ共有する。
- 新規バリアントはpath_masterだけでなく、transit_masterのLRT走行・停留所リストにも追加する。

### LRT停留所のホーム

Unityが停留所の下地パスへ相対式ホーム（各軌道の外側）を自動で描く。部品は各バリアントのフォルダに置き（p242はp222と同形状を共有）、無ければスキップする（`lrt_platform`だけは簡易スラブで代替）。通常（無印）のみ著作し、`_close`は作らない。下表の寸法はUnity座標（+X=軌道から外向き、+Y上）で、レール踏面・軌道中心が基準。

| パーツ | 原点と配置 | 寸法・造形 |
|---|---|---|
| `lrt_platform` | 軌道中心線・レール踏面。右側用（Blender -X）で左側はXミラー。著作長4mを`maxTileLength: 0`で反復するため、モデル内を長手1m以下で分割する | 乗降縁X=1.35（車体半幅1.27+0.08）、デッキ上面Y=+0.30、幅W=1.2m（222/242）／2.0m（303/431）、側面の下端Y=-0.15（222/242/303）／-0.9（431）。内縁に白い縁石と点状ブロック帯、上面は舗装、外縁に防護柵（222/242/431。303は無し） |
| `lrt_platform_end` | デッキ端の断面中央（横位置=軌道中心+1.35+W/2）・レール踏面。+Zがデッキから外向き。左右対称でミラーしない | 上面はZ=0でY=+0.30から、222/242/303はZ=3.0でY=-0.025（路面）、431はZ=6.0でY=-0.475（スプライン基準面）まで下る。X±W/2、側面は各下端まで |
| `lrt_shelter` | デッキ上面・足跡中心。開放面が-X（軌道側）。右側は外縁に背を付け、左側はY軸180°回転 | 足跡X∈[-0.45,0.45]・Z∈[-2,2]。背面の2本柱と片持ち屋根（上面から約2.4m）、背面の板の前にベンチ。全バリアント共有（p222で著作） |

- 材質は既存のみ（PATH_CONCRETE・PATH_PAVING_STONE・PATH_YELLOW_BLOCK・PATH_METAL・PATH_WOOD・BD_WINDOW・BD_CONCRETE）。外部素材を取り込まない。
- 柵は外縁から0.075m以内、上屋の背面の柱・板は外縁から0.09m以上内側に置き、上屋を外縁に寄せても柵と重ならないようにする。
- 非公開の制作ツールにある`genesis-gallery/pipeline/jobs/build_lrt_paths.py`が著作元（ホーム部品・431道床を含む。303の舗装と設備は`build_mall_paths.py`）。exportの`--only`は部分一致なので、変更したものだけを指定する（例 `p222/lrt_platform,p222/lrt_shelter,p303/body,p303/lrt_pole,p431/body,p431/lrt_`）。`p431/body`は`body_elevated`にも一致する。
- 検証はバリデータ（lrt_track/lrt_wire/lrt_pole/lrt_platform/lrt_platform_end/lrt_shelterの原点・寸法。デッキ上面は材質別bboxで測る）と`city-genesis/unity/Tests/Lrt/run.ps1`。レール位置、直立柱、カーブ、相互接続、FBXとラッパープレハブのGUIDに加え、実FBXとUnity定数の突き合わせ（303/431の舗装・道床の全幅12m/7m、303の柱の足元±5.4m=`LrtRoadParts.MALL_POLE_OFFSET`と吊り金具±1.75m、ホーム部品の内縁・幅・上面・著作長、スロープの長さと下端=前後の地面）を確認する。

## 歩行者モールの点設備

303/304/305の舗装・bench/bin/planter/advert/treeは非公開の制作ツール`genesis-gallery/pipeline/jobs/build_mall_paths.py`が著作する。304は幅12mの中央島、305は幅20mで中心から左右4mの2列、303は複線LRTの外側（±5.1m）に並べる小型設備。新設備は`bench_type`/`bin_type`/`planter_type`/`advert_type`の各`*_allowed`・`default_*`と個別Pathの0/1選択に従う。規約・位相・向きはG文書§5.5、検証はTests/Lrt/run.ps1。

## 送電線1101・1102

鉄塔pillar_capは通常と_closeを著作し、PowerLinePathがPathDrawContext.ResolvePrefab経由で選択する（Instantiate配置の例外）。大型は38m・3段二導体、小型は17m・3相。Farは同じ支持点からUnityが簡略鉄塔と電線を生成するため、_farは作らない。bodyは120m径間の制作見本で、実行時のたるみは支持点間に生成する。道路幅へ横縮小しない。共有ノードはPowerLineSupportが1基だけ表示する。著作・寸法・再生成は非公開制作ツールgenesis-galleryのpipeline/jobs/power_lines.mdとbuild_power_lines.py、実FBXのLOD・共有所有権の検証はcity-genesis/unity/Tests/PowerLines/run.ps1。
