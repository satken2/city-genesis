/**
 * 建物建設パネル
 *
 * window_type="building_construction" のポップアップウィンドウで表示する専用パネル。
 * 建物タイプ(bd_type)の選択、バリアント選択、階数設定、形状プレビューを行う。
 *
 * 操作フロー:
 *   1. 建物タイププルダウンでカテゴリを選択 → バリアントタイルが表示される
 *   2. バリアントタイルをクリックして選択 → 説明文が表示される
 *   3. IPC で graphic モジュールに STATE_EDIT_BUILDING を指示
 *   4. graphic 側で土地区画が選択されると edit_building.select_parcel が通知される
 *      (土地区画外の地点なら edit_building.select_point で直接配置モードに入る)
 *   5. 形状プレビュー・情報欄が表示される
 *   6. 階数を変更すると edit_building.update_floors が送信される
 *   7. [建設を確定] で UI が直接バックエンドに送信する(道路建設と同じ責務分担。
 *      区画モード=POST /buildings、直接配置=POST /buildings/direct、
 *      既存建物の編集=PATCH /agents/{id})。成功後は session:reload で Unity に再描画させる
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import type { AgentMasterEntry, ParcelData } from "../../types";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { resolveParcelIcon } from "../../utils/parcelIcon";

import { setHintbarText } from "../../utils/hintbar";
import {
  sendChangeState,
  sendChangeDisplay,
  sendEditBuildingUpdateFloors,
  sendEditBuildingUpdateShape,
  sendEditBuildingUpdateEffectRange,
  sendEditBuildingUpdateDirectParams,
  sendFocusGraphic,
  sendSessionReload,
  IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL,
  IPC_EVENT_EDIT_BUILDING_SELECT_POINT,
  IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL,
  IPC_EVENT_EDIT_BUILDING_UPDATE_DIRECT_ANGLE,
} from "../../utils/ipc";
import type { EffectRangeRegion } from "../../utils/ipc";
import { fetchApi, ApiError } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";

import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { BuildingFacilityInfoCard } from "./BuildingFacilityInfoCard";
import { showNotification } from "../../utils/notification";
import { BUILDING_PUBLIC, VARIANT_CITY } from "../../constants";
import "./BuildingConstructionPanel.css";

// 直接配置モードで default_width が未設定のバリアント用のフォールバック(m)。
// バックエンド crud_buildings.DEFAULT_BUILDING_WIDTH と揃える。
const DEFAULT_BUILDING_WIDTH = 20.0;

// 回転角(度)を [0, 360) に正規化する。回転ステッパーはマイナス・360超を許容するため、
// 確定送信時にこの関数で mod 360 する(例: 450→90, -90→270)。
function normalizeAngle(angle: number): number {
  return ((angle % 360) + 360) % 360;
}

type BuildingPreviewData = {
  polygon: number[][];
  height: number;
  center_point: number[];
  area?: number;
};

type BuildingConstructionPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
  editAgentId?: number | null;
};

/* ---- メインコンポーネント ---- */

export function BuildingConstructionPanel({ apiBaseUrl, onClose, editAgentId }: BuildingConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const appWindow = useMemo(() => getCurrentWindow(), []);

  const [selectedBdType, setSelectedBdType] = useState<number | null>(null);
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  const [floors, setFloors] = useState(1);
  // 建ぺい率(0.0〜1.0)。敷地面積に対する建築面積の上限割合。プレビュー形状・建設コストの算出に用いる。
  const [coverageRatio, setCoverageRatio] = useState(0.6);
  // 外観バリアント(visual_variant1/2/3)。0.0〜1.0の値で建物の外観を決定する。
  // 新規建設時はバリアント選択時に0.5で初期化され、スライダーで調整後、確定時にバックエンドへ送信される。
  // 編集モードでは既存建物の値を読み込む。GET /agents/{id}がvisual_variantを返さない場合はnullのままになり、
  // 確定時にnullのフィールドは送信されないため現在値が維持される（ユーザーがスライダーを動かさなければ外観は変わらない）。
  const [visualVariant, setVisualVariant] = useState<number | null>(null);
  const [visualVariant2, setVisualVariant2] = useState<number | null>(null);
  const [visualVariant3, setVisualVariant3] = useState<number | null>(null);
  const [parcelId, setParcelId] = useState<number | null>(null);
  const [parcelData, setParcelData] = useState<ParcelData | null>(null);
  const [isParcelLoading, setIsParcelLoading] = useState(false);
  // 直接配置モード(土地区画外クリック)の状態。pickedPosition が非nullのとき直接配置モードで、
  // parcelId(区画モード)とは相互に排他となる。
  const [pickedPosition, setPickedPosition] = useState<number[] | null>(null);
  // 直接配置の底面矩形サイズ(横幅・縦幅・回転角)。バリアント選択時に default_width で初期化する。
  const [rectWidth, setRectWidth] = useState(DEFAULT_BUILDING_WIDTH);
  const [rectDepth, setRectDepth] = useState(DEFAULT_BUILDING_WIDTH);
  const [rectAngle, setRectAngle] = useState(0);
  // 直接配置の設置可否(バックエンドの preview-direct が返す valid / invalid_reason)。
  const [directValid, setDirectValid] = useState(false);
  const [directInvalidReason, setDirectInvalidReason] = useState<string | null>(null);
  const [buildingPreview, setBuildingPreview] = useState<BuildingPreviewData | null>(null);
  const [estimatedCost, setEstimatedCost] = useState<number | null>(null);
  // 完全に平坦な地形（傾斜ゼロ）で建てた場合の標準コスト。推定コストが標準の何倍かの表示に使う。
  const [estimatedStandardCost, setEstimatedStandardCost] = useState<number | null>(null);
  // 推定建設期間(日)。道路パネルと同じ枠組みで表示する。
  const [estimatedDurationDays, setEstimatedDurationDays] = useState<number | null>(null);
  // TYPE_STANDARD(一般施設)のときのみ返る、最大稼働時の推定生産量・雇用人数・稼働コスト(いずれもsimulation_daysあたり)。
  const [estimatedMaxProduction, setEstimatedMaxProduction] = useState<number | null>(null);
  const [estimatedEmployment, setEstimatedEmployment] = useState<number | null>(null);
  const [estimatedOperatingCost, setEstimatedOperatingCost] = useState<number | null>(null);
  const [isCostLoading, setIsCostLoading] = useState(false);
  // 選択中の施設がインフラ供給施設(効果範囲を持つ)で、効果範囲を3D表示中かどうか。
  // 効果範囲の凡例表示の出し分けに使う。
  const [effectRangeActive, setEffectRangeActive] = useState(false);

  const [cityHallPlaced, setCityHallPlaced] = useState<boolean>(true);
  const [templateMode, setTemplateMode] = useState<boolean>(false);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();

    const fetchStatus = async () => {
      try {
        const baseUrl = apiBaseUrl.replace(/\/+$/, "");
        
        // city と session を並行して取得
        const [cityRes, sessionRes] = await Promise.all([
          fetch(`${baseUrl}/api/v1/city`, { signal: controller.signal }),
          fetch(`${baseUrl}/api/v1/sessions`, { signal: controller.signal }),
        ]);

        if (cityRes.ok && sessionRes.ok) {
          const cityData = await cityRes.json();
          const sessionData = await sessionRes.json();
          
          setCityHallPlaced(cityData.city?.city_hall_placed ?? false);
          setTemplateMode(sessionData.template_mode ?? false);
        }
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch city or session status:", err);
        }
      }
    };

    void fetchStatus();

    return () => controller.abort();
  }, [apiBaseUrl]);

  const isCityHallPlacedGateActive = !cityHallPlaced && !templateMode;

  /* ---- 既存建物編集モード: データ取得 ---- */
  useEffect(() => {
    if (editAgentId == null || !apiBaseUrl) return;
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/agents/${editAgentId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{ economic_agent: { bd_type: number; variant: number; floors: number; parcel_id: number; visual_variant?: number; visual_variant2?: number; visual_variant3?: number } }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        const agent = data.economic_agent;
        setSelectedBdType(agent.bd_type);
        setSelectedVariant(agent.variant);
        setFloors(agent.floors);
        setParcelId(agent.parcel_id);
        // 既存建物の外観バリアントを初期値として読み込む。
        // single_agentスキーマがvisual_variantを含まない場合はundefinedとなり、
        // その場合はnullのまま（確定時に送信されないので現在値が維持される）。
        if (agent.visual_variant != null) setVisualVariant(agent.visual_variant);
        if (agent.visual_variant2 != null) setVisualVariant2(agent.visual_variant2);
        if (agent.visual_variant3 != null) setVisualVariant3(agent.visual_variant3);
        // バリアント選択に伴い Unity 側に建物建設モードを通知する
        sendChangeState("STATE_EDIT_BUILDING", { building_variant: agent.variant, agent_id: editAgentId }).catch((err) => {
          console.error("[BuildingConstructionPanel] sendChangeState failed:", err);
        });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch existing agent:", err);
        }
      });

    return () => controller.abort();
  }, [editAgentId, apiBaseUrl]);

  // 市庁舎未設置かつ通常モード（神モード以外）の時に、自動的に「公共施設」の「市政府」を選択状態にする
  useEffect(() => {
    if (isCityHallPlacedGateActive && editAgentId == null) {
      setSelectedBdType(BUILDING_PUBLIC);
      // マスターデータ取得後にのみ市庁舎を自動選択する。未取得のまま選択すると
      // min_floors 等が読めず floors を誤った初期値で固定してしまい、
      // 一度 selectedVariant が確定すると下の再選択ガードで訂正されなくなるため。
      if (selectedVariant !== VARIANT_CITY && masterData) {
        setSelectedVariant(VARIANT_CITY);
        const entry = masterData.agents[VARIANT_CITY];
        // 市庁舎バリアントの最小階数を初期値にする。1階固定にすると min_floors>1 の
        // 市庁舎で「1階のまま確定→バックエンドが階数エラーで拒否」になるため。
        setFloors(entry?.min_floors ?? 1);
        // 直接配置の底面サイズを default_width × default_height で初期化する
        // (default_height 未設定なら正方形)。
        const initialWidth = entry?.default_width ?? DEFAULT_BUILDING_WIDTH;
        const initialDepth = entry?.default_height ?? initialWidth;
        setRectWidth(initialWidth);
        setRectDepth(initialDepth);
        setRectAngle(0);
        setVisualVariant(Math.random());
        setVisualVariant2(Math.random());
        setVisualVariant3(Math.random());
        sendChangeState("STATE_EDIT_BUILDING", { building_variant: VARIANT_CITY, agent_id: null }).catch((err) => {
          console.error("[BuildingConstructionPanel] sendChangeState for city hall failed:", err);
        });
      }
    }
  }, [isCityHallPlacedGateActive, selectedVariant, masterData, editAgentId]);

  /* ---- ヒントバー表示 ---- */

  /**
   * パネルが開いたときにヒントバーに建物建設の操作説明を表示し、
   * パネルが閉じられたときにヒントをクリアする。
   */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      await setHintbarText(t("hint.buildingConstruction"));
    })().catch((err) => {
      if (!cancelled) {
        console.error("[BuildingConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t]);

  /* ---- マスターデータからの派生 ---- */

  /**
   * 建物として建設可能なバリアント一覧 (building === true)
   * TYPE_BUILDING(共有建物)だけでなく、building=trueのTYPE_STANDARD(一般施設)、
   * TYPE_EDUCATION(教育機関)、TYPE_TRADE(輸出入拠点)など、
   * 全ての建設可能エージェントを含む。建物種類(bd_type)でグループ化して表示する。
   */
  const buildingVariants = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.agents).filter(
      ([, entry]) => entry.building
    ) as [string, AgentMasterEntry][];
  }, [masterData]);

  /** ユニークな建物タイプ一覧 */
  const availableBdTypes = useMemo(() => {
    if (isCityHallPlacedGateActive) {
      return [BUILDING_PUBLIC];
    }
    const typeSet = new Set<number>();
    for (const [, entry] of buildingVariants) {
      if (entry.bd_type != null) {
        typeSet.add(entry.bd_type);
      }
    }
    return [...typeSet].sort((a, b) => a - b);
  }, [buildingVariants, isCityHallPlacedGateActive]);

  /** 選択中の建物タイプに属するバリアント */
  const variantsForType = useMemo(() => {
    if (selectedBdType === null) return [];
    const filtered = buildingVariants.filter(([, entry]) => entry.bd_type === selectedBdType);
    if (isCityHallPlacedGateActive && selectedBdType === BUILDING_PUBLIC) {
      return filtered.filter(([idStr]) => parseInt(idStr) === VARIANT_CITY);
    }
    return filtered;
  }, [buildingVariants, selectedBdType, isCityHallPlacedGateActive]);

  /** 選択中のバリアントのマスターデータ */
  const selectedVariantData = useMemo((): AgentMasterEntry | null => {
    if (selectedVariant === null || !masterData) return null;
    return masterData.agents[selectedVariant] ?? null;
  }, [masterData, selectedVariant]);

  /** 現在直接配置モード(土地区画外クリック)かどうか。pickedPosition が非nullなら直接配置。 */
  const isDirectMode = pickedPosition !== null;
  /** 選択中バリアントの底面が可変か。false のとき底面は default_width 四方の正方形で固定。 */
  const isResizable = selectedVariantData?.resizable ?? true;
  /** 直接配置の初期底面サイズ(default_width × default_height)。 */
  const defaultWidth = selectedVariantData?.default_width ?? DEFAULT_BUILDING_WIDTH;
  const defaultHeight = selectedVariantData?.default_height ?? defaultWidth;

  /** 選択中の土地区画で建設可能な建物種別の表示名一覧（allowed_bd_mask から解決） */
  const allowedBuildingNames = useMemo(() => {
    if (!parcelData || !masterData) return [];
    const mask = parcelData.allowed_bd_mask ?? 0;
    if (mask === 0) return [];
    const names: string[] = [];
    for (const [bdTypeIdStr, bdTypeEntry] of Object.entries(masterData.bd_types)) {
      const bdTypeId = parseInt(bdTypeIdStr);
      if ((mask & bdTypeId) !== 0) {
        names.push(bdTypeEntry.display_name);
      }
    }
    return names;
  }, [parcelData, masterData]);

  /* ---- プレビュー取得 ---- */

  const fetchPreview = useCallback(
    async (variant: number, pId: number, f: number, cr: number, signal?: AbortSignal) => {
      const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview`;
      try {
        const result = await fetch(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            variant,
            parcel_id: pId,
            floors: f,
            coverage_ratio: cr,
          }),
          signal,
        });
        if (!result.ok) throw new Error(`HTTP ${result.status}`);
        const data: BuildingPreviewData = await result.json();
        if (!signal?.aborted) {
          setBuildingPreview(data);
          // バックエンドから取得した形状データをUnity側に送信し、3Dプレビューを表示する
          sendEditBuildingUpdateShape(
            data.polygon,
            f,
            data.center_point,
            data.height,
          ).catch((err) => {
            console.error("[BuildingConstruction] sendEditBuildingUpdateShape failed:", err);
          });
        }
      } catch (err) {
        if (!signal?.aborted) {
          console.error("Failed to fetch building preview:", err);
        }
      }
    },
    [apiBaseUrl]
  );

  /* ---- バリアント・土地区画・階数のいずれかが変化したらプレビューを再取得 ---- */

  useEffect(() => {
    if (selectedVariant === null || parcelId === null) return;
    const controller = new AbortController();
    void fetchPreview(selectedVariant, parcelId, floors, coverageRatio, controller.signal);
    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, coverageRatio, fetchPreview]);

  /* ---- 推定建設コストの取得 ---- */
  useEffect(() => {
    if (selectedVariant === null || parcelId === null) {
      setEstimatedCost(null);
      setEstimatedStandardCost(null);
      setEstimatedDurationDays(null);
      setEstimatedMaxProduction(null);
      setEstimatedEmployment(null);
      setEstimatedOperatingCost(null);
      return;
    }
    const controller = new AbortController();
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/construction-cost`;
    fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        parcel_id: parcelId,
        floors,
        construction_ratio: coverageRatio,
      }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        if (!controller.signal.aborted) {
          setEstimatedCost(data.estimated_construction_cost ?? null);
          setEstimatedStandardCost(data.standard_construction_cost ?? null);
          setEstimatedDurationDays(data.estimated_duration_days ?? null);
          // 生産系の3項目はTYPE_STANDARDのときのみ返るため、未定義なら null にリセットする。
          setEstimatedMaxProduction(data.estimated_max_production ?? null);
          setEstimatedEmployment(data.estimated_employment ?? null);
          setEstimatedOperatingCost(data.estimated_operating_cost ?? null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch construction cost:", err);
          setEstimatedCost(null);
          setEstimatedStandardCost(null);
          setEstimatedDurationDays(null);
          setEstimatedMaxProduction(null);
          setEstimatedEmployment(null);
          setEstimatedOperatingCost(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });

    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, coverageRatio, apiBaseUrl]);

  /* ---- インフラ施設の効果範囲の取得と3D可視化 ---- */

  /**
   * インフラ供給施設(変電所・下水処理場・電波塔などサービス財を生産する施設)を選択して
   * 敷地を選んだとき、その施設を建てた場合の効果範囲をバックエンドから取得し、
   * Unity の 3D 上に可視化する。効果範囲は施設位置・地形・階数(電波塔の送信点高さ)で決まるため、
   * これらが変わるたびに再取得する。効果範囲を持たない variant(通常建物)には restricted:false が返り、
   * その場合は 3D 表示をクリアする。
   */
  useEffect(() => {
    if (selectedVariant === null || parcelId === null) {
      setEffectRangeActive(false);
      void sendEditBuildingUpdateEffectRange(null).catch(() => {});
      return;
    }
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-effect-range`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ variant: selectedVariant, parcel_id: parcelId, floors }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{
          restricted: boolean;
          region?: EffectRangeRegion | null;
        }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        if (data.restricted && data.region) {
          setEffectRangeActive(true);
          void sendEditBuildingUpdateEffectRange(data.region).catch((err) => {
            console.error("[BuildingConstruction] sendEditBuildingUpdateEffectRange failed:", err);
          });
        } else {
          setEffectRangeActive(false);
          void sendEditBuildingUpdateEffectRange(null).catch(() => {});
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch effect range:", err);
          setEffectRangeActive(false);
          void sendEditBuildingUpdateEffectRange(null).catch(() => {});
        }
      });

    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, apiBaseUrl]);

  /* ---- アンマウント時に効果範囲表示をクリア ---- */
  useEffect(() => {
    return () => {
      void sendEditBuildingUpdateEffectRange(null).catch(() => {});
    };
  }, []);

  /* ---- 直接配置モード: 底面プレビューと設置可否の取得 ---- */

  /**
   * 直接配置モード(pickedPosition が非null)で、バリアント・地点・サイズ・回転・階数の
   * いずれかが変わったら、バックエンドに矩形底面のプレビューを問い合わせる。
   * 返ってきた底面ポリゴンを Unity の3Dプレビュー(edit_building:update_shape)へ送り、
   * 2Dキャンバス用に buildingPreview へも反映する。設置可否(valid/invalid_reason)を保持する。
   * resizable=false のバリアントはバックエンドが width/depth を default_width に強制するため、
   * 返却値の実効サイズを入力欄へ反映する。
   */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-direct`;
    fetchApi<{
      polygon: number[][];
      height: number;
      center_point: number[];
      area: number;
      valid: boolean;
      invalid_reason: string | null;
      width: number;
      depth: number;
      angle: number;
      resizable: boolean;
    }>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        position: pickedPosition,
        floors,
        width: rectWidth,
        depth: rectDepth,
        angle: rectAngle,
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setBuildingPreview({
          polygon: data.polygon,
          height: data.height,
          center_point: data.center_point,
          area: data.area,
        });
        setDirectValid(data.valid);
        setDirectInvalidReason(data.invalid_reason ?? null);
        // resizable=false では要求サイズが default_width に強制されるため入力欄へ反映する
        // (無限ループを避けるため実値が異なるときだけ更新する)。
        if (Math.abs(data.width - rectWidth) > 1e-6) setRectWidth(data.width);
        if (Math.abs(data.depth - rectDepth) > 1e-6) setRectDepth(data.depth);
        // Unity の3Dプレビューへ底面形状を送る
        sendEditBuildingUpdateShape(
          data.polygon,
          floors,
          data.center_point,
          data.height,
        ).catch((err) => {
          console.error("[BuildingConstruction] sendEditBuildingUpdateShape (direct) failed:", err);
        });
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct preview:", err);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, apiBaseUrl]);

  /* ---- 直接配置モード: 推定建設コストの取得 ---- */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/construction-cost`;
    fetchApi<Record<string, number | null>>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        position: pickedPosition,
        floors,
        width: rectWidth,
        depth: rectDepth,
        angle: rectAngle,
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setEstimatedCost(data.estimated_construction_cost ?? null);
        setEstimatedStandardCost(data.standard_construction_cost ?? null);
        setEstimatedDurationDays(data.estimated_duration_days ?? null);
        setEstimatedMaxProduction(data.estimated_max_production ?? null);
        setEstimatedEmployment(data.estimated_employment ?? null);
        setEstimatedOperatingCost(data.estimated_operating_cost ?? null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct construction cost:", err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, apiBaseUrl]);

  /* ---- 直接配置モード: インフラ施設の効果範囲の取得と3D可視化 ---- */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();

    fetchApi<{ restricted: boolean; region?: EffectRangeRegion | null }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-effect-range`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ variant: selectedVariant, position: pickedPosition, floors }),
        signal: controller.signal,
      },
    )
      .then((data) => {
        if (controller.signal.aborted) return;
        if (data.restricted && data.region) {
          setEffectRangeActive(true);
          void sendEditBuildingUpdateEffectRange(data.region).catch(() => {});
        } else {
          setEffectRangeActive(false);
          void sendEditBuildingUpdateEffectRange(null).catch(() => {});
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct effect range:", err);
          setEffectRangeActive(false);
          void sendEditBuildingUpdateEffectRange(null).catch(() => {});
        }
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, apiBaseUrl]);

  /* ---- 直接配置モード: 矩形パラメータをUnityへ同期(カーソル追従ゴースト用) ---- */

  /**
   * バリアント選択中は、横幅・縦幅・回転角・階数の変更を Unity へ送り、
   * カーソル追従のリアルタイム矩形ゴーストを更新させる。地点確定前(フェーズA)でも
   * デフォルト値を送ることで、ホバー時点からゴーストを表示できる。
   */
  useEffect(() => {
    if (selectedVariant === null) return;
    void sendEditBuildingUpdateDirectParams(rectWidth, rectDepth, rectAngle, floors).catch(
      (err) => {
        console.error("[BuildingConstruction] sendEditBuildingUpdateDirectParams failed:", err);
      },
    );
  }, [selectedVariant, rectWidth, rectDepth, rectAngle, floors]);

  /* ---- バリアント選択解除時に関連状態をリセット ---- */

  useEffect(() => {
    if (selectedVariant === null) {
      setParcelId(null);
      setBuildingPreview(null);
      // 直接配置モードの状態もリセットする。
      setPickedPosition(null);
      setDirectValid(false);
      setDirectInvalidReason(null);
    }
  }, [selectedVariant]);

  /* ---- IPCイベントリスナー: 土地区画選択 (Unity → UI) ---- */

  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{
        parcel_id: number;
        position?: number[];
        is_edit_mode: boolean;
        editing_building_id: number;
        polygon?: number[][];
      }>(IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL, (event) => {
        console.log("[BuildingConstruction] edit_building.select_parcel", event.payload);
        // 区画モードに入る。直接配置モードとは排他なので pickedPosition をクリアする。
        setPickedPosition(null);
        setParcelId(event.payload.parcel_id);
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant]);

  /* ---- IPCイベントリスナー: 直接配置の地点選択 (Unity → UI) ---- */

  /**
   * 建物建設モード中に土地区画外の地点がクリックされたら、直接配置モードに入る。
   * 区画モードとは排他なので parcelId をクリアし、クリック地点を底面矩形の中心とする。
   */
  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ position: number[]; angle?: number }>(IPC_EVENT_EDIT_BUILDING_SELECT_POINT, (event) => {
        console.log("[BuildingConstruction] edit_building.select_point", event.payload);
        setParcelId(null);
        setParcelData(null);
        setPickedPosition(event.payload.position);
        // 中ボタンドラッグでゴーストを回した角度を確定地点へ引き継ぐ。
        if (typeof event.payload.angle === "number") {
          setRectAngle(event.payload.angle);
        }
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant]);

  /* ---- IPCイベントリスナー: 建設不可パーセル通知 (Unity → UI) ---- */

  /**
   * 建物種類と互換性のない土地区画がクリックされた場合、警告通知を表示する。
   * 例: 港湾建物選択中に陸地区画をクリック、通常建物選択中に沿岸区画をクリック等。
   */
  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ parcel_id: number }>(IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL, () => {
        showNotification({
          message: t("building_construction.invalidParcel"),
          durationMs: 3000,
        }).catch((err) => {
          console.error("[BuildingConstruction] showNotification failed:", err);
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant, t]);

  /* ---- IPCイベントリスナー: 直接配置の回転角更新 (Unity → UI) ---- */

  /**
   * 中ボタンドラッグによる回転が終了(リリース)したら、Unity側の確定角度で
   * 回転ステッパー(rectAngle)を更新する。ドラッグ中は送られず、リリース時に1回だけ届く。
   */
  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ angle: number }>(IPC_EVENT_EDIT_BUILDING_UPDATE_DIRECT_ANGLE, (event) => {
        console.log("[BuildingConstruction] edit_building.update_direct_angle", event.payload);
        if (typeof event.payload.angle === "number") {
          setRectAngle(event.payload.angle);
        }
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant]);

  /* ---- 土地区画データ取得 ---- */

  /**
   * 土地区画が選択されたら、バックエンドから区画の詳細情報（面積、規制値など）を取得する。
   * 取得したデータは敷地情報パネルの表示に使用する。
   */
  useEffect(() => {
    if (parcelId === null) {
      setParcelData(null);
      return;
    }
    const controller = new AbortController();
    setIsParcelLoading(true);

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ParcelData>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setParcelData(data);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch parcel:", err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsParcelLoading(false);
      });

    return () => controller.abort();
  }, [parcelId, apiBaseUrl]);

  /* ---- 建物面積計算 ---- */

  const buildingArea = useMemo(() => {
    if (!buildingPreview) return null;
    if (buildingPreview.area != null) return buildingPreview.area;
    if (!buildingPreview.polygon) return null;
    return polygonArea(buildingPreview.polygon);
  }, [buildingPreview]);

  /* ---- ハンドラ ---- */

  function handleBdTypeChange(bdType: number | null) {
    setSelectedBdType(bdType);
    setSelectedVariant(null);
    setBuildingPreview(null);
  }

  function handleVariantClick(variantId: number) {
    if (selectedVariant === variantId) {
      setSelectedVariant(null);
      // 外観バリアントをリセット
      setVisualVariant(null);
      setVisualVariant2(null);
      setVisualVariant3(null);
      sendChangeState("STATE_NORMAL").catch((err) => {
        console.error("[BuildingConstruction] sendChangeState failed:", err);
      });
    } else {
      // 建物バリアントを選択した際、そのバリアントのマスターデータに定義された最小階数を初期値として設定する。
      // これにより、1階では建てられない建物（例: 高層ビル）を選択した際にも、適切な階数でプレビューが表示される。
      const entry = masterData?.agents[variantId];
      const initialFloors = entry?.min_floors ?? 1;
      setSelectedVariant(variantId);
      setFloors(initialFloors);
      // 直接配置モードの底面サイズを default_width × default_height で初期化する
      // (default_height 未設定なら正方形)。
      const initialWidth = entry?.default_width ?? DEFAULT_BUILDING_WIDTH;
      const initialDepth = entry?.default_height ?? initialWidth;
      setRectWidth(initialWidth);
      setRectDepth(initialDepth);
      setRectAngle(0);
      // 新規建設時の外観バリアント初期値。編集モードの場合は別途useEffectで既存値を読み込み上書きされる。
      setVisualVariant(Math.random());
      setVisualVariant2(Math.random());
      setVisualVariant3(Math.random());
      sendChangeState("STATE_EDIT_BUILDING", { building_variant: variantId, agent_id: editAgentId ?? null }).catch((err) => {
        console.error("[BuildingConstruction] sendChangeState failed:", err);
      });
    }
  }

  function handleFloorChange(value: number) {
    setFloors(value);
    sendEditBuildingUpdateFloors(value).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateFloors failed:", err);
    });
  }



  /**
   * 確定失敗時のエラー通知。ApiError のエラーコードを i18n キーへマップし、
   * 対応する翻訳がなければバックエンドのメッセージをそのまま表示する。
   */
  function notifyConfirmError(err: unknown) {
    console.error("[BuildingConstruction] confirm failed:", err);
    let message = t("building_construction.confirmFailed");
    let code: string | undefined;
    if (err instanceof ApiError) {
      message = err.message;
      code = err.code;
    } else if (err instanceof Error) {
      message = err.message;
    }
    const i18nKey = code ? `errors.${code}` : null;
    const displayMessage =
      i18nKey && i18nKey !== "errors.UNKNOWN_ERROR"
        ? t(i18nKey, { defaultValue: message })
        : message;
    void showNotification({ message: displayMessage, durationMs: 5000 });
  }

  /**
   * 建設確定に成功した後の共通処理。
   * 効果範囲オーバーレイを明示的に消し(ウィンドウを閉じるとアンマウント時の
   * クリア送信が届かない可能性があるため)、Unity にセッションリロードを送って
   * 新しい建物を描画させる。
   * 先に STATE_NORMAL に戻してから表示モードを切り替える。
   * STATE_NORMAL でないと view:change_view の変更が _normalStateMemory に記憶されず、
   * 後から Rust 側が STATE_NORMAL を送った際に default に戻ってしまうため。
   * 最後に、ユーザーが建設結果を確認できるよう計画中表示をONにしてパネルを閉じる。
   */
  async function finishAfterConfirm() {
    await sendEditBuildingUpdateEffectRange(null).catch(() => {});
    await sendSessionReload();
    await sendChangeState("STATE_NORMAL");
    await sendChangeDisplay("building", "default", null, ["plan"]);
    onClose();
  }

  /**
   * 外観バリアントのリクエストフィールドを組み立てる。
   * null(編集モードで現在値を取得できなかった場合)のフィールドは送信しない。
   * これにより、ユーザーがスライダーを動かさなければ現在の外観が維持される。
   */
  function buildVisualVariantFields(): Record<string, number> {
    return {
      ...(visualVariant != null ? { visual_variant: visualVariant } : {}),
      ...(visualVariant2 != null ? { visual_variant2: visualVariant2 } : {}),
      ...(visualVariant3 != null ? { visual_variant3: visualVariant3 } : {}),
    };
  }

  /**
   * 区画モードの建設確定ハンドラ。UI が直接バックエンドに送信する(道路建設と同じ責務分担)。
   * 新規建設は POST /buildings、既存建物の編集は PATCH /agents/{id} を呼ぶ。
   */
  async function handleParcelConfirm() {
    if (selectedVariant === null || parcelId === null || selectedVariantData === null) return;
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    try {
      if (editAgentId != null) {
        await fetchApi(`${baseUrl}/api/v1/agents/${editAgentId}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            variant: selectedVariant,
            floors,
            ...buildVisualVariantFields(),
          }),
        });
      } else {
        const created = await fetchApi<{ agent_id: number }>(`${baseUrl}/api/v1/buildings/`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            parcel_id: parcelId,
            type: selectedVariantData.type,
            variant: selectedVariant,
            construction_ratio: coverageRatio,
            floors,
            ...buildVisualVariantFields(),
          }),
        });
        if (created && created.agent_id) {
          void invoke("open_registered_window", {
            windowType: "agent_detail",
            entityId: String(created.agent_id),
          });
        }
      }
      await finishAfterConfirm();
    } catch (err) {
      notifyConfirmError(err);
    }
  }

  /**
   * 直接配置モードの建設確定ハンドラ。UI が直接 POST /buildings/direct に送る。
   * バックエンドが矩形の土地区画を自動生成し、その全面に建物を作成する。
   */
  async function handleDirectConfirm() {
    if (selectedVariant === null || pickedPosition === null || selectedVariantData === null) return;
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    try {
      const created = await fetchApi<{ agent_id: number }>(`${baseUrl}/api/v1/buildings/direct`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          variant: selectedVariant,
          type: selectedVariantData.type,
          position: pickedPosition,
          floors,
          width: rectWidth,
          depth: rectDepth,
          // 回転角はマイナス・360超も入力可能なため、確定時に mod 360 で [0,360) に正規化する。
          angle: normalizeAngle(rectAngle),
          ...buildVisualVariantFields(),
        }),
      });
      if (created && created.agent_id) {
        void invoke("open_registered_window", {
          windowType: "agent_detail",
          entityId: String(created.agent_id),
        });
      }
      await finishAfterConfirm();
    } catch (err) {
      notifyConfirmError(err);
    }
  }

  /**
   * パネル全体のクリックイベントを捕捉する共通のハンドラ。
   * ボタン要素やバリアントタイルなど、ユーザーが明示的に対話する要素をクリックした際に、
   * 直後にUnity側での3Dビュー操作をシームレスに行えるよう、Unityにフォーカスを強制する。
   * 階数入力欄（NumberStepperのインプット）など、入力フォーカスを維持すべき要素をクリックした場合は
   * Unityへのフォーカス移動を行わないよう、対象を制限している。
   */
  const handlePanelClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const button = target.closest("button, [role='button']");
    if (button) {
      sendFocusGraphic().catch(() => {});
    }
  }, []);

  /* ---- マスターデータ未取得時 ---- */

  if (!masterData) {
    return <p className="bc-muted">{t("common.loading")}</p>;
  }

  /* ---- 計算値 ---- */

  const heightPerFloor = selectedVariantData?.height_per_floor ?? 3.5;
  const totalHeight = floors * heightPerFloor;
  const totalFloorArea = buildingArea != null ? buildingArea * floors : null;

  /* ---- レンダリング ---- */

  return (
    <div className="bc-layout" onClick={handlePanelClick}>
      {/* 建物タイプ選択 */}
      <div className="bc-section">
        <select
          className="bc-select"
          value={selectedBdType ?? ""}
          onChange={(e) =>
            handleBdTypeChange(e.target.value ? parseInt(e.target.value) : null)
          }
          disabled={editAgentId != null || isCityHallPlacedGateActive}
        >
          <option value="">{t("building_construction.selectBdType")}</option>
          {availableBdTypes.map((bdType) => (
            <option key={bdType} value={bdType}>
              {masterData.bd_types[bdType]?.display_name ?? `Type ${bdType}`}
            </option>
          ))}
        </select>
      {/* バリアント選択 */}
        {variantsForType.length === 0 ? (
          <div className="bc-empty-variants">
            {selectedBdType === null
              ? t("building_construction.selectVariant")
              : t("building_construction.noVariants")}
          </div>
        ) : (
          <select
            className="bc-select"
            value={selectedVariant ?? ""}
            onChange={(e) => {
              const val = e.target.value;
              if (val) {
                const variantId = parseInt(val);
                if (selectedVariant !== variantId) {
                  handleVariantClick(variantId);
                }
              } else {
                if (selectedVariant !== null) {
                  handleVariantClick(selectedVariant);
                }
              }
            }}
            style={{ marginTop: 10 }}
            disabled={isCityHallPlacedGateActive}
          >
            <option value="">{t("building_construction.selectVariant")}</option>
            {variantsForType.map(([idStr, entry]) => (
              <option key={idStr} value={idStr}>
                {entry.display_name}
              </option>
            ))}
          </select>
        )}
      </div>

      {/* 説明文 */}
      {selectedVariantData && (
        <div className="bc-section">
          <div className="bc-description-frame">
            <img
              src={resolveAgentIcon(
                selectedVariant!,
                selectedVariantData.type,
                selectedVariantData.building ?? false,
                selectedVariantData.bd_type ?? 0
              )}
              alt={selectedVariantData.display_name}
              className="bc-description-icon"
              draggable={false}
            />
            <div className="bc-description-body">
              <div className="bc-description-name">{selectedVariantData.display_name}</div>
              <div className="bc-description-text">
                {selectedVariantData.description || "—"}
              </div>
            </div>
          </div>

          {/* インフラ施設の効果範囲の凡例（効果範囲を3D表示中のみ） */}
          {effectRangeActive && (
            <div className="bc-effect-range-legend">
              <div className="bc-effect-range-legend-title">
                {t("building_construction.effectRangeTitle")}
              </div>
              <div className="bc-effect-range-legend-row">
                <span className="bc-effect-range-swatch bc-effect-range-swatch-area" />
                {t("building_construction.effectRangeArea")}
              </div>
            </div>
          )}



          {/* 直接配置モードの情報パネル(設置可否の理由表示) */}
          {isDirectMode && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.directPlacement")}
                </div>
                <div className="bc-description-text">
                  {directValid ? (
                    t("building_construction.directPlaceable")
                  ) : (
                    <span className="bc-direct-invalid">
                      {directInvalidReason
                        ? t(`building_construction.directInvalid.${directInvalidReason}`, {
                            defaultValue: t("building_construction.directInvalid.generic"),
                          })
                        : t("building_construction.directInvalid.generic")}
                    </span>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* 敷地情報パネル(区画モードのみ) */}
          {!isDirectMode && (
          <div className="bc-description-frame" style={{ marginTop: 10 }}>
            {parcelId === null ? (
              <div className="bc-description-body bc-parcel-prompt">
                <div className="bc-description-text">
                  {t("building_construction.selectParcelHint")}
                </div>
              </div>
            ) : (
              <>
                <img
                  src={parcelData ? resolveParcelIcon(parcelData.parcel_type) : resolveParcelIcon(0)}
                  alt=""
                  className="bc-description-icon"
                  draggable={false}
                />
                <div className="bc-description-body">
                  <div className="bc-description-name">
                    {t("building_construction.parcelSelected", { id: parcelId })}
                  </div>
                  <div className="bc-description-text">
                    {isParcelLoading || !parcelData ? (
                      t("common.loading")
                    ) : (
                      <>
                        {t("building_construction.landArea")}: {parcelData.area.toFixed(1)}{t("building_construction.unitSqm")}
                        <br />
                        {parcelData.limit_height != null && (
                          <>{t("building_construction.heightLimit")}: {parcelData.limit_height.toFixed(1)}{t("building_construction.unitMeter")}<br /></>
                        )}
                        {parcelData.limit_coverage_ratio != null && (
                          <>{t("building_construction.coverageRatioLimit")}: {Math.round(parcelData.limit_coverage_ratio * 100)}%<br /></>
                        )}
                        {parcelData.limit_volume_ratio != null && (
                          <>{t("building_construction.volumeRatioLimit")}: {Math.round(parcelData.limit_volume_ratio * 100)}%<br /></>
                        )}
                        {allowedBuildingNames.length > 0 && (
                          <>{t("building_construction.allowedBuildings")}: {allowedBuildingNames.join(", ")}</>
                        )}
                      </>
                    )}
                  </div>
                </div>
              </>
            )}
          </div>
          )}
        </div>
      )}

      {/* 階数・建ぺい率設定 (左右2カラム) — 区画選択後に表示 */}
      {selectedVariantData && parcelId !== null && (
        <div className="bc-section">
          <div className="bc-split-row">
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.floorCount")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={floors}
                  onChange={handleFloorChange}
                  min={selectedVariantData.min_floors ?? 1}
                  max={selectedVariantData.max_floors ?? 50}
                  step={1}
                  integerOnly={true}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitFloors")}</span>
              </div>
            </div>
            {/* 建ぺい率は底面可変(resizable)の建物のみ。固定底面の建物では非表示。 */}
            {isResizable && (
              <div className="bc-split-col">
                <div className="bc-section-title">{t("building_construction.coverageRatio")}</div>
                <div className="bc-stepper-row">
                  <NumberStepper
                    value={Math.round(coverageRatio * 100)}
                    onChange={(v) => setCoverageRatio(v / 100)}
                    min={10}
                    max={100}
                    step={5}
                    integerOnly={true}
                  />
                  <span className="bc-stepper-unit">%</span>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/* 直接配置モードの設定: 階数 + 横幅/縦幅/回転角 — 地点選択後に表示。
          底面は矩形そのものなので建ぺい率は無い。resizable=false のバリアントは
          横幅・縦幅を固定(入力不可)とし、回転のみ編集できる。 */}
      {selectedVariantData && isDirectMode && (
        <div className="bc-section">
          <div className="bc-split-row">
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.floorCount")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={floors}
                  onChange={handleFloorChange}
                  min={selectedVariantData.min_floors ?? 1}
                  max={selectedVariantData.max_floors ?? 50}
                  step={1}
                  integerOnly={true}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitFloors")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.rotation")}</div>
              <div className="bc-stepper-row">
                {/* 回転角はマイナス・360超も許容(クランプしない)。確定時に mod 360 で正規化する。 */}
                <NumberStepper
                  value={rectAngle}
                  onChange={setRectAngle}
                  step={5}
                  integerOnly={false}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitDegree")}</span>
              </div>
            </div>
          </div>
          <div className="bc-split-row" style={{ marginTop: 10 }}>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.width")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={rectWidth}
                  onChange={setRectWidth}
                  min={1}
                  max={500}
                  step={1}
                  integerOnly={false}
                  disabled={!isResizable}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitMeter")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.depth")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={rectDepth}
                  onChange={setRectDepth}
                  min={1}
                  max={500}
                  step={1}
                  integerOnly={false}
                  disabled={!isResizable}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitMeter")}</span>
              </div>
            </div>
          </div>
          {!isResizable && (
            <div className="bc-muted" style={{ marginTop: 6 }}>
              {t("building_construction.fixedFootprintNote", { width: defaultWidth, height: defaultHeight })}
            </div>
          )}
        </div>
      )}

      {/* 建設情報カード(全 type 共通。詳細パネルと同じ共通カードを使う) */}
      {buildingPreview && (
        <div className="bc-section">
          <ConstructionInfoCard
            title={t("construction.info")}
            buildingArea={buildingArea}
            totalHeight={totalHeight}
            totalFloorArea={totalFloorArea}
            estimatedCost={estimatedCost}
            standardCost={estimatedStandardCost}
            costRatioHelp={t("building_construction.costRatioHelp")}
            estimatedDurationDays={estimatedDurationDays}
            isCostLoading={isCostLoading}
          />
        </div>
      )}

      {/* 施設情報カード(type ごとに内容が異なる。中身が無い type ではカード自体を出さない) */}
      {buildingPreview && (
        <BuildingFacilityInfoCard
          variantData={selectedVariantData}
          estimatedMaxProduction={estimatedMaxProduction}
          estimatedEmployment={estimatedEmployment}
          estimatedOperatingCost={estimatedOperatingCost}
          isCostLoading={isCostLoading}
        />
      )}



      {/* アクションボタン (パネル下部に固定) */}
      {selectedVariant && (
        <div className="bc-section bc-action-bar">
          <div className="bc-actions">
            <button
              className="bc-btn"
              type="button"
              onClick={() => {
                // 仮決定した設置位置(区画モードの選択区画/直接配置モードのクリック地点)だけを取り消す。
                // バリアント選択・外観バリアントは維持し、続けて別の位置を選び直せるようにする。
                setParcelId(null);
                setParcelData(null);
                setPickedPosition(null);
                setBuildingPreview(null);
                setDirectValid(false);
                setDirectInvalidReason(null);
                // Unity を建物建設モードのまま位置未確定の状態へ戻し、表示中のゴースト形状を消す。
                sendChangeState("STATE_EDIT_BUILDING", {
                  building_variant: selectedVariant,
                  agent_id: editAgentId ?? null,
                }).catch((err) => {
                  console.error("[BuildingConstruction] sendChangeState (cancel) failed:", err);
                });
              }}
            >
              {t("building_construction.cancel")}
            </button>
            <button
              className="bc-btn bc-btn-confirm"
              // 区画モードは区画選択済み、直接配置モードは設置可能(directValid)なら確定できる。
              disabled={isDirectMode ? !directValid : parcelId === null}
              type="button"
              onClick={async () => {
                if (isDirectMode) {
                  await handleDirectConfirm();
                } else {
                  await handleParcelConfirm();
                }
              }}
            >
              {t("building_construction.confirm")}
            </button>
          </div>
        </div>
      )}

    </div>
  );
}

/* ---- ユーティリティ ---- */

/** Shoelace formula で多角形の面積を計算 */
function polygonArea(polygon: number[][]): number {
  let area = 0;
  const n = polygon.length;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    area += polygon[i][0] * polygon[j][1];
    area -= polygon[j][0] * polygon[i][1];
  }
  return Math.abs(area) / 2;
}
