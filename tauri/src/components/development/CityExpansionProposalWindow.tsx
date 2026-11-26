/**
 * エポック終了時の自動都市拡張提案ウィンドウ。
 * 民間建設できる空き地が少ないときに画面中央へ開き、拡張する幹線・路地・土地区画を
 * 2D地図上にプレビュー表示する。OK を押すとそのまま道路の計画登録と区画作成を実行する。
 *
 * ウィンドウは閉じても再利用されるため、proposal_id が変わるたびに内部状態と地図表示範囲を初期化する。
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useMasterData } from "../../contexts/MasterDataContext";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { sendSessionReload } from "../../utils/ipc";
import { resolveIntlLocale } from "../../utils/locale";
import { formatExactAmount } from "../../utils/money";
import { CityMapCanvas, type CityMapCanvasHandle, type CityMapViewState } from "../map/CityMapCanvas";
import { drawDevelopmentOverlay, type PreviewRoad } from "./developmentMapOverlay";
import "./CityExpansionProposalWindow.css";

export type ExpansionProposal = {
  proposal_id: string;
  roads: PreviewRoad[];
  parcels: [number, number][][];
  blocks_added: number;
  estimated_cost: number;
  bounds: [number, number, number, number];
  vacant_parcels: number;
  total_parcels: number;
  vacant_area_m2: number;
  cooldown_epochs: number;
};

type Props = {
  apiBaseUrl: string;
  proposal?: ExpansionProposal | null;
  onClose: () => void | Promise<void>;
};

const PROPOSAL_MAP_LAYERS = {
  elevation: true,
  parcels: true,
  buildings: true,
  allRoads: true,
};

function paddedBounds(bounds: [number, number, number, number]) {
  const [x1, z1, x2, z2] = bounds;
  const span = Math.max(240, x2 - x1, z2 - z1);
  const pad = Math.max(120, span * 0.35);
  return {
    x1: x1 - pad,
    z1: z1 - pad,
    x2: x2 + pad,
    z2: z2 + pad,
  };
}

export function CityExpansionProposalWindow({ apiBaseUrl, proposal, onClose }: Props) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const locale = resolveIntlLocale(i18n.language);
  const mapRef = useRef<CityMapCanvasHandle>(null);

  const [suppress, setSuppress] = useState(false);
  const [submitting, setSubmitting] = useState<"apply" | "dismiss" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);
  const proposalId = proposal?.proposal_id;

  useEffect(() => {
    setSuppress(false);
    setSubmitting(null);
    setError(null);
    if (!proposal) return;
    mapRef.current?.reload();
    const target = paddedBounds(proposal.bounds);
    // 再利用直後・初回マウント直後のどちらでもレイアウト確定後に範囲へ合わせる。
    const timer = window.setTimeout(() => {
      mapRef.current?.fitBounds(target);
      setOverlayVersion((v) => v + 1);
    }, 60);
    return () => window.clearTimeout(timer);
  }, [proposalId, proposal]);

  const overlayDraw = useCallback(
    (ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
      if (!proposal) return;
      drawDevelopmentOverlay(ctx, view, {
        plan: { roads: proposal.roads, parcels: proposal.parcels },
        masterData,
      });
    },
    [proposal, masterData],
  );

  useEffect(() => setOverlayVersion((v) => v + 1), [overlayDraw]);

  const handleDismiss = useCallback(async () => {
    if (submitting !== null) return;
    setSubmitting("dismiss");
    setError(null);
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city-development/expansion-proposal/dismiss"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ proposal_id: proposalId ?? null, suppress }),
      });
    } catch (err) {
      // 見送り通信に失敗してもウィンドウ自体は閉じて操作を妨げない。
      console.error("failed to dismiss expansion proposal", err);
    } finally {
      setSubmitting(null);
    }
    await onClose();
  }, [apiBaseUrl, onClose, proposalId, submitting, suppress]);

  const handleApply = useCallback(async () => {
    if (!proposal || submitting !== null) return;
    setSubmitting("apply");
    setError(null);
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city-development/expansion-proposal/apply"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ proposal_id: proposal.proposal_id }),
      });
      await sendSessionReload().catch(console.error);
      setSubmitting(null);
      await onClose();
    } catch (err) {
      setError(describeError(err, t));
      setSubmitting(null);
    }
  }, [apiBaseUrl, onClose, proposal, submitting, t]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || submitting !== null) return;
      event.preventDefault();
      void handleDismiss();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [handleDismiss, submitting]);

  if (!proposal) {
    return (
      <div className="expansion-proposal expansion-proposal--empty">
        <p>{t("common.noData")}</p>
        <div className="expansion-proposal__actions">
          <button type="button" onClick={() => void onClose()}>{t("common.close")}</button>
        </div>
      </div>
    );
  }

  const busy = submitting !== null;
  const costText = formatExactAmount(Math.round(proposal.estimated_cost), locale, true, { maximumFractionDigits: 0 });

  return (
    <div className="expansion-proposal">
      <header className="expansion-proposal__header">
        <p className="expansion-proposal__message">
          {t("cityDevelopment.proposal.message")}
        </p>
        <div className="expansion-proposal__summary">
          <span>
            {t("cityDevelopment.proposal.vacantInfo", {
              vacant: proposal.vacant_parcels,
              total: proposal.total_parcels,
            })}
          </span>
          <span>
            {t("cityDevelopment.proposal.planInfo", {
              blocks: proposal.blocks_added,
              roads: proposal.roads.length,
              parcels: proposal.parcels.length,
              cost: costText,
            })}
          </span>
        </div>
      </header>

      <div className="expansion-proposal__map">
        <CityMapCanvas
          ref={mapRef}
          apiBaseUrl={apiBaseUrl}
          layers={PROPOSAL_MAP_LAYERS}
          interactive
          showCompass
          overlayVersion={overlayVersion}
          overlayDraw={overlayDraw}
        />
        <div className="expansion-proposal__legend" aria-hidden={true}>
          <span className="expansion-proposal__legend-item">
            <span className="expansion-proposal__swatch expansion-proposal__swatch--arterial" />
            {t("cityDevelopment.proposal.legendArterial")}
          </span>
          <span className="expansion-proposal__legend-item">
            <span className="expansion-proposal__swatch expansion-proposal__swatch--lane" />
            {t("cityDevelopment.proposal.legendLane")}
          </span>
          <span className="expansion-proposal__legend-item">
            <span className="expansion-proposal__swatch expansion-proposal__swatch--parcel" />
            {t("cityDevelopment.proposal.legendParcel")}
          </span>
        </div>
      </div>

      {error && (
        <div className="expansion-proposal__error" role="alert">
          {error}
        </div>
      )}

      <footer className="expansion-proposal__footer">
        <label className="expansion-proposal__suppress">
          <input
            type="checkbox"
            checked={suppress}
            disabled={busy}
            onChange={(e) => setSuppress(e.currentTarget.checked)}
          />
          <span>
            {t("cityDevelopment.proposal.suppressLabel", { epochs: proposal.cooldown_epochs })}
          </span>
        </label>
        <div className="expansion-proposal__actions">
          <button
            type="button"
            disabled={busy}
            onClick={() => void handleDismiss()}
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            className="expansion-proposal__primary"
            disabled={busy}
            onClick={() => void handleApply()}
          >
            {submitting === "apply" ? t("cityDevelopment.creating") : t("common.ok")}
          </button>
        </div>
      </footer>
    </div>
  );
}
