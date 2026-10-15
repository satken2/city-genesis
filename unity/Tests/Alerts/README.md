# 空間警告の検証

## 実行

```powershell
./run.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

結果は `unity/Temp/AlertsQA` に出る(`result.txt`・`qa.log`)。
