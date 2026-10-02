# チュートリアル基盤

- `definitions/*.json` が実行定義、`schema.json` が構造、`model.ts` が型、`definitions.ts` が登録と意味検証。任意の関数・式・URLを実行する仕組みにはしない。
- JSON Schemaの検証は `schemaValidator.generated.ts`(Ajv standalone の事前生成コード)で行う。CSPで `eval` を許していないため、実行時に Ajv でコンパイルしない。生成物は編集禁止で、`schema.json` を変えたら `npm run gen:tutorial-validator`(`tools/tutorial/generate-schema-validator.mjs`)で作り直す。`npm run dev`・`npm run build` の前処理も再生成し、`npm run test:tutorial` は最初に最新かを検査する。定義は地形を持たない(開始する都市の地形と初期配置はコアが同梱する教材の都市で、ニューゲームはチュートリアルIDだけを送る)。段階の `world.camera` がカメラの案内先。
- `engine.ts` は条件と明示的アクション。条件は都市に書き込まない。名前付き対象IDはリソース別に扱う。一覧に無い条件項目は単体APIで補う。
- `environment.ts` だけがHTTPと再描画のアダプタを持つ。開始時にコアが作った初期都市の対象(`support.*` / `lesson.*`)を束縛へ取り込むので、定義はそれらを `bind` なしで参照できる。取得済みMasterDataを受け取る。道路APIの `curved` (0/1)はここでbooleanへ正規化する。書き込み前のセーブID確認を外さない。
- `useTutorialGuide.ts` が専用ウィンドウ内の進行管理。HUDの常駐モード(`hudMode` 条件)は、各WebView間で共有される `hud:mode_changed` を直接購読して `facts.hudMode` に入れる(観測の `GUIDE_OBSERVE` は使わない)。束縛・作成結果不明の記録はシナリオとセーブIDで隔離する。HTTP作成の結果が不明なら再POSTしない。
- `bus.ts` は各WebViewの現在の案内。制限状態を永続化しない。終了したrunIdと古いrevisionを受理しない。
- `TutorialLayer.tsx` / `dom.ts` は操作名で案内・制限を適用する。強調の見た目もここだけが付け、押せない(`disabled`)要素には付けない。同じ操作名が並ぶタイル・カードは `data-guide-value` を持ち、定義の `名前:値` で1つだけを指す(値を取れる名前は `definitions.ts` の `VALUE_TARGETS`。値は番号で、`parcel.mode` だけ作成方法の名前)。アイコンパネルは方針に関わらず段階の `allow`・`highlight` にあるボタンだけを押せる。システムトレイと設定・保存/ロード・新規ゲーム・確認ダイアログなどの関連画面は制限対象外。除外一覧は `dom.ts` が正。既存のdisabledを上書きしない。解除時に他の処理が設定したinertを消さない。
- 建物・道路・鉄道・インフラの種別を選んで建てさせるステップと公共交通の路線の新設は、パネルを開く段階から確定までの全段階を `strict` にし、案内した操作(`name:値` のタイル・カード・戻る・取消・確定など)だけを `allow` に書く(`npm run test:tutorial` が全定義で検査する)。複数段階のステップは達成条件(`complete`)を満たすと `findPhase` が最終段階にし、`check`(「次へ」)も最終段階でなくても達成条件そのものを確かめる。確定でパネルが閉じても進めるため。
- 全チュートリアルの全ステップを定義で書く。`components/tutorial/tutorialSteps.ts` は定義から一覧・タイトル・進捗表示の情報を導くだけで判定を持たない。ステップのタイトルは `tutorial.<シナリオID>.step<legacyStep>.title`。
- `reclamation` は造成・埋立地内の区画・市庁舎の4ステップ、`acquisition` は線路計画・買収観測・公共施設の撤去・線路完成の4ステップ。造成と撤去の入口は `hud.grading` / `agent.demolish`(建物詳細の取り壊しボタン)。案内先は通常パネルと同じ操作名を使う。
- 作者の設計書は `docs/tutorials/`、公開契約は `docs/docs/programs/チュートリアル基盤.md`。`npm run test:tutorial` と7言語の翻訳整合を検証する。
- 同じステップで複数対象を順に施工する場合、先の対象の保存済み達成条件を `any` でパネル・種別選択条件にも含める。後の対象には開く・種別選択の段階を置き、戻る・閉じるで先の対象へ巻き戻さない。起動時も `findPhase` で保存済み達成を確認して案内先を決める。
- 曲線・勾配を通常の2Dパネルで編集させる段階は `path.shapeEditor`(開くボタン)、`path.plan`(平面図と全体表示)、`path.profile`(縦断面)を許可する。図の親枠に操作名を付けるのでキャンバスのドラッグも制限を通り、クリック時のフォーカスでDeleteなどのキー操作も使える。
- `controls`(基本操作編)はコックピット・表示パネル・アイコンパネル・エポック・カメラ操作・フライ/撮影モード・都市の境界の7ステップで、都市へは書き込まない。最終段階以外の説明は `page` 条件(「次へ」で送るページ。`useTutorialGuide` の `advance` が `facts.pages` に記録し、`Tutorial.tsx` は「次へ」でまず `advance` を呼び、ページ送りでなければ `check` する)で進める。`page` は最終段階以外の `until` 全体にだけ置け、`id` は定義内で一意、`complete` には使えない(`definitions.ts` が検証)。前の段階へ戻ると、戻り先以降のページ送りは未送信に戻る。ステップを始め直すと全ページが未送信になる。強調したボタンは押せる。アイコンパネルは強調した1アイコンしか押せない。
- `tourism` はTourism Cityを舞台に、ホテル・客船ターミナル・プレイヤーによるバス路線新設・エポック進行・成果確認の5ステップ。路線新設は既存の `transit.*` 操作で案内し、開業判定はコアの `checks.line` を読む。

- `scenario4` はMetroCityを使い、ホーム・駅舎・買収と病院撤去・幹線道路上の高架・基地・曲線勾配の接続・給電・開業・車両調達と運行確認の13ステップ。曲線編集は observe とマーカーで案内し、直線2点の位置制約を付けない。新規ゲームでは独立 acquisition を選択肢に出さないが、その保存済み教材の定義は維持する。`mining` はMineCity2の既存区画を使い、建てる施設種別は変えない。
