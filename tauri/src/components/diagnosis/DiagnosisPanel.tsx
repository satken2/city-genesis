/**
 * 診断ウィンドウ(window_type="diagnosis")
 *
 * 経済主体(施設・公共交通路線)の不調について、バックエンドの診断API
 * (GET /api/v1/agents/{id}/diagnosis)が返す因果の木を「症状 → 直接の原因 → その原因 → …」
 * の順に縦に並べて表示する。木の各ノードは type と params だけを持ち、文言はここで
 * i18n キー diagnosis.nodes.<type> から組み立てる(L1通知と同じ方式)。
 *
 * ノードが指す対象(targets)は、施設なら詳細パネルを開くリンク、パスならパス詳細を開く
 * リンク、ノード(接続点・途切れ箇所)は位置のチップとして表示する。あわせて、木に現れた
 * パスと施設を Unity の3Dビュー上でハイライトし(発信元 "diagnosis")、ウィンドウを閉じると
 * Rust 側(window.rs)がハイライトを消す。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { sendHighlightObjects, sendClearHighlights } from "../../utils/ipc";
import "../agent/AgentDetailPanel.css";
import "./DiagnosisPanel.css";

/** ハイライトの発信元。Unity はソースごとに独立レイヤーで保持する */
const HIGHLIGHT_SOURCE = "diagnosis";

type DiagnosisTarget = {
  kind: "agent" | "path" | "node";
  id: number;
  name?: string;
  position?: [number, number] | null;
};

type DiagnosisNode = {
  type: string;
  params: Record<string, unknown>;
  targets: DiagnosisTarget[];
  children: DiagnosisNode[];
};

type DiagnosisResponse = {
  agent_id: number;
  name: string;
  epoch: number;
  roots: DiagnosisNode[];
};

type DiagnosisPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/** 木を深さ優先で走査し、全ノードを1つの配列に集める */
function flatten(nodes: DiagnosisNode[], out: DiagnosisNode[] = []): DiagnosisNode[] {
  for (const node of nodes) {
    out.push(node);
    flatten(node.children, out);
  }
  return out;
}

export function DiagnosisPanel({ apiBaseUrl, pathTemplate, initialEntityId }: DiagnosisPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [data, setData] = useState<DiagnosisResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);
    fetchApi<DiagnosisResponse>(url, { signal: controller.signal })
      .then((response) => {
        if (controller.signal.aborted) return;
        setData(response);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Unknown error");
        setLoading(false);
      });
    return () => controller.abort();
  }, [apiBaseUrl, pathTemplate, initialEntityId, refreshKey]);

  // 木に現れたパスと施設を3Dビューでハイライトする。施設IDは建物の agent_id として
  // 渡し、建物を持たない主体(路線など)のIDはUnity側で単に無視される。
  useEffect(() => {
    if (!data) return;
    const pathIds = new Set<number>();
    const agentIds = new Set<number>();
    for (const node of flatten(data.roots)) {
      for (const target of node.targets) {
        if (target.kind === "path") pathIds.add(target.id);
        if (target.kind === "agent") agentIds.add(target.id);
      }
    }
    if (pathIds.size === 0 && agentIds.size === 0) return;
    void sendHighlightObjects(
      {
        parcels: { blocking: [], removable: [], shavable: [], exempt: [] },
        building_ids: Array.from(agentIds),
        colliding_path_ids: Array.from(pathIds),
      },
      HIGHLIGHT_SOURCE,
    );
    return () => {
      void sendClearHighlights(HIGHLIGHT_SOURCE);
    };
  }, [data]);

  /** params を表示用に整える(製品名の解決・数値の丸め) */
  function displayParams(params: Record<string, unknown>): Record<string, unknown> {
    const out: Record<string, unknown> = { ...params };
    const productId = params.product_id;
    if (typeof productId === "number") {
      const product = masterData?.products[productId];
      out.product_name = product?.display_name ?? params.product_name ?? String(productId);
    }
    for (const key of ["supply", "demand", "received", "required", "quota", "total_labor"]) {
      if (typeof out[key] === "number") out[key] = Math.round(out[key] as number).toLocaleString();
    }
    if (typeof out.ratio === "number") out.ratio = Math.round((out.ratio as number) * 100);
    if (typeof out.unemployment_rate === "number") {
      out.unemployment_rate = ((out.unemployment_rate as number) * 100).toFixed(1);
    }
    if (typeof out.distance === "number") out.distance = Math.round(out.distance as number);
    return out;
  }

  function openWindow(windowType: string, entityId: number) {
    void invoke("open_registered_window", { windowType, entityId: String(entityId) }).catch((err) => {
      console.error("failed to open window", err);
    });
  }

  function renderTarget(target: DiagnosisTarget, index: number) {
    if (target.kind === "agent") {
      return (
        <button
          key={`a${target.id}-${index}`}
          className="entity-link"
          type="button"
          onClick={() => openWindow("agent_detail", target.id)}
        >
          {target.name || t("diagnosis.target.agent", { id: target.id })}
        </button>
      );
    }
    if (target.kind === "path") {
      return (
        <button
          key={`p${target.id}-${index}`}
          className="entity-link"
          type="button"
          onClick={() => openWindow("path_detail", target.id)}
        >
          {t("diagnosis.target.path", { id: target.id })}
        </button>
      );
    }
    const pos = target.position;
    return (
      <span key={`n${target.id}-${index}`} className="diagnosis-node-chip">
        {pos
          ? t("diagnosis.target.nodeAt", { x: Math.round(pos[0]), z: Math.round(pos[1]) })
          : t("diagnosis.target.node", { id: target.id })}
      </span>
    );
  }

  function renderNode(node: DiagnosisNode, depth: number, key: string) {
    const params = displayParams(node.params);
    const title = t(`diagnosis.nodes.${node.type}.title`, { ...params, defaultValue: node.type });
    const description = t(`diagnosis.nodes.${node.type}.description`, { ...params, defaultValue: "" });
    return (
      <div key={key} className="diagnosis-node" style={{ marginLeft: Math.min(depth, 6) * 14 }}>
        <div className={`diagnosis-card diagnosis-card--depth${Math.min(depth, 3)}`}>
          <div className="diagnosis-card-title">
            {depth > 0 && <span className="diagnosis-arrow">↳</span>}
            {title}
          </div>
          {description && <div className="diagnosis-card-desc">{description}</div>}
          {node.targets.length > 0 && (
            <div className="diagnosis-targets">{node.targets.map(renderTarget)}</div>
          )}
        </div>
        {node.children.map((child, i) => renderNode(child, depth + 1, `${key}-${i}`))}
      </div>
    );
  }

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }
  if (error || !data) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  return (
    <div className="agent-detail diagnosis-panel">
      <div className="diagnosis-header">
        <div className="diagnosis-header-name">{data.name}</div>
        <button
          className="diagnosis-refresh-btn"
          type="button"
          onClick={() => setRefreshKey((k) => k + 1)}
        >
          {t("diagnosis.refresh")}
        </button>
      </div>
      <p className="diagnosis-note">{t("diagnosis.note")}</p>
      {data.roots.length === 0 ? (
        <div className="diagnosis-card diagnosis-card--ok">
          <div className="diagnosis-card-title">{t("diagnosis.noProblem.title")}</div>
          <div className="diagnosis-card-desc">{t("diagnosis.noProblem.description")}</div>
        </div>
      ) : (
        data.roots.map((root, i) => renderNode(root, 0, `r${i}`))
      )}
    </div>
  );
}
