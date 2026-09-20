/**
 * 経済主体作成パネル (window_type="agent_creation")。テンプレートモード専用。
 *
 * 建物ではない経済主体(民間施設=TYPE_STANDARD / 輸出入拠点=TYPE_TRADE)を、既存の共用建物へ
 * 入居済み・稼働中の状態で1件作る。通常プレイではスポーン→転居の2フェーズを経て建物に入る
 * ものを、シナリオ制作では待たずに任意の施設を置きたいために用意した。
 * 作成そのものはバックエンドの POST /api/v1/templates/tenant-agent が行う
 * (占有量の加算・地区/区画の継承・家賃と賃金のシード・ACTIVE化)。
 *
 * 操作の流れ:
 *   種別 → バリアント(プルダウン) → 「建物を選択」→ 3Dビューで建物をクリック → 占有階数 → 作成。
 *
 * 建物の選択は建設パネルの「既存建物をコピー」と同じUnityの選択モード(MODE_PICK_BUILDING)を
 * 借りる(`sendPickCopySource` → `building_construction:copy_source`)。Unity側は「建物を1つ選ぶ」
 * だけの汎用動作で、こちらは通知の source_agent_id だけを使う。通知を建設パネルと取り合わない
 * よう、両ウィンドウは mutex グループで同時に開けない。選択待ちのままパネルを閉じたときは
 * Rust側(`window.rs` の EDIT_MODE_WINDOW_TYPES)が STATE_NORMAL を送って選択モードを解く。
 *
 * 占有量の実体は面積(m²)で階の概念は無いが、制作者には「階数」で指定させ、
 * 占有面積 = 階数 × 建物の1フロア面積(base_area) として送る。上限は
 * その種別(space_type)の空き面積 ÷ 1フロア面積。非共有建物は1テナントが全床を占有する
 * 契約なので階数は固定で、既に入居者がいれば選べない。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";

import {
  IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE,
  sendChangeState,
  sendPickCopySource,
} from "../../utils/ipc";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { STATUS_ACTIVE, STATUS_PAUSED, TYPE_BUILDING, TYPE_STANDARD, TYPE_TRADE } from "../../constants";
import type { AgentData } from "../../types";
import "./AgentCreationPanel.css";

/** このウィンドウの種別。IPCの送信元ラベルに使う */
const WINDOW_TYPE = "agent_creation";

/** 作成できる経済主体の種別(バックエンドの _TENANT_AGENT_TYPES と同じ) */
const AGENT_TYPE_OPTIONS = [
  { type: TYPE_STANDARD, labelKey: "templateTools.agentCreation.typeStandard" },
  { type: TYPE_TRADE, labelKey: "templateTools.agentCreation.typeTrade" },
] as const;

/** 空き階数の切り捨てで float32 由来の丸め誤差に負けないための許容量 */
const FLOOR_EPSILON = 1e-6;

/** POST /api/v1/templates/tenant-agent の戻り値 */
type TenantAgentResult = {
  agent_id: number;
  name: string;
  variant: number;
  building_agent_id: number;
  space_type: number;
  realestate_amount: number;
  floors: number;
  vacancy_after: number;
};

/** Unityの建物選択モードからの通知(source_agent_id 以外は使わない) */
type CopySourcePayload = { source_agent_id: number };

/** 面積表示は小数を切り捨てて整数で出す(m² の端数は制作者の判断に影響しない) */
function formatArea(value: number): string {
  return Math.floor(value).toLocaleString();
}

export function AgentCreationPanel({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [agentType, setAgentType] = useState<number>(TYPE_STANDARD);
  const [variant, setVariant] = useState<number | null>(null);
  const [building, setBuilding] = useState<AgentData | null>(null);
  const [buildingLoading, setBuildingLoading] = useState(false);
  const [picking, setPicking] = useState(false);
  const [floors, setFloors] = useState(1);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  // 建設パネルと同じ通知名を使うため、自分が選択を要求している間だけ受け取る。
  const pickingRef = useRef(false);
  pickingRef.current = picking;

  /** 選択中の種別に属する、建物ではないバリアント */
  const variantOptions = useMemo(() => {
    if (!masterData) return [] as { id: number; name: string }[];
    return Object.entries(masterData.agents)
      .map(([id, entry]) => ({ id: Number(id), entry }))
      .filter(({ entry }) => entry.type === agentType && !entry.building)
      .sort((a, b) => a.id - b.id)
      .map(({ id, entry }) => ({ id, name: entry.display_name }));
  }, [masterData, agentType]);

  // 種別を切り替えたら、その種別の先頭バリアントへ寄せる(空なら未選択)。
  useEffect(() => {
    if (variant !== null && variantOptions.some((v) => v.id === variant)) return;
    setVariant(variantOptions[0]?.id ?? null);
  }, [variantOptions, variant]);

  /* ---- 建物の読み込み ---- */
  const loadBuilding = useCallback(
    async (agentId: number) => {
      setBuildingLoading(true);
      setMessage(null);
      try {
        const result = await fetchApi<{ economic_agent: AgentData }>(
          buildApiUrl(apiBaseUrl, "/api/v1/agents/{id}", String(agentId)),
        );
        setBuilding(result.economic_agent);
      } catch (err) {
        setBuilding(null);
        setMessage({ text: describeError(err, t("templateTools.agentCreation.loadingBuilding")), error: true });
      } finally {
        setBuildingLoading(false);
      }
    },
    [apiBaseUrl, t],
  );

  /* ---- Unityの建物選択モード ---- */
  const stopPicking = useCallback(async () => {
    setPicking(false);
    // 選択モードは入力プリセットの上書きなので、STATE_NORMAL へ戻して解除する。
    await sendChangeState("STATE_NORMAL", undefined, WINDOW_TYPE);
  }, []);

  const startPicking = useCallback(async () => {
    setMessage(null);
    setPicking(true);
    try {
      await sendPickCopySource(WINDOW_TYPE);
    } catch (err) {
      console.error("[AgentCreation] sendPickCopySource failed:", err);
      setPicking(false);
    }
  }, []);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<CopySourcePayload>(IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE, (event) => {
      if (!pickingRef.current) return;
      const agentId = event.payload?.source_agent_id;
      if (agentId == null) return;
      void stopPicking().catch(console.error);
      void loadBuilding(agentId);
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, [loadBuilding, stopPicking]);

  // 選択待ちのままアンマウントされたら選択モードを解く(×で閉じた場合はRust側が担う)。
  useEffect(() => {
    return () => {
      if (pickingRef.current) {
        void sendChangeState("STATE_NORMAL", undefined, WINDOW_TYPE).catch(console.error);
      }
    };
  }, []);

  /* ---- 空き床の評価 ---- */
  const variantEntry = variant !== null ? masterData?.agents[variant] : undefined;
  const buildingEntry = building ? masterData?.agents[building.variant] : undefined;
  const availability = useMemo(() => {
    if (!building || !variantEntry) return null;
    if (!building.is_bd || building.type !== TYPE_BUILDING) {
      return { ok: false as const, reasonKey: "templateTools.agentCreation.notSharedBuilding" };
    }
    if (building.status !== STATUS_ACTIVE && building.status !== STATUS_PAUSED) {
      return { ok: false as const, reasonKey: "templateTools.agentCreation.statusUnavailable" };
    }
    const baseArea = building.base_area ?? 0;
    const spaceKey = String(variantEntry.space_type);
    const capacity = building.capacity?.[spaceKey] ?? 0;
    const occupied = building.occupied?.[spaceKey] ?? 0;
    if (capacity <= 0 || baseArea <= 0) {
      return { ok: false as const, reasonKey: "templateTools.agentCreation.noSpaceType" };
    }
    // 建物バリアントは shared_building を必ず持つ。未定義はバックエンドと同じく非共有とみなす。
    const shared = buildingEntry?.shared_building === true;
    if (!shared) {
      if (occupied > 0) {
        return { ok: false as const, reasonKey: "templateTools.agentCreation.alreadyOccupied" };
      }
      const fullFloors = Math.max(1, Math.round(capacity / baseArea));
      return { ok: true as const, shared: false, baseArea, vacancy: capacity, maxFloors: fullFloors, minFloors: fullFloors };
    }
    const vacancy = Math.max(capacity - occupied, 0);
    const maxFloors = Math.floor(vacancy / baseArea + FLOOR_EPSILON);
    if (maxFloors < 1) {
      return { ok: false as const, reasonKey: "templateTools.agentCreation.noSpaceType" };
    }
    return { ok: true as const, shared: true, baseArea, vacancy, maxFloors, minFloors: 1 };
  }, [building, variantEntry, buildingEntry]);

  // 建物やバリアントが変わって上限が動いたら、階数を範囲内へ丸める。
  useEffect(() => {
    if (!availability?.ok) return;
    setFloors((prev) => Math.min(Math.max(prev, availability.minFloors), availability.maxFloors));
  }, [availability]);

  const occupiedArea = availability?.ok ? floors * availability.baseArea : 0;
  const spaceMin = variantEntry?.space_min ?? 0;
  const spaceMax = variantEntry?.space_max ?? 0;
  const belowMin = availability?.ok === true && spaceMin > 0 && occupiedArea < spaceMin - FLOOR_EPSILON;
  const aboveMax = availability?.ok === true && spaceMax > 0 && occupiedArea > spaceMax + FLOOR_EPSILON;
  const canCreate = availability?.ok === true && variant !== null && !busy && !belowMin && !aboveMax;

  /* ---- 作成 ---- */
  async function createAgent() {
    if (!canCreate || !building || variant === null) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await fetchApi<TenantAgentResult>(buildApiUrl(apiBaseUrl, "/api/v1/templates/tenant-agent"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ variant, building_agent_id: building.agent_id, floors }),
      });
      setMessage({
        text: t("templateTools.agentCreation.created", {
          name: result.name,
          area: formatArea(result.realestate_amount),
          floors: result.floors,
        }),
        error: false,
      });
      // 続けて同じ建物へ入居させられるよう、占有量を取り直して空きを更新する。
      await loadBuilding(building.agent_id);
    } catch (err) {
      setMessage({ text: describeError(err, t("templateTools.agentCreation.createFailed")), error: true });
    } finally {
      setBusy(false);
    }
  }

  /** ApiError は errors.<code> の翻訳、無ければサーバのメッセージをそのまま出す */
  function describeError(err: unknown, fallback: string): string {
    if (err instanceof ApiError) {
      const i18nKey = `errors.${err.code}`;
      return i18nKey !== "errors.UNKNOWN_ERROR" ? t(i18nKey, { defaultValue: err.message }) : err.message;
    }
    return fallback;
  }

  const controlsDisabled = busy || picking;

  return (
    <div className="agent-creation">
      <p className="agent-creation__hint">{t("templateTools.agentCreation.hint")}</p>

      <section className="agent-creation__section">
        <div className="agent-creation__label">{t("templateTools.agentCreation.agentType")}</div>
        <div className="agent-creation__seg" role="radiogroup">
          {AGENT_TYPE_OPTIONS.map((opt) => (
            <button
              key={opt.type}
              type="button"
              role="radio"
              aria-checked={agentType === opt.type}
              className={`agent-creation__seg-btn${agentType === opt.type ? " agent-creation__seg-btn--active" : ""}`}
              disabled={controlsDisabled}
              onClick={() => setAgentType(opt.type)}
            >
              {t(opt.labelKey)}
            </button>
          ))}
        </div>
      </section>

      <section className="agent-creation__section">
        <label className="agent-creation__label" htmlFor="agent-creation-variant">
          {t("templateTools.agentCreation.variant")}
        </label>
        <select
          id="agent-creation-variant"
          className="agent-creation__select"
          value={variant ?? ""}
          disabled={controlsDisabled || variantOptions.length === 0}
          onChange={(e) => setVariant(Number(e.target.value))}
        >
          {variantOptions.length === 0 && <option value="">{t("templateTools.agentCreation.noVariants")}</option>}
          {variantOptions.map((v) => (
            <option key={v.id} value={v.id}>
              {v.name}
            </option>
          ))}
        </select>
        {variantEntry?.description && <p className="agent-creation__description">{variantEntry.description}</p>}
      </section>

      <section className="agent-creation__section">
        <div className="agent-creation__label">{t("templateTools.agentCreation.building")}</div>
        <div className="agent-creation__row">
          {picking ? (
            <button type="button" className="agent-creation__btn" onClick={() => void stopPicking().catch(console.error)}>
              {t("templateTools.agentCreation.cancelPick")}
            </button>
          ) : (
            <button type="button" className="agent-creation__btn" disabled={busy} onClick={() => void startPicking()}>
              {t("templateTools.agentCreation.pickBuilding")}
            </button>
          )}
          <span className="agent-creation__building">
            {picking
              ? t("templateTools.agentCreation.picking")
              : buildingLoading
                ? t("templateTools.agentCreation.loadingBuilding")
                : building
                  ? t("templateTools.agentCreation.buildingInfo", {
                      name: building.name,
                      floors: building.floors ?? 1,
                      area: formatArea(building.base_area ?? 0),
                    })
                  : t("templateTools.agentCreation.noBuilding")}
          </span>
        </div>
        {availability && !availability.ok && <p className="agent-creation__warn">{t(availability.reasonKey)}</p>}
      </section>

      {availability?.ok && (
        <section className="agent-creation__section">
          <div className="agent-creation__label">{t("templateTools.agentCreation.floors")}</div>
          {availability.shared ? (
            <>
              <p className="agent-creation__note">
                {t("templateTools.agentCreation.vacancy", {
                  floors: availability.maxFloors,
                  area: formatArea(availability.vacancy),
                })}
              </p>
              <div className="agent-creation__row">
                <input
                  type="range"
                  min={1}
                  max={availability.maxFloors}
                  step={1}
                  value={floors}
                  disabled={controlsDisabled}
                  onChange={(e) => setFloors(Number(e.target.value))}
                />
                <input
                  type="number"
                  className="agent-creation__number"
                  min={1}
                  max={availability.maxFloors}
                  step={1}
                  value={floors}
                  disabled={controlsDisabled}
                  onChange={(e) => {
                    const v = Number.parseInt(e.target.value, 10);
                    if (!Number.isNaN(v)) setFloors(Math.min(Math.max(v, 1), availability.maxFloors));
                  }}
                />
              </div>
            </>
          ) : (
            <p className="agent-creation__note">
              {t("templateTools.agentCreation.fullOccupancy", {
                floors: availability.maxFloors,
                area: formatArea(availability.vacancy),
              })}
            </p>
          )}
          <p className="agent-creation__note">
            {t("templateTools.agentCreation.occupiedArea", { area: formatArea(occupiedArea) })}
          </p>
          {belowMin && (
            <p className="agent-creation__warn">
              {t("templateTools.agentCreation.belowSpaceMin", { min: formatArea(spaceMin) })}
            </p>
          )}
          {aboveMax && (
            <p className="agent-creation__warn">
              {t("templateTools.agentCreation.aboveSpaceMax", { max: formatArea(spaceMax) })}
            </p>
          )}
        </section>
      )}

      <div className="agent-creation__actions">
        <button
          type="button"
          className="agent-creation__btn agent-creation__btn--primary"
          disabled={!canCreate}
          onClick={() => void createAgent()}
        >
          {busy ? t("templateTools.agentCreation.creating") : t("templateTools.agentCreation.create")}
        </button>
      </div>

      {message && (
        <div className={`agent-creation__message${message.error ? " agent-creation__message--error" : ""}`}>{message.text}</div>
      )}
    </div>
  );
}
