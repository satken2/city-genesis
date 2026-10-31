/**
 * Path speed-limit block (inside path detail traffic/speed settings).
 *
 * Show the effective speed limit as a road sign, with these rows beside it:
 * - City speed limit: per-variant regulation value (max_speed from GET /api/v1/city), dimmed as a reference.
 * - This road's speed limit: an input. Empty inherits the city value; a number sets override_max_speed for this road.
 *   Clearing removes the override and lets the backend restore the city value.
 * - Passing speed: shape-derived speed. Accident risk rises as the limit exceeds passing speed, so place them
 *   together for comparison.
 *
 * No save button. Automatically PATCH after SAVE_DELAY_MS of inactivity to coalesce typing.
 * Apply immediately on blur or Enter. The sign immediately reflects input (overrides always take precedence over
 * city values, so the eventual effective value is already known). For invalid input or failed application,
 * show the value actually effective on the server.
 */

import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { PATH_DEFAULT } from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { SpeedLimitSign } from "../common/SpeedLimitSign";
import "./PathSpeedLimitEditor.css";

/** Delay after typing stops before applying (ms). */
const SAVE_DELAY_MS = 800;

/** Maximum input speed limit (km/h). Three digits to fit the sign's white area. */
const SPEED_LIMIT_INPUT_MAX = 999;

/** Fields used here from GET /api/v1/city (keys are string path_variant values). */
type CityResponse = {
  city?: { max_speed?: Record<string, number> };
};

type ParsedSpeedInput =
  | { kind: "inherit" }
  | { kind: "override"; speed: number }
  | { kind: "invalid" };

type SaveStatus = "idle" | "saving" | "saved" | "error";

/** Normalize IME full-width digits and periods to ASCII. */
function toHalfWidth(text: string): string {
  return text.replace(/[０-９．]/g, (ch) => String.fromCharCode(ch.charCodeAt(0) - 0xfee0));
}

function parseSpeedInput(text: string): ParsedSpeedInput {
  const trimmed = text.trim();
  if (trimmed === "") return { kind: "inherit" };
  if (!/^\d+(\.\d+)?$/.test(trimmed)) return { kind: "invalid" };
  const speed = Number(trimmed);
  return speed <= SPEED_LIMIT_INPUT_MAX ? { kind: "override", speed } : { kind: "invalid" };
}

/** Derive the override to send from input. Empty → null (inherit city), invalid → undefined (do not send). */
function overrideTargetOf(text: string): number | null | undefined {
  const parsed = parseSpeedInput(text);
  if (parsed.kind === "invalid") return undefined;
  return parsed.kind === "override" ? parsed.speed : null;
}

/** Server-side override. null means no override (inherit the city value). */
function savedOverrideOf(path: PathData): number | null {
  return path.override_max_speed && path.max_speed != null ? path.max_speed : null;
}

/**
 * Compare overrides. The server stores speed limits as float32, so sending 33.3 can return 33.29999….
 * Strict equality would incorrectly detect edits during sending and trigger endless resends; compare with tolerance.
 */
function sameOverride(a: number | null, b: number | null): boolean {
  if (a == null || b == null) return a === b;
  return Math.abs(a - b) < 1e-3;
}

type PathSpeedLimitEditorProps = {
  path: PathData;
  apiBaseUrl: string;
  onSaved: (updated: PathData) => void;
};

export function PathSpeedLimitEditor({ path, apiBaseUrl, onSaved }: PathSpeedLimitEditorProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const inputId = useId();

  const [input, setInput] = useState(() => {
    const saved = savedOverrideOf(path);
    // Remove float32 artifacts (33.29999…) before displaying in the input.
    return saved != null ? String(Number(saved.toPrecision(6))) : "";
  });
  const [cityLimit, setCityLimit] = useState<number | null>(null);
  const [status, setStatus] = useState<SaveStatus>("idle");

  // Saving is called by timers, Enter, blur, and resends after completion, so keep current input and server values
  // in refs to make the same decision regardless of the caller.
  const latestInputRef = useRef(input);
  const savedOverrideRef = useRef<number | null>(savedOverrideOf(path));
  const savingRef = useRef(false);
  const timerRef = useRef<number | null>(null);
  const saveRef = useRef<() => Promise<void>>(async () => {});

  const pathId = path.path_id;
  const pathVariant = path.path_variant;

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<CityResponse>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), { signal: controller.signal })
      .then((res) => {
        // As in backend City.get_max_speed, fall back to PATH_DEFAULT when no value exists for the variant.
        const table = res.city?.max_speed ?? {};
        const value = table[String(pathVariant)] ?? table[String(PATH_DEFAULT)];
        setCityLimit(typeof value === "number" ? value : null);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) console.error("[PathSpeedLimitEditor] city fetch failed:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl, pathVariant]);

  const save = useCallback(async () => {
    if (savingRef.current) return; // Changes made during sending are sent by the post-completion recheck.
    const target = overrideTargetOf(latestInputRef.current);
    if (target === undefined || sameOverride(target, savedOverrideRef.current)) return;

    savingRef.current = true;
    setStatus("saving");
    try {
      const updated = await fetchApi<PathData>(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathId)), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(
          target == null ? { override_max_speed: false } : { override_max_speed: true, max_speed: target },
        ),
      });
      savedOverrideRef.current = savedOverrideOf(updated);
      onSaved(updated);
    } catch (err) {
      // Do not retry failures immediately (the same payload would keep failing). Retry on the next input or blur.
      console.error("[PathSpeedLimitEditor] speed limit update failed:", err);
      setStatus("error");
      return;
    } finally {
      savingRef.current = false;
    }
    // Resend if edited during sending; otherwise mark applied.
    const latest = overrideTargetOf(latestInputRef.current);
    if (latest !== undefined && !sameOverride(latest, savedOverrideRef.current)) {
      void saveRef.current();
    } else {
      setStatus("saved");
    }
  }, [apiBaseUrl, pathId, onSaved]);

  useEffect(() => {
    saveRef.current = save;
  }, [save]);

  const flush = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
    void saveRef.current();
  }, []);

  // Do not lose pending input when unmounting for reuse with a different path.
  useEffect(() => flush, [flush]);

  const handleChange = (text: string) => {
    const next = toHalfWidth(text);
    latestInputRef.current = next;
    setInput(next);
    setStatus("idle");
    if (timerRef.current !== null) window.clearTimeout(timerRef.current);
    timerRef.current = window.setTimeout(() => {
      timerRef.current = null;
      void saveRef.current();
    }, SAVE_DELAY_MS);
  };

  const parsed = parseSpeedInput(input);
  // Effective limit shown on the sign. Overrides always take precedence over city values. While input is empty and city data is loading,
  // use the path value if the server has no override, because it already equals the city value.
  // For invalid input or failed application, show the value actually effective on the server.
  const effectiveSpeed =
    parsed.kind === "invalid" || status === "error"
      ? path.max_speed
      : parsed.kind === "override"
        ? parsed.speed
        : cityLimit ?? (path.override_max_speed ? null : path.max_speed);

  const formatSpeed = (speed: number) => `${speed.toLocaleString(locale, { maximumFractionDigits: 1 })} km/h`;

  const statusText =
    parsed.kind === "invalid"
      ? t("path.speedLimitInvalid", { max: SPEED_LIMIT_INPUT_MAX })
      : status === "saving"
        ? t("path.speedLimitSaving")
        : status === "saved"
          ? t("path.speedLimitSaved")
          : status === "error"
            ? t("path.speedLimitSaveFailed")
            : "";
  const statusModifier = parsed.kind === "invalid" || status === "error" ? "error" : status;

  return (
    <div className="path-speed-limit">
      <SpeedLimitSign
        speed={effectiveSpeed}
        label={
          effectiveSpeed != null
            ? t("path.speedLimitSign", { speed: formatSpeed(effectiveSpeed) })
            : t("path.maxSpeed")
        }
      />
      <div className="path-speed-limit__rows">
        <span className="path-speed-limit__label path-speed-limit__label--reference">
          {t("path.citySpeedLimit")}
        </span>
        <span className="path-speed-limit__value path-speed-limit__value--reference">
          {cityLimit != null ? formatSpeed(cityLimit) : "—"}
        </span>

        <label className="path-speed-limit__label" htmlFor={inputId}>
          {t("path.roadSpeedLimit")}
        </label>
        <span className="path-speed-limit__value">
          <input
            id={inputId}
            type="text"
            inputMode="decimal"
            autoComplete="off"
            spellCheck={false}
            className="path-speed-limit__input"
            value={input}
            placeholder={t("path.roadSpeedLimitUnset")}
            title={t("path.roadSpeedLimitHelp")}
            aria-invalid={parsed.kind === "invalid"}
            onChange={(e) => handleChange(e.target.value)}
            onBlur={flush}
            onKeyDown={(e) => {
              if (e.key === "Enter") flush();
            }}
          />
          <span className="path-speed-limit__unit">km/h</span>
          <span className={`path-speed-limit__status path-speed-limit__status--${statusModifier}`} aria-live="polite">
            {statusText}
          </span>
        </span>

        {path.base_speed != null && (
          <>
            <span className="path-speed-limit__label">{t("path.baseSpeed")}</span>
            <span className="path-speed-limit__value">{formatSpeed(Math.round(path.base_speed))}</span>
          </>
        )}
      </div>
    </div>
  );
}
