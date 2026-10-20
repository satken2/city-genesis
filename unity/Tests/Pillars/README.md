# 橋脚の干渉回避の検証

## 実行

- `./run.ps1`: 橋脚が下を通る道路を避ける判定と、門型の柱を逃がす位置の計算を .NET 上で確かめる。
- `./run-unity.ps1`: 本編の `GeneratePillars` と実FBXで、下の道路の追加・移動・削除に応じた橋脚の作り直しと、門型橋脚の配置・接地を確かめる。

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。`run-unity.ps1` は Unity の場所を csproj から求め、`-UnityExe` で指定もできる。

`run.ps1` の結果はコンソールに、`run-unity.ps1` の結果は `unity/Temp/PillarUnityQA` に出る(`result.txt`・`qa.log`・描画見本 `portal-preview.png`)。`run-unity.ps1` は合格すると、門型橋脚の2部品(`p4/pillar_leg`・`pillar_crossbeam`)の FBX の meta とミラープレハブを本編の `Assets` へ写す。
