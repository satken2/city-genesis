# blender/

3Dモデルの原本(カテゴリ別 Blender ライブラリ .blend)を置くディレクトリ。`unity/Assets/Models/` の FBX はここからの書き出し物で、FBX を直接編集しない。
ファイル構成と規約は [README.md](README.md)、パーツの規約は `docs/docs/geometries/I_建物パーツ作成マニュアル.md` を参照。

## 知らないと危険なこと

- 編集・書き出し・検証の道具はここには無く、別レポジトリ `genesis-gallery`(非公開。`city-genesis` と並置される前提)にある。FBX への書き戻しは `../genesis-gallery/pipeline/library.py export --category <cat> --only <パス>`。ここには .blend 以外を置かない。
- 手著作でないモデルは `genesis-gallery/pipeline/jobs/build_*.py` が形状をコードで持っており、ジョブを再実行すると .blend 側の手編集が消える。編集前にジョブの有無を確認する。ジョブと対象モデルの対応・制作記録は `genesis-gallery/pipeline/jobs/*.md`。
- 駅舎の番号は `path_variant` と一致させる。`stations.blend` は2001〜2007、`paths.blend` の専用通路はp2002・p2004・p2005・p2007を使う。対応するフォルダと通路方式は公開仕様 `Q_駅舎パスのメッシュ生成.md` を参照。
- 通常(Normal)を再著作したら、接写(Close)・遠景(Far)を別ジョブで著作しているモデルはそのジョブも実行し直す。接写ジョブは通常の FBX・bbox・原点・材質を保つ前提で作られている。
- `props.blend` の 308/309(手動配置のモノレール車両)は、走行車両 `Models/Vehicles/24/0`・`middle` の原本でもある。書き出し直したら同じファイルを `Vehicles/24` にも置く(`unity/Tests/Vehicles/run.ps1 -TransitOnly` がバイト一致を検査する)。
- 路線車両(vehicles.blend のバス12・LRT14・メトロ20・中距離21・貨物22の機関車、props.blend の 308/309)の帯は専用の材質で塗り分けてあり、Unity がその材質だけを路線カラーで塗り替える(`unity/Assets/Resources/Config/transit_vehicle_livery.json`)。帯の材質を車体の他の部位に流用しない。
- テクスチャは `//../unity/Assets/Models/<カテゴリ>/Textures/` の相対パス参照。このディレクトリを移動・改名すると参照が切れる。
- Unity は `unity/` 配下しかインポートしないので、ここに .blend を置いても Unity のアセットにはならない(意図的に `unity/` の外にある)。
- 既成建物の道路間口は `road_access_{entry|exit|both}_*` と子Empty `a`・`b`・`inside` をモデル内に保持する(I文書§11)。通常・接写・遠景で同じ位置にし、FBXへこの階層も出力する。1081は貨物駅ジョブがマーカーを再生成する。
- `*.blend1` は Blender の自動バックアップで `.gitignore` 済み。履歴は Git が持つので残さない。

## 詳細の参照先

- 仕様(公開): `docs/docs/geometries/` の I_建物パーツ作成マニュアル、F1_モジュラー建物のメッシュ生成、F3_既成建物のメッシュ生成、G_道路メッシュの生成、N_線路メッシュの生成、O_車両の走行描画、M_地形プロップ。
- 制作手順: Skill `genesis-building-parts`(建物)、`genesis-path-parts`(道路・線路)、`genesis-prop-parts`(プロップ)。
- モデルごとの制作記録・再生成コマンド(非公開): `genesis-gallery/pipeline/jobs/*.md`。
