# 交差点の回帰検証

## 実行

- `./run.ps1`: 交差点の切断・角・勾配のある路面、線路のつなぎ目のS字、高速道路のランプの結合、駅の設計標高の索引の計算を .NET 上で確かめる。
- `./run-unity.ps1`: 実FBXと本編の生成器で、交差点と本線の接合、道路と線路の混在交差点、高速道路の合流を確かめる。
- `./run-unity.ps1 -PavedModels`: 上の検証を p1 の代わりに p2 のモデルで行う。
- `./run-unity.ps1 -Equipment`: 街灯・柵・電柱などの道路設備の接地と向きを確かめる。
- `./run-unity.ps1 -Platforms`: 駅のホーム・軌道・分岐器・ホーム終端と、軌道の位置が違う線路のつなぎ目を確かめる。
- `./run-unity.ps1 -PlatformEnds`: `-Platforms` のうちホーム終端だけを確かめる。
- `./run-unity.ps1 -RailConnectors`: `-Platforms` のうち分岐器だけを確かめる。
- `./run-unity.ps1 -RailYard`: 鉄道とモノレールの車両基地の配置計画と組み立てを確かめる。
- `./run-unity.ps1 -StationHouse`: 駅舎の橋と通路の高さ・クリック面を確かめる。
- `./run-unity.ps1 -LandmarkPaths`: 植栽街路・レンガ高架・吊り橋を確かめる。`-SyncLandmarkPrefabs` も付けると、検証のあとでラッパープレハブを本編へ写す(既存は上書きせず、GUID が食い違えば止まる)。

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。`run-unity.ps1` は Unity の場所を csproj から求め、`-UnityExe` で指定もできる。

`run.ps1` の結果はコンソールに、`run-unity.ps1` の結果は `unity/Temp/IntersectionUnityQA` に出る(`result.txt`・`qa.log`・描画画像の PNG)。
