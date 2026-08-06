import { useEffect, useRef, useState, type ReactNode } from "react";
import { Link, NavLink, useLocation } from "../router";
import { useSession } from "../auth/SessionContext";
import { useApiQuery } from "../hooks/useApiQuery";
import { useOnlineStatus } from "../hooks/useOnlineStatus";
import { useStatusStream } from "../hooks/useStatusStream";
import { api } from "../lib/api";
import { classNames } from "../lib/format";
import type { ReviewCounts, StaffRole } from "../types";
import { Icon, type IconName } from "./Icon";
import {
  Avatar,
  Badge,
  Button,
  Field,
  IconButton,
  InlineAlert,
  Modal,
} from "./ui";

interface NavItem {
  to: string;
  label: string;
  icon: IconName;
  roles?: StaffRole[];
  end?: boolean;
}

const navigation: NavItem[] = [
  { to: "/", label: "ダッシュボード", icon: "dashboard", end: true },
  {
    to: "/review",
    label: "採点待ち・確認",
    icon: "queue",
    roles: ["administrator", "teacher"],
  },
  {
    to: "/sessions",
    label: "テスト実施",
    icon: "sessions",
    roles: ["administrator", "teacher", "scanOperator"],
  },
  {
    to: "/templates",
    label: "テストひな形",
    icon: "templates",
    roles: ["administrator", "teacher"],
  },
  {
    to: "/students",
    label: "生徒",
    icon: "students",
    roles: ["administrator", "teacher"],
  },
  {
    to: "/reports",
    label: "帳票",
    icon: "reports",
    roles: ["administrator", "teacher", "readOnlyReviewer"],
  },
  {
    to: "/admin",
    label: "管理",
    icon: "admin",
    roles: ["administrator"],
  },
];

const roleLabels: Record<StaffRole, string> = {
  administrator: "管理者",
  teacher: "先生",
  scanOperator: "スキャン担当",
  readOnlyReviewer: "閲覧担当",
};

export function AppShell({ children }: { children: ReactNode }) {
  const { user, hasAnyRole, logout, reload } = useSession();
  const location = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);
  const [profileOpen, setProfileOpen] = useState(false);
  const [passwordOpen, setPasswordOpen] = useState(false);
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [passwordConfirmation, setPasswordConfirmation] = useState("");
  const [passwordWorking, setPasswordWorking] = useState(false);
  const [passwordError, setPasswordError] = useState<string>();
  const [clockNow, setClockNow] = useState(() => Date.now());
  const [extendingSession, setExtendingSession] = useState(false);
  const expiryHandled = useRef(false);
  const profileRef = useRef<HTMLDivElement>(null);
  const online = useOnlineStatus();
  const canReview = hasAnyRole("administrator", "teacher");
  const stream = useStatusStream(Boolean(user && canReview));
  const review = useApiQuery<ReviewCounts>(
    "shell-review-counts",
    (signal) => api.get("/review/counts", undefined, signal),
    Boolean(user && canReview),
  );

  useEffect(() => {
    setMenuOpen(false);
    setProfileOpen(false);
  }, [location.pathname]);

  useEffect(() => {
    const timer = window.setInterval(() => setClockNow(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, []);

  const sessionExpiry = user?.sessionExpiresAt
    ? Date.parse(user.sessionExpiresAt)
    : Number.NaN;
  const sessionSecondsRemaining = Number.isFinite(sessionExpiry)
    ? Math.max(0, Math.ceil((sessionExpiry - clockNow) / 1_000))
    : null;
  const showSessionWarning =
    sessionSecondsRemaining !== null && sessionSecondsRemaining <= 120;

  useEffect(() => {
    if (sessionSecondsRemaining !== 0 || expiryHandled.current) return;
    expiryHandled.current = true;
    void reload();
  }, [reload, sessionSecondsRemaining]);

  useEffect(() => {
    if (sessionSecondsRemaining !== null && sessionSecondsRemaining > 0) {
      expiryHandled.current = false;
    }
  }, [sessionSecondsRemaining]);

  async function extendSession() {
    setExtendingSession(true);
    try {
      await api.get("/auth/me");
      setClockNow(Date.now());
    } finally {
      setExtendingSession(false);
    }
  }

  useEffect(() => {
    if (user?.mustChangePassword) {
      setPasswordOpen(true);
    }
  }, [user?.mustChangePassword]);

  useEffect(() => {
    const handleStatus = (event: Event) => {
      const detail = (event as CustomEvent<{ type?: string }>).detail;
      if (detail?.type === "review.counts") review.reload();
    };
    window.addEventListener("ooki:status", handleStatus);
    return () => window.removeEventListener("ooki:status", handleStatus);
  }, [review]);

  useEffect(() => {
    function handleOutside(event: MouseEvent) {
      if (
        profileOpen &&
        profileRef.current &&
        !profileRef.current.contains(event.target as Node)
      ) {
        setProfileOpen(false);
      }
    }
    document.addEventListener("mousedown", handleOutside);
    return () => document.removeEventListener("mousedown", handleOutside);
  }, [profileOpen]);

  const visibleNavigation = navigation.filter(
    (item) => !item.roles || hasAnyRole(...item.roles),
  );
  const reviewCount =
    (review.data?.needsNameReview || 0) +
    (review.data?.needsGradeReview || 0);
  const passwordMismatch =
    Boolean(passwordConfirmation) && newPassword !== passwordConfirmation;

  async function changePassword() {
    if (
      !currentPassword ||
      newPassword.length < 12 ||
      passwordMismatch
    ) {
      return;
    }

    setPasswordWorking(true);
    setPasswordError(undefined);
    try {
      await api.post("/auth/change-password", {
        currentPassword,
        newPassword,
      });
      setCurrentPassword("");
      setNewPassword("");
      setPasswordConfirmation("");
      setPasswordOpen(false);
      await reload();
    } catch (reason) {
      setPasswordError(
        reason instanceof Error
          ? reason.message
          : "パスワードを変更できませんでした。",
      );
    } finally {
      setPasswordWorking(false);
    }
  }

  return (
    <div className="app">
      <a className="skip-link" href="#main-content">
        本文へ移動
      </a>
      <aside
        className={classNames("sidebar", menuOpen && "sidebar--open")}
        aria-label="メインメニュー"
      >
        <div className="sidebar__brand">
          <Link to="/" aria-label="Ooki Grader ダッシュボード">
            <div className="brand-mark" aria-hidden="true">
              <span>大</span>
            </div>
            <div className="sidebar__brand-copy">
              <strong>Ooki Grader</strong>
            </div>
          </Link>
          <IconButton
            className="sidebar__close"
            label="メニューを閉じる"
            icon="close"
            onClick={() => setMenuOpen(false)}
          />
        </div>

        <nav className="sidebar__nav">
          <span className="sidebar__label">メニュー</span>
          {visibleNavigation.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) =>
                classNames("nav-link", isActive && "nav-link--active")
              }
            >
              <Icon name={item.icon} size={21} />
              <span>{item.label}</span>
              {item.to === "/review" && reviewCount > 0 ? (
                <span className="nav-link__count" aria-label={`${reviewCount}件`}>
                  {reviewCount > 99 ? "99+" : reviewCount}
                </span>
              ) : null}
            </NavLink>
          ))}
        </nav>

        <div className="sidebar__foot">
          <div className="privacy-note">
            <Icon name="lock" size={17} />
            <span>データは学校内ホストで管理されています</span>
          </div>
          <small>Ooki Grader v0.1</small>
        </div>
      </aside>
      {menuOpen ? (
        <button
          className="sidebar-backdrop"
          aria-label="メニューを閉じる"
          onClick={() => setMenuOpen(false)}
        />
      ) : null}

      <div className="app__body">
        <header className="topbar">
          <div className="topbar__left">
            <IconButton
              className="mobile-menu-button"
              label="メニューを開く"
              icon="menu"
              onClick={() => setMenuOpen(true)}
            />
            <div
              className={classNames(
                "connection-pill",
                stream.state === "connected" && "connection-pill--ok",
                !online && "connection-pill--warning",
              )}
              title={
                stream.state === "connected"
                  ? "ホストから状態更新を受信しています"
                  : "状態更新への接続を確認しています"
              }
            >
              <Icon name="connection" size={17} />
              <span>
                {stream.state === "connected" ? "ホスト接続中" : "接続確認中"}
              </span>
            </div>
          </div>
          <div className="topbar__right">
            {stream.storageWarning ? (
              <Link className="storage-alert" to="/admin?tab=storage">
                <Icon name="storage" size={17} />
                <span>保存容量を確認</span>
              </Link>
            ) : null}
            <div className="profile-menu" ref={profileRef}>
              <button
                className="profile-menu__trigger"
                type="button"
                aria-expanded={profileOpen}
                aria-haspopup="menu"
                onClick={() => setProfileOpen((value) => !value)}
              >
                <Avatar name={user?.displayName} size="small" />
                <span className="profile-menu__copy">
                  <strong>{user?.displayName}</strong>
                  <small>
                    {user?.roles.map((role) => roleLabels[role]).join("・")}
                  </small>
                </span>
                <Icon name="chevronDown" size={16} />
              </button>
              {profileOpen ? (
                <div className="profile-popover" role="menu">
                  <div className="profile-popover__identity">
                    <strong>{user?.displayName}</strong>
                    <span>{user?.username}</span>
                  </div>
                  <button
                    role="menuitem"
                    type="button"
                    onClick={() => {
                      setProfileOpen(false);
                      setPasswordError(undefined);
                      setPasswordOpen(true);
                    }}
                  >
                    <Icon name="lock" size={18} />
                    パスワードを変更
                  </button>
                  <button
                    role="menuitem"
                    type="button"
                    onClick={() => void logout()}
                  >
                    <Icon name="logout" size={18} />
                    ログアウト
                  </button>
                </div>
              ) : null}
            </div>
          </div>
        </header>
        {!online ? (
          <InlineAlert tone="warning" title="インターネット接続を確認できません">
            <p>
              学校内でのアップロードや確認は続けられます。AI処理は接続が戻るまで安全に待機します。
            </p>
          </InlineAlert>
        ) : null}
        {showSessionWarning ? (
          <div className="session-expiry-warning" role="alert">
            <div>
              <strong>まもなくセッションが終了します</strong>
              <span>
                未保存の編集内容を確認してください。残り
                {Math.floor((sessionSecondsRemaining ?? 0) / 60)}:
                {String((sessionSecondsRemaining ?? 0) % 60).padStart(2, "0")}
              </span>
            </div>
            <Button
              size="small"
              disabled={extendingSession || sessionSecondsRemaining === 0}
              onClick={() => void extendSession()}
            >
              {extendingSession ? "延長中…" : "作業を続ける"}
            </Button>
          </div>
        ) : null}
        <main id="main-content" className="main-content" tabIndex={-1}>
          {children}
        </main>
      </div>
      <Modal
        open={passwordOpen}
        onClose={() => {
          if (!passwordWorking && !user?.mustChangePassword) {
            setPasswordOpen(false);
          }
        }}
        title={
          user?.mustChangePassword
            ? "一時パスワードを変更してください"
            : "パスワードを変更"
        }
        description={
          user?.mustChangePassword
            ? "ほかの機能を使う前に、本人だけが知っている新しいパスワードを設定します。"
            : "変更後、ほかの端末でログイン中のセッションは終了します。"
        }
        size="small"
        footer={
          <>
            {!user?.mustChangePassword ? (
              <Button
                variant="secondary"
                onClick={() => setPasswordOpen(false)}
                disabled={passwordWorking}
              >
                キャンセル
              </Button>
            ) : null}
            <Button
              onClick={() => void changePassword()}
              disabled={
                passwordWorking ||
                !currentPassword ||
                newPassword.length < 12 ||
                !passwordConfirmation ||
                passwordMismatch
              }
            >
              {passwordWorking ? "変更しています…" : "パスワードを変更"}
            </Button>
          </>
        }
      >
        {user?.mustChangePassword ? (
          <InlineAlert tone="warning">
            <p>
              一時パスワードは再利用できません。この画面を閉じた場合は、管理者に再設定を依頼してください。
            </p>
          </InlineAlert>
        ) : null}
        {passwordError ? (
          <InlineAlert tone="danger">
            <p>{passwordError}</p>
          </InlineAlert>
        ) : null}
        <div className="form-grid">
          <Field
            label={user?.mustChangePassword ? "一時パスワード" : "現在のパスワード"}
            htmlFor="profile-current-password"
            required
          >
            <input
              id="profile-current-password"
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.target.value)}
            />
          </Field>
          <Field
            label="新しいパスワード"
            htmlFor="profile-new-password"
            required
            hint="12文字以上。ほかのサービスと同じパスワードは使わないでください。"
          >
            <input
              id="profile-new-password"
              type="password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(event) => setNewPassword(event.target.value)}
            />
          </Field>
          <Field
            label="新しいパスワード（確認）"
            htmlFor="profile-password-confirmation"
            required
            error={
              passwordMismatch ? "新しいパスワードが一致しません。" : undefined
            }
          >
            <input
              id="profile-password-confirmation"
              type="password"
              autoComplete="new-password"
              value={passwordConfirmation}
              onChange={(event) =>
                setPasswordConfirmation(event.target.value)
              }
            />
          </Field>
        </div>
      </Modal>
    </div>
  );
}
