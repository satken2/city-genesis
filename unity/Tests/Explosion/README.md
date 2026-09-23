# 爆破エフェクトの検証

`./run-unity.ps1` は本編のC#ランタイム全体をコンパイルし、`Temp/ExplosionQA` の独立したUnityで実際の `FireEffectFactory` / `VisualEffectManager` とシェーダー・音声を実行する。本編シーン・セーブ・HTTP APIには接続しない。

`result.txt` に結果、`Captures` に炎・煙・消滅の画像を出す。音源のモノラル・長さ・先読み設定、有限バースト、音量と途中ミュート、timeScale=0での消滅、セッション全消去を検証する。テスト床面と瓦礫の代役は単純なプリミティブで、都市での建物差し替えそのものの結合試験ではない。
