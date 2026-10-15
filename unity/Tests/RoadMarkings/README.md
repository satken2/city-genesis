# 路面標示の描画検証

## 実行

```powershell
./run-unity.ps1 -Wait
```

`-Wait` を省くと、Unity を起動してすぐ戻る。

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity は既定で `C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe` を使い、`-UnityExe` で変えられる。

結果は `unity/Temp/RoadMarkingsQA` に出る(`result.txt`・`qa.log`・描画画像の PNG)。
