# blender/

3Dモデルの原本(カテゴリ別ライブラリ .blend)を置くディレクトリ。`unity/Assets/Models/` のFBXはここからの書き出し物で、FBXを直接編集しない。
ファイル構成と規約は [README.md](README.md) を参照。

## 知らないと絶対わからない仕様

- 鉄道の接写版は非公開制作ツールの`build_rail_close.py`が通常の外板断面から曲面を著作する。vehicles.blendの14/20/21とprops.blendの308/309が対象。走行車両の接写版は`build_vehicle_props.py`でProps/370〜379の接写別名へ同期する。通常のFBXは保持する。

- 2000は `residential_parts.py`、2004・2008・2013は `build_retail_industrial_lods.py`（非公開制作ツール）が通常・接写を著作する。2008の広告は `BD_MALL_ADS` と4区画のPNGを共有し、広告文字のジオメトリは持たない。新設prefixの選択設定はUnityの正規ミラー生成後に再生成する。

- **編集・書き出し・検証の道具は別レポジトリ `genesis-gallery` にある**(ワークスペース上で `city-genesis` と並置される前提。`../genesis-gallery/pipeline/library.py export --category <cat> --only <パス>` でFBXへ書き戻す)。ここには .blend 以外を置かない。
- 手著作でないモデルは `genesis-gallery/pipeline/jobs/build_*.py` が形状をコードで持っており、ジョブを再実行すると .blend 側の手編集が消える。編集前にジョブの有無を確認すること。
- 建物の汎用接写パーツは非公開制作ツールの `build_building_lods.py` が無印から著作する。通常用を再著作した後はこのジョブも実行する。`lod_author=building_surface_lod_v1` のコレクションだけを再生成し、市政府・戸建て住宅の個別ジョブが持つCloseは保持する。
- テクスチャは `//../unity/Assets/Models/<カテゴリ>/Textures/` の相対パス参照。このディレクトリを移動・改名すると参照が切れる。
- Unity は `unity/` 配下しかインポートしないので、ここに .blend を置いても Unity のアセットにはならない(意図的に `unity/` の外にある)。
- `*.blend1` は Blender の自動バックアップで、`.gitignore` で除外している。履歴はGitが持つので残さない。
