# チュートリアル基盤のUnity検証

## 実行

- `./run-unity.ps1`: IPC で受けたチュートリアルの指示に従い、道路の配置入力を制限・案内することを確かめる。
- `tauri/` で `npm run test:tutorial`: Tauri 側の条件判定・操作の制限・段階の進行を確かめる。

`run-unity.ps1` には Unity Editor・dotnet と、Unity が生成した `unity/Assembly-CSharp.csproj` が要る。Unity の場所は csproj から求め、`-UnityExe` で指定もできる。

`run-unity.ps1` の結果は `unity/Temp/TutorialQA` に出る(`result.txt`・`qa.log`・マーカーの描画画像 `markers.png`)。`npm run test:tutorial` の結果はコンソールに出る。
