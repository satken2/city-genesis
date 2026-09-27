# チュートリアル基盤のUnity検証

Unityルートで `./Tests/Tutorial/run-unity.ps1` を実行する。エディタの場所は生成csprojから取得し、必要なら `-UnityExe` で指定する。本編をコンパイルした後、`Temp/TutorialQA` の独立したUnityプロジェクトからそのアセンブリを読み込む。実行中のゲームやセーブへ接続しない。

IPC形式の辞書を受信させ、通常時の素通り、始終点とクリック種類、範囲外、余分な頂点、曲線・盛土、実際のRoadPlanManagerの配置と編集状態、revision・runId、終了時の解除を検証する。マーカーのメッシュとCollider不在、解除時の非表示も確認する。

`Temp/TutorialQA/result.txt` が結果、`qa.log` がログ、`markers.png` が描画画像。隔離環境は標準レンダーパイプラインなので、画像の検証では `OverlayQA.shader` による頂点色描画を使用する。本編のURP設定やTauriネイティブウィンドウを含む通しプレイの代用ではない。

Tauri側は `cd tauri; npm run test:tutorial`。条件・アクション・DOM制限に加え、実際のReact hookをイベントバスのテストアダプタに接続し、段階の進行・閉鎖・再開・異なるセーブ・初期化再試行・解除を検証する。7言語と32ステップの設計件数も確認する。

HTTP契約を実バックエンドで確認するときは、セーブIDが `tutorial-qa-isolated` の専用テストプロセスを用意し、`TUTORIAL_API_TEST_URL` を指定して同じコマンドを実行する。実プレイ中のバックエンドを指定しない。これは非公開コアがある環境での追加検証であり、OSS側の通常テストには不要。
