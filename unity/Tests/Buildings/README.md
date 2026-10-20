# 建物の回帰検証

## 実行

- `./run-unity.ps1`: 角パーツ(`*-c1.fbx`)がどの内角・向きでも両側の辺に沿うことと、フェンスのパーツの解決を、本編の `ModularBodyBuilder` で確かめる。
- `./run-generation.ps1`: 実FBXの多層躯体の生成結果と、生成時間・一時Mesh数を確かめる。
- `./run-crane.ps1`: 工事中クレーンの組み立てと旋回を確かめる。
- `./run-open-space.ps1`: 駐車場・駅前ロータリーなどのオープンスペースの配置と描画を、さまざまな形の区画で確かめる。

`run-generation.ps1` は `-OutputName baseline` で変更前の生成結果を保存し、変更後に `-BaselineDirectory <baseline の絶対パス>` を付けて実行すると生成メッシュの差を検査する。

PowerShell 7・Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/` の `BuildingCornerQA`・`BuildingGenerationQA/<OutputName>`(既定は `current`)・`ConstructionCraneQA`・`OpenSpaceQA` に出る(どれも `result.txt`・`qa.log`。生成は `timings.csv`、オープンスペースは PNG の見本と配置 JSON も出す)。`run-open-space.ps1` は合格すると、配置 JSON を隣の `genesis-gallery` リポジトリの `pipeline/layouts` へ写し、本編に無い a3001〜a3004 のミラープレハブと meta を本編の `Assets` へ写す。
