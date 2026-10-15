# 道路施工の描画検証

## 実行

```powershell
./run-unity.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/ConstructionPathQA` に出る(`result.txt`・`qa.log`・施工段階ごとの描画画像の PNG)。合格すると、本編に無い工事車両(`construction_*`)のミラープレハブと FBX の meta を本編の `Assets` へ写す。
