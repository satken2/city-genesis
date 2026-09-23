# blender/

洋上風力1071・洋上石油1072はtop-prebuiltの通常/接写/遠景、受入基地1073は岸壁に置くroofpropと接写・遠景prebuiltを持つ。1071は1055の白い風車を再利用する。海底1104/1301はbodyと接写だけをpaths.blendに持ち、FarはUnity生成。非公開制作ツールのbuild_offshore.pyが著作し、import_offshore.ps1が実FBXとLODを検証する。制作記録は同ツールのoffshore.md。 1072の多層設備は非公開制作ツールのoffshore_platform.pyに定義し、build_offshore.py --platform-onlyで3LODのみを再著作できる。

観光名所5種類は非公開制作ツールの `build_landmarks.py` が `buildings.blend` の `landmark-*/0/` へ通常・接写・専用遠景を著作する。大展望塔のみNソウルタワー風の白い円柱・段状展望室・赤白アンテナで、75m階高のbase/mid/top、他4種類はtopだけ。既成配置の回転込みでマスターの敷地寸法へ収める。通常12,000・接写40,000・遠景5,000推定頂点/部品。`import_landmarks.ps1` は実FBX・敷地寸法・3距離・塔の2/4/8階を検証して正規ミラーを同期する。仕様は公開F3/I文書。

3Dモデルの原本(カテゴリ別ライブラリ .blend)を置くディレクトリ。`unity/Assets/Models/` のFBXはここからの書き出し物で、FBXを直接編集しない。
ファイル構成と規約は [README.md](README.md) を参照。

ホテル2016・2017は非公開制作ツールの `build_hotels.py` が `buildings.blend` に通常・接写を著作する。
2016は張り出す1階・左右の長さが異なる中層・テラスを残して後退する最上階の3種のprebuiltを階高3.5mで積む4〜5階の中庭旅館。`--only 2016`で単独著作できる。2017は階高3mのモジュラー壁と車寄せ付きentrance。
遠景はUnityの簡易生成を使う。仕様は公開I文書§9.2、実FBXと三距離の検証は同ツールの `import_hotels.ps1`。

## 知らないと絶対わからない仕様

- モノレール502のホームドアはplatform_doors.pyの共通造形をbuild_monorail.pyとbuild_platform_close.py（いずれも非公開制作ツール）から呼び、platform本体に組み込む。通常・接写とも8m周期、床上1.3mの腰高ドアで、独立パーツは持たない。

- ホーム451/461・462・502のplatform/platform_wallとロープウェイ602のstation_shellの接写版は、非公開制作ツールの`build_platform_close.py`が通常形状を基に著作する。通常ジョブの後に接写ジョブを実行する。通常のFBX・bbox・原点・既存材質を保持し、8m周期のベンチ・照明・取付金物を加える。

- 鉄道の接写版は非公開制作ツールの`build_rail_close.py`が通常の外板断面から曲面を著作する。vehicles.blendの14/20/21/23とprops.blendの308/309が対象。走行車両の接写版は`build_vehicle_props.py`でProps/370〜379・384〜387の接写別名へ同期する。通常のFBXは保持する。

- 2000は `residential_parts.py`、2004・2008・2013は `build_retail_industrial_lods.py`（非公開制作ツール）が通常・接写を著作する。2008の広告は `BD_MALL_ADS` と4区画のPNGを共有し、広告文字のジオメトリは持たない。新設prefixの選択設定はUnityの正規ミラー生成後に再生成する。

- **編集・書き出し・検証の道具は別レポジトリ `genesis-gallery` にある**(ワークスペース上で `city-genesis` と並置される前提。`../genesis-gallery/pipeline/library.py export --category <cat> --only <パス>` でFBXへ書き戻す)。ここには .blend 以外を置かない。
- 手著作でないモデルは `genesis-gallery/pipeline/jobs/build_*.py` が形状をコードで持っており、ジョブを再実行すると .blend 側の手編集が消える。編集前にジョブの有無を確認すること。
- 建物の汎用接写パーツは非公開制作ツールの `build_building_lods.py` が無印から著作する。通常用を再著作した後はこのジョブも実行する。`lod_author=building_surface_lod_v1` のコレクションだけを再生成し、市政府・戸建て住宅の個別ジョブが持つCloseは保持する。
- テクスチャは `//../unity/Assets/Models/<カテゴリ>/Textures/` の相対パス参照。このディレクトリを移動・改名すると参照が切れる。
- Unity は `unity/` 配下しかインポートしないので、ここに .blend を置いても Unity のアセットにはならない(意図的に `unity/` の外にある)。
- `*.blend1` は Blender の自動バックアップで、`.gitignore` で除外している。履歴はGitが持つので残さない。

メトロ20・中距離21の接写先頭車は、非公開制作ツールの`rail_cab_surfaces.py`が平面の側面・屋根・前面ガラスと、その接合円弧を著作する。全面を膨らませる補間を適用しない。`build_rail_close.py --heads-only`で先頭4外観を再生成できる。


高速鉄道23は先頭2外観（0/1）と中間2外観（middle/middle_1）に接写版を持ち、自由配置別名384〜387にも同期する。通常FBXは保持する。高速車の前頭部は長い鼻先と曲面風防を一体の断面座標で著作し、メトロ20・中距離21の平面ガラスとは区別する。再生成は非公開制作ツールの`build_rail_close.py --high-speed-only`と`high_speed_surfaces.py`、比較は`render_high_speed.py`。走行車の既存LODと自由配置用PropRenderModelが接写版を選択し、路線マスターの対応は変更しない。


鉄道の専用遠景は14/20/21/22/23の全18モデルとモノレール308/309にある。非公開制作ツールの`build_rail_far.py`が車体箱と左右各一枚の窓帯を著作し、車輪・台車・屋上機器を省略する。貨車には窓を付けない。原点と水平範囲は通常版を維持し、車輪・集電装置を省くため上下の範囲は通常版の内側でよい。鉄道12〜42三角形・モノレール62三角形、既存材質を共有する。`build_vehicle_props.py`は370〜387の遠景別名も同期する。走行版は画面比率3.5%未満で`_far`を選び、25%以上では存在する`_close`を選ぶ。専用版がないLODは通常へフォールバックする。自由配置はPropRenderModelの既存の投影LODで専用遠景を使う。通常・接写モデルは保持する。

送電鉄塔1101/1102は非公開制作ツールのbuild_power_lines.pyがpaths.blendのpillar_capとpillar_cap_closeを著作する。大型38m・小型17m、両LODのbboxを保持する。bodyは120m径間の制作見本。実行時はPowerLinePathが電線とFarの鉄塔を生成し、bodyを道路のように伸縮しない。制作記録は同ツールのpipeline/jobs/power_lines.md、実FBX検証はUnity Tests/PowerLines/run.ps1。

モノレール308/309の接写版は、非公開制作ツールの`monorail_close_surfaces.py`で平らな側板・丸い屋根肩・傾斜した前面ガラスを著作する。青白塗装、角丸の二重窓枠、扉の敷居・取手、ワイパー、床下の点検カバー、空調ファン、連結部の蛇腹を持つ。文字・ロゴ・参考画像そのものは含めない。全LODで14mの連結ピッチとタイヤ接地原点を共有する。床下カバーは台車部の下端-0.25m、中央部+0.57mを斜面でつなぎ、桁用の半幅0.51mを守る。`build_rail_close.py`へprops.blendを渡すと先頭・中間を再生成する。
