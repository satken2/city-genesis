/**
 * パスの制限速度ブロック(パス詳細の交通・速度設定セクション内)
 *
 * 最終的に効いている制限速度を道路標識で示し、その右に次の行を並べる。
 * - 都市の制限速度: 規制ウィンドウでバリアントごとに決める値(GET /api/v1/city の max_speed)。参考として薄く出す
 * - この道路の制限速度: 入力欄。空欄なら都市の値に従い、数値を入れるとこの道路だけの個別指定
 *   (override_max_speed)になる。空欄に戻すと個別指定を解除し、バックエンドが都市の値へ戻す
 * - 通過速度: 形状から決まる速度(showBaseSpeed のときだけ)。制限速度が通過速度を大きく上回るほど
 *   事故が起きやすいため、制限速度と並べて見比べられるようにする
 *
 * 保存ボタンは持たない。入力が止まってから SAVE_DELAY_MS 待って自動で PATCH する(連続入力を1回にまとめる)。
 * フォーカスが外れたとき・Enter では待たずに反映する。標識は入力中の値を即座に映す
 * (個別指定は常に都市の値より優先されるため、反映前でも最終的な値が確定している)。
 * ただし入力が不正な間と反映に失敗したときは、サーバー上で実際に効いている値を映す。
 */

import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { PathData } from "../../types";
import { PATH_DEFAULT } from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { SpeedLimitSign } from "../common/SpeedLimitSign";
import "./PathSpeedLimitEditor.css";

/** 入力が止まってから反映するまでの待ち時間(ms) */
const SAVE_DELAY_MS = 800;

/** 入力できる制限速度の上限(km/h)。標識の白地に収まる3桁に合わせる */
const SPEED_LIMIT_INPUT_MAX = 999;

/** GET /api/v1/city のうち、ここで使う部分(キーは path_variant の文字列) */
type CityResponse = {
  city?: { max_speed?: Record<string, number> };
};

type ParsedSpeedInput =
  | { kind: "inherit" }
  | { kind: "override"; speed: number }
  | { kind: "invalid" };

type SaveStatus = "idle" | "saving" | "saved" | "error";

/** IMEで入った全角数字・全角ピリオドを半角へ寄せる */
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

/** 入力から送るべき個別指定を求める。空欄は null(都市の値に従う)、不正な入力は undefined(送らない) */
function overrideTargetOf(text: string): number | null | undefined {
  const parsed = parseSpeedInput(text);
  if (parsed.kind === "invalid") return undefined;
  return parsed.kind === "override" ? parsed.speed : null;
}

/** サーバー上の個別指定の値。個別指定なし(都市の値に従う)は null */
function savedOverrideOf(path: PathData): number | null {
  return path.override_max_speed && path.max_speed != null ? path.max_speed : null;
}

/**
 * 個別指定どうしが同じか。サーバーは制限速度を float32 で持つため、33.3 を送ると 33.29999… が返る。
 * 厳密に比べると「送信中に書き換えられた」と誤判定して送り直しが止まらなくなるので、許容差で比べる。
 */
function sameOverride(a: number | null, b: number | null): boolean {
  if (a == null || b == null) return a === b;
  return Math.abs(a - b) < 1e-3;
}

type PathSpeedLimitEditorProps = {
  path: PathData;
  apiBaseUrl: string;
  /** 通過速度の行を出すか(形状から通過速度を算出する種別だけ) */
  showBaseSpeed: boolean;
  onSaved: (updated: PathData) => void;
};

export function PathSpeedLimitEditor({ path, apiBaseUrl, showBaseSpeed, onSaved }: PathSpeedLimitEditorProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const inputId = useId();

  const [input, setInput] = useState(() => {
    const saved = savedOverrideOf(path);
    // float32 由来の端数(33.29999…)を落として入力欄に出す
    return saved != null ? String(Number(saved.toPrecision(6))) : "";
  });
  const [cityLimit, setCityLimit] = useState<number | null>(null);
  const [status, setStatus] = useState<SaveStatus>("idle");

  // 保存処理はタイマー・Enter・フォーカス喪失・送信完了後の再送から呼ばれるため、
  // 最新の入力とサーバー上の値は ref で持ち、どの呼び出し元から走っても同じ判断をさせる。
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
        // バックエンドの City.get_max_speed と同じく、バリアントの値が無ければ PATH_DEFAULT の値を使う。
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
    if (savingRef.current) return; // 送信中の変更は、送信完了後の再チェックで送られる
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
      // 失敗時は送り直さない(同じ内容で失敗し続けるため)。次の入力・フォーカス喪失で再送される。
      console.error("[PathSpeedLimitEditor] speed limit update failed:", err);
      setStatus("error");
      return;
    } finally {
      savingRef.current = false;
    }
    // 送信中に書き換えられていれば送り直し、そうでなければ反映済みにする。
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

  // 別のパスへ使い回されてアンマウントされるときに、待ち時間中の入力を取りこぼさない。
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
  // 標識に出す最終的な制限速度。個別指定は都市の値より常に優先する。空欄で都市の値が未取得の間は、
  // サーバー上で個別指定が無ければパスの値そのものが都市の値なのでそれを使う。
  // 入力が不正なときと反映に失敗したときは、サーバー上で実際に効いている値を出す。
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

        {showBaseSpeed && path.base_speed != null && (
          <>
            <span className="path-speed-limit__label">{t("path.baseSpeed")}</span>
            <span className="path-speed-limit__value">{formatSpeed(Math.round(path.base_speed))}</span>
          </>
        )}
      </div>
    </div>
  );
}
