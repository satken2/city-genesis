# チュートリアル基盤

- `definitions/*.json` が実行定義、`schema.json` が構造、`model.ts` が型、`definitions.ts` が登録と意味検証。任意の関数・式・URLを実行する仕組みにはしない。
- `engine.ts` は条件と明示的アクション。条件は都市に書き込まない。名前付き対象IDはリソース別に扱う。一覧に無い条件項目は単体APIで補う。
- `environment.ts` だけがHTTPと再描画のアダプタを持つ。取得済みMasterDataを受け取る。書き込み前のセーブID確認を外さない。
- `useTutorialGuide.ts` が専用ウィンドウ内の進行管理。束縛・作成結果不明の記録はシナリオとセーブIDで隔離する。HTTP作成の結果が不明なら再POSTしない。
- `bus.ts` は各WebViewの現在の案内。制限状態を永続化しない。終了したrunIdと古いrevisionを受理しない。
- `TutorialLayer.tsx` / `dom.ts` は操作名で案内・制限を適用する。既存のdisabledを上書きしない。解除時に他の処理が設定したinertを消さない。
- `legacyStep` のあるステップだけ新基盤が担当する。未登録のステップは既存 `components/tutorial/tutorialSteps.ts` の表示と判定を使用する。
- 作者の設計書は `docs/tutorials/`、公開契約は `docs/docs/programs/チュートリアル基盤.md`。`npm run test:tutorial` と7言語の翻訳整合を検証する。
