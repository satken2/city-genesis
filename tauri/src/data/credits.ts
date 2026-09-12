/**
 * クレジット表示の掲載データ
 *
 * フリー素材サイトやOSSライブラリの規約には「利用に際してクレジット表記が必要」と
 * 定めるものがあり、その表記義務を果たすための一覧。設定ウィンドウから開く
 * クレジットウィンドウ(`CreditsWindow`)がこの配列をそのまま描画する。
 *
 * ## 記載の原則
 *
 * - **表記が不要な素材も、出所が分かっているものは載せる。** 後から規約が変わった
 *   場合や、出所を問われた場合に追跡できるようにするため。表記が本当に必須な行だけを
 *   選び出そうとすると、判断ミスが即ライセンス違反になる。
 * - **`requiredText` は規約が指定する文面をそのまま入れる。** 翻訳・省略・改変を
 *   しないこと(「この表記のとおりに書くこと」が条件になっている素材があるため)。
 * - 素材を追加・差し替えしたら、**同じコミットでこの配列も更新する。** 出所を
 *   確認せずに取り込んだ素材は使えない(調査の手順は
 *   `city-genesis/docs/docs/programs/素材クレジット管理.md` を参照)。
 *
 * ## 翻訳について
 *
 * 作者名・サイト名・ライセンス名・要求文面は固有名詞かつ原文のまま出すべきものなので
 * i18n に入れない。多言語化するのはカテゴリ見出しと項目ラベルだけ(`credits.*`)。
 */

/**
 * 掲載カテゴリ。
 * ウィンドウ内での並び順は `CREDIT_CATEGORY_ORDER` が決め、
 * 見出しの表示名は i18n キー `credits.categories.<カテゴリ>` から引く。
 */
export type CreditCategory =
  | "bgm"
  | "sfx"
  | "image"
  | "font"
  | "model"
  | "texture"
  | "engine"
  | "library";

/** カテゴリの表示順序 */
export const CREDIT_CATEGORY_ORDER: CreditCategory[] = [
  "bgm",
  "sfx",
  "image",
  "font",
  "model",
  "texture",
  "engine",
  "library",
];

export type CreditEntry = {
  /** 一覧の見出しになる名前。素材名・素材セット名・ライブラリ名のいずれか */
  title: string;
  category: CreditCategory;
  /** 作者名・権利者名 */
  author?: string;
  /** 配布元の名称(サイト名・組織名) */
  source?: string;
  /** 配布元URL。指定すると見出しが既定ブラウザで開くリンクになる */
  url?: string;
  /** ライセンス名(`CC BY 4.0` / `MIT` など) */
  license?: string;
  /**
   * 規約が特定の文面での表記を要求している場合、その文面。
   * 原文をそのまま等幅で表示するため、翻訳も改変もしない。
   */
  requiredText?: string;
  /** 使用箇所などの補足。翻訳対象外なので固有名詞や短い語にとどめる */
  note?: string;
};

/**
 * 掲載データ本体。
 *
 * 新しい素材を足すときのテンプレート:
 *
 * ```ts
 * {
 *   title: "素材名またはセット名",
 *   category: "sfx",
 *   author: "作者名",
 *   source: "配布サイト名",
 *   url: "https://example.com/...",
 *   license: "CC BY 4.0",
 *   requiredText: "規約が指定する表記文面(あれば)",
 *   note: "使用箇所",
 * },
 * ```
 */
export const CREDIT_ENTRIES: CreditEntry[] = [
  {
    title: "Rock051 (崖の岩肌テクスチャ)",
    category: "texture",
    source: "ambientCG",
    url: "https://ambientcg.com/a/Rock051",
    license: "CC0 1.0",
    note: "Terrain cliff",
  },
  {
    title: "Unity",
    category: "engine",
    author: "Unity Technologies",
    url: "https://unity.com",
    note: "3D rendering",
  },
  {
    title: "Tauri",
    category: "library",
    url: "https://tauri.app",
    license: "MIT OR Apache-2.0",
    note: "UI runtime",
  },
  {
    title: "React",
    category: "library",
    url: "https://react.dev",
    license: "MIT",
  },
  {
    title: "three.js",
    category: "library",
    url: "https://threejs.org",
    license: "MIT",
  },
  {
    title: "Recharts",
    category: "library",
    url: "https://recharts.org",
    license: "MIT",
  },
  {
    title: "i18next / react-i18next",
    category: "library",
    url: "https://www.i18next.com",
    license: "MIT",
  },
  {
    title: "MessagePack for JavaScript",
    category: "library",
    url: "https://github.com/msgpack/msgpack-javascript",
    license: "ISC",
  },
  {
    title: "FastAPI",
    category: "library",
    url: "https://fastapi.tiangolo.com",
    license: "MIT",
    note: "Simulation backend",
  },
  {
    title: "NumPy",
    category: "library",
    url: "https://numpy.org",
    license: "BSD-3-Clause",
    note: "Simulation backend",
  },
];
