/**
 * クレジット表示の掲載データ
 *
 * フリー素材サイトの規約には「利用に際してクレジット表記が必要」と定めるものがあり、
 * その表記義務を果たすための一覧。設定ウィンドウから開く
 * クレジットウィンドウ(`CreditsWindow`)がこの配列をそのまま描画する。
 *
 * ## 記載の原則
 *
 * - **表記が不要な素材も、出所が分かっているものは載せる。** 後から規約が変わった
 *   場合や、出所を問われた場合に追跡できるようにするため。表記が本当に必須な行だけを
 *   選び出そうとすると、判断ミスが即ライセンス違反になる。
 * - **自作の素材(自前のBlenderスクリプトやAI補助で作ったモデル等)は載せない。**
 *   クレジットは他者の成果物を利用する際の出所表記であり、自作物には表記すべき
 *   相手がいない。自作物の制作記録は `genesis-gallery` 側に置く。
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
 *
 * 対象は素材(アセット)のみで、ゲームエンジンやOSSライブラリは扱わない。
 * それらの著作権表示・ライセンス条文は配布物へ別途同梱する
 * (`city-genesis/docs/docs/programs/素材クレジット管理.md` を参照)。
 */
export type CreditCategory = "bgm" | "sfx" | "image" | "font" | "model" | "texture" | "data";

/** カテゴリの表示順序 */
export const CREDIT_CATEGORY_ORDER: CreditCategory[] = [
  "bgm",
  "sfx",
  "image",
  "font",
  "model",
  "texture",
  "data",
];

export type CreditEntry = {
  /** 一覧の見出しになる名前。素材名または素材セット名 */
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
    title: "Badge",
    category: "image",
    author: "Magnific",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/badge_3135728",
    license: "Flaticon Free License",
    requiredText: "Badge icon designed by Magnific from Flaticon",
    note: "New game cleared badge (ui/badge.png). Source confirmed from the downloaded file's Zone.Identifier. License verified 2026-10-03.",
  },
  {
    title: "Explode001",
    category: "sfx",
    author: "mitchelk",
    source: "Freesound",
    url: "https://freesound.org/people/mitchelk/sounds/136765/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Building explosion. Converted from stereo WAV to mono Ogg Vorbis (44.1 kHz, quality 4). License verified 2026-09-23.",
  },
  {
    title: "drip.wav",
    category: "sfx",
    author: "Nightflame",
    source: "Freesound",
    url: "https://freesound.org/people/Nightflame/sounds/188642/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Placing a stop or waypoint in the transit route editor. Converted from mono WAV to mono Ogg Vorbis (44.1 kHz, quality 4). License verified 2026-09-26.",
  },
  {
    title: "Paving Stones 033",
    category: "texture",
    source: "ambientCG",
    url: "https://ambientcg.com/view?id=PavingStones033",
    license: "CC0 1.0 Universal",
    note: "歩行者専用道路301・303・304・305。1K-PNGのColor・NormalGLを使用。2026-09-19取得。",
  },
  {
    title: "city pasture Den Bosch Nethernlands 1013 am 170328_1113",
    category: "sfx",
    author: "klankbeeld",
    source: "Freesound",
    url: "https://freesound.org/people/klankbeeld/sounds/808956/",
    license: "CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/",
    note: "Base ambience. Unity ambience. Converted from WAV to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-13.",
  },
  {
    title: "City from pasture NL summer 140713_0298.wav",
    category: "sfx",
    author: "klankbeeld",
    source: "Freesound",
    url: "https://freesound.org/people/klankbeeld/sounds/242539/",
    license: "CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/",
    note: "Near-ground city ambience. Unity ambience. Converted from WAV to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-13.",
  },
  {
    title: "Rain with distant traffic",
    category: "sfx",
    author: "olsonbock623",
    source: "Freesound",
    url: "https://freesound.org/people/olsonbock623/sounds/442296/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Drizzle. Unity ambience. Converted from WAV to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-13.",
  },
  {
    title: "Rain_03 (wav-24bit-96khz)",
    category: "sfx",
    author: "vincefred",
    source: "Freesound",
    url: "https://freesound.org/people/vincefred/sounds/515839/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Rain. Original recording includes distant thunder. Unity ambience. Converted from WAV to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-13.",
  },
  {
    title: "Rain without thunder",
    category: "sfx",
    author: "lebaston100",
    source: "Freesound",
    url: "https://freesound.org/people/lebaston100/sounds/346562/",
    license: "CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/",
    note: "Heavy rain. Unity ambience. Converted from WAV to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-13.",
  },
  {
    title: "CONSTRUCTION WORK",
    category: "sfx",
    author: "SamuelGremaud",
    source: "Freesound",
    url: "https://freesound.org/people/SamuelGremaud/sounds/544335/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Building construction and demolition site loop. Unity 3D ambience. Converted from WAV to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-15.",
  },
  {
    title: "Construction Site",
    category: "sfx",
    author: "BO_Nanuz",
    source: "Freesound",
    url: "https://freesound.org/people/BO_Nanuz/sounds/753879/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Road and rail construction site loop. Unity 3D ambience. Converted from MP3 to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-15.",
  },
  {
    title: "Engine_loop_NO-shepard-tone-effect.wav",
    category: "sfx",
    author: "GazarianGorik",
    source: "Freesound",
    url: "https://freesound.org/people/GazarianGorik/sounds/628966/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Car and bus engine loop, pitch follows speed. Unity 3D ambience. Converted to Ogg Vorbis, loudness normalized and loop crossfaded. License verified 2026-09-15.",
  },
  {
    title: "Electric Train Motor Idle - Loop (Queensland Rail InterCity Express)",
    category: "sfx",
    author: "JotrainG",
    source: "Freesound",
    url: "https://freesound.org/people/JotrainG/sounds/700034/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Train motor loop, playback speed and pitch follow train speed. Converted from WAV to Ogg Vorbis and loudness normalized. License verified 2026-09-20.",
  },
  {
    title: "Piano loops 204 octave down long loop 120 bpm",
    category: "bgm",
    author: "josefpres",
    source: "Freesound",
    url: "https://freesound.org/s/849657/",
    license: "CC0 1.0",
  },
  {
    title: "Piano loops 207 octave down short loop 120 bpm",
    category: "bgm",
    author: "josefpres",
    source: "Freesound",
    url: "https://freesound.org/s/850757/",
    license: "CC0 1.0",
  },
  {
    title: "Tada!",
    category: "sfx",
    author: "Yoshicakes77",
    source: "Freesound",
    url: "https://freesound.org/s/850021/",
    license: "CC0 1.0",
    note: "Quest complete",
  },
  {
    title: "VS Button Click 04",
    category: "sfx",
    author: "Vilkas_Sound",
    source: "Freesound",
    url: "https://freesound.org/s/707041/",
    license: "CC BY 4.0",
    requiredText:
      '"VS Button Click 04" by Vilkas_Sound (https://freesound.org/s/707041/) — licensed under CC BY 4.0',
  },
  {
    title: "trash.wav",
    category: "sfx",
    author: "dullbananas",
    source: "Freesound",
    url: "https://freesound.org/s/540877/",
    license: "CC BY 3.0",
    requiredText: '"trash.wav" by dullbananas (https://freesound.org/s/540877/) — licensed under CC BY 3.0',
    note: "Removal click",
  },
  {
    title: "SFX",
    category: "sfx",
    author: "Trinity Theatrical Studio",
    note: "UI click",
  },
  {
    title: "Bribery",
    category: "image",
    author: "juicy_fish",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/bribery_5945366",
    license: "Flaticon Free License",
    requiredText: "Bribery icon designed by juicy_fish from Flaticon",
  },
  {
    title: "Car accident",
    category: "image",
    author: "tulpahn",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/car-accident_7481581",
    license: "Flaticon Free License",
    requiredText: "Car accident icon designed by tulpahn from Flaticon",
  },
  {
    title: "Road",
    category: "image",
    author: "IconBaandar",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/road_14755314",
    license: "Flaticon Free License",
    requiredText: "Road icon designed by IconBaandar from Flaticon",
  },
  {
    title: "Crack",
    category: "image",
    author: "Three musketeers",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/crack_16455914",
    license: "Flaticon Free License",
    requiredText: "Crack icon designed by Three musketeers from Flaticon",
  },
  {
    title: "Fire",
    category: "image",
    author: "Magnific",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/fire_785116",
    license: "Flaticon Free License",
    requiredText: "Fire icon designed by Magnific from Flaticon",
  },
  {
    title: "Shooting",
    category: "image",
    author: "Magnific",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/shooting_5322199",
    license: "Flaticon Free License",
    requiredText: "Shooting icon designed by Magnific from Flaticon",
  },
  {
    title: "Siren",
    category: "image",
    author: "Magnific",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/siren_2785693",
    license: "Flaticon Free License",
    requiredText: "Siren icon designed by Magnific from Flaticon",
  },
  {
    title: "Smoke",
    category: "image",
    author: "Iconjam",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/smoke_12252967",
    license: "Flaticon Free License",
    requiredText: "Smoke icon designed by Iconjam from Flaticon",
  },
  {
    title: "Water",
    category: "image",
    author: "meaicon",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/water_18383632",
    license: "Flaticon Free License",
    requiredText: "Water icon designed by meaicon from Flaticon",
  },
  {
    title: "Flash",
    category: "image",
    author: "Smashicons",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/flash_222506",
    license: "Flaticon Free License",
    requiredText: "Flash icon designed by Smashicons from Flaticon",
  },
  {
    title: "Flight",
    category: "image",
    author: "Good Ware",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/flight_4283171",
    license: "Flaticon Free License",
    requiredText: "Flight icon designed by Good Ware from Flaticon",
    note: "Stat icon: tourism (stats/tourism.png)",
  },
  {
    title: "Virus",
    category: "image",
    author: "Magnific",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/virus_7691073",
    license: "Flaticon Free License",
    requiredText: "Virus icon designed by Magnific from Flaticon",
    note: "Stat icon: sanitation (stats/sanitation.png)",
  },
  {
    title: "Party popper",
    category: "image",
    author: "Mihimihi",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/party-popper_9609176",
    license: "Flaticon Free License",
    requiredText: "Party popper icon designed by Mihimihi from Flaticon",
    note: "Stat icon: entertainment (stats/entertainment.png)",
  },
  {
    title: "Health insurance",
    category: "image",
    author: "nawicon",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/health-insurance_2869818",
    license: "Flaticon Free License",
    requiredText: "Health insurance icon designed by nawicon from Flaticon",
    note: "Stat icon: healthcare (stats/healthcare.png)",
  },
  {
    title: "Bank",
    category: "image",
    author: "syafii5758",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/bank_9555155",
    license: "Flaticon Free License",
    requiredText: "Bank icon designed by syafii5758 from Flaticon",
    note: "Stat icon: GDP (stats/gdp.png)",
  },
  {
    title: "Money",
    category: "image",
    author: "vectorsmarket15",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/money_12740855",
    license: "Flaticon Free License",
    requiredText: "Money icon designed by vectorsmarket15 from Flaticon",
    note: "Stat icon: exchange rate (stats/exchange_rate.png)",
  },
  {
    title: "Briefcase",
    category: "image",
    author: "Magnific",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/briefcase_6964194",
    license: "Flaticon Free License",
    requiredText: "Briefcase icon designed by Magnific from Flaticon",
    note: "Stat icon: unemployment (stats/unemployment.png)",
  },
  {
    title: "Rock051 (崖の岩肌テクスチャ)",
    category: "texture",
    source: "ambientCG",
    url: "https://ambientcg.com/a/Rock051",
    license: "CC0 1.0",
    note: "Terrain cliff",
  },
  {
    title: "Aerial Beach 01 (砂の地面テクスチャ)",
    category: "texture",
    author: "Rob Tuytel",
    source: "Poly Haven",
    url: "https://polyhaven.com/a/aerial_beach_01",
    license: "CC0 1.0",
    note: "Terrain ground: desert and snowfield (2K diffuse). License verified 2026-09-25.",
  },
  {
    title: "Withered Grass (枯れ草の地面テクスチャ)",
    category: "texture",
    author: "Charlotte Baglioni",
    source: "Poly Haven",
    url: "https://polyhaven.com/a/withered_grass",
    license: "CC0 1.0",
    note: "Terrain ground: semi-arid (2K diffuse). License verified 2026-09-25.",
  },
  {
    title: "標高タイル(基盤地図情報数値標高モデル)",
    category: "data",
    source: "国土地理院 地理院タイル",
    url: "https://maps.gsi.go.jp/development/ichiran.html",
    license: "国土地理院コンテンツ利用規約 — https://www.gsi.go.jp/kikakuchousei/kikakuchousei40182.html",
    requiredText: "地理院タイル（標高タイル（基盤地図情報数値標高モデル））を加工して作成",
    note: "Tutorial terrain: yoron (the terrain of the bundled tutorial cities set on Yoron Island). Resampled to a 10 m metric grid, small islets removed and sea floor synthesized. License verified 2026-09-18.",
  },
];

/**
 * 作者名が未確認の Flaticon アイコン(クレジット未完成の残作業)。
 *
 * Flaticon の無料ライセンスは **作者名を含む表記**(`designed by <作者名> from Flaticon`)を
 * 要求するため、作者名が埋まるまで `CREDIT_ENTRIES` へは入れない(不完全な表記を出しても
 * 義務を果たしたことにならず、誤った印象だけを与えるため)。
 *
 * 各要素は `https://www.flaticon.com/free-icon/<ここ>` のスラッグ。ページを開くと作者名と
 * 表記用の文面が表示されるので、確認できたものから `CREDIT_ENTRIES` へ移すこと。
 * 一覧はブラウザのダウンロード履歴に残っていた取得元URLから復元したもので、
 * **ゲームに入っている全アイコンを網羅してはいない**(履歴が2026年2月以前を保持しておらず、
 * それ以前に取得したアイコンの取得元は未特定)。
 *
 * なお Flaticon の有料プランに加入して取得し直せば表記義務そのものが外れる。
 * アイコン点数を考えると、そちらのほうが運用は軽い。
 */
export const FLATICON_PENDING_SLUGS: string[] = [
  "90-degrees_11207561",
  "anchor-point_5340625",
  "arrows_8145804",
  "bridge_6088487",
  "bulldozer_4819578",
  "car_3774278",
  "check_14090371",
  "close_1828778",
  "close_9068699",
  "construction_2486950",
  "copy_4922982",
  "crystal_12298496",
  "dashboard_11502622",
  "deadline_1479689",
  "diskette_2874091",
  "door_9403330",
  "down-arrow_608258",
  "earthquake_6143198",
  "eye_159604",
  "flying-airplane_69987",
  "frame_18384126",
  "github_2111432",
  "hand_16740809",
  "heatmap_6868944",
  "help-web-button_18436",
  "hourglass_483610",
  "land_10053849",
  "layers_6982571",
  "line_9613381",
  "map_592245",
  "natural-disaster_3436860",
  "next_15114481",
  "no-money_2762618",
  "pen_10336582",
  "region_992219",
  "reload_9311549",
  "road-intersection_2062607",
  "rocket_1195017",
  "sand-clock_726210",
  "search_3031293",
  "setting_3019014",
  "settings_2099058",
  "sign_16001176",
  "sketch_681560",
  "sketchbook_1286028",
  "table_9515212",
  "tool_9109810",
  "traffic-jam_4886426",
  "train_2062152",
  "transfer_4906853",
  "turn-back_3585896",
  "unlocked-padlock_880",
  "up-arrow_626075",
  "visibility_3395544",
  "wrench_9459152",
];
