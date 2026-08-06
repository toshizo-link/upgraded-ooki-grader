import { useState, type FormEvent, type ReactNode } from "react";
import { ApiError } from "../lib/api";
import { Button, ErrorState, Field, InlineAlert } from "../components/ui";
import { Icon } from "../components/Icon";
import { useSession } from "./SessionContext";

export function SessionBoundary({ children }: { children: ReactNode }) {
  const session = useSession();

  if (session.status === "loading") {
    return (
      <div className="auth-screen">
        <div className="auth-loading" role="status">
          <Brand />
          <span className="spinner spinner--large" aria-hidden="true" />
          <p>学校内サーバーに接続しています</p>
        </div>
      </div>
    );
  }

  if (session.status === "error") {
    return (
      <div className="auth-screen">
        <div className="auth-panel auth-panel--error">
          <Brand />
          <ErrorState
            error={session.error || undefined}
            onRetry={() => void session.reload()}
            title="Ooki Grader に接続できません"
          />
          <p className="auth-support">
            この端末が学校内ネットワークに接続され、ホストサービスが起動しているか確認してください。
          </p>
        </div>
      </div>
    );
  }

  if (session.status === "bootstrapRequired") {
    return <Bootstrap />;
  }

  if (session.status === "unauthenticated") {
    return <SignIn />;
  }

  return children;
}

function Bootstrap() {
  const { completeBootstrap } = useSession();
  const [values, setValues] = useState({
    token: "",
    username: "",
    displayName: "",
    password: "",
    confirmation: "",
  });
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string>();
  const passwordMismatch =
    Boolean(values.confirmation) && values.password !== values.confirmation;

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (passwordMismatch || values.password.length < 12) return;
    setSubmitting(true);
    setError(undefined);
    try {
      await completeBootstrap({
        token: values.token.trim(),
        schoolName: "大木スクール",
        username: values.username.trim(),
        displayName: values.displayName.trim(),
        password: values.password,
      });
    } catch (reason) {
      if (reason instanceof ApiError && [401, 403, 410].includes(reason.status)) {
        setError(
          "初期設定トークンを確認してください。トークンは発行から24時間または初回使用まで有効です。",
        );
      } else {
        setError(
          reason instanceof Error
            ? reason.message
            : "初期管理者を作成できませんでした。",
        );
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="auth-screen auth-screen--bootstrap">
      <main className="auth-panel auth-panel--bootstrap">
        <Brand />
        <div className="auth-panel__heading">
          <div className="eyebrow">ホスト限定の初期設定</div>
          <h1>最初の管理者を作成</h1>
          <p>
            この操作は学校内ホストから一度だけ実行できます。技術担当者から受け取った初期設定トークンを入力してください。
          </p>
        </div>
        <InlineAlert tone="warning" title="安全な場所で設定してください">
          <p>
            初期設定トークンとパスワードは記録画面やログに残しません。共用端末での入力は避けてください。
          </p>
        </InlineAlert>
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
        <form className="bootstrap-form" onSubmit={handleSubmit}>
          <Field
            label="初期設定トークン"
            htmlFor="bootstrap-token"
            required
            hint="インストーラーまたは技術担当者が発行した一回限りの文字列"
          >
            <input
              id="bootstrap-token"
              type="password"
              autoComplete="off"
              value={values.token}
              onChange={(event) =>
                setValues({ ...values, token: event.target.value })
              }
              required
              autoFocus
            />
          </Field>
          <fieldset>
            <legend>管理者アカウント</legend>
            <div className="form-grid form-grid--2">
              <Field label="ユーザー名" htmlFor="bootstrap-username" required>
                <input
                  id="bootstrap-username"
                  autoComplete="username"
                  value={values.username}
                  onChange={(event) =>
                    setValues({ ...values, username: event.target.value })
                  }
                  required
                />
              </Field>
              <Field label="表示名" htmlFor="bootstrap-display-name" required>
                <input
                  id="bootstrap-display-name"
                  autoComplete="name"
                  value={values.displayName}
                  onChange={(event) =>
                    setValues({ ...values, displayName: event.target.value })
                  }
                  placeholder="例：大木 太郎"
                  required
                />
              </Field>
              <Field
                label="パスワード"
                htmlFor="bootstrap-password"
                required
                hint="12文字以上。ほかのサービスと異なるものを使用してください。"
                error={
                  values.password && values.password.length < 12
                    ? "12文字以上で入力してください。"
                    : undefined
                }
              >
                <input
                  id="bootstrap-password"
                  type="password"
                  autoComplete="new-password"
                  minLength={12}
                  value={values.password}
                  onChange={(event) =>
                    setValues({ ...values, password: event.target.value })
                  }
                  required
                />
              </Field>
              <Field
                label="パスワード（確認）"
                htmlFor="bootstrap-confirmation"
                required
                error={
                  passwordMismatch ? "パスワードが一致しません。" : undefined
                }
              >
                <input
                  id="bootstrap-confirmation"
                  type="password"
                  autoComplete="new-password"
                  minLength={12}
                  value={values.confirmation}
                  onChange={(event) =>
                    setValues({ ...values, confirmation: event.target.value })
                  }
                  required
                />
              </Field>
            </div>
          </fieldset>
          <Button
            type="submit"
            size="large"
            disabled={
              submitting ||
              passwordMismatch ||
              values.password.length < 12 ||
              !values.token.trim() ||
              !values.username.trim() ||
              !values.displayName.trim()
            }
          >
            {submitting ? "管理者を作成しています…" : "初期設定を完了"}
          </Button>
        </form>
      </main>
    </div>
  );
}

function Brand() {
  return (
    <div className="auth-brand" aria-label="Ooki Grader">
      <div className="brand-mark" aria-hidden="true">
        <span>大</span>
      </div>
      <div>
        <strong>Ooki Grader</strong>
      </div>
    </div>
  );
}

function SignIn() {
  const { login } = useSession();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string>();

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(undefined);
    setSubmitting(true);
    try {
      await login(username.trim(), password);
    } catch (reason) {
      if (reason instanceof ApiError && reason.status === 429) {
        setError(
          "試行回数が上限に達しました。しばらく待ってからもう一度お試しください。",
        );
      } else {
        setError("ユーザー名またはパスワードを確認してください。");
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="auth-screen">
      <main className="auth-panel">
        <Brand />
        <div className="auth-panel__heading">
          <h1>職員ログイン</h1>
          <p>学校から発行されたアカウントでログインしてください。</p>
        </div>
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
        <form onSubmit={handleSubmit} className="auth-form">
          <Field label="ユーザー名" htmlFor="username" required>
            <input
              id="username"
              name="username"
              autoComplete="username"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              required
              autoFocus
            />
          </Field>
          <Field label="パスワード" htmlFor="password" required>
            <input
              id="password"
              name="password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
            />
          </Field>
          <Button
            type="submit"
            size="large"
            disabled={submitting || !username.trim() || !password}
          >
            {submitting ? "確認しています…" : "ログイン"}
          </Button>
        </form>
        <div className="auth-security">
          <Icon name="lock" size={17} />
          <span>通信とデータは学校内のホストで保護されています</span>
        </div>
      </main>
    </div>
  );
}
