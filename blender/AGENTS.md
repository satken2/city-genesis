# blender/

ロープウェイの搬器はpaths.blendのp601/cabinへ著作する。地形プロップではなく吊り点原点の点設備で、Normal/Close共通・Far非表示。非公開制作ツールのbuild_ropeway_cabins.py --cabin-onlyがropeway_cabin_surfaces.pyの曲面車体・二重窓枠・湾曲吊り腕を生成する。車底-3.17m、屋根-1.02m、客室中心Blender X=-0.22m、ヨーク中心X=-0.65m。客室外寸は幅1.96m・奥行き2.32m・高さ2.15m、床と天井の厚みを見込む室内高は約1.95m。腰板約0.68m、屋根は窓枠に沿う薄い蓋とする。exportはp601/cabinだけに絞れる。仕様は公開O文書、検証はUnity Tests/Ropeway/animation.ps1。

立体駐車場3005は非公開制作ツールのbuild_parking_garage.pyがa3005/0へ17パーツを著作する。6m幅・3m階高の開放壁、独立入口、屋上腰壁と照明付きtop-w1を通常/接写で持つ。interior-floorで厚さ22cmの各階床・アスファルト屋上を有効化し、黒い奥面を持たない。parking_ramp.pyが正面の辺の外階段パーツbase/mid/top-stair1と各Closeを著作する。16×6.4m・高低差3mの折り返しスロープを元の頂点1に配置し、描画用の本体だけを後退させる。保存polygon・経済面積・占有コライダーは維持する。Farは可変底面を保つ既存簡易生成。import_parking_garage.ps1が実FBX・27組み立て・床の表裏・開口の見通し・凹形状・未指定時の無変更を検証し、正規ミラーを同期する。仕様は公開F1/I文書、制作・AI補助開示記録は非公開制作ツールのpipeline/jobs/parking_garage.md。

洋上風力1071・洋上石油1072はtop-prebuiltの通常/接写/遠景、受入基地1073は岸壁に置くroofpropと接写・遠景prebuiltを持つ。1071は1055の白い風車を再利用する。海底1104/1301はbodyと接写だけをpaths.blendに持ち、FarはUnity生成。非公開制作ツールのbuild_offshore.pyが著作し、import_offshore.ps1が実FBXとLODを検証する。制作記録は同ツールのoffshore.md。 1072の多層設備は非公開制作ツールのoffshore_platform.pyに定義し、build_offshore.py --platform-onlyで3LODのみを再著作できる。

観光名所5種類は非公開制作ツールの `build_landmarks.py` が `buildings.blend` の `landmark-*/0/` へ通常・接写・専用遠景を著作する。大展望塔のみNソウルタワー風の白い円柱・段状展望室・赤白アンテナで、75m階高のbase/mid/top、他4種類はtopだけ。既成配置の回転込みでマスターの敷地寸法へ収める。通常12,000・接写40,000・遠景5,000推定頂点/部品。`import_landmarks.ps1` は実FBX・敷地寸法・3距離・塔の2/4/8階を検証して正規ミラーを同期する。仕様は公開F3/I文書。

交差点の信号機 `traffic_signal_jp_{pole,arm,head}` は非公開制作ツールの `build_prop_traffic_signals.py` が props.blend へ著作する。Unityが交差点ごとに3部品を1メッシュへ結合するので、原点は3部品とも柱の接地面の高さ、アームはBlender Y±0.5の単位長で長手だけ伸縮され、灯器は吊り金具の真下が原点で表示面-Y。仕様は公開G文書§11.8、実FBX検証はUnity Tests/TrafficSignals/run.ps1。

3Dモデルの原本(カテゴリ別ライブラリ .blend)を置くディレクトリ。`unity/Assets/Models/` のFBXはここからの書き出し物で、FBXを直接編集しない。
ファイル構成と規約は [README.md](README.md) を参照。

ホテル2016・2017は非公開制作ツールの `build_hotels.py` が `buildings.blend` に通常・接写を著作する。
2016は張り出す1階・左右の長さが異なる中層・テラスを残して後退する最上階の3種のprebuiltを階高3.5mで積む4〜5階の中庭旅館。`--only 2016`で単独著作できる。2017は階高3mのモジュラー壁と車寄せ付きentrance。
2017の客室壁は幅1.31mの灰色タイル帯を縦に通し、中央桟・張り出す枠や水切りを持たない一枚の固定ガラスを置く。階間の横帯は持たない。通常は疎な目地、接写は千鳥目地と薄い黒色シールの見込みを表す。
遠景はUnityの簡易生成を使う。仕様は公開I文書§9.2、実FBXと三距離の検証は同ツールの `import_hotels.ps1`。

## 知らないと絶対わからない仕様

- モノレール502のホームドアはplatform_doors.pyの共通造形をbuild_monorail.pyとbuild_platform_close.py（いずれも非公開制作ツール）から呼び、platform本体に組み込む。通常・接写とも8m周期、床上1.3mの腰高ドアで、独立パーツは持たない。

- ホーム451/461・462・502のplatform/platform_wallとロープウェイ602のstation_shellの接写版は、非公開制作ツールの`build_platform_close.py`が通常形状を基に著作する。通常ジョブの後に接写ジョブを実行する。通常のFBX・bbox・原点・既存材質を保持し、8m周期のベンチ・照明・取付金物を加える。

- 鉄道の接写版は非公開制作ツールの`build_rail_close.py`が通常の外板断面から曲面を著作する。vehicles.blendの14/21/23とprops.blendの308/309が対象で、メトロ20は`metro_vehicles.py`が通常と同じ寸法表から接写を作る。走行車両の接写版は`build_vehicle_props.py`でProps/370〜379・384〜389の接写別名へ同期する。通常のFBXは保持する。

- 戸建て2000（a2000・a2000s2・a2000s3）と小屋2015は `house_parts.py`、2004・2008・2013は `build_retail_industrial_lods.py`（いずれも非公開制作ツール）が通常・接写を著作する。2000・2015の遠景は閾値0の `mid/top-prebuilt_far`。附属棟は背面の辺の外階段パーツ（base/mid/top-stair3）で持つ。2008の広告は `BD_MALL_ADS` と4区画のPNGを共有し、広告文字のジオメトリは持たない。新設prefixの選択設定はUnityの正規ミラー生成後に再生成する。

- props.blend の308/309(手動配置のモノレール車両)は、モノレール路線の走行車両(Unityの `Models/Vehicles/24/0`・`middle`)の原本でもある。308/309のFBXを書き出し直したら、同じファイルを `Vehicles/24` にも置く(`unity/Tests/Vehicles/run.ps1 -TransitOnly` がバイト一致を検査する)。

- 路線車両(vehicles.blendのバス12・LRT14・メトロ20・中距離21と、props.blendの308/309)の帯は専用の材質で塗り分けてあり、Unityがその材質だけを路線カラーで塗り替える(`unity/Assets/Resources/Config/transit_vehicle_livery.json`、公開O文書§O.10.4)。帯の材質を車体の他の部位に流用しないこと。

- **編集・書き出し・検証の道具は別レポジトリ `genesis-gallery` にある**(ワークスペース上で `city-genesis` と並置される前提。`../genesis-gallery/pipeline/library.py export --category <cat> --only <パス>` でFBXへ書き戻す)。ここには .blend 以外を置かない。
- 手著作でないモデルは `genesis-gallery/pipeline/jobs/build_*.py` が形状をコードで持っており、ジョブを再実行すると .blend 側の手編集が消える。編集前にジョブの有無を確認すること。
- 集合住宅2001は `build_a2001_variants.py`（閾値200・800と共通遠景は `apartment_parts.py`）、商業施設付きオフィス2010は `build_a2010.py`（いずれも非公開制作ツール）が著作する。2001の閾値0は旧手著作の移植品で、形状の正はこの .blend（ジョブは著作高さの補正だけを行う）。
- 小規模オフィス2002・大規模オフィス2012の閾値200/400/600/800は非公開制作ツールの `build_office_variants.py` が通常・接写を著作する。閾値0は別(2002は `build_a2002.py`、2012は旧手著作の移植品)で、このジョブは触らない。材質 `BD_BLUE_GLASS`・`BD_RED_BRICK` はこのジョブが新設した共有材。仕様は公開I文書§9.7。
- 建物の汎用接写パーツは非公開制作ツールの `build_building_lods.py` が無印から著作する。通常用を再著作した後はこのジョブも実行する。`lod_author=building_surface_lod_v1` のコレクションだけを再生成し、市政府・戸建て住宅の個別ジョブが持つCloseは保持する。
- テクスチャは `//../unity/Assets/Models/<カテゴリ>/Textures/` の相対パス参照。このディレクトリを移動・改名すると参照が切れる。
- Unity は `unity/` 配下しかインポートしないので、ここに .blend を置いても Unity のアセットにはならない(意図的に `unity/` の外にある)。
- `*.blend1` は Blender の自動バックアップで、`.gitignore` で除外している。履歴はGitが持つので残さない。

中距離21の接写先頭車は、非公開制作ツールの`rail_cab_surfaces.py`が平面の側面・屋根・前面ガラスと、その接合円弧を著作する。全面を膨らませる補間を適用しない。`build_rail_close.py --heads-only`で先頭2外観を再生成できる。

メトロ20は3外観(0=スタンダード・1=近未来・2=レトロ)で、路線ごとにプレイヤーが選ぶ(API `vehicle_model`)。非公開制作ツールの`metro_vehicles.py`が通常・接写・遠景を同じ寸法表から著作し(`build_vehicles.py`/`build_rail_close.py`/`build_rail_far.py`の`--metro-only`)、自由配置別名は372〜375・388・389。仕様・再生成・制作記録は非公開制作ツールの`pipeline/jobs/metro_vehicles.md`。


高速鉄道23は先頭2外観（0/1）と中間2外観（middle/middle_1）に接写版を持ち、自由配置別名384〜387にも同期する。通常FBXは保持する。高速車の前頭部は長い鼻先と曲面風防を一体の断面座標で著作し、中距離21の平面ガラスとは区別する。再生成は非公開制作ツールの`build_rail_close.py --high-speed-only`と`high_speed_surfaces.py`、比較は`render_high_speed.py`。走行車の既存LODと自由配置用PropRenderModelが接写版を選択し、路線マスターの対応は変更しない。


鉄道の専用遠景は14/20/21/22/23の全20モデルとモノレール308/309にある。非公開制作ツールの`build_rail_far.py`が車体箱と左右各一枚の窓帯を著作し、車輪・台車・屋上機器を省略する。貨車には窓を付けない。原点と水平範囲は通常版を維持し、車輪・集電装置を省くため上下の範囲は通常版の内側でよい。鉄道12〜42三角形・モノレール62三角形、既存材質を共有する。`build_vehicle_props.py`は370〜389の遠景別名も同期する。走行版は画面比率3.5%未満で`_far`を選び、25%以上では存在する`_close`を選ぶ。専用版がないLODは通常へフォールバックする。自由配置はPropRenderModelの既存の投影LODで専用遠景を使う。通常・接写モデルは保持する。

送電鉄塔1101/1102は非公開制作ツールのbuild_power_lines.pyがpaths.blendのpillar_capとpillar_cap_closeを著作する。大型38m・小型17m、両LODのbboxを保持する。bodyは120m径間の制作見本。実行時はPowerLinePathが電線とFarの鉄塔を生成し、bodyを道路のように伸縮しない。制作記録は同ツールのpipeline/jobs/power_lines.md、実FBX検証はUnity Tests/PowerLines/run.ps1。

モノレール308/309の接写版は、非公開制作ツールの`monorail_close_surfaces.py`で平らな側板・丸い屋根肩・傾斜した前面ガラスを著作する。青白塗装、角丸の二重窓枠、扉の敷居・取手、ワイパー、床下の点検カバー、空調ファン、連結部の蛇腹を持つ。文字・ロゴ・参考画像そのものは含めない。全LODで14mの連結ピッチとタイヤ接地原点を共有する。床下カバーは台車部の下端-0.25m、中央部+0.57mを斜面でつなぎ、桁用の半幅0.51mを守る。`build_rail_close.py`へprops.blendを渡すと先頭・中間を再生成する。
