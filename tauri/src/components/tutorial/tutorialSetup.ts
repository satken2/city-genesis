/**
 * チュートリアルの初期都市状態を構築するセットアップ処理
 *
 * 一部のチュートリアルシナリオは、開始時にあらかじめ都市の一部
 * (道路・土地区画・天然資源など)が配置されている状態から始める。
 * バックエンドはチュートリアル中 `gd3.template_mode = True` で動作しており、
 * この状態では REST API に送信したデータがバリデーションなしでそのまま反映される。
 * その性質を利用し、フロントエンド(Tauri)から必要なエンティティを POST/PATCH して
 * 初期状態を作り上げる。
 *
 * 各シナリオのセットアップ関数は SCENARIO_SETUPS に登録され、
 * Tutorial コンポーネントがゲーム開始(tutorial:show)を検知したときに一度だけ実行する。
 * バックエンドの状態を直接見て冪等性を判定するため、
 * 既に構築済みの都市に対して二重に実行されることはない。
 */

import { fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import {
  STATUS_ACTIVE,
  PATH_STREET,
  PARCEL_LAND,
  VARIANT_APARTMENT,
} from "../../constants";
import type { PathListResponse } from "../../types/generated";

/** GET /api/v1/master/paths のレスポンス(自動生成の型が無いためここで定義する)。
 * マスターの各バリアントは経路種別を `type` フィールドに持つ(PATH_STREET 等)。 */
type PathMasterResponse = {
  paths: Record<string, { type?: number }>;
};

/** API URL の末尾スラッシュを正規化する */
function normalizeBaseUrl(url: string): string {
  return url.replace(/\/+$/, "");
}

/**
 * localStorage キー。
 * セットアップ時に確定した「右上の道路」と「初期地区」の ID を記録し、
 * ステップの完了判定(tutorialSteps.ts)から参照する。
 */
export const TUTORIAL2_TOPRIGHT_PATH_KEY = "tutorial2_topright_path_id";
export const TUTORIAL2_MAIN_ZONE_KEY = "tutorial2_main_zone_id";

/**
 * UI 状態(バックエンドからは観測できない)をチュートリアル判定へ橋渡しするための
 * localStorage フラグキー。値は "1"(ON)/"0"(OFF)。
 * それぞれ対応するコンポーネントが自身の表示状態に合わせて書き込む。
 * - RESOURCE_VISIBLE: 表示パネルで天然資源が表示状態か(useDisplayOptions)
 * - ZONE_PANEL_OPEN: 地区詳細パネルが開かれているか(ZoneDetailPanel)
 * - PARCEL_PANEL_OPEN: 土地区画詳細パネルが開かれているか(ParcelDetailPanel)
 */
export const TUTORIAL_FLAG_RESOURCE_VISIBLE = "tutorial2_resource_visible";
export const TUTORIAL_FLAG_ZONE_PANEL_OPEN = "tutorial2_zone_panel_open";
export const TUTORIAL_FLAG_PARCEL_PANEL_OPEN = "tutorial2_parcel_panel_open";

/**
 * シナリオ3「インフラを整備しよう」で構築した土地区画の ID を記録する localStorage キー。
 * セットアップ時に確定した区画 ID をステップの完了判定(tutorialSteps.ts)から参照し、
 * 「左の広い区画に太陽光発電所」「民家隣の空き区画に変電所」といった立地の妥当性確認に用いる。
 * - SOLAR_PARCEL: 都市左側の広い区画(太陽光発電所を建てる場所)
 * - SUBSTATION_PARCEL: 都市右側・民家の隣の空き区画(変電所を建てる場所)
 * - HOUSE_PARCEL: 民家(集合住宅)が最初から建っている区画
 */
export const TUTORIAL3_SOLAR_PARCEL_KEY = "tutorial3_solar_parcel_id";
export const TUTORIAL3_SUBSTATION_PARCEL_KEY = "tutorial3_substation_parcel_id";
export const TUTORIAL3_HOUSE_PARCEL_KEY = "tutorial3_house_parcel_id";

/**
 * H 型道路網のノード ID。
 * template_mode では PATCH でノード ID を直接指定できるため、
 * 交差点(NL/NR)を共有する形で 6 個の端点ノードを割り当てて H 形状を表現する。
 * 自動生成されるノード ID(小さな整数から採番される)との衝突を避けるため、
 * 十分大きな値を用いる。
 */
const NODE = {
  TL: 9001, // 左上端点
  BL: 9002, // 左下端点
  NL: 9003, // 左の交差点(縦棒と横棒の接点)
  TR: 9004, // 右上端点
  BR: 9005, // 右下端点
  NR: 9006, // 右の交差点(縦棒と横棒の接点)
} as const;

/**
 * H 型道路網の座標定義。
 * flat マップの初期解放セル(おおよそ x,z ∈ [-500, 500])の内側に収まるよう配置する。
 * 座標は [x, y, z](y は地面からの高さで、街路は常に 0)。
 */
const LEFT_X = -150;
const RIGHT_X = 150;
const TOP_Z = 250;
const BOTTOM_Z = -250;
const MID_Z = 0;

/** 1本の道路の作成定義 */
type RoadDef = {
  tempId: string;
  start: [number, number, number];
  end: [number, number, number];
  startNode: number;
  endNode: number;
};

/**
 * H を構成する 5 本の道路。
 * 左右の縦棒はそれぞれ交差点(NL/NR)で上下 2 本に分割され、
 * 横棒 1 本が両交差点を結ぶ。合計 5 本。
 * "right_top"(右上の道路)がチュートリアルで新しい地区に分割される対象であり、
 * 天然資源(鉱脈)もこの道路に重なるように配置する。
 */
const ROADS: RoadDef[] = [
  {
    tempId: "left_top",
    start: [LEFT_X, 0, TOP_Z],
    end: [LEFT_X, 0, MID_Z],
    startNode: NODE.TL,
    endNode: NODE.NL,
  },
  {
    tempId: "left_bottom",
    start: [LEFT_X, 0, MID_Z],
    end: [LEFT_X, 0, BOTTOM_Z],
    startNode: NODE.NL,
    endNode: NODE.BL,
  },
  {
    tempId: "crossbar",
    start: [LEFT_X, 0, MID_Z],
    end: [RIGHT_X, 0, MID_Z],
    startNode: NODE.NL,
    endNode: NODE.NR,
  },
  {
    tempId: "right_top",
    start: [RIGHT_X, 0, MID_Z],
    end: [RIGHT_X, 0, TOP_Z],
    startNode: NODE.NR,
    endNode: NODE.TR,
  },
  {
    tempId: "right_bottom",
    start: [RIGHT_X, 0, MID_Z],
    end: [RIGHT_X, 0, BOTTOM_Z],
    startNode: NODE.NR,
    endNode: NODE.BR,
  },
];

/** 土地区画を道路の左右に生成する際の幅(道路方向)と奥行き(道路から離れる方向) */
const PARCEL_WIDTH = 40;
const PARCEL_DEPTH = 30;

/**
 * 天然資源(鉱脈)の埋蔵ポリゴン。
 * 右上の道路(x=150, z∈[0,250])とその左右の土地区画にだけ重なり、
 * 横棒(z=0)や右下の道路(z<0)には掛からない範囲にする。座標は [x, z]。
 */
const RESOURCE_POLYGON: [number, number][] = [
  [RIGHT_X - PARCEL_DEPTH - 5, 20],
  [RIGHT_X + PARCEL_DEPTH + 5, 20],
  [RIGHT_X + PARCEL_DEPTH + 5, TOP_Z - 10],
  [RIGHT_X - PARCEL_DEPTH - 5, TOP_Z - 10],
];

/** 鉱脈の埋蔵量。ゲーム性には影響しない適当な大きめの値。 */
const RESOURCE_MAX_AMOUNT = 1_000_000;

/**
 * PATH_STREET(街路)に属する path_variant を1つ選ぶ。
 * バリアント ID をハードコードせず、マスターデータから街路タイプのものを探して用いる。
 */
async function resolveStreetVariant(base: string): Promise<number> {
  const master = await fetchApi<PathMasterResponse>(`${base}/api/v1/master/paths`);
  const paths = master.paths ?? {};
  for (const [variantId, entry] of Object.entries(paths)) {
    if (entry.type === PATH_STREET) {
      return Number(variantId);
    }
  }
  throw new Error("PATH_STREET に対応する path_variant が見つかりません");
}

/**
 * 「鉱脈」に対応する resource_type を選ぶ。
 * display_name に「鉱脈」を含むものを優先し、見つからなければ最初の資源種別を用いる。
 */
async function resolveMineResourceType(base: string): Promise<number> {
  const master = await fetchApi<{ resource_types?: Record<string, { display_name?: string }> }>(
    `${base}/api/v1/master/resource_types`
  );
  const types = master.resource_types ?? {};
  const entries = Object.entries(types);
  // 「鉱脈」を最優先、無ければ「鉱」を含むもの、それも無ければ先頭を用いる
  const mine =
    entries.find(([, v]) => (v.display_name ?? "").includes("鉱脈")) ??
    entries.find(([, v]) => (v.display_name ?? "").includes("鉱"));
  if (mine) return Number(mine[0]);
  if (entries.length > 0) return Number(entries[0][0]);
  throw new Error("天然資源種別マスターが空です");
}

/**
 * バックエンドが既にチュートリアル2の初期状態を持っているかを判定する。
 * 5本以上の街路が存在すれば構築済みとみなす(冪等性の担保)。
 */
async function isAlreadyBuilt(base: string): Promise<boolean> {
  try {
    const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
    const streets = data.paths.filter((p) => p.path_type === PATH_STREET);
    return streets.length >= ROADS.length;
  } catch {
    return false;
  }
}

/**
 * シナリオ2「地区設定をしよう」の初期状態を構築する。
 *
 * 1. H型の5本の街路を POST し、template_mode で直接 ACTIVE に PATCH する
 *    (このときノード ID も割り当てて H の連結性を表現する)。
 * 2. 5本すべてを1つの地区(初期地区)にまとめる。
 * 3. 各道路の左右に土地区画を生成する。
 * 4. 右上の道路とその土地区画に重なるように鉱脈(天然資源)を配置する。
 */
async function setupDistrictScenario(apiBaseUrl: string): Promise<void> {
  const base = normalizeBaseUrl(apiBaseUrl);

  if (await isAlreadyBuilt(base)) {
    return;
  }

  const streetVariant = await resolveStreetVariant(base);

  // 1. 5本の街路を一括作成する
  const createRes = await fetchApi<{ paths: { path_temp_id: string; path_id: number }[] }>(
    `${base}/api/v1/paths`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        paths: ROADS.map((r) => ({
          path_temp_id: r.tempId,
          path_type: PATH_STREET,
          path_variant: streetVariant,
          start_point: r.start,
          end_point: r.end,
          curved: false,
        })),
      }),
    }
  );

  // temp_id → path_id の対応表を作る
  const idByTemp = new Map<string, number>();
  for (const m of createRes.paths) {
    if (m.path_temp_id != null) idByTemp.set(m.path_temp_id, m.path_id);
  }

  // 2. 各道路を ACTIVE 化し、ノード ID を割り当てる(template_mode で無検証反映)
  const allPathIds: number[] = [];
  for (const r of ROADS) {
    const pathId = idByTemp.get(r.tempId);
    if (pathId == null) continue;
    allPathIds.push(pathId);
    await fetchApi(`${base}/api/v1/paths/${pathId}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        status: STATUS_ACTIVE,
        start_node_id: r.startNode,
        end_node_id: r.endNode,
      }),
    });
  }

  const topRightPathId = idByTemp.get("right_top");

  // 3. 5本すべてを含む「初期地区」を作成する(都市全体が1つの地区の状態)
  const zoneRes = await fetchApi<{ zone: { zone_id: number } }>(
    `${base}/api/v1/zones`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path_ids: allPathIds }),
    }
  );
  const mainZoneId = zoneRes.zone?.zone_id;

  // ステップ判定に使う ID を記録する
  if (topRightPathId != null) {
    localStorage.setItem(TUTORIAL2_TOPRIGHT_PATH_KEY, String(topRightPathId));
  }
  if (mainZoneId != null) {
    localStorage.setItem(TUTORIAL2_MAIN_ZONE_KEY, String(mainZoneId));
  }

  // 4. 各道路の左右に土地区画を生成する。
  // 交差点付近では区画が重なって個別に失敗しうるが、1本の失敗で全体を止めないよう
  // 道路ごとに握りつぶして継続する(ベストエフォート)。
  for (const pathId of allPathIds) {
    try {
      await fetchApi(`${base}/api/v1/parcels`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          mode: "along_path",
          path_id: pathId,
          side: "both",
          width: PARCEL_WIDTH,
          height: PARCEL_DEPTH,
        }),
      });
    } catch (e) {
      console.warn(`[tutorialSetup] 道路 ${pathId} の土地区画生成に失敗:`, e);
    }
  }

  // 5. 右上の道路と土地区画に重なる鉱脈を配置する
  try {
    const resourceType = await resolveMineResourceType(base);
    await fetchApi(`${base}/api/v1/resources`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        polygon: RESOURCE_POLYGON,
        resource_type: resourceType,
        max_amount: RESOURCE_MAX_AMOUNT,
      }),
    });
  } catch (e) {
    console.warn("[tutorialSetup] 鉱脈の配置に失敗:", e);
  }

  // Unity 側に最新状態を再ロードさせる
  await sendSessionReload().catch(() => {});
}

/**
 * シナリオ3「インフラを整備しよう」の道路・土地区画の座標定義。
 * flat マップは海岸線がおおよそ x,z ∈ [-250, 250] にあるため、区画の奥行き分の
 * バッファを見込んで全要素を x,z ∈ [-200, 200] の範囲に収める(範囲外は海岸線外となり
 * PathOutsideCoastlineError で作成に失敗する)。中央を東西に貫く1本の長い道路と、
 * その左端・右端に土地区画を配置する。
 * 座標は道路端点が [x, y, z](y は街路なので常に 0)、区画ポリゴンは [x, z] の2次元。
 */
const INFRA_ROAD_START: [number, number, number] = [-200, 0, 0];
const INFRA_ROAD_END: [number, number, number] = [200, 0, 0];

/**
 * インフラシナリオの道路端点ノード ID。
 * template_mode では PATCH でノード ID を直接指定できるため、
 * 自動採番(小さな整数)との衝突を避けて十分大きな固定値を割り当てる。
 */
const INFRA_ROAD_NODE = {
  LEFT: 9101,
  RIGHT: 9102,
} as const;

/**
 * 3つの土地区画のポリゴン([x, z] の頂点列)。すべて x,z ∈ [-200, 200] に収める。
 * - SOLAR(左): 太陽光発電所を建てる区画。発電所用にやや大きめに取る。
 * - HOUSE(右): 民家(集合住宅)が最初から建っている区画。
 * - SUBSTATION(右): 民家の隣にある空き区画。ここに変電所を建てさせる。
 * 前面(道路側)の辺を街路のすぐ脇(z=2)に置き、道路に接するように配置する
 * (街路の幅は数m程度のため、この位置で区画の前面が道路縁に接する)。
 * 民家と変電所は道路の同じ側で x 方向に隣り合うように並べる。
 */
const INFRA_SOLAR_POLYGON: [number, number][] = [
  [-190, 2],
  [-166, 2],
  [-166, 34],
  [-190, 34],
];
const INFRA_HOUSE_POLYGON: [number, number][] = [
  [110, 2],
  [124, 2],
  [124, 16],
  [110, 16],
];
const INFRA_SUBSTATION_POLYGON: [number, number][] = [
  [128, 2],
  [142, 2],
  [142, 16],
  [128, 16],
];

/** GET /api/v1/master/agents のレスポンス(自動生成の型が無いためここで定義する)。
 * 各バリアントは建設に必要な type(経済主体種別)と階数の下限 min_floors を持つ。 */
type AgentMasterResponse = {
  agents: Record<string, { type?: number; min_floors?: number }>;
};

/** POST /api/v1/buildings のレスポンス(single_agent スキーマを直接返す)。 */
type BuildingCreateResponse = {
  agent_id: number;
};

/**
 * バックエンドが既にシナリオ3の初期状態を持っているかを判定する。
 * 初期配置の民家(集合住宅)が存在すれば構築済みとみなす(冪等性の担保)。
 * シナリオ2の判定(街路5本)とは初期構成が異なるため、専用のロジックを用いる。
 */
async function isInfraAlreadyBuilt(base: string): Promise<boolean> {
  try {
    const data = await fetchApi<{ buildings: { variant?: number }[] }>(
      `${base}/api/v1/buildings`
    );
    return data.buildings.some((b) => b.variant === VARIANT_APARTMENT);
  } catch {
    return false;
  }
}

/**
 * 都市左側に1つの土地区画を manual モードで作成し、その parcel_id を返す。
 * polygon は [x, z] の頂点列で、template_mode では取得済みセル内であればそのまま登録される。
 */
async function createTutorialParcel(
  base: string,
  polygon: [number, number][]
): Promise<number | null> {
  try {
    const res = await fetchApi<{ parcel?: { parcel_id: number } }>(
      `${base}/api/v1/parcels`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          mode: "manual",
          polygon,
          parcel_type: PARCEL_LAND,
        }),
      }
    );
    return res.parcel?.parcel_id ?? null;
  } catch (e) {
    console.warn("[tutorialSetup] 土地区画の作成に失敗:", e);
    return null;
  }
}

/**
 * シナリオ3「インフラを整備しよう」の初期状態を構築する。
 *
 * 1. 都市中央を東西に貫く長い道路を1本 POST し、template_mode で直接 ACTIVE に PATCH する
 *    (このときノード ID も割り当てて経路グラフ上の端点を確定する)。
 * 2. その道路を含む地区を作成し、以降に作る土地区画の所属地区を確定させる。
 * 3. 道路の左端に広い区画(太陽光発電所用)、右端に2つの区画(民家用・変電所用)を作成する。
 * 4. 民家(集合住宅)を右端の一方の区画に建設し、template_mode で即 ACTIVE(完成)にする。
 *
 * これにより「電力の供給されていない民家が既にある都市」を再現し、
 * プレイヤーに発電所・変電所・送電線を整備させる導線を用意する。
 */
async function setupInfraScenario(apiBaseUrl: string): Promise<void> {
  const base = normalizeBaseUrl(apiBaseUrl);

  if (await isInfraAlreadyBuilt(base)) {
    return;
  }

  const streetVariant = await resolveStreetVariant(base);

  // 1. 東西に貫く長い道路を1本作成する
  const createRes = await fetchApi<{ paths: { path_temp_id: string; path_id: number }[] }>(
    `${base}/api/v1/paths`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        paths: [
          {
            path_temp_id: "main_road",
            path_type: PATH_STREET,
            path_variant: streetVariant,
            start_point: INFRA_ROAD_START,
            end_point: INFRA_ROAD_END,
            curved: false,
          },
        ],
      }),
    }
  );

  const roadId = createRes.paths.find((p) => p.path_temp_id === "main_road")?.path_id;
  if (roadId == null) {
    console.warn("[tutorialSetup] 道路の作成に失敗したため初期化を中断します");
    return;
  }

  // 2. 道路を ACTIVE 化し、端点ノード ID を割り当てる(template_mode で無検証反映)
  await fetchApi(`${base}/api/v1/paths/${roadId}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      status: STATUS_ACTIVE,
      start_node_id: INFRA_ROAD_NODE.LEFT,
      end_node_id: INFRA_ROAD_NODE.RIGHT,
    }),
  });

  // 3. 道路を含む地区を作成する(土地区画の所属地区を確定させるため)
  try {
    await fetchApi(`${base}/api/v1/zones`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path_ids: [roadId] }),
    });
  } catch (e) {
    console.warn("[tutorialSetup] 地区の作成に失敗:", e);
  }

  // 4. 3つの土地区画(左=太陽光用、右=民家用・変電所用)を作成し、ID を記録する
  const solarParcelId = await createTutorialParcel(base, INFRA_SOLAR_POLYGON);
  const houseParcelId = await createTutorialParcel(base, INFRA_HOUSE_POLYGON);
  const substationParcelId = await createTutorialParcel(base, INFRA_SUBSTATION_POLYGON);

  if (solarParcelId != null) {
    localStorage.setItem(TUTORIAL3_SOLAR_PARCEL_KEY, String(solarParcelId));
  }
  if (houseParcelId != null) {
    localStorage.setItem(TUTORIAL3_HOUSE_PARCEL_KEY, String(houseParcelId));
  }
  if (substationParcelId != null) {
    localStorage.setItem(TUTORIAL3_SUBSTATION_PARCEL_KEY, String(substationParcelId));
  }

  // 5. 民家(集合住宅)を右端の区画に建設し、template_mode で即 ACTIVE(完成)にする
  if (houseParcelId != null) {
    try {
      const master = await fetchApi<AgentMasterResponse>(`${base}/api/v1/master/agents`);
      const apartment = master.agents?.[String(VARIANT_APARTMENT)] ?? {};
      const agentType = apartment.type;
      const minFloors = apartment.min_floors ?? 1;

      // 建物作成は必須5フィールド(parcel_id, type, variant, construction_ratio, floors)を要求する。
      // type と階数下限はマスターから解決し、数値のハードコードを避ける。
      const created = await fetchApi<BuildingCreateResponse>(`${base}/api/v1/buildings`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          parcel_id: houseParcelId,
          type: agentType,
          variant: VARIANT_APARTMENT,
          construction_ratio: 1.0,
          floors: minFloors,
        }),
      });

      // 建設をスキップして即完成扱いにする(template_mode でステータス制限をバイパス)
      if (created.agent_id != null) {
        await fetchApi(`${base}/api/v1/agents/${created.agent_id}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ status: STATUS_ACTIVE }),
        });
      }
    } catch (e) {
      console.warn("[tutorialSetup] 民家の建設に失敗:", e);
    }
  }

  // Unity 側に最新状態を再ロードさせる
  await sendSessionReload().catch(() => {});
}

/**
 * シナリオ ID ごとの初期状態セットアップ関数。
 * 初期状態の構築が不要なシナリオ(basic など)は登録しない。
 */
const SCENARIO_SETUPS: Record<string, (apiBaseUrl: string) => Promise<void>> = {
  scenario2: setupDistrictScenario,
  scenario3: setupInfraScenario,
};

/**
 * 指定シナリオの初期状態セットアップを実行する。
 * 登録がないシナリオでは何もしない。
 * セットアップの失敗はチュートリアル全体を止めないよう握りつぶし、コンソールに記録する。
 */
export async function runTutorialSetup(
  tutorialId: string,
  apiBaseUrl: string
): Promise<void> {
  const setup = SCENARIO_SETUPS[tutorialId];
  if (!setup || !apiBaseUrl) return;
  try {
    await setup(apiBaseUrl);
  } catch (e) {
    console.error(`[tutorialSetup] シナリオ ${tutorialId} の初期化に失敗しました:`, e);
  }
}
