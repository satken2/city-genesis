/**
 * エポック実行確認ウィンドウ
 *
 * シミュレーション実行前に現在のゲーム内日付と実行後の日付を表示し、
 * ユーザーの最終確認を行うポップアップウィンドウ。
 * タイトルバーを持たないウィンドウ(レジストリの chromeless)で、閉じるのは「いいえ」と Esc。実行中は閉じない。
 * ウィンドウの高さは内容に合わせる(文字サイズの設定と言語で内容の高さが変わり、固定の高さでは余白が残るか溢れるため)。
 * 変えるのは大きさだけで、位置は Rust 側が決めたまま動かさない。
 *
 * エポック数セレクターにより1〜10エポックの連続実行が可能。
 * 複数エポックを選択した場合は「実行後の日付」がその分だけ先の日付になる。
 * 2つの日付の間は季節の帯でつなぎ、これから進む期間が通る季節を色で示す(季節の区切りは `utils/season`)。
 * テンプレートモード(シナリオ制作)では上限を外し、数値を直接入力できる
 * (都市を育てるために数百エポックを一気に回す用途。1回のHTTPリクエストで完了まで待つ)。
 *
 * 確定直後にHTTP実行と日没演出を並行して開始する。確認ウィンドウは非表示で実行を維持する。
 * 日没1.5秒・暗転0.5秒の描画完了後、実行済みなら結果へ、未完了ならTipsへ進む。
 * Tipsは実行完了時に自動で閉じる。結果画面を閉じるまでは背景を黒く保ち、HUDも無効にする。
 * 朝への復帰は結果画面の共通クローズ処理が担当する(×・閉じるボタンの両方)。
 *
 * エポック実行完了後は、結果や達成クエストを**別の独立したウィンドウ**として開く
 * (HUDから他のウィンドウを開くのと同じ open_registered_window 経由)。
 * こうすることで各ウィンドウは window-registry のサイズで Rust 側により中央配置される。
 * 1. epoch_result ウィンドウ … レスポンスに epoch_report が含まれる場合。達成クエストも引き継ぐ
 *    (結果を確認して閉じた後、notify="modal" のクエストがあればお祝いウィンドウが続く)。
 * 2. quest_complete ウィンドウ … レポートが無く notify="modal" のクエストが1件以上ある場合。
 * また、notify="toast" の達成クエストと期限切れで失敗したクエスト(failed_quests)は、
 * ウィンドウを開かず通知ポップアップ(showNotification)で軽く知らせる。
 * いずれの場合も、確認ウィンドウ自身は役目を終えて onClose で閉じる。
 *
 * バックエンドが実行を拒否した場合(403=Steam未認証のエポック上限、409 EPOCH_ALREADY_RUNNING=別の実行の最中、
 * 503=シャットダウン中 等)や通信エラーの場合は、演出とタスクバー無効化を解除して確認ウィンドウを再表示する。
 * エラーレスポンス時はさらに、その理由を通知ポップアップで表示する(コアの本文は英語固定なので、
 * 理由の分かる拒否は専用の翻訳文にする)。
 */

import { useEffect, useRef, useState, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow, LogicalSize } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { sendSessionReload } from "../../utils/ipc";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { beginEpochPresentation } from "../../utils/epochPresentation";
import { runEpochWithPresentation } from "../../utils/epochFlow";
import type { EpochReport } from "./EpochResultWindow";
import { logInfoToFile } from "../../utils/log";
import { showNotification } from "../../utils/notification";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale, resolveLocalizedText } from "../../utils/locale";
import { useTemplateMode } from "../../hooks/useTemplateMode";
import { useFontSize } from "../../contexts/FontSizeContext";
import type { QuestNotification } from "../../types";
import { SaveDialogButton } from "./SaveDialog";
import { ClockIcon, MinusIcon, PlusIcon, SeasonIcon } from "./SaveIcons";
import { DAYS_PER_YEAR, SEASON_STARTS, dayOfYear, seasonOf, type Season } from "../../utils/season";
import "./EpochConfirmWindow.css";

type CityData = {
  name: string;
  current_date: number;
  simulation_days: number;
};

function formatDate(daysSinceEpoch: number, locale: string): string {
  return formatEpochDate(daysSinceEpoch, locale);
}

type EpochConfirmWindowProps = {
  apiBaseUrl: string;
  onClose: () => void;
};

type EpochResponse = {
  epoch?: number;
  current_date: number;
  epoch_report?: EpochReport;
  completed_quests?: QuestNotification[];
  failed_quests?: QuestNotification[];
};

/** 選択可能なエポック数の上限(通常プレイ)。テンプレートモードでは適用しない */
const MAX_EPOCH_COUNT = 10;

/** テンプレートモードで直接入力できるエポック数の上限(誤入力で数万エポック回すのを防ぐ安全弁) */
const TEMPLATE_MAX_EPOCH_COUNT = 10000;

/** 季節の帯で、季節の境目をぼかす幅の半分(日) */
const SEASON_BLEND_DAYS = 8;

/**
 * 季節の帯の塗り。1年分の季節の色の並びを繰り返し、帯の全長を実行する日数(CSS変数 --epoch-confirm-span)として
 * 位置を決める。日数をCSS変数のまま渡すので、エポック数を変えると帯の縮尺がなめらかに変わる。
 */
function seasonBandGradient(currentDate: number): string {
  const start = dayOfYear(currentDate);
  const at = (day: number) => `calc(${day - start} / var(--epoch-confirm-span) * 100%)`;
  const color = (season: Season) => `var(--epoch-confirm-${season})`;
  let previous: Season = "winter";
  const stops = [`${color(previous)} ${at(0)}`];
  for (const { season, day } of SEASON_STARTS) {
    stops.push(`${color(previous)} ${at(day - SEASON_BLEND_DAYS)}`, `${color(season)} ${at(day + SEASON_BLEND_DAYS)}`);
    previous = season;
  }
  stops.push(`${color(previous)} ${at(DAYS_PER_YEAR)}`);
  return `repeating-linear-gradient(to bottom, ${stops.join(", ")})`;
}

/** 帯の端に置く季節の印。都市を読み込むまでは季節を出さない */
function SeasonBadge({ className, season }: { className: string; season?: Season }) {
  return (
    <span className={`epoch-confirm-badge ${className} epoch-confirm-badge--${season ?? "none"}`} aria-hidden={true}>
      {season && <SeasonIcon season={season} />}
    </span>
  );
}

export function EpochConfirmWindow({ apiBaseUrl, onClose }: EpochConfirmWindowProps) {
  const { i18n, t } = useTranslation();
  const [city, setCity] = useState<CityData | null>(null);
  const [executing, setExecuting] = useState(false);
  const executingRef = useRef(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const [epochCount, setEpochCount] = useState(1);
  const templateMode = useTemplateMode(apiBaseUrl);
  const { fontSize } = useFontSize();
  const maxEpochCount = templateMode ? TEMPLATE_MAX_EPOCH_COUNT : MAX_EPOCH_COUNT;

  /** 直接入力(テンプレートモード)。空欄や範囲外は1〜上限に丸める */
  function handleCountInput(raw: string) {
    const parsed = Number.parseInt(raw, 10);
    if (Number.isNaN(parsed)) {
      setEpochCount(1);
      return;
    }
    setEpochCount(Math.max(1, Math.min(maxEpochCount, parsed)));
  }

  useEffect(() => {
    fetchApi<{ city: CityData }>(buildApiUrl(apiBaseUrl, "/api/v1/city"))
      .then((data) => setCity(data.city))
      .catch(console.error);
  }, [apiBaseUrl]);

  // 高さは内容が変わったときだけ測り、ウィンドウの大きさの変化には反応しない。閉じたウィンドウは
  // このパネルがマウントされたまま別のポップアップへ転用されるので、大きさの変化を監視すると転用先の大きさを書き換えてしまう。
  useEffect(() => {
    const root = rootRef.current;
    const shell = root?.closest<HTMLElement>(".popup-shell");
    if (!root || !shell) return;
    const border = shell.offsetHeight - shell.clientHeight;
    const height = Math.min(root.scrollHeight + border, window.screen.availHeight || Infinity);
    if (Math.abs(height - window.innerHeight) < 1) return;
    void getCurrentWindow().setSize(new LogicalSize(window.innerWidth, height)).catch(console.error);
  }, [city, epochCount, templateMode, i18n.language, fontSize]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || executingRef.current) return;
      event.preventDefault();
      onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  const locale = resolveIntlLocale(i18n.language);
  const currentDateStr = city ? formatDate(city.current_date, locale) : "—";
  const nextDateStr = city
    ? formatDate(city.current_date + city.simulation_days * epochCount, locale)
    : "—";

  async function handleConfirm() {
    if (executingRef.current) return;
    executingRef.current = true;
    setExecuting(true);
    logInfoToFile(`epoch confirm: start count=${epochCount}`);
    let presentation: Awaited<ReturnType<typeof beginEpochPresentation>> | undefined;
    let simulationCompleted = false;
    try {
      presentation = await beginEpochPresentation();
      void getCurrentWindow().hide().catch(console.error);
      const url = buildApiUrl(apiBaseUrl, `/api/v1/epochs?count=${epochCount}`);
      logInfoToFile(`epoch confirm: POST ${url}`);
      const data = await runEpochWithPresentation(
        () => fetchApi<EpochResponse>(url, { method: "POST" }),
        presentation.darkened,
        presentation.setTipsVisible,
      );
      simulationCompleted = true;
      if (!await presentation.isCurrent()) return;
      await sendSessionReload().catch(console.error);
      logInfoToFile(`epoch confirm: done epoch=${data.epoch ?? "?"}`);

      const quests: QuestNotification[] = data.completed_quests ?? [];
      const failedQuests: QuestNotification[] = data.failed_quests ?? [];
      const modalQuests = quests.filter((q) => q.notify === "modal");
      const toastQuests = quests.filter((q) => q.notify === "toast");

      // notify="toast" の達成クエストはウィンドウを開かず通知ポップアップで軽く知らせる。
      // 期限切れで失敗したクエストも同様に通知する(お祝いではないため常にトースト)。
      for (const q of toastQuests) {
        void showNotification({
          message: t("questComplete.toastCompleted", { name: resolveLocalizedText(q.name, i18n.language) }),
          durationMs: 5000,
        });
      }
      for (const q of failedQuests) {
        void showNotification({
          message: t("questComplete.toastFailed", { name: resolveLocalizedText(q.name, i18n.language) }),
          durationMs: 5000,
        });
      }

      // 結果レポートがあれば結果ウィンドウを新規に開く。達成クエストは結果ウィンドウを
      // 閉じた後に表示するため、一緒に渡して引き継がせる。
      if (data.epoch_report) {
        await invoke("open_registered_window", {
          windowType: "epoch_result",
          params: {
            extra: {
              epochPresentationId: presentation.id,
              report: data.epoch_report,
              epochCount,
              currentDate: Number(data.current_date),
              completedQuests: quests,
              failedQuests,
            },
          },
        });
        logInfoToFile("epoch confirm: epoch_result window opened");
      } else if (modalQuests.length > 0) {
        // レポートが無くお祝い対象のクエストだけある場合は、クエストウィンドウを直接開く。
        await invoke("open_registered_window", {
          windowType: "quest_complete",
          params: { extra: { quests, openedAt: Date.now() } },
        });
      }

      if (!data.epoch_report) await presentation.finish();

      // 確認ウィンドウは役目を終えたので閉じる。
      onClose();
    } catch (error) {
      console.error("failed to execute epoch", error);
      if (presentation && !await presentation.isCurrent().catch(() => true)) return;
      await presentation?.finish().catch(console.error);
      // 実行済みの処理を、結果表示の失敗だけで再実行させない。
      if (simulationCompleted) onClose();
      else await getCurrentWindow().show().catch(console.error);
      void showNotification({
        message: error instanceof ApiError && error.status === 403
          ? t("epochConfirm.errorSteamLimit")
          : error instanceof ApiError && error.code === "EPOCH_ALREADY_RUNNING"
            ? t("epochConfirm.errorAlreadyRunning")
            : t("epochConfirm.errorFailed", { reason: error instanceof Error ? error.message : String(error) }),
        durationMs: 8000,
      });
      setExecuting(false);
      executingRef.current = false;
    }
  }

  return (
    <div className="epoch-confirm" ref={rootRef}>
      <header className="epoch-confirm-header">
        <h1 className="epoch-confirm-title"><ClockIcon /><span>{t("windowType.epoch_confirm")}</span></h1>
        <p className="epoch-confirm-message">{t("epochConfirm.message")}</p>
      </header>

      {/* 現在の日付 → エポック数 → 実行後の日付 を季節の帯でつなぎ、エポック数で進む時間の長さと季節を示す */}
      <div className="epoch-confirm-timeline">
        <span
          className="epoch-confirm-band"
          style={city ? {
            "--epoch-confirm-span": String(city.simulation_days * epochCount),
            backgroundImage: seasonBandGradient(city.current_date),
          } as CSSProperties : undefined}
          aria-hidden={true}
        />
        <div className="epoch-confirm-step epoch-confirm-step--from">
          <SeasonBadge className="epoch-confirm-badge--from" season={city ? seasonOf(city.current_date) : undefined} />
          <span className="epoch-confirm-label">{t("epochConfirm.currentDate")}</span>
          <span className="epoch-confirm-date">{currentDateStr}</span>
        </div>

        {/* 1〜MAX_EPOCH_COUNT の範囲で連続実行数を指定できる(テンプレートモードは上限なし・直接入力可) */}
        <div className="epoch-confirm-step epoch-confirm-step--count">
          <span className="epoch-confirm-label">{t("epochConfirm.epochCount")}</span>
          <div className="epoch-confirm-stepper">
            <button
              className="epoch-confirm-stepper-button"
              onClick={() => setEpochCount((c) => Math.max(1, c - 1))}
              disabled={executing || epochCount <= 1}
              type="button"
              aria-label="−"
            >
              <MinusIcon />
            </button>
            {templateMode ? (
              <input
                className="epoch-confirm-count epoch-confirm-count--input"
                type="number"
                min={1}
                max={TEMPLATE_MAX_EPOCH_COUNT}
                value={epochCount}
                disabled={executing}
                onChange={(e) => handleCountInput(e.target.value)}
              />
            ) : (
              <span className="epoch-confirm-count">{epochCount}</span>
            )}
            <button
              className="epoch-confirm-stepper-button"
              onClick={() => setEpochCount((c) => Math.min(maxEpochCount, c + 1))}
              disabled={executing || epochCount >= maxEpochCount}
              type="button"
              aria-label="+"
            >
              <PlusIcon />
            </button>
          </div>
        </div>

        <div className="epoch-confirm-step epoch-confirm-step--to">
          <SeasonBadge
            className="epoch-confirm-badge--to"
            season={city ? seasonOf(city.current_date + city.simulation_days * epochCount) : undefined}
          />
          <span className="epoch-confirm-label">{t("epochConfirm.afterDate")}</span>
          {/* 日付が変わるたびに差し替えて、切り替わりの動きを出す */}
          <span key={nextDateStr} className="epoch-confirm-date">{nextDateStr}</span>
        </div>
      </div>

      <div className="epoch-confirm-actions">
        <SaveDialogButton disabled={executing} onClick={onClose}>{t("epochConfirm.no")}</SaveDialogButton>
        <SaveDialogButton kind="primary" busy={executing} onClick={() => void handleConfirm()}>
          {executing ? t("epochConfirm.executing") : t("epochConfirm.yes")}
        </SaveDialogButton>
      </div>
    </div>
  );
}
