import {
  useEffect,
  useId,
  useRef,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type ReactNode,
} from "react";
import { ApiError } from "../lib/api";
import { classNames, initials } from "../lib/format";
import { Icon, type IconName } from "./Icon";

export function Button({
  variant = "primary",
  size = "medium",
  leadingIcon,
  children,
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "secondary" | "quiet" | "danger";
  size?: "small" | "medium" | "large";
  leadingIcon?: IconName;
}) {
  return (
    <button
      className={classNames(
        "button",
        `button--${variant}`,
        `button--${size}`,
        className,
      )}
      {...props}
    >
      {leadingIcon ? <Icon name={leadingIcon} size={18} /> : null}
      <span>{children}</span>
    </button>
  );
}

export function IconButton({
  label,
  icon,
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  label: string;
  icon: IconName;
}) {
  return (
    <button
      type="button"
      className={classNames("icon-button", className)}
      aria-label={label}
      title={label}
      {...props}
    >
      <Icon name={icon} />
    </button>
  );
}

export function Card({
  className,
  children,
  ...props
}: HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={classNames("card", className)} {...props}>
      {children}
    </div>
  );
}

export function PageHeader({
  eyebrow,
  title,
  description,
  actions,
  backAction,
}: {
  eyebrow?: string;
  title: string;
  description?: string;
  actions?: ReactNode;
  backAction?: ReactNode;
}) {
  return (
    <div className="page-header">
      <div className="page-header__copy">
        {backAction}
        {eyebrow ? <div className="eyebrow">{eyebrow}</div> : null}
        <h1>{title}</h1>
        {description ? <p>{description}</p> : null}
      </div>
      {actions ? <div className="page-header__actions">{actions}</div> : null}
    </div>
  );
}

type Tone =
  | "neutral"
  | "info"
  | "success"
  | "warning"
  | "danger"
  | "accent";

export function Badge({
  children,
  tone = "neutral",
  dot = false,
  className,
}: {
  children: ReactNode;
  tone?: Tone;
  dot?: boolean;
  className?: string;
}) {
  return (
    <span className={classNames("badge", `badge--${tone}`, className)}>
      {dot ? <span className="badge__dot" aria-hidden="true" /> : null}
      {children}
    </span>
  );
}

const statusMap: Record<string, { label: string; tone: Tone }> = {
  uploading: { label: "アップロード中", tone: "info" },
  validating: { label: "ファイルを確認中", tone: "info" },
  preprocessing: { label: "画像を準備中", tone: "info" },
  awaiting_name: { label: "生徒名を確認中", tone: "info" },
  awaitingName: { label: "生徒名を確認中", tone: "info" },
  awaiting_grading: { label: "AI採点待ち", tone: "neutral" },
  awaitingGrading: { label: "AI採点待ち", tone: "neutral" },
  awaiting_ai: { label: "AI処理待ち", tone: "neutral" },
  awaitingAi: { label: "AI処理待ち", tone: "neutral" },
  grading: { label: "AI採点中", tone: "info" },
  gemini_batch_running: { label: "AI採点中", tone: "info" },
  geminiBatchRunning: { label: "AI採点中", tone: "info" },
  openrouter_queued: { label: "OpenRouter採点待ち", tone: "info" },
  openRouterQueued: { label: "OpenRouter採点待ち", tone: "info" },
  budget_blocked: { label: "AI利用上限で保留", tone: "warning" },
  budgetBlocked: { label: "AI利用上限で保留", tone: "warning" },
  needs_attention: { label: "画像の確認が必要", tone: "warning" },
  needsAttention: { label: "画像の確認が必要", tone: "warning" },
  needs_name_review: { label: "生徒名の確認が必要", tone: "warning" },
  needsNameReview: { label: "生徒名の確認が必要", tone: "warning" },
  needs_grade_review: { label: "採点の確認が必要", tone: "warning" },
  needsGradeReview: { label: "採点の確認が必要", tone: "warning" },
  ready_for_review: { label: "確認できます", tone: "accent" },
  readyForReview: { label: "確認できます", tone: "accent" },
  ready_to_finalize: { label: "確定できます", tone: "success" },
  readyToFinalize: { label: "確定できます", tone: "success" },
  finalized: { label: "確定済み", tone: "success" },
  reopened: { label: "採点を修正中", tone: "warning" },
  failed: { label: "処理に失敗", tone: "danger" },
  voided: { label: "処理対象外", tone: "neutral" },
  cancelled: { label: "取り消し済み", tone: "neutral" },
  expired: { label: "期限切れ", tone: "warning" },
  finalizing: { label: "受信処理中", tone: "info" },
  scan_deleted: { label: "画像削除済み", tone: "neutral" },
  scanDeleted: { label: "画像削除済み", tone: "neutral" },
  active: { label: "公開中", tone: "success" },
  draft: { label: "下書き", tone: "neutral" },
  stale: { label: "旧設定", tone: "warning" },
  published: { label: "公開済み", tone: "success" },
  retired: { label: "利用終了", tone: "warning" },
  archived: { label: "アーカイブ", tone: "neutral" },
  open: { label: "受付中", tone: "success" },
  closed: { label: "終了", tone: "neutral" },
  healthy: { label: "正常", tone: "success" },
  passed: { label: "正常", tone: "success" },
  degraded: { label: "要確認", tone: "warning" },
  unavailable: { label: "利用不可", tone: "danger" },
  blocked: { label: "利用不可", tone: "danger" },
  unknown: { label: "未設定", tone: "neutral" },
  queued: { label: "待機中", tone: "neutral" },
  running: { label: "実行中", tone: "info" },
  prepared: { label: "送信準備済み", tone: "neutral" },
  submitting: { label: "AI処理を開始中", tone: "info" },
  submitted: { label: "AI処理中", tone: "info" },
  delayed: { label: "処理遅延", tone: "warning" },
  manual_review: { label: "管理者の確認が必要", tone: "danger" },
  reconcile_required: { label: "リモート照合が必要", tone: "warning" },
  succeeded: { label: "完了", tone: "success" },
  not_started: { label: "未開始", tone: "neutral" },
  pending: { label: "待機中", tone: "neutral" },
  completed: { label: "完了", tone: "success" },
  retrying: { label: "再試行中", tone: "warning" },
  verified: { label: "作成済み", tone: "success" },
  rendering: { label: "作成中", tone: "info" },
  generating: { label: "作成中", tone: "info" },
  retry_waiting: { label: "再試行待ち", tone: "warning" },
  retryWaiting: { label: "再試行待ち", tone: "warning" },
  correct: { label: "正解", tone: "success" },
  partial: { label: "一部正解", tone: "accent" },
  incorrect: { label: "不正解", tone: "danger" },
  blank: { label: "無解答", tone: "neutral" },
  unreadable: { label: "判読困難", tone: "warning" },
};

export function StatusBadge({ status }: { status: string }) {
  const mapped = statusMap[status] || {
    label: status ? "状態を確認中" : "不明",
    tone: "neutral" as Tone,
  };
  return (
    <Badge tone={mapped.tone} dot>
      {mapped.label}
    </Badge>
  );
}

export function LoadingState({
  label = "読み込んでいます",
  compact = false,
}: {
  label?: string;
  compact?: boolean;
}) {
  return (
    <div
      className={classNames("loading-state", compact && "loading-state--compact")}
      role="status"
      aria-live="polite"
    >
      <span className="spinner" aria-hidden="true" />
      <span>{label}</span>
    </div>
  );
}

export function SkeletonRows({ rows = 4 }: { rows?: number }) {
  return (
    <div className="skeleton-list" aria-label="読み込み中" role="status">
      {Array.from({ length: rows }, (_, index) => (
        <div className="skeleton-row" key={index}>
          <span className="skeleton skeleton--circle" />
          <span className="skeleton skeleton--wide" />
          <span className="skeleton skeleton--short" />
        </div>
      ))}
    </div>
  );
}

export function EmptyState({
  icon = "file",
  title,
  description,
  action,
}: {
  icon?: IconName;
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <div className="empty-state">
      <div className="empty-state__icon">
        <Icon name={icon} size={26} />
      </div>
      <h2>{title}</h2>
      <p>{description}</p>
      {action ? <div className="empty-state__action">{action}</div> : null}
    </div>
  );
}

export function ErrorState({
  error,
  onRetry,
  title = "情報を読み込めませんでした",
  compact = false,
}: {
  error?: Error;
  onRetry?: () => void;
  title?: string;
  compact?: boolean;
}) {
  const apiError = error instanceof ApiError ? error : undefined;
  return (
    <div
      className={classNames("error-state", compact && "error-state--compact")}
      role="alert"
    >
      <div className="error-state__icon">
        <Icon name="alert" />
      </div>
      <div>
        <strong>{title}</strong>
        <p>{error?.message || "しばらくしてから、もう一度お試しください。"}</p>
        {apiError?.correlationId ? (
          <small>問い合わせ番号: {apiError.correlationId}</small>
        ) : null}
      </div>
      {onRetry ? (
        <Button
          type="button"
          variant="secondary"
          size="small"
          leadingIcon="retry"
          onClick={onRetry}
        >
          再読み込み
        </Button>
      ) : null}
    </div>
  );
}

export function InlineAlert({
  tone = "info",
  title,
  children,
  action,
}: {
  tone?: "info" | "warning" | "danger" | "success";
  title?: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  const icon: IconName =
    tone === "success" ? "check" : tone === "info" ? "info" : "alert";
  return (
    <div
      className={classNames("inline-alert", `inline-alert--${tone}`)}
      role={tone === "danger" ? "alert" : undefined}
    >
      <Icon name={icon} size={19} />
      <div className="inline-alert__copy">
        {title ? <strong>{title}</strong> : null}
        <div>{children}</div>
      </div>
      {action ? <div className="inline-alert__action">{action}</div> : null}
    </div>
  );
}

export function Field({
  label,
  htmlFor,
  required,
  hint,
  error,
  children,
  className,
}: {
  label: string;
  htmlFor: string;
  required?: boolean;
  hint?: string;
  error?: string;
  children: ReactNode;
  className?: string;
}) {
  const hintId = useId();
  const errorId = useId();
  return (
    <div className={classNames("field", className)}>
      <label htmlFor={htmlFor}>
        {label}
        {required ? <span className="field__required">必須</span> : null}
      </label>
      {children}
      {hint ? (
        <small className="field__hint" id={hintId}>
          {hint}
        </small>
      ) : null}
      {error ? (
        <small className="field__error" id={errorId} role="alert">
          {error}
        </small>
      ) : null}
    </div>
  );
}

export function SearchInput({
  value,
  onChange,
  placeholder = "検索",
  label = "検索",
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  label?: string;
}) {
  return (
    <label className="search-input">
      <span className="sr-only">{label}</span>
      <Icon name="search" size={18} />
      <input
        type="search"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
      />
      {value ? (
        <button
          type="button"
          aria-label="検索条件をクリア"
          onClick={() => onChange("")}
        >
          <Icon name="close" size={16} />
        </button>
      ) : null}
    </label>
  );
}

export function Modal({
  open,
  title,
  description,
  onClose,
  children,
  footer,
  size = "medium",
}: {
  open: boolean;
  title: string;
  description?: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  size?: "small" | "medium" | "large";
}) {
  const titleId = useId();
  const descriptionId = useId();
  const panelRef = useRef<HTMLDivElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (!open) return;
    previousFocus.current = document.activeElement as HTMLElement;
    const timer = window.setTimeout(() => {
      const focusable = panelRef.current?.querySelector<HTMLElement>(
        "button, input, select, textarea, [href], [tabindex]:not([tabindex='-1'])",
      );
      focusable?.focus();
    }, 0);
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      window.clearTimeout(timer);
      document.removeEventListener("keydown", handleKeyDown);
      previousFocus.current?.focus();
    };
  }, [onClose, open]);

  if (!open) return null;
  return (
    <div
      className="modal-layer"
      role="presentation"
      onMouseDown={(event) => {
        if (event.currentTarget === event.target) onClose();
      }}
    >
      <div
        ref={panelRef}
        className={classNames("modal", `modal--${size}`)}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
      >
        <header className="modal__header">
          <div>
            <h2 id={titleId}>{title}</h2>
            {description ? <p id={descriptionId}>{description}</p> : null}
          </div>
          <IconButton label="閉じる" icon="close" onClick={onClose} />
        </header>
        <div className="modal__body">{children}</div>
        {footer ? <footer className="modal__footer">{footer}</footer> : null}
      </div>
    </div>
  );
}

export function Tabs<T extends string>({
  value,
  onChange,
  tabs,
  label,
}: {
  value: T;
  onChange: (value: T) => void;
  tabs: Array<{ value: T; label: string; count?: number }>;
  label: string;
}) {
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {tabs.map((tab) => (
        <button
          key={tab.value}
          type="button"
          role="tab"
          aria-selected={value === tab.value}
          tabIndex={value === tab.value ? 0 : -1}
          className={classNames(value === tab.value && "is-active")}
          onClick={() => onChange(tab.value)}
          onKeyDown={(event) => {
            const currentIndex = tabs.findIndex(
              (candidate) => candidate.value === tab.value,
            );
            let nextIndex = currentIndex;
            if (event.key === "ArrowRight") {
              nextIndex = (currentIndex + 1) % tabs.length;
            } else if (event.key === "ArrowLeft") {
              nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
            } else if (event.key === "Home") {
              nextIndex = 0;
            } else if (event.key === "End") {
              nextIndex = tabs.length - 1;
            } else {
              return;
            }

            event.preventDefault();
            const nextTab = tabs[nextIndex];
            if (!nextTab) return;
            onChange(nextTab.value);
            const tabButtons =
              event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>(
                '[role="tab"]',
              );
            tabButtons?.[nextIndex]?.focus();
          }}
        >
          {tab.label}
          {tab.count !== undefined ? <span>{tab.count}</span> : null}
        </button>
      ))}
    </div>
  );
}

export function Meter({
  value,
  max,
  label,
  displayValue,
  tone = "default",
}: {
  value: number;
  max: number;
  label: string;
  displayValue?: string;
  tone?: "default" | "warning" | "danger";
}) {
  const percentage = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0;
  return (
    <div className={classNames("meter", `meter--${tone}`)}>
      <div className="meter__label">
        <span>{label}</span>
        {displayValue ? <strong>{displayValue}</strong> : null}
      </div>
      <div
        className="meter__track"
        role="meter"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={max}
        aria-valuenow={value}
      >
        <span style={{ width: `${percentage}%` }} />
      </div>
    </div>
  );
}

export function Avatar({
  name,
  size = "medium",
}: {
  name?: string;
  size?: "small" | "medium" | "large";
}) {
  return (
    <span className={classNames("avatar", `avatar--${size}`)} aria-hidden="true">
      {initials(name)}
    </span>
  );
}

export function Score({
  earned,
  possible,
  compact = false,
}: {
  earned: string;
  possible: string;
  compact?: boolean;
}) {
  return (
    <span className={classNames("score", compact && "score--compact")}>
      <strong>{earned}</strong>
      <span>/ {possible}点</span>
    </span>
  );
}

export function VisuallyHidden({ children }: { children: ReactNode }) {
  return <span className="sr-only">{children}</span>;
}
