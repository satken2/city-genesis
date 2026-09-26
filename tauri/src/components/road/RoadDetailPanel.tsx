/**
 * 道路詳細パネル
 *
 * window_type="road_detail" の右パネル。交差点で分かれた複数のパス(区間)を1本にまとめた「道路」を扱う。
 * 体裁は路線詳細(transit/TransitDetailPanel)・パス詳細と同じ右パネルの語彙(agent-detail-* のセクション・
 * 下線式タブ・小型ボタン)に揃え、ヘッダ直下に道路色の帯を敷く。
 *
 * タブ:
 *   - 基本情報 ... 延長・平均交通量・最大混雑度・渋滞損失のタイルと、区間数・経由地区・所要時間などの表
 *   - 全体図   ... 都市2Dマップ上で区間を道路色の帯で強調(RoadOverviewMap)
 *   - 路線図   ... 縦型ストリップマップ(RoadStripMap)
 *   - 交通量   ... キロ程を横軸にした指標のグラフ(RoadTrafficProfile)
 *   - 設定     ... 名前・色・起点と終点の入れ替え・区間の編集・直進方向への延長・道路の削除
 *
 * 開いている間は3Dビューに道路の強調帯(world:set_road_focus)を出し、区間・色が変わったら送り直す。
 * 閉じたときの消去と区間編集モードの打ち切りは Rust 側(window.rs の close_window_by_label)が行う
 * (ウィンドウは非表示で再利用され、アンマウント処理が走らないことがあるため)。
 *
 * 区間編集モード中は Unity から届く edit_road:toggle_path で所属を付け外しし、結果の全集合を
 * edit_road:set_members で送り返す。Unity のモードは UI の知らない理由(Esc・別のモードへの切り替えなど)でも
 * 終わるので、編集中の表示は Unity の edit_road:ended を受けて落とす。
 * 道路・所属を変えたら notifyRoadsChanged で他ウィンドウと Unity に知らせる。
 * 道路の区間が0本になるとバックエンドが道路を削除するので、取得が404になったら削除済みの表示に切り替える。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { ApiError } from "../../utils/api";
import {
  IPC_EVENT_ROAD_EDIT_ENDED,
  IPC_EVENT_ROAD_TOGGLE_PATH,
  sendClearRoadFocus,
  sendEndRoadMemberEdit,
  sendRoadEditSetMembers,
  sendRoadFocus,
  sendStartRoadMemberEdit,
  type RoadEditEndedPayload,
  type RoadTogglePathPayload,
} from "../../utils/ipc";
import { clearHintbarTextIf, setHintbarText } from "../../utils/hintbar";
import { resolveIntlLocale } from "../../utils/locale";
import { resolvePathIcon } from "../../utils/pathIcon";
import { ROAD_COLOR_PALETTE, ROAD_NAME_MAX_LENGTH } from "../../constants";
import { InfoHeader } from "../common/InfoHeader";
import { EditableLabel } from "../common/EditableLabel";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import { ZoneLink } from "../common/ZoneLink";
import { Meter } from "../common/Meter";
import {
  ROADS_CHANGED_EVENT,
  deleteRoad,
  fetchRoadDetail,
  fetchRoadList,
  isNamedRoadPathType,
  notifyRoadsChanged,
  resolveTraceBbox,
  roadDisplayName,
  traceRoad,
  updateRoad,
  updateRoadMembers,
} from "./roadApi";
import type { RoadDetail, RoadSummary, RoadTraceResponse } from "./roadTypes";
import {
  ROAD_VC_CONGESTED,
  dominantPathType,
  formatInt,
  formatKm,
  formatPercentValue,
  lanesSummary,
  sortedLanes,
} from "./roadFormat";
import { RoadOverviewMap } from "./RoadOverviewMap";
import { RoadStripMap } from "./RoadStripMap";
import { RoadTrafficProfile } from "./RoadTrafficProfile";
// 右パネルの体裁(セクション・グリッド・タブ)は経済主体詳細、帯・タイル・小型ボタン・削除ブロックは路線詳細と共有する。
import "../agent/AgentDetailPanel.css";
import "../transit/TransitDetailPanel.css";
import "./RoadDetailPanel.css";

type TabId = "basic" | "map" | "diagram" | "traffic" | "settings";

type RoadDetailPanelProps = {
  apiBaseUrl: string;
  initialEntityId: string;
  /** パス詳細で新しい道路を作った直後に開かれた場合。設定タブで直進方向への延長を提案する */
  offerTrace?: boolean;
  /** 延長の提案の起点にする区間(新しい道路を作ったパス) */
  seedPathId?: number;
};

type Notice = { text: string; tone: "info" | "error" };

/**
 * 追加候補(直進方向の自動延長)。offer=true は新規作成直後の提案。
 * bbox は候補の範囲で、全体図の表示範囲に含める(範囲外の区間は地図に読み込まれず描けないため)。
 */
type TracePreview = { ids: number[]; offer: boolean; bbox: [number, number, number, number] | null };

/**
 * 区間編集モードを始めた道路詳細パネルが、他の道路詳細パネルへ知らせる Tauri のグローバルイベント
 * (ペイロードは { label })。Unity の区間編集モードは1つだけで、edit_road:toggle_path は全ウィンドウへ
 * 届くため、後から始めたパネル以外は編集中の状態を捨てる(残すと別の道路へ区間を付け替えてしまう)。
 */
const ROAD_MEMBER_EDIT_STARTED_EVENT = "road:member_edit_started";

/** 道路詳細を閉じるときに Rust(window.rs)がそのパネルだけへ送るイベント */
const ROAD_PANEL_CLOSING_EVENT = "road:panel_closing";

/**
 * 閉じたパネルはウィンドウが非表示で残り、リスナーも生きている。非表示のパネルから3Dの強調帯や
 * Unity のステートを操作すると、表示中の別パネルの強調・編集モードを壊すので、送る前に確かめる。
 */
function isWindowVisible(): Promise<boolean> {
  return getCurrentWindow().isVisible().catch(() => true);
}

function errorText(err: unknown, t: TFunction): string {
  if (err instanceof ApiError) {
    return t(`errors.${err.code}`, { defaultValue: err.message });
  }
  return err instanceof Error ? err.message : t("errors.UNKNOWN_ERROR");
}

export function RoadDetailPanel({ apiBaseUrl, initialEntityId, offerTrace = false, seedPathId }: RoadDetailPanelProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const roadId = Number(initialEntityId);

  const [road, setRoad] = useState<RoadDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [deleted, setDeleted] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [activeTab, setActiveTab] = useState<TabId>(offerTrace ? "settings" : "basic");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [memberEditing, setMemberEditing] = useState(false);
  const [tracePreview, setTracePreview] = useState<TracePreview | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);

  // 非同期のイベントハンドラから最新値を読むための写し
  const membersRef = useRef<number[]>([]);
  const previewRef = useRef<number[]>([]);
  const editingRef = useRef(false);
  const roadListRef = useRef<RoadSummary[]>([]);
  const lastMembersSigRef = useRef<string>("");
  const toggleChainRef = useRef<Promise<void>>(Promise.resolve());
  const offeredRef = useRef(false);
  /** 区間編集の開始ごとの番号。Unity の edit_road:ended と照合し、遅れて届いた前回の終了で新しい編集を落とさない */
  const sessionRef = useRef(0);
  /** このパネルがヒントバーに出した案内(片付けるときに、他のパネルの案内を消さないよう照合する) */
  const hintTextRef = useRef("");

  const refresh = useCallback(() => setRefreshKey((k) => k + 1), []);

  /* ---- 取得 ---- */
  useEffect(() => {
    if (!Number.isFinite(roadId) || roadId <= 0) {
      setError(`Invalid road id: ${initialEntityId}`);
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    fetchRoadDetail(apiBaseUrl, roadId, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        setRoad(data);
        setError(null);
        setDeleted(false);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 404) {
          setDeleted(true);
          setRoad(null);
        } else {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
        setLoading(false);
      });
    return () => controller.abort();
  }, [apiBaseUrl, roadId, initialEntityId, refreshKey]);

  // 他のウィンドウでの道路の変更と、エポック実行の完了(交通量などの集計値が変わる)で取り直す
  useEffect(() => {
    const unlisteners: (() => void)[] = [];
    let disposed = false;
    const add = (p: Promise<() => void>) =>
      p.then((fn) => (disposed ? fn() : unlisteners.push(fn))).catch(console.error);
    add(listen(ROADS_CHANGED_EVENT, () => refresh()));
    add(
      listen<{ executing: boolean }>("epoch:executing", (event) => {
        if (!event.payload.executing) refresh();
      }),
    );
    return () => {
      disposed = true;
      unlisteners.forEach((fn) => fn());
    };
  }, [refresh]);

  /* ---- 3Dビューの強調帯 ---- */
  // 区間編集モード中の候補は edit_road:set_members で送るので、強調帯に候補を含めるのはモード外だけ
  const focusIds = useMemo(() => {
    if (!road) return null;
    const preview = !memberEditing && tracePreview ? tracePreview.ids : [];
    return [...road.path_ids, ...preview.filter((id) => !road.path_ids.includes(id))];
  }, [road, memberEditing, tracePreview]);
  const focusSig = road && focusIds ? `${road.road_id}|${road.color}|${focusIds.join(",")}` : "";

  useEffect(() => {
    if (!road || !focusIds || focusSig === "") return;
    const { road_id, color } = road;
    void isWindowVisible().then((visible) => {
      if (visible) void sendRoadFocus(road_id, color, focusIds);
    });
    // 取り直しのたびに同じ内容を送らないよう、内容の署名で比較する
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusSig]);

  // 3Dの強調帯は1本だけなので、道路詳細を複数開いているときはフォーカスを得たパネルの道路を出し直す
  const focusRef = useRef<{ roadId: number; color: string; ids: number[] } | null>(null);
  focusRef.current = road && focusIds ? { roadId: road.road_id, color: road.color, ids: focusIds } : null;
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;
    getCurrentWindow()
      .onFocusChanged(({ payload: focused }) => {
        const f = focusRef.current;
        if (focused && f) void sendRoadFocus(f.roadId, f.color, f.ids);
      })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    if (road) membersRef.current = road.path_ids;
  }, [road]);

  useEffect(() => {
    previewRef.current = tracePreview?.ids ?? [];
  }, [tracePreview]);

  // アンマウント時には何も送らない。閉じたときの後始末は Rust が行い、非表示のウィンドウが別の種類に
  // 転用されてアンマウントされる時点では、表示中の別の道路詳細の強調やヒントを消してしまうため。
  useEffect(() => {
    if (!deleted) return;
    void isWindowVisible().then((visible) => {
      if (visible) void sendClearRoadFocus(roadId);
    });
  }, [deleted, roadId]);

  /** 区間編集モード中の所属・候補を Unity へ送る(同じ内容は送り直さない) */
  const syncEditMembers = useCallback(
    (pathIds: number[], previewIds: number[]) => {
      if (!editingRef.current) return;
      const preview = previewIds.filter((id) => !pathIds.includes(id));
      const sig = `${pathIds.join(",")}|${preview.join(",")}`;
      if (sig === lastMembersSigRef.current) return;
      lastMembersSigRef.current = sig;
      void sendRoadEditSetMembers(roadId, pathIds, preview);
    },
    [roadId],
  );

  useEffect(() => {
    if (memberEditing && road) syncEditMembers(road.path_ids, tracePreview?.ids ?? []);
  }, [memberEditing, road, tracePreview, syncEditMembers]);

  const loadRoadList = useCallback(() => {
    fetchRoadList(apiBaseUrl)
      .then((res) => {
        roadListRef.current = res.roads;
      })
      .catch(console.error);
  }, [apiBaseUrl]);

  /* ---- 区間編集モード ---- */
  /**
   * 編集中の表示を落とす(Unity へは何も送らない)。clearHint=true なら自分の案内をヒントバーから片付ける。
   * 別の道路詳細が編集を始めたときは、その案内で上書き済みなので片付けない。
   */
  const dropMemberEdit = useCallback((clearHint: boolean) => {
    editingRef.current = false;
    lastMembersSigRef.current = "";
    setMemberEditing(false);
    if (clearHint && hintTextRef.current !== "") void clearHintbarTextIf(hintTextRef.current);
    hintTextRef.current = "";
  }, []);

  // Unity はこの道路の区間編集モード中のときだけ終える。UI の知らないうちに別のモードへ移っていても
  // それを打ち切らないので、非表示のパネル・削除や404の後始末から呼んでもよい
  const endMemberEdit = useCallback(() => {
    if (!editingRef.current) return;
    dropMemberEdit(true);
    void sendEndRoadMemberEdit(roadId);
  }, [dropMemberEdit, roadId]);

  // 他の道路詳細パネルが区間編集モードを始めたら、こちらの編集中の状態は Unity 側で無効になっている
  useEffect(() => {
    const ownLabel = getCurrentWindow().label;
    let unlisten: (() => void) | undefined;
    let disposed = false;
    listen<{ label: string }>(ROAD_MEMBER_EDIT_STARTED_EVENT, (event) => {
      if (event.payload.label === ownLabel || !editingRef.current) return;
      dropMemberEdit(false);
    })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [dropMemberEdit]);

  // Unity の区間編集モードが終わった(理由を問わない)。自分の道路の今回の編集なら表示を落とす
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;
    listen<RoadEditEndedPayload>(IPC_EVENT_ROAD_EDIT_ENDED, (event) => {
      const { road_id: endedRoadId, session_id: sessionId } = event.payload;
      if (!editingRef.current || endedRoadId !== roadId) return;
      // 番号は正の整数で採番する。欠けている・0以下(Unity が受け取れなかったときの既定値)は照合できないので道路IDだけで判断する
      const endedSession = Number(sessionId);
      if (Number.isFinite(endedSession) && endedSession > 0 && endedSession !== sessionRef.current) return;
      dropMemberEdit(true);
    })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [roadId, dropMemberEdit]);

  // 閉じられた。Unity のモードは Rust が edit_road:cancel で終えるので、表示と案内だけ片付ける
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let disposed = false;
    getCurrentWindow()
      .listen(ROAD_PANEL_CLOSING_EVENT, () => {
        if (editingRef.current) dropMemberEdit(true);
        setTracePreview(null);
      })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [dropMemberEdit]);

  const startMemberEdit = useCallback(() => {
    if (!road) return;
    editingRef.current = true;
    setMemberEditing(true);
    lastMembersSigRef.current = `${road.path_ids.join(",")}|`;
    // Unity 側で int に収まる正の値にする。時刻から採るのは、閉じて開き直した同じ道路のパネル(前回の番号を覚えていない)とも重ならないため
    const nextSession = (Date.now() % 1_000_000_000) || 1;
    sessionRef.current = nextSession !== sessionRef.current ? nextSession : (nextSession % 999_999_999) + 1;
    void emit(ROAD_MEMBER_EDIT_STARTED_EVENT, { label: getCurrentWindow().label });
    void sendStartRoadMemberEdit(road.road_id, road.color, road.path_ids, sessionRef.current);
    // 道路名を含めて他の道路詳細の案内と区別できるようにする(片付けるときの照合に使う)
    const hint = t("road.memberEditHintFor", {
      road: roadDisplayName(road.name, road.road_id, t),
      hint: t("road.memberEditHint"),
    });
    hintTextRef.current = hint;
    void setHintbarText(hint);
    loadRoadList();
    setNotice(null);
  }, [road, t, loadRoadList]);

  // 他のウィンドウの操作で道路が消えた(取得が404)ときも区間編集モードを終える
  useEffect(() => {
    if (deleted) endMemberEdit();
  }, [deleted, endMemberEdit]);

  /** 区間が0本になって道路が削除されたときの後始末 */
  const handleRoadVanished = useCallback(() => {
    endMemberEdit();
    setTracePreview(null);
    setDeleted(true);
    setRoad(null);
    void notifyRoadsChanged(roadId);
  }, [endMemberEdit, roadId]);

  const handleToggle = useCallback(
    async (payload: RoadTogglePathPayload) => {
      if (!editingRef.current) return;
      if (!isNamedRoadPathType(payload.path_type)) {
        setNotice({ text: t("road.notNamedRoadType"), tone: "error" });
        return;
      }
      const members = new Set(membersRef.current);
      let body: { add?: number[]; remove?: number[] };
      if (payload.shift) {
        const res = await traceRoad(apiBaseUrl, { pathId: payload.path_id, roadId });
        const add = [...new Set([payload.path_id, ...res.path_ids])].filter((id) => !members.has(id));
        if (add.length === 0) {
          setNotice({ text: t("road.noTraceCandidates"), tone: "info" });
          return;
        }
        body = { add };
      } else if (members.has(payload.path_id)) {
        body = { remove: [payload.path_id] };
      } else {
        body = { add: [payload.path_id] };
      }

      // 別の道路に属していた区間は付け替えになるので、元の道路名を控えておく
      const sources = new Set<string>();
      for (const id of body.add ?? []) {
        const owner = roadListRef.current.find((r) => r.road_id !== roadId && r.path_ids.includes(id));
        if (owner) sources.add(roadDisplayName(owner.name, owner.road_id, t));
      }

      const res = await updateRoadMembers(apiBaseUrl, roadId, body);
      if (res.deleted || !res.road) {
        handleRoadVanished();
        return;
      }
      membersRef.current = res.road.path_ids;
      syncEditMembers(res.road.path_ids, previewRef.current);
      if (sources.size > 0) {
        setNotice({ text: t("road.reassigned", { roads: [...sources].join(t("road.listSeparator")) }), tone: "info" });
      } else if (body.remove) {
        setNotice({ text: t("road.segmentRemoved", { id: payload.path_id }), tone: "info" });
      } else {
        setNotice({ text: t("road.segmentsAdded", { n: body.add?.length ?? 0 }), tone: "info" });
      }
      await notifyRoadsChanged(roadId);
      loadRoadList();
      refresh();
    },
    [apiBaseUrl, roadId, t, handleRoadVanished, syncEditMembers, loadRoadList, refresh],
  );

  useEffect(() => {
    if (!memberEditing) return;
    let unlisten: (() => void) | undefined;
    let disposed = false;
    listen<RoadTogglePathPayload>(IPC_EVENT_ROAD_TOGGLE_PATH, (event) => {
      // 連打で所属の更新が前後しないよう、1件ずつ順に処理する
      toggleChainRef.current = toggleChainRef.current
        .then(() => handleToggle(event.payload))
        .catch((err: unknown) => {
          setNotice({ text: errorText(err, t), tone: "error" });
          // 他の操作で区間が消えていた(分割・結合・削除)なら、取り直して次のクリックに備える
          refresh();
        });
    })
      .then((fn) => (disposed ? fn() : (unlisten = fn)))
      .catch(console.error);
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [memberEditing, handleToggle, t, refresh]);

  /* ---- 直進方向の自動延長 ---- */
  const showTracePreview = useCallback(
    async (res: RoadTraceResponse, offer: boolean) => {
      const fresh = res.path_ids.filter((id) => !membersRef.current.includes(id));
      if (fresh.length === 0) {
        if (!offer) setNotice({ text: t("road.noTraceCandidates"), tone: "info" });
        setTracePreview(null);
        return;
      }
      const bbox = await resolveTraceBbox(apiBaseUrl, res, fresh).catch(() => null);
      setTracePreview({ ids: fresh, offer, bbox });
      setNotice(null);
    },
    [apiBaseUrl, t],
  );

  const handleTraceFromEnds = useCallback(async () => {
    setBusy(true);
    try {
      const res = await traceRoad(apiBaseUrl, { roadId });
      await showTracePreview(res, false);
    } catch (err) {
      setNotice({ text: errorText(err, t), tone: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, roadId, showTracePreview, t]);

  // 新しい道路を作った直後は、作成元の区間から直進方向にたどった候補を一度だけ提案する
  useEffect(() => {
    if (!road || !offerTrace || seedPathId == null || offeredRef.current) return;
    offeredRef.current = true;
    traceRoad(apiBaseUrl, { pathId: seedPathId, roadId })
      .then((res) => showTracePreview(res, true))
      .catch(console.error);
  }, [road, offerTrace, seedPathId, apiBaseUrl, roadId, showTracePreview]);

  const confirmTrace = useCallback(async () => {
    if (!tracePreview) return;
    setBusy(true);
    try {
      const res = await updateRoadMembers(apiBaseUrl, roadId, { add: tracePreview.ids });
      setTracePreview(null);
      if (res.deleted || !res.road) {
        handleRoadVanished();
        return;
      }
      membersRef.current = res.road.path_ids;
      syncEditMembers(res.road.path_ids, []);
      setNotice({ text: t("road.segmentsAdded", { n: tracePreview.ids.length }), tone: "info" });
      await notifyRoadsChanged(roadId);
      refresh();
    } catch (err) {
      setNotice({ text: errorText(err, t), tone: "error" });
    } finally {
      setBusy(false);
    }
  }, [apiBaseUrl, roadId, tracePreview, handleRoadVanished, syncEditMembers, refresh, t]);

  /* ---- 名前・色・向き・削除 ---- */
  const saveRoad = useCallback(
    async (body: { name?: string; color?: string; reverse?: boolean }) => {
      await updateRoad(apiBaseUrl, roadId, body);
      await notifyRoadsChanged(roadId);
      refresh();
    },
    [apiBaseUrl, roadId, refresh],
  );

  /** EditableLabel は例外の文言をその場に出すので、長さ超過もここで投げる */
  const handleNameSave = useCallback(
    async (next: string) => {
      const name = next.trim().replace(/\s+/g, " ");
      if (name.length > ROAD_NAME_MAX_LENGTH) {
        throw new Error(t("road.nameTooLong", { max: ROAD_NAME_MAX_LENGTH }));
      }
      try {
        await saveRoad({ name });
      } catch (err) {
        throw new Error(errorText(err, t));
      }
    },
    [saveRoad, t],
  );

  const runAction = useCallback(
    async (action: () => Promise<void>) => {
      setBusy(true);
      try {
        await action();
      } catch (err) {
        setNotice({ text: errorText(err, t), tone: "error" });
      } finally {
        setBusy(false);
      }
    },
    [t],
  );

  const handleDelete = useCallback(async () => {
    setBusy(true);
    try {
      endMemberEdit();
      await deleteRoad(apiBaseUrl, roadId);
      await notifyRoadsChanged(roadId);
      await invoke("close_current_window");
    } catch (err) {
      setNotice({ text: errorText(err, t), tone: "error" });
      setConfirmDelete(false);
      setBusy(false);
    }
  }, [apiBaseUrl, roadId, endMemberEdit, t]);

  /* ---- 描画 ---- */
  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }
  if (deleted) {
    return (
      <div className="agent-detail">
        <div className="road-detail__deleted" role="status">
          <strong>{t("road.deletedTitle")}</strong>
          <p>{t("road.deletedDescription")}</p>
        </div>
      </div>
    );
  }
  if (error || !road) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const stats = road.stats;
  const displayName = roadDisplayName(road.name, road.road_id, t);
  const km = (m: number) => `${formatKm(m, locale)} km`;
  const dash = "—";
  const openWindow = (windowType: string, id: number) => {
    void invoke("open_registered_window", { windowType, entityId: String(id) });
  };

  const tabs: { id: TabId; label: string }[] = [
    { id: "basic", label: t("road.tabs.basic") },
    { id: "map", label: t("road.tabs.map") },
    { id: "diagram", label: t("road.tabs.diagram") },
    { id: "traffic", label: t("road.tabs.traffic") },
    { id: "settings", label: t("road.tabs.settings") },
  ];

  const speedLimitText =
    stats.speed_limit_min == null || stats.speed_limit_max == null
      ? dash
      : Math.round(stats.speed_limit_min) === Math.round(stats.speed_limit_max)
        ? `${Math.round(stats.speed_limit_min)} km/h`
        : `${Math.round(stats.speed_limit_min)}–${Math.round(stats.speed_limit_max)} km/h`;

  const minutes = (v: number | null) => (v == null ? dash : t("road.minutesValue", { v: v.toLocaleString(locale, { maximumFractionDigits: 1 }) }));
  const kmh = (v: number | null) => (v == null ? dash : `${v.toLocaleString(locale, { maximumFractionDigits: 1 })} km/h`);

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolvePathIcon(dominantPathType(road.segments))}
        showStatus={false}
        variantName={lanesSummary(stats, t)}
        name={displayName}
        onNameSave={handleNameSave}
        nameValue={road.name}
      />
      <div className="transit-detail__stripe" style={{ background: road.color }} aria-hidden="true" />

      {memberEditing && (
        <div className="road-detail__editing" role="status">
          <span className="road-detail__editing-text">
            {t("road.memberEditing", { n: road.segment_count })}
          </span>
          <button type="button" className="transit-detail__btn transit-detail__btn--primary" onClick={endMemberEdit}>
            {t("road.memberEditDone")}
          </button>
        </div>
      )}

      {tracePreview && (
        <div className="road-detail__preview" role="status">
          <span className="road-detail__preview-text">
            {t(tracePreview.offer ? "road.traceOffer" : "road.tracePreview", { n: tracePreview.ids.length })}
          </span>
          <div className="transit-detail__actions">
            <button
              type="button"
              className="transit-detail__btn transit-detail__btn--ghost"
              disabled={busy}
              onClick={() => setTracePreview(null)}
            >
              {t("road.traceDiscard")}
            </button>
            <button
              type="button"
              className="transit-detail__btn transit-detail__btn--primary"
              disabled={busy}
              onClick={() => void confirmTrace()}
            >
              {t("road.traceConfirm")}
            </button>
          </div>
        </div>
      )}

      {notice && (
        <p className={`road-detail__notice road-detail__notice--${notice.tone}`}>
          {notice.text}
          <button type="button" className="road-detail__notice-close" aria-label={t("road.dismiss")} onClick={() => setNotice(null)}>
            ×
          </button>
        </p>
      )}

      <div className="agent-detail-tabs">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={`agent-detail-tab${tab.id === activeTab ? " agent-detail-tab--active" : ""}`}
            onClick={() => setActiveTab(tab.id)}
          >
            {tab.label}
          </button>
        ))}
      </div>

      <div className="agent-detail-tab-content">
        {activeTab === "basic" && (
          <>
            <section className="agent-detail-section">
              <div className="transit-detail__tiles">
                <StatTile label={t("road.stats.length")} value={formatKm(stats.length_m, locale)} unit="km" />
                <StatTile
                  label={t("road.stats.avgTraffic")}
                  value={stats.avg_daily_traffic == null ? dash : formatInt(stats.avg_daily_traffic, locale)}
                  unit={stats.avg_daily_traffic == null ? undefined : t("road.unit.vehiclesPerDay")}
                />
                <StatTile
                  label={t("road.stats.maxVc")}
                  value={stats.max_vc == null ? dash : formatPercentValue(stats.max_vc.value, locale)}
                  tone={stats.max_vc != null && stats.max_vc.value >= ROAD_VC_CONGESTED ? "negative" : undefined}
                  hint={stats.max_vc == null
                    ? undefined
                    : `${formatKm(stats.max_vc.from_km * 1000, locale)}–${formatKm(stats.max_vc.to_km * 1000, locale)} km`}
                  onClick={stats.max_vc == null ? undefined : () => openWindow("path_detail", stats.max_vc!.path_id)}
                  title={stats.max_vc == null ? undefined : t("road.openMaxVcSegment")}
                />
                <StatTile
                  label={t("road.stats.delay")}
                  value={stats.delay_vehicle_hours.toLocaleString(locale, { maximumFractionDigits: 1 })}
                  unit={t("road.unit.vehicleHoursPerDay")}
                />
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.tabs.basic")}</h3>
              <div className="agent-detail-grid road-detail__grid">
                <span className="agent-detail-label">{t("road.stats.segments")}</span>
                <span className="agent-detail-value">{formatInt(stats.segment_count, locale)}</span>

                <span className="agent-detail-label">{t("road.stats.zones")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {road.zones.length === 0
                    ? dash
                    : road.zones.map((z, i) => (
                        <span key={z.zone_id} className="road-detail__zone">
                          {i > 0 && <span className="road-detail__sep">{t("road.listSeparator")}</span>}
                          <ZoneLink zoneId={z.zone_id} zoneName={z.name} />
                        </span>
                      ))}
                </span>

                <span className="agent-detail-label">{t("road.stats.lanes")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {stats.lanes_composition.length === 0
                    ? dash
                    : sortedLanes(stats)
                        .map((l) => t("road.lanesLength", { n: l.lanes, length: km(l.length_m) }))
                        .join(t("road.listSeparator"))}
                </span>

                <span className="agent-detail-label">{t("road.stats.speedLimit")}</span>
                <span className="agent-detail-value">{speedLimitText}</span>

                <span className="agent-detail-label">{t("road.stats.travelTime")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {/* 算出できない理由は、計画中・工事中の区間があるときだけそれと言い、無ければ稼働停止・通行不能を挙げる */}
                  {stats.travel_time_min == null && stats.travel_time_free_min == null
                    ? (
                        <span className="road-detail__muted">
                          {t(stats.planned_length_m > 0
                            ? "road.stats.travelTimeUnavailable"
                            : "road.stats.travelTimeUnavailableImpassable")}
                        </span>
                      )
                    : t("road.stats.travelTimeValue", {
                        congested: minutes(stats.travel_time_min),
                        free: minutes(stats.travel_time_free_min),
                      })}
                </span>

                <span className="agent-detail-label">{t("road.stats.travelSpeed")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {stats.travel_speed_kmh == null && stats.travel_speed_free_kmh == null
                    ? dash
                    : t("road.stats.travelSpeedValue", {
                        congested: kmh(stats.travel_speed_kmh),
                        free: kmh(stats.travel_speed_free_kmh),
                      })}
                </span>

                <span className="agent-detail-label">{t("road.stats.vehicleKm")}</span>
                <span className="agent-detail-value">
                  {formatInt(stats.vehicle_km, locale)}
                  <span className="transit-detail__unit">{t("road.unit.vehicleKmPerDay")}</span>
                </span>

                <span className="agent-detail-label">{t("road.stats.congestedLength")}</span>
                <span className="agent-detail-value">{km(stats.congested_length_m)}</span>

                <span className="agent-detail-label">{t("road.stats.durability")}</span>
                <span className="agent-detail-value transit-detail__meter">
                  {stats.avg_durability == null ? (
                    dash
                  ) : (
                    <>
                      <Meter value={stats.avg_durability} />
                      <span className="transit-detail__meter-value">{formatPercentValue(stats.avg_durability, locale)}</span>
                      {stats.low_durability_count > 0 && (
                        <span className="road-detail__warn-tag">{t("road.stats.lowDurability", { n: stats.low_durability_count })}</span>
                      )}
                    </>
                  )}
                </span>

                <span className="agent-detail-label">{t("road.stats.accidents")}</span>
                <span className="agent-detail-value">
                  {t("road.segmentsCount", { n: stats.accident_segment_count })}
                </span>

                <span className="agent-detail-label">{t("road.stats.planned")}</span>
                <span className="agent-detail-value">{km(stats.planned_length_m)}</span>

                <span className="agent-detail-label">{t("road.stats.transitLines")}</span>
                <span className="agent-detail-value road-detail__wrap">
                  {road.transit_lines.length === 0
                    ? dash
                    : road.transit_lines.map((line) => (
                        <button
                          key={line.line_agent_id}
                          type="button"
                          className="road-detail__line-chip"
                          onClick={() => openWindow("transit_detail", line.line_agent_id)}
                        >
                          <i style={{ background: line.color }} />
                          {line.name !== "" ? line.name : `#${line.line_agent_id}`}
                        </button>
                      ))}
                </span>
              </div>
            </section>
          </>
        )}

        {activeTab === "map" && (
          <section className="agent-detail-section">
            <RoadOverviewMap
              apiBaseUrl={apiBaseUrl}
              bbox={road.bbox}
              pathIds={road.path_ids}
              color={road.color}
              previewPathIds={tracePreview?.ids ?? null}
              previewBbox={tracePreview?.bbox ?? null}
            />
          </section>
        )}

        {activeTab === "diagram" && (
          <section className="agent-detail-section">
            {road.segments.length > 0 ? <RoadStripMap road={road} /> : <p className="agent-detail-muted">{t("road.noSegments")}</p>}
          </section>
        )}

        {activeTab === "traffic" && (
          <section className="agent-detail-section">
            <RoadTrafficProfile road={road} />
          </section>
        )}

        {activeTab === "settings" && (
          <>
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.tabs.settings")}</h3>
              <div className="agent-detail-grid transit-detail__settings-grid">
                <span className="agent-detail-label">{t("road.settings.name")}</span>
                <span className="agent-detail-value">
                  <EditableLabel value={road.name} displayText={displayName} allowEmpty={true} onSave={handleNameSave} />
                </span>
                <span className="agent-detail-label">{t("road.settings.color")}</span>
                <span className="agent-detail-value transit-detail__color-value">
                  <ColorSwatchPicker
                    value={road.color.toLowerCase()}
                    presets={ROAD_COLOR_PALETTE.map((c) => c.toLowerCase())}
                    disabled={busy}
                    onChange={(hex) => {
                      if (hex.toLowerCase() === road.color.toLowerCase()) return;
                      void runAction(() => saveRoad({ color: hex }));
                    }}
                  />
                </span>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.settings.directionTitle")}</h3>
              <div className="road-detail__action-row">
                <span className="road-detail__action-desc">{t("road.settings.reverseDescription")}</span>
                <button
                  type="button"
                  className="transit-detail__btn transit-detail__btn--ghost"
                  disabled={busy}
                  onClick={() => void runAction(() => saveRoad({ reverse: true }))}
                >
                  {t("road.settings.reverse")}
                </button>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("road.settings.segmentsTitle")}</h3>
              <div className="road-detail__action-row">
                <span className="road-detail__action-desc">
                  {t("road.settings.segmentsSummary", { n: road.segment_count, length: km(road.length_m) })}
                </span>
                {memberEditing ? (
                  <button type="button" className="transit-detail__btn transit-detail__btn--primary" onClick={endMemberEdit}>
                    {t("road.memberEditDone")}
                  </button>
                ) : (
                  <button type="button" className="transit-detail__btn transit-detail__btn--ghost" disabled={busy} onClick={startMemberEdit}>
                    {t("road.settings.editSegments")}
                  </button>
                )}
              </div>
              <div className="road-detail__action-row">
                <span className="road-detail__action-desc">{t("road.settings.traceDescription")}</span>
                <button
                  type="button"
                  className="transit-detail__btn transit-detail__btn--ghost"
                  disabled={busy}
                  onClick={() => void handleTraceFromEnds()}
                >
                  {t("road.settings.trace")}
                </button>
              </div>
            </section>

            <section className="agent-detail-section transit-detail__danger">
              <div className="transit-detail__danger-row">
                <div className="transit-detail__danger-text">
                  <span className="transit-detail__danger-title">{t("road.settings.deleteTitle")}</span>
                  <span className="transit-detail__danger-desc">{t("road.settings.deleteDescription")}</span>
                </div>
                {confirmDelete ? (
                  <div className="transit-detail__actions">
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--ghost"
                      disabled={busy}
                      onClick={() => setConfirmDelete(false)}
                    >
                      {t("road.settings.cancel")}
                    </button>
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--danger-solid"
                      disabled={busy}
                      onClick={() => void handleDelete()}
                    >
                      {busy ? t("common.processing") : t("road.settings.deleteConfirm")}
                    </button>
                  </div>
                ) : (
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--danger"
                    onClick={() => setConfirmDelete(true)}
                  >
                    {t("road.settings.delete")}
                  </button>
                )}
              </div>
            </section>
          </>
        )}
      </div>
    </div>
  );
}

type StatTileProps = {
  label: string;
  value: string;
  unit?: string;
  tone?: "positive" | "negative";
  hint?: string;
  title?: string;
  onClick?: () => void;
};

/** 基本情報のタイル(ラベル上・値下)。意匠は路線詳細の実績タイルと共通 */
function StatTile({ label, value, unit, tone, hint, title, onClick }: StatTileProps) {
  const className = [
    "transit-detail__tile",
    tone && `transit-detail__tile--${tone}`,
    onClick && "road-detail__tile--link",
  ]
    .filter(Boolean)
    .join(" ");
  const body = (
    <>
      <span className="transit-detail__tile-label">{label}</span>
      <span className="transit-detail__tile-value">
        {value}
        {unit && unit !== "" && <span className="transit-detail__unit">{unit}</span>}
      </span>
      {hint && <span className="road-detail__tile-hint">{hint}</span>}
    </>
  );
  return onClick ? (
    <button type="button" className={className} title={title} onClick={onClick}>
      {body}
    </button>
  ) : (
    <div className={className}>{body}</div>
  );
}
