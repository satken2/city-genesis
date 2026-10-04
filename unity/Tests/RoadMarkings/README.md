# 路面標示の描画検証

Unityで本編のC#プロジェクトを生成した環境から、次を実行する。

```powershell
./unity/Tests/RoadMarkings/run-unity.ps1 -Wait
```

Unityの場所が異なる場合は`-UnityExe`を指定する。`-Wait`を省略すると起動後に戻る。本編をビルドし、`unity/Temp/RoadMarkingsQA`へ独立したURPプロジェクトを作る。本編シーンを起動・変更せず、実際のランタイムDLLとFBXを使う。Unity Editorのライセンスと描画可能なGPUが必要。

結果は`result.txt`、詳細ログは`qa.log`、画像は同じディレクトリのPNG。方向規則、実路面の標示、表示モードからの復元、交差点進入部、キャッシュ復帰、40種類同時表示による高解像度容量超過、視野外ページの追い出し、400m勾配道路のClose/Normal/Far、終了時の解放を検証する。

4車線舗装道路の固定区間LODは、実FBXの先頭がコンクリートでもマスターの路面材質でFarを描くこと、材質指定の変更・未指定・解決失敗時の扱い、Close/Normalのサブメッシュ材質の保持を検査する。描画画像は`four-lane-far-asphalt.png`。

EditModeの独立検証では通常のMonoBehaviour通知とゲームフレームが進まないため、表示解除・復帰の通知とフレーム予算の更新を明示する。大都市セーブでの実FPSやプレイヤーの長時間移動を測るベンチマークではない。
