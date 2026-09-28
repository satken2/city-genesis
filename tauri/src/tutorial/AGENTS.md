# チュートリアル基盤

- `definitions/*.json` が実行定義、`schema.json` が構造、`model.ts` が型、`definitions.ts` が登録と意味検証。任意の関数・式・URLを実行する仕組みにはしない。
- JSON Schemaの検証は `schemaValidator.generated.ts`(Ajv standalone の事前生成コード)で行う。CSPで `eval` を許していないため、実行時に Ajv でコンパイルしない。生成物は編集禁止で、`schema.json` を変えたら `npm run gen:tutorial-validator`(`tools/tutorial/generate-schema-validator.mjs`)で作り直す。`npm run dev`・`npm run build` の前処理も再生成し、`npm run test:tutorial` は最初に最新かを検査する。定義の `terrainMap` が開始する地形、段階の `world.camera` がカメラの案内先。
- `engine.ts` は条件と明示的アクション。条件は都市に書き込まない。名前付き対象IDはリソース別に扱う。一覧に無い条件項目は単体APIで補う。
- `environment.ts` だけがHTTPと再描画のアダプタを持つ。開始時にコアが作った初期都市の対象(`support.*` / `lesson.*`)を束縛へ取り込むので、定義はそれらを `bind` なしで参照できる。取得済みMasterDataを受け取る。道路APIの `curved` (0/1)はここでbooleanへ正規化する。書き込み前のセーブID確認を外さない。
- `useTutorialGuide.ts` が専用ウィンドウ内の進行管理。束縛・作成結果不明の記録はシナリオとセーブIDで隔離する。HTTP作成の結果が不明なら再POSTしない。
- `bus.ts` は各WebViewの現在の案内。制限状態を永続化しない。終了したrunIdと古いrevisionを受理しない。
- `TutorialLayer.tsx` / `dom.ts` は操作名で案内・制限を適用する。強調の見た目もここだけが付ける。アイコンパネルは方針に関わらず段階の `allow`・`highlight` にあるボタンだけを押せる。システムトレイと設定・保存/ロード・新規ゲーム・確認ダイアログなどの関連画面は制限対象外。除外一覧は `dom.ts` が正。既存のdisabledを上書きしない。解除時に他の処理が設定したinertを消さない。
- 全チュートリアルの全ステップを定義で書く。`components/tutorial/tutorialSteps.ts` は定義から一覧・タイトル・進捗表示の情報を導くだけで判定を持たない。ステップのタイトルは `tutorial.<シナリオID>.step<legacyStep>.title`。
- 作者の設計書は `docs/tutorials/`、公開契約は `docs/docs/programs/チュートリアル基盤.md`。`npm run test:tutorial` と7言語の翻訳整合を検証する。
