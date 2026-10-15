# 爆破エフェクトの検証

## 実行

```powershell
./run-unity.ps1
```

Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity はスクリプトに固定した `C:/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe` を使う。

スクリプトは Unity を起動してすぐ戻るので、Unity の終了を待って結果を見る。結果は `unity/Temp/ExplosionQA` に出る(`result.txt`・`qa.log`・`Captures/` の炎・煙・消滅の画像)。
