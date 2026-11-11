# 地形と道路・floorの整合検証

## 実行

```powershell
./run-unity.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/TerrainSurfaceQA` に出る(`result.txt`・`qa.log`・描画画像 `floor-only-*.png`・`floor-terrain-*.png`・`adaptive-*.png`)。`result.txt` の `Adaptive` の行は、接写以外の地面パネルの頂点数と生成時間(5回の中央値)である。
