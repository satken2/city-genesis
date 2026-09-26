/**
 * 所属道路の選択
 *
 * 区間(街路・高速道路のパス)をどの道路(名前付き道路)に属させるかを選ぶ部品群。
 * - `RoadMembershipRow`: パス詳細パネルの「所属道路」行。選ぶとその場で保存する。
 *   新しい道路はこの区間1本で作成し、続けて道路詳細を「直進方向へ延長しますか」の提案付きで開く。
 * - `RoadSelect`: 選択肢(なし / 既存の道路 / 新しい道路)のプルダウン。道路建設パネルは
 *   `deferred` で使い、新しい道路は名前だけを預かって確定送信の後に作成する。
 * - `useRoadList`: 道路一覧の取得。`ROADS_CHANGED_EVENT` を受けて取り直す。
 *
 * 見た目はタグ行(common/TagRow・TagSelect)と揃え、同じCSSのクラスを借りる。
 * 所属の正は区間側の road_id で、未所属(名もなき道)は VALUE_NONE(-1)。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

import { ROAD_NAME_MAX_LENGTH } from "../../constants";
import { resolveIntlLocale } from "../../utils/locale";
import {
  ROADS_CHANGED_EVENT,
  createRoad,
  fetchRoadList,
  hasRoad,
  notifyRoadsChanged,
  roadDisplayName,
  setPathRoad,
} from "./roadApi";
import type { RoadSummary } from "./roadTypes";
import "../common/TagSelect.css";
import "./RoadMembershipRow.css";

/** パスの詳細・一覧の応答が持つ所属道路。手書きの PathData を変えずに足すための型 */
export type PathRoadFields = { road_id?: number | null };

/** 所属の選択。new は「新しい道路」(name は空文字も可。表示側が「道路 #id」を補う) */
export type RoadChoice = { kind: "none" } | { kind: "road"; roadId: number } | { kind: "new"; name: string };

/** road_id から選択を作る。未所属・不正値は none */
export function roadChoiceOf(roadId: number | null | undefined): RoadChoice {
  return hasRoad(roadId) ? { kind: "road", roadId } : { kind: "none" };
}

/**
 * プルダウンで「新しい道路」を表す番兵値。道路の選択肢の値は正の整数の文字列なので衝突しない。
 */
const NEW_ROAD_OPTION = "new";

/** 道路一覧を取得し、他のウィンドウでの道路の変更に追従する。enabled=false の間は取得しない */
export function useRoadList(apiBaseUrl: string | undefined, enabled = true) {
  const [roads, setRoads] = useState<RoadSummary[]>([]);

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      if (!apiBaseUrl || !enabled) return;
      const data = await fetchRoadList(apiBaseUrl, signal);
      if (!signal?.aborted) setRoads(data.roads ?? []);
    },
    [apiBaseUrl, enabled],
  );

  useEffect(() => {
    const controller = new AbortController();
    reload(controller.signal).catch((err) => {
      if (!controller.signal.aborted) console.error("failed to load roads", err);
    });
    return () => controller.abort();
  }, [reload]);

  useEffect(() => {
    if (!enabled) return;
    const unlistenPromise = listen(ROADS_CHANGED_EVENT, () => {
      reload().catch((err) => console.error("failed to reload roads", err));
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [reload, enabled]);

  return { roads, reload };
}

type RoadSelectProps = {
  roads: RoadSummary[];
  value: RoadChoice;
  onChange: (choice: RoadChoice) => void;
  disabled?: boolean;
  /**
   * true: 新しい道路は名前を預かるだけ(value が new の間は名前の入力欄を出し、入力のたびに onChange)。
   * false: 名前を入れて「作成」を押したときに onCreate を呼ぶ(作成そのものは呼び出し側が行う)。
   */
  deferred?: boolean;
  onCreate?: (name: string) => void;
};

export function RoadSelect({ roads, value, onChange, disabled, deferred = false, onCreate }: RoadSelectProps) {
  const { t, i18n } = useTranslation();
  const [creatingLocal, setCreatingLocal] = useState(false);
  const [input, setInput] = useState("");
  const creating = deferred ? value.kind === "new" : creatingLocal;
  const valueKey = value.kind === "road" ? `road:${value.roadId}` : value.kind;

  // 保存が済んで選択が変わったら入力欄を閉じる(失敗したときは開いたまま残して再試行させる)。
  useEffect(() => {
    setCreatingLocal(false);
  }, [valueKey]);

  const options = useMemo(() => {
    const collator = new Intl.Collator(resolveIntlLocale(i18n.language));
    const list = roads.map((road) => ({
      id: road.road_id,
      label: roadDisplayName(road.name, road.road_id, t),
      color: road.color,
    }));
    // 一覧の取り直しが追いつく前でも、現在の所属を選択肢から落とさない。
    if (value.kind === "road" && !list.some((option) => option.id === value.roadId)) {
      list.push({ id: value.roadId, label: roadDisplayName("", value.roadId, t), color: "" });
    }
    return list.sort((a, b) => collator.compare(a.label, b.label) || a.id - b.id);
  }, [roads, value, t, i18n.language]);

  const selectedColor = value.kind === "road" ? options.find((option) => option.id === value.roadId)?.color : undefined;

  if (creating) {
    const name = deferred && value.kind === "new" ? value.name : input;
    const setName = (next: string) => (deferred ? onChange({ kind: "new", name: next }) : setInput(next));
    const cancel = () => (deferred ? onChange({ kind: "none" }) : setCreatingLocal(false));
    return (
      <div className="tag-select">
        <input
          className="tag-select__input"
          type="text"
          value={name}
          autoFocus
          maxLength={ROAD_NAME_MAX_LENGTH}
          placeholder={t("path.road.newPlaceholder")}
          disabled={disabled}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !deferred) onCreate?.(name.trim());
            if (e.key === "Escape") cancel();
          }}
        />
        {!deferred && (
          <button className="tag-select__btn" type="button" disabled={disabled} onClick={() => onCreate?.(name.trim())}>
            {t("path.road.create")}
          </button>
        )}
        <button className="tag-select__btn" type="button" disabled={disabled} onClick={cancel}>
          {t("path.road.cancel")}
        </button>
      </div>
    );
  }

  return (
    <div className="tag-select">
      {selectedColor && <span className="road-select__swatch" style={{ background: selectedColor }} aria-hidden="true" />}
      <select
        className="tag-select__select"
        value={value.kind === "road" ? String(value.roadId) : ""}
        disabled={disabled}
        onChange={(e) => {
          const next = e.target.value;
          if (next === NEW_ROAD_OPTION) {
            setInput("");
            if (deferred) onChange({ kind: "new", name: "" });
            else setCreatingLocal(true);
            return;
          }
          onChange(next === "" ? { kind: "none" } : { kind: "road", roadId: Number(next) });
        }}
      >
        <option value="">{t("path.road.none")}</option>
        {options.map((option) => (
          <option key={option.id} value={String(option.id)}>
            {option.label}
          </option>
        ))}
        <option value={NEW_ROAD_OPTION}>{t("path.road.createNew")}</option>
      </select>
    </div>
  );
}

type RoadMembershipRowProps = {
  apiBaseUrl: string;
  pathId: number;
  roadId: number | null | undefined;
  roads: RoadSummary[];
  /** 所属を変更できた(roadId=null は名もなき道に戻した)。呼び出し側は表示を取り直す */
  onChanged: (roadId: number | null) => void;
};

/** パス詳細パネルの「所属道路」行。街路・高速道路のパスにだけ出す(判定は呼び出し側) */
export function RoadMembershipRow({ apiBaseUrl, pathId, roadId, roads, onChanged }: RoadMembershipRowProps) {
  const { t } = useTranslation();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const openRoadDetail = useCallback((id: number, extra?: Record<string, unknown>) => {
    void invoke("open_registered_window", {
      windowType: "road_detail",
      entityId: String(id),
      ...(extra ? { params: { extra } } : {}),
    }).catch(console.error);
  }, []);

  const handleChange = useCallback(
    async (choice: RoadChoice) => {
      if (choice.kind === "new") return;
      const next = choice.kind === "road" ? choice.roadId : null;
      if (next === (hasRoad(roadId) ? roadId : null)) return;
      setSaving(true);
      setError(null);
      try {
        await setPathRoad(apiBaseUrl, pathId, next);
        // 付け替えでは元の道路と新しい道路の両方が変わるので、道路を限定せずに知らせる。
        await notifyRoadsChanged(undefined, "path_detail");
        onChanged(next);
      } catch (err) {
        console.error("failed to change road membership", err);
        setError(t("path.road.saveFailed"));
      } finally {
        setSaving(false);
      }
    },
    [apiBaseUrl, pathId, roadId, onChanged, t],
  );

  const handleCreate = useCallback(
    async (name: string) => {
      setSaving(true);
      setError(null);
      try {
        const { road } = await createRoad(apiBaseUrl, { name, path_ids: [pathId] });
        await notifyRoadsChanged(undefined, "path_detail");
        onChanged(road.road_id);
        // 1区間だけの道路は作りかけなので、道路詳細で直進方向への延長を提案する。
        openRoadDetail(road.road_id, { offerTrace: true, seedPathId: pathId });
      } catch (err) {
        console.error("failed to create road", err);
        setError(t("path.road.createFailed"));
      } finally {
        setSaving(false);
      }
    },
    [apiBaseUrl, pathId, onChanged, openRoadDetail, t],
  );

  return (
    <div className="tag-row road-membership-row">
      <span className="tag-row__label">{t("path.road.label")}</span>
      <RoadSelect
        roads={roads}
        value={roadChoiceOf(roadId)}
        onChange={(choice) => void handleChange(choice)}
        onCreate={(name) => void handleCreate(name)}
        disabled={saving}
      />
      {hasRoad(roadId) && (
        <button type="button" className="tag-select__btn" onClick={() => openRoadDetail(roadId)}>
          {t("path.road.openDetail")}
        </button>
      )}
      {error && <span className="tag-select__error road-membership-row__error">{error}</span>}
    </div>
  );
}
