/**
 * Credit display data. This list fulfills attribution requirements imposed by some free asset sites.
 * CreditsWindow, opened from settings, renders this array directly.
 *
 * ## Inclusion rules
 *
 * - **Include assets with known sources even when attribution is optional.** This preserves traceability if
 * terms change or the source is questioned. Trying to include only strictly required credits risks license
 * violations from misclassification.
 * - **Exclude original assets (models made with our own Blender scripts or AI assistance, etc.).** Credits
 * attribute others' work; original work has no external creator to credit. Keep production records in
 * `genesis-gallery`.
 * - **Copy `requiredText` exactly as specified by the terms.** Do not translate, shorten, or alter it; some
 * assets require exact wording.
 * - **Update this array in the same commit when adding/replacing assets.** Assets cannot be used without
 * verifying their source (see Skill `asset-credits` for the procedure).
 *
 * ## Translation
 *
 * Creator, site, and license names and required wording must remain in their original form, outside i18n.
 * Localize only category headings and field labels (`credits.*`).
 */

/**
 * Credit categories. `CREDIT_CATEGORY_ORDER` defines window order; i18n key `credits.categories.<category>`
 * supplies headings. Covers assets only, excluding game engines and OSS libraries. Bundle their copyright
 * notices and license text separately (see `city-genesis/docs/ja/contributing/素材クレジット管理.md`).
 * Terrain and geographic data are credited by each scenario (`scenario_credits`), not here, because maps are
 * added, removed and downloaded independently of the game.
 */
export type CreditCategory = "bgm" | "sfx" | "image" | "font" | "model" | "texture";

/** Category display order. */
export const CREDIT_CATEGORY_ORDER: CreditCategory[] = [
  "bgm",
  "sfx",
  "image",
  "font",
  "model",
  "texture",
];

export type CreditEntry = {
  /** List heading: asset or asset set name. */
  title: string;
  category: CreditCategory;
  /** Creator or rights holder name. */
  author?: string;
  /** Distributor name (site or organization). */
  source?: string;
  /** Source URL. When present, the heading becomes a link opened in the default browser. */
  url?: string;
  /** License name (`CC BY 4.0`, `MIT`, etc.). */
  license?: string;
  /**
   * Exact wording required by the terms, if any. Display the original verbatim in monospace; do not translate or
   * alter it.
   */
  requiredText?: string;
  /** Additional notes, such as usage location. Not translated; keep to proper names or short terms. */
  note?: string;
};

/**
 * Credit entries.
 *
 * Template for adding assets:
 *
 * ```ts
 * {
 *   title: "Asset or set name",
 *   category: "sfx",
 *   author: "Creator name",
 *   source: "Distribution site",
 *   url: "https://example.com/...",
 *   license: "CC BY 4.0",
 *   requiredText: "Exact attribution required by the terms, if any",
 *   note: "Usage location",
 * },
 * ```
 */
export const CREDIT_ENTRIES: CreditEntry[] = [
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
    title: "Lost SoulZ The Labyrinth Theme",
    category: "bgm",
    author: "naomuhibu",
    source: "Freesound",
    url: "https://freesound.org/people/naomuhibu/sounds/732050/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "January BGM (bgm-01.opus). Converted from MP3 to Ogg Opus with ffmpeg at 112 kbps VBR. License verified 2026-10-04.",
  },
  {
    title: "Maybe Peaceful Sound",
    category: "bgm",
    author: "MessySloth",
    source: "Freesound",
    url: "https://freesound.org/people/MessySloth/sounds/750289/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "July BGM (bgm-07.opus). Converted from WAV to Ogg Opus with ffmpeg at 112 kbps VBR. License verified 2026-10-04.",
  },
  {
    title: "A Quiet Day (Loop).mp3",
    category: "bgm",
    author: "Adam_Zero",
    source: "Freesound",
    url: "https://freesound.org/people/Adam_Zero/sounds/678985/",
    license: "CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/",
    note: "October BGM (bgm-10.opus). Converted from MP3 to Ogg Opus with ffmpeg at 112 kbps VBR. License verified 2026-10-04.",
  },
  {
    title: "Relaxing Orchestra Music (Village)",
    category: "bgm",
    author: "Migfus20",
    source: "Freesound",
    url: "https://freesound.org/people/Migfus20/sounds/683260/",
    license: "CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/",
    note: "May BGM (bgm-05.opus). Original title: Village Prosper. Converted from MP3 to Ogg Opus with ffmpeg at 112 kbps VBR. License verified 2026-10-04.",
  },
  {
    title: "Nostalgic Childhood - Grand Piano.mp3",
    category: "bgm",
    author: "akirawakira",
    source: "Freesound",
    url: "https://freesound.org/people/akirawakira/sounds/488683/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "September BGM (bgm-09.opus). Converted from MP3 to Ogg Opus with ffmpeg at 112 kbps VBR. License verified 2026-10-04.",
  },
  {
    title: "christmas carols on piano.wav",
    category: "bgm",
    author: "Casonika",
    source: "Freesound",
    url: "https://freesound.org/people/Casonika/sounds/496393/",
    license: "CC BY 4.0 — https://creativecommons.org/licenses/by/4.0/",
    note: "December BGM (bgm-12.opus). Converted from WAV to Ogg Opus with ffmpeg at 112 kbps VBR. License verified 2026-10-04.",
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
    title: "Congrats",
    category: "sfx",
    author: "Fupicat",
    source: "Freesound",
    url: "https://freesound.org/people/Fupicat/sounds/607207/",
    license: "CC0 1.0 — https://creativecommons.org/publicdomain/zero/1.0/",
    note: "Achievement unlocked (achievement.opus). Converted from stereo WAV to Ogg Opus with ffmpeg at 96 kbps VBR. License verified 2026-10-25.",
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
    title: "Crack",
    category: "image",
    author: "Three musketeers",
    source: "Flaticon",
    url: "https://www.flaticon.com/free-icon/crack_16455914",
    license: "Flaticon Free License",
    requiredText: "Crack icon designed by Three musketeers from Flaticon",
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
];

/**
 * Flaticon icons with unverified creators (remaining incomplete credits). Flaticon's free license requires
 * **attribution including the creator's name** (`designed by <creator> from Flaticon`); do not add to
 * `CREDIT_ENTRIES` until verified. Incomplete attribution does not fulfill the requirement and is misleading.
 *
 * Each entry is the slug in `https://www.flaticon.com/free-icon/<slug>`. Open the page to verify creator and
 * attribution wording, then move to `CREDIT_ENTRIES`. Recovered from source URLs in browser download history;
 * **this does not cover all icons in the game** (history does not extend before February 2026, so older sources
 * remain unidentified).
 *
 * Obtaining the assets again through a paid Flaticon subscription removes attribution requirements. Given the
 * icon count, that is easier to maintain.
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
