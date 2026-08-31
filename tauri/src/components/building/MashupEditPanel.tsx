/**
 * マッシュアップ建物のサブ建物配置エディタ
 *
 * window_type="mashup_edit" のポップアップウィンドウで表示する専用パネル。
 * マッシュアップ建物(MODEL_MASHUP)を構成するサブ建物(校舎・体育館などの棟)の配置を
 * 2Dキャンバス上で編集する。経済状態に影響しない見た目の編集のため、建物の status を
 * 問わずいつでも開ける。棟の階数はここでは編集できない(親建物の floors を棟定義の
 * 階数範囲でクランプした値をバックエンドが配信時に導出する)。
 *
 * 画面構成:
 *   - 上部: 配置可能なサブ建物のカード一覧。底面矩形は敷地キャンバスと同じ縮尺(実サイズ)で描く
 *   - 下部: 敷地(土地区画ポリゴン)と配置済みサブ建物のキャンバス
 *
 * 操作:
 *   - カードを敷地へドラッグ: サブ建物を新規配置
 *   - 配置済みサブ建物をドラッグ: 移動
 *   - Shift を押しながらドラッグ: 回転(棟の中心を軸にカーソル方向へ回す)
 *   - クリックで選択、Delete キーまたは削除ボタンで撤去
 *   - 既存のサブ建物との重なりは許容する(バックエンドも検証しない)
 *
 * 保存フロー:
 *   [保存] で PUT /buildings/{id}/sub-buildings に配置リストを一括送信し、成功後に
 *   session:reload を送って Unity に建物を再描画させ、ウィンドウを閉じる。
 *
 * 座標系:
 *   position は建物中心(=土地区画の center_point)基準の相対XZ座標、rotation は
 *   XZ平面で+X基準の反時計回りの角度(度)。キャンバスは Z 軸を画面上向きに描く。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { SubBuildingMasterEntry } from "../../types";
import { fetchApi, ApiError } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { showNotification } from "../../utils/notification";
import { useMasterData } from "../../contexts/MasterDataContext";
import { MASHUP_CELL_SIZE } from "../../constants";
import "./MashupEditPanel.css";

/**
 * サブ建物1棟分の配置エントリ(GET/PUT /buildings/{id}/sub-buildings の形式)。
 * 棟の階数は編集対象外(親建物の floors からバックエンドが配信時に導出する)。
 */
type SubBuildingPlacement = {
  subbuilding_id: number;
  /** 建物中心基準の相対XZ座標 [x, z] */
  position: number[];
  /** XZ平面の回転角(度・+X基準の反時計回り) */
  rotation: number;
};

/** キャンバスのドラッグ操作の状態 */
type DragState =
  | { kind: "move"; index: number; startWorld: [number, number]; origPosition: [number, number] }
  | { kind: "rotate"; index: number; startAngle: number; origRotation: number }
  /** カードからの新規配置ドラッグ。world はカーソルが敷地キャンバス上にあるときのみ非null */
  | { kind: "new"; subbuildingId: number; world: [number, number] | null };

/** ワールド座標(XZ)⇔キャンバス座標の変換 */
type ViewTransform = {
  /** ピクセル/メートル */
  scale: number;
  toScreen: (x: number, z: number) => [number, number];
  toWorld: (px: number, py: number) => [number, number];
};

type MashupEditPanelProps = {
  apiBaseUrl: string;
  initialEntityId: string;
  onClose: () => void;
};

/** キャンバス内側の余白(px) */
const CANVAS_PADDING = 24;

/** サブ建物種類の塗り分けパレット(subbuilding_id から決定論的に選ぶ) */
const SUBBUILDING_COLORS = [
  "#60a5fa", "#f472b6", "#4ade80", "#fbbf24",
  "#a78bfa", "#f87171", "#22d3ee", "#fb923c",
];

function colorForSubbuilding(subbuildingId: number): string {
  const index = Math.abs(Math.trunc(subbuildingId)) % SUBBUILDING_COLORS.length;
  return SUBBUILDING_COLORS[index];
}

/**
 * サブ建物の底面寸法(m)。マスターの width / height はセル数単位(1単位=MASHUP_CELL_SIZE m)で
 * 定義されており、未定義の棟はセル1つ分として扱う(バックエンドと同じ規約)。
 */
function subbuildingSize(entry: SubBuildingMasterEntry | undefined): [number, number] {
  return [(entry?.width ?? 1) * MASHUP_CELL_SIZE, (entry?.height ?? 1) * MASHUP_CELL_SIZE];
}

/**
 * 配置エントリの底面矩形のワールド座標の4頂点を返す。
 * rotation の u軸(幅方向)=(cosθ, sinθ)、v軸(奥行き方向)=(-sinθ, cosθ) は
 * バックエンドのセルグリッド(generate_mashup_cells)と同じ規約。
 */
function placementCorners(
  placement: SubBuildingPlacement,
  centerPoint: number[],
  entry: SubBuildingMasterEntry | undefined,
): [number, number][] {
  const [width, height] = subbuildingSize(entry);
  const rad = (placement.rotation * Math.PI) / 180;
  const ux = Math.cos(rad);
  const uz = Math.sin(rad);
  const vx = -uz;
  const vz = ux;
  const cx = centerPoint[0] + placement.position[0];
  const cz = centerPoint[1] + placement.position[1];
  const hw = width / 2;
  const hh = height / 2;
  return [
    [cx - ux * hw - vx * hh, cz - uz * hw - vz * hh],
    [cx + ux * hw - vx * hh, cz + uz * hw - vz * hh],
    [cx + ux * hw + vx * hh, cz + uz * hw + vz * hh],
    [cx - ux * hw + vx * hh, cz - uz * hw + vz * hh],
  ];
}

/** ワールド座標の点が配置エントリの底面矩形内にあるか(回転を考慮したローカル座標判定) */
function hitTestPlacement(
  worldX: number,
  worldZ: number,
  placement: SubBuildingPlacement,
  centerPoint: number[],
  entry: SubBuildingMasterEntry | undefined,
): boolean {
  const [width, height] = subbuildingSize(entry);
  const rad = (placement.rotation * Math.PI) / 180;
  const ux = Math.cos(rad);
  const uz = Math.sin(rad);
  const dx = worldX - (centerPoint[0] + placement.position[0]);
  const dz = worldZ - (centerPoint[1] + placement.position[1]);
  const localU = dx * ux + dz * uz;
  const localV = dx * -uz + dz * ux;
  return Math.abs(localU) <= width / 2 && Math.abs(localV) <= height / 2;
}

export function MashupEditPanel({ apiBaseUrl, initialEntityId, onClose }: MashupEditPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const agentId = useMemo(() => parseInt(initialEntityId, 10), [initialEntityId]);
  const baseUrl = apiBaseUrl.replace(/\/+$/, "");

  const [variant, setVariant] = useState<number | null>(null);
  const [agentName, setAgentName] = useState<string>("");
  const [parcelPolygon, setParcelPolygon] = useState<number[][] | null>(null);
  const [centerPoint, setCenterPoint] = useState<number[] | null>(null);
  const [facadeEdgeIndex, setFacadeEdgeIndex] = useState<number[]>([]);
  const [placements, setPlacements] = useState<SubBuildingPlacement[]>([]);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [drag, setDrag] = useState<DragState | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [isLoaded, setIsLoaded] = useState(false);
  const [isSaving, setIsSaving] = useState(false);

  const resizeObserverRef = useRef<ResizeObserver | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number }>({ width: 0, height: 0 });

  /* ---- データ取得 ---- */

  /**
   * 建物(variant / parcel_id)→ 土地区画(polygon / center_point)と現在の配置リストを取得する。
   * 配置リストは編集用のローカルstateへコピーし、保存まではバックエンドに送信しない。
   */
  useEffect(() => {
    if (!baseUrl || !Number.isFinite(agentId)) return;
    const controller = new AbortController();

    (async () => {
      try {
        const agentRes = await fetchApi<{
          economic_agent: { variant: number; parcel_id?: number; name: string };
        }>(`${baseUrl}/api/v1/agents/${agentId}`, { signal: controller.signal });
        const agent = agentRes.economic_agent;
        if (!agent.parcel_id) {
          setLoadError(t("mashup_edit.noParcel"));
          return;
        }

        const [parcelRes, subRes] = await Promise.all([
          fetchApi<{ polygon: number[][]; center_point: number[]; facade_edge_index: number[] | number }>(
            `${baseUrl}/api/v1/parcels/${agent.parcel_id}`,
            { signal: controller.signal },
          ),
          fetchApi<{ sub_buildings: SubBuildingPlacement[] }>(
            `${baseUrl}/api/v1/buildings/${agentId}/sub-buildings`,
            { signal: controller.signal },
          ),
        ]);
        if (controller.signal.aborted) return;

        setVariant(agent.variant);
        setAgentName(agent.name);
        setParcelPolygon(parcelRes.polygon);
        setCenterPoint(parcelRes.center_point);
        const facade = parcelRes.facade_edge_index;
        setFacadeEdgeIndex(Array.isArray(facade) ? facade : facade != null ? [facade] : []);
        // 配信エントリには model_prefix 等の解決済みフィールドが含まれるため、編集対象の3項目だけを持つ
        setPlacements(
          (subRes.sub_buildings ?? []).map((p) => ({
            subbuilding_id: p.subbuilding_id,
            position: [p.position[0], p.position[1]],
            rotation: p.rotation ?? 0,
          })),
        );
        setIsLoaded(true);
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("[MashupEdit] Failed to load building data:", err);
          setLoadError(err instanceof Error ? err.message : String(err));
        }
      }
    })();

    return () => controller.abort();
  }, [baseUrl, agentId, t]);

  /* ---- マスターデータからの派生 ---- */

  /** このバリアントに属するサブ建物マスターの一覧(カード表示用) */
  const subbuildingEntries = useMemo(() => {
    if (!masterData || variant === null) return [];
    return Object.entries(masterData.subbuildings)
      .filter(([, entry]) => entry.agent_variant === variant)
      .map(([idStr, entry]) => [parseInt(idStr, 10), entry] as const);
  }, [masterData, variant]);

  /** subbuilding_id → マスターエントリの逆引き(描画・当たり判定用) */
  const subbuildingById = useMemo(() => {
    const map = new Map<number, SubBuildingMasterEntry>();
    for (const [id, entry] of subbuildingEntries) map.set(id, entry);
    return map;
  }, [subbuildingEntries]);

  /* ---- キャンバスサイズの追従 ---- */

  /**
   * 敷地キャンバスの器のサイズ監視。マスターデータと建物情報が揃うまではローディング表示に
   * 差し替わっていて器が存在しないため、マウント時の useEffect ではなくコールバックrefで
   * 監視を開始する(要素が実際に生成された瞬間に張り、外れた瞬間に切る)。
   */
  const setCanvasContainerRef = useCallback((container: HTMLDivElement | null) => {
    resizeObserverRef.current?.disconnect();
    if (!container) {
      resizeObserverRef.current = null;
      return;
    }
    const observer = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect;
      if (rect) setCanvasSize({ width: Math.floor(rect.width), height: Math.floor(rect.height) });
    });
    observer.observe(container);
    resizeObserverRef.current = observer;
  }, []);

  /* ---- ビュー変換(敷地全体をキャンバスへフィット) ---- */

  const view = useMemo((): ViewTransform | null => {
    if (!parcelPolygon || parcelPolygon.length === 0) return null;
    if (canvasSize.width <= CANVAS_PADDING * 2 || canvasSize.height <= CANVAS_PADDING * 2) return null;

    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (const [x, z] of parcelPolygon) {
      minX = Math.min(minX, x); maxX = Math.max(maxX, x);
      minZ = Math.min(minZ, z); maxZ = Math.max(maxZ, z);
    }
    const worldW = Math.max(maxX - minX, 1e-6);
    const worldH = Math.max(maxZ - minZ, 1e-6);
    const scale = Math.min(
      (canvasSize.width - CANVAS_PADDING * 2) / worldW,
      (canvasSize.height - CANVAS_PADDING * 2) / worldH,
    );
    // 敷地をキャンバス中央に置く。Z軸は画面上向き(スクリーンYと逆)に描く
    const offsetX = (canvasSize.width - worldW * scale) / 2;
    const offsetY = (canvasSize.height - worldH * scale) / 2;
    return {
      scale,
      toScreen: (x, z) => [offsetX + (x - minX) * scale, offsetY + (maxZ - z) * scale],
      toWorld: (px, py) => [minX + (px - offsetX) / scale, maxZ - (py - offsetY) / scale],
    };
  }, [parcelPolygon, canvasSize]);

  /* ---- 描画 ---- */

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !view || !parcelPolygon || !centerPoint) return;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    const dpr = window.devicePixelRatio || 1;
    canvas.width = canvasSize.width * dpr;
    canvas.height = canvasSize.height * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, canvasSize.width, canvasSize.height);

    // 敷地ポリゴン
    ctx.beginPath();
    parcelPolygon.forEach(([x, z], i) => {
      const [px, py] = view.toScreen(x, z);
      if (i === 0) ctx.moveTo(px, py);
      else ctx.lineTo(px, py);
    });
    ctx.closePath();
    ctx.fillStyle = "rgba(148, 163, 184, 0.12)";
    ctx.fill();
    ctx.strokeStyle = "rgba(148, 163, 184, 0.7)";
    ctx.lineWidth = 1.5;
    ctx.stroke();

    // 接道辺(前面)をハイライト。primary の棟をどちらへ寄せるかの目印になる
    const vertexCount = parcelPolygon.length;
    for (const edgeIndex of facadeEdgeIndex) {
      if (edgeIndex < 0 || edgeIndex >= vertexCount) continue;
      const [x1, z1] = parcelPolygon[edgeIndex];
      const [x2, z2] = parcelPolygon[(edgeIndex + 1) % vertexCount];
      const [px1, py1] = view.toScreen(x1, z1);
      const [px2, py2] = view.toScreen(x2, z2);
      ctx.beginPath();
      ctx.moveTo(px1, py1);
      ctx.lineTo(px2, py2);
      ctx.strokeStyle = "rgba(255, 159, 26, 0.9)";
      ctx.lineWidth = 3;
      ctx.stroke();
    }

    // 配置済みサブ建物
    const drawPlacement = (placement: SubBuildingPlacement, options: { selected?: boolean; ghost?: boolean }) => {
      const entry = subbuildingById.get(placement.subbuilding_id);
      const corners = placementCorners(placement, centerPoint, entry);
      const color = colorForSubbuilding(placement.subbuilding_id);

      ctx.save();
      if (options.ghost) ctx.globalAlpha = 0.55;
      ctx.beginPath();
      corners.forEach(([x, z], i) => {
        const [px, py] = view.toScreen(x, z);
        if (i === 0) ctx.moveTo(px, py);
        else ctx.lineTo(px, py);
      });
      ctx.closePath();
      ctx.fillStyle = `${color}55`;
      ctx.fill();
      ctx.strokeStyle = color;
      ctx.lineWidth = options.selected ? 3 : 1.5;
      ctx.stroke();

      // 棟名ラベル(棟の中心に水平表示。矩形が小さいときは省略)
      const [labelX, labelY] = view.toScreen(
        centerPoint[0] + placement.position[0],
        centerPoint[1] + placement.position[1],
      );
      const [width, height] = subbuildingSize(entry);
      const minSidePx = Math.min(width, height) * view.scale;
      if (minSidePx >= 24) {
        ctx.globalAlpha = options.ghost ? 0.7 : 1;
        ctx.fillStyle = "#e5e7eb";
        ctx.font = "11px sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(entry?.display_name ?? String(placement.subbuilding_id), labelX, labelY);
      }
      ctx.restore();
    };

    placements.forEach((placement, index) => {
      drawPlacement(placement, { selected: index === selectedIndex });
    });

    // 新規配置ドラッグ中のゴースト
    if (drag?.kind === "new" && drag.world) {
      drawPlacement(
        {
          subbuilding_id: drag.subbuildingId,
          position: [drag.world[0] - centerPoint[0], drag.world[1] - centerPoint[1]],
          rotation: 0,
        },
        { ghost: true },
      );
    }
  }, [view, parcelPolygon, centerPoint, facadeEdgeIndex, placements, selectedIndex, drag, subbuildingById, canvasSize]);

  /* ---- キャンバス操作(選択・移動・回転) ---- */

  /** ポインタイベントからキャンバスローカル座標を取り出してワールド座標へ変換する */
  const eventToWorld = useCallback(
    (clientX: number, clientY: number): [number, number] | null => {
      const canvas = canvasRef.current;
      if (!canvas || !view) return null;
      const rect = canvas.getBoundingClientRect();
      const px = clientX - rect.left;
      const py = clientY - rect.top;
      if (px < 0 || py < 0 || px > rect.width || py > rect.height) return null;
      return view.toWorld(px, py);
    },
    [view],
  );

  const handleCanvasPointerDown = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      if (e.button !== 0 || !centerPoint) return;
      const world = eventToWorld(e.clientX, e.clientY);
      if (!world) return;
      const [wx, wz] = world;

      // 上に描かれているもの(配列の後方)を優先して当たり判定する
      let hitIndex: number | null = null;
      for (let i = placements.length - 1; i >= 0; i--) {
        if (hitTestPlacement(wx, wz, placements[i], centerPoint, subbuildingById.get(placements[i].subbuilding_id))) {
          hitIndex = i;
          break;
        }
      }

      if (hitIndex === null) {
        setSelectedIndex(null);
        return;
      }

      setSelectedIndex(hitIndex);
      e.currentTarget.setPointerCapture(e.pointerId);
      const placement = placements[hitIndex];
      if (e.shiftKey) {
        // 回転: 棟中心→カーソルの角度の変化量を初期回転へ加算する
        const cx = centerPoint[0] + placement.position[0];
        const cz = centerPoint[1] + placement.position[1];
        setDrag({
          kind: "rotate",
          index: hitIndex,
          startAngle: Math.atan2(wz - cz, wx - cx),
          origRotation: placement.rotation,
        });
      } else {
        setDrag({
          kind: "move",
          index: hitIndex,
          startWorld: [wx, wz],
          origPosition: [placement.position[0], placement.position[1]],
        });
      }
    },
    [centerPoint, eventToWorld, placements, subbuildingById],
  );

  const handleCanvasPointerMove = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      if (!drag || drag.kind === "new" || !centerPoint) return;
      const canvas = canvasRef.current;
      if (!canvas || !view) return;
      // ドラッグ中はキャンバス外へ出ても追従させたいため、範囲クランプせずに変換する
      const rect = canvas.getBoundingClientRect();
      const [wx, wz] = view.toWorld(e.clientX - rect.left, e.clientY - rect.top);

      if (drag.kind === "move") {
        const dx = wx - drag.startWorld[0];
        const dz = wz - drag.startWorld[1];
        setPlacements((prev) =>
          prev.map((p, i) =>
            i === drag.index
              ? { ...p, position: [drag.origPosition[0] + dx, drag.origPosition[1] + dz] }
              : p,
          ),
        );
      } else {
        const placement = placements[drag.index];
        if (!placement) return;
        const cx = centerPoint[0] + placement.position[0];
        const cz = centerPoint[1] + placement.position[1];
        const angle = Math.atan2(wz - cz, wx - cx);
        const deltaDeg = ((angle - drag.startAngle) * 180) / Math.PI;
        const rotation = Math.round(drag.origRotation + deltaDeg);
        setPlacements((prev) =>
          prev.map((p, i) => (i === drag.index ? { ...p, rotation } : p)),
        );
      }
    },
    [drag, centerPoint, view, placements],
  );

  const handleCanvasPointerUp = useCallback(
    (e: React.PointerEvent<HTMLCanvasElement>) => {
      if (drag && drag.kind !== "new") {
        e.currentTarget.releasePointerCapture(e.pointerId);
        setDrag(null);
      }
    },
    [drag],
  );

  /* ---- カードからの新規配置ドラッグ ---- */

  /**
   * カード上で pointerdown したらドラッグを開始し、window レベルの pointermove/up で追跡する。
   * カーソルが敷地キャンバス上にある間はゴーストを描き、キャンバス上で離したら配置を追加する。
   */
  const startNewDrag = useCallback((subbuildingId: number) => {
    setDrag({ kind: "new", subbuildingId, world: null });
  }, []);

  useEffect(() => {
    if (drag?.kind !== "new") return;

    const onPointerMove = (e: PointerEvent) => {
      const world = eventToWorld(e.clientX, e.clientY);
      setDrag((prev) => (prev?.kind === "new" ? { ...prev, world } : prev));
    };
    const onPointerUp = (e: PointerEvent) => {
      const world = eventToWorld(e.clientX, e.clientY);
      setDrag(null);
      if (world && centerPoint) {
        setPlacements((prev) => [
          ...prev,
          {
            subbuilding_id: drag.subbuildingId,
            position: [world[0] - centerPoint[0], world[1] - centerPoint[1]],
            rotation: 0,
          },
        ]);
        setSelectedIndex(placements.length);
      }
    };

    window.addEventListener("pointermove", onPointerMove);
    window.addEventListener("pointerup", onPointerUp);
    return () => {
      window.removeEventListener("pointermove", onPointerMove);
      window.removeEventListener("pointerup", onPointerUp);
    };
  }, [drag, eventToWorld, centerPoint, placements.length]);

  /* ---- 選択中の棟の削除(Delete キー / ボタン) ---- */

  const deleteSelected = useCallback(() => {
    if (selectedIndex === null) return;
    setPlacements((prev) => prev.filter((_, i) => i !== selectedIndex));
    setSelectedIndex(null);
  }, [selectedIndex]);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Delete" && e.key !== "Backspace") return;
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA")) return;
      deleteSelected();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [deleteSelected]);

  /* ---- 保存 ---- */

  /**
   * 配置リストを一括置換で送信し、Unity に session:reload で再描画を要求してから閉じる。
   * バックエンド側で全エントリが検証されるため、失敗時は既存配置が変更されないままになる。
   */
  const handleSave = useCallback(async () => {
    if (isSaving) return;
    setIsSaving(true);
    try {
      await fetchApi(`${baseUrl}/api/v1/buildings/${agentId}/sub-buildings`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          sub_buildings: placements.map((p) => ({
            subbuilding_id: p.subbuilding_id,
            position: p.position,
            rotation: p.rotation,
          })),
        }),
      });
      await sendSessionReload();
      onClose();
    } catch (err) {
      console.error("[MashupEdit] Failed to save sub-building layout:", err);
      let message = t("mashup_edit.saveFailed");
      if (err instanceof ApiError) {
        const i18nKey = err.code ? `errors.${err.code}` : null;
        message =
          i18nKey && i18nKey !== "errors.UNKNOWN_ERROR"
            ? t(i18nKey, { defaultValue: err.message })
            : err.message;
      } else if (err instanceof Error) {
        message = err.message;
      }
      void showNotification({ message, durationMs: 5000 });
    } finally {
      setIsSaving(false);
    }
  }, [isSaving, baseUrl, agentId, placements, onClose, t]);

  /* ---- レンダリング ---- */

  if (loadError) {
    return (
      <div className="me-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{loadError}</p>
      </div>
    );
  }

  if (!masterData || !isLoaded) {
    return <p className="me-muted">{t("common.loading")}</p>;
  }

  // カードの底面矩形は敷地キャンバスと同じ縮尺で描く(ビュー未確定の間は仮縮尺)
  const cardScale = view?.scale ?? 3;

  return (
    <div className="me-layout">
      <div className="me-header">
        <span className="me-header-name">{agentName}</span>
        <span className="me-header-hint">{t("mashup_edit.hint")}</span>
      </div>

      {/* サブ建物カード一覧 */}
      {subbuildingEntries.length === 0 ? (
        <div className="me-muted">{t("mashup_edit.noSubbuildings")}</div>
      ) : (
        <div className="me-cards">
          {subbuildingEntries.map(([id, entry]) => {
            const [width, height] = subbuildingSize(entry);
            return (
              <div
                key={id}
                className="me-card"
                onPointerDown={(e) => {
                  e.preventDefault();
                  startNewDrag(id);
                }}
              >
                <div className="me-card-rect-frame">
                  <div
                    className="me-card-rect"
                    style={{
                      width: Math.max(width * cardScale, 6),
                      height: Math.max(height * cardScale, 6),
                      borderColor: colorForSubbuilding(id),
                      background: `${colorForSubbuilding(id)}33`,
                    }}
                  />
                </div>
                <div className="me-card-name">{entry.display_name}</div>
                <div className="me-card-size">
                  {t("mashup_edit.sizeLabel", { width, height })}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* 敷地キャンバス */}
      <div className="me-canvas-container" ref={setCanvasContainerRef}>
        <canvas
          ref={canvasRef}
          className="me-canvas"
          style={{ width: canvasSize.width, height: canvasSize.height }}
          onPointerDown={handleCanvasPointerDown}
          onPointerMove={handleCanvasPointerMove}
          onPointerUp={handleCanvasPointerUp}
        />
      </div>

      {/* アクションボタン */}
      <div className="me-actions">
        <button
          className="me-btn"
          type="button"
          disabled={selectedIndex === null}
          onClick={deleteSelected}
        >
          {t("mashup_edit.deleteSelected")}
        </button>
        <div className="me-actions-spacer" />
        <button className="me-btn" type="button" onClick={onClose}>
          {t("mashup_edit.cancel")}
        </button>
        <button
          className="me-btn me-btn-confirm"
          type="button"
          disabled={isSaving}
          onClick={() => void handleSave()}
        >
          {t("mashup_edit.save")}
        </button>
      </div>
    </div>
  );
}
