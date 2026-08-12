import { useEffect, useState } from "react";
import { useSearchParams } from "../router";
import { Icon } from "../components/Icon";
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  InlineAlert,
  LoadingState,
  Meter,
  Modal,
  PageHeader,
  SearchInput,
  StatusBadge,
  Tabs,
} from "../components/ui";
import { useApiQuery } from "../hooks/useApiQuery";
import { api, asPaged, newIdempotencyKey } from "../lib/api";
import {
  formatBytes,
  formatDateTime,
} from "../lib/format";
import type {
  AdminHealth,
  AdminStorage,
  AiProvider,
  DurableJob,
  HealthComponent,
  PagedResponse,
  StaffRole,
} from "../types";

type AdminTab = "health" | "ai" | "staff" | "storage" | "jobs";

interface AiConnection {
  id: string;
  provider: AiProvider;
  configured: boolean;
  keyFingerprint?: string;
  modelId: string;
  state: string;
  timeoutSeconds: number;
  concurrencyLimit: number;
  revision: number;
  lastCapabilityProbe?: {
    state: string;
    checkedAt?: string;
    safeErrorCode?: string;
    imageInput?: boolean;
    structuredOutput?: boolean;
  };
}

interface AiCapabilityProbeResult {
  state: string;
  authentication: boolean;
  modelAvailable: boolean;
  imageInput: boolean;
  structuredOutput: boolean;
  usageMetadata: boolean;
  safeErrorCode?: string;
  checkedAt?: string;
}

const geminiDefaultModel = "gemini-3.5-flash-lite";
const deepSeekV4FlashModel = "deepseek/deepseek-v4-flash";

interface AiTaskProfile {
  id: string;
  name: string;
  taskType: string;
  connectionId: string;
  modelId: string;
  processingStrategy: string;
  promptVersion: string;
  schemaVersion: string;
  thinkingLevel: string;
  mediaResolution: string;
  maxOutputTokens: number;
  concurrencyLimit: number;
  active: boolean;
  stale?: boolean;
  activatedAt?: string;
  revision: number;
}

interface AiBudget {
  id: string;
  dailyWarningUsdMicros: number;
  dailyHardUsdMicros: number;
  monthlyWarningUsdMicros: number;
  monthlyHardUsdMicros: number;
  usdToJpyMicros: number;
  active: boolean;
  revision: number;
}

interface AiUsage {
  from: string;
  to: string;
  requestCount: number;
  estimatedUsdMicros: number;
  estimatedJpyMicros: number;
  byModel: {
    provider: string;
    model: string;
    requestCount: number;
    estimatedUsdMicros: number;
    estimatedJpyMicros: number;
    totalTokens: number;
  }[];
}

interface AiPricingSnapshot {
  id: string;
  provider: string;
  modelId: string;
  inputUsdMicrosPerMillionTokens: number;
  outputUsdMicrosPerMillionTokens: number;
  thinkingUsdMicrosPerMillionTokens: number;
  sourceUrl: string;
  effectiveAt: string;
  capturedAt: string;
}

interface AiLatencyMetrics {
  sampleCount: number;
  averageMilliseconds: number | null;
  p95Milliseconds: number | null;
}

interface AiMetrics {
  window: {
    from: string;
    to: string;
    days: number;
    maximumDays: number;
  };
  totals: {
    requestCount: number;
    successCount: number;
    failureCount: number;
    ambiguousCount: number;
    dispatchAttemptCount: number;
    retriedRequestCount: number;
    retryAttemptCount: number;
    errors: {
      rateLimited429: number;
      provider5Xx: number;
      schemaOrOutputValidation: number;
    };
    tokens: {
      usageRecordCount: number;
      input: number;
      cached: number;
      output: number;
      thinking: number;
      total: number;
    };
    cost: {
      estimatedUsdMicros: number;
      estimatedJpyMicros: number;
    };
    queueWait: AiLatencyMetrics;
    providerLatency: AiLatencyMetrics;
    teacherCorrection: {
      available: boolean;
      reviewedQuestionCount: number;
      correctedQuestionCount: number;
      rateBasisPoints: number | null;
    };
  };
  stateCounts: {
    state: string;
    count: number;
  }[];
  sampling: {
    latencySampleLimit: number;
    latencySamplesTruncated: boolean;
  };
}

interface BackupRecord {
  id: string;
  state: string;
  integrityResult?: string;
  requestedAt?: string;
  startedAt?: string;
  completedAt?: string;
  verifiedAt?: string;
  lastVerificationAt?: string;
  errorCode?: string;
  safeErrorDetail?: string;
}

interface BackupListResponse extends PagedResponse<BackupRecord> {
  configuration: {
    enabled: boolean;
    configured: boolean;
    encryptionConfirmed: boolean;
    destinationAccessible: boolean;
    destinationRootPath?: string;
    includeManagedScans: boolean;
    scheduleLocalTime: string;
    nextScheduledAt?: string;
    componentState: string;
    errorCode?: string;
    detail?: string;
  };
}

interface BackupRestorePlan {
  canRestore: boolean;
  backupId: string;
  checkedAt: string;
  integrityResult: string;
  requiresMigration: boolean;
  managedScansIncluded: boolean;
  requiredActions: string[];
}

interface StaffAccount {
  id: string;
  username: string;
  displayName: string;
  status: "active" | "disabled";
  roles: StaffRole[];
  lastLoginAt?: string;
  lockoutUntil?: string;
  credentialChangedAt: string;
  mustChangePassword: boolean;
  passwordSetupExpiresAt?: string;
  createdAt: string;
  updatedAt: string;
  revision: number;
}

interface RoleDefinition {
  name: StaffRole;
  displayName: string;
}

const staffRoleLabels: Record<StaffRole, string> = {
  administrator: "管理者",
  teacher: "先生",
  scanOperator: "スキャン担当",
  readOnlyReviewer: "閲覧担当",
};

export function AdminPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const requested = searchParams.get("tab") as AdminTab | null;
  const tab: AdminTab = ["health", "ai", "staff", "storage", "jobs"].includes(
    requested || "",
  )
    ? (requested as AdminTab)
    : "health";
  const health = useApiQuery<AdminHealth>(
    "admin-health",
    (signal) => api.get("/admin/health", undefined, signal),
    tab === "health",
  );
  const storage = useApiQuery<AdminStorage>(
    "admin-storage",
    (signal) => api.get("/admin/storage", undefined, signal),
    tab === "storage",
  );
  const jobs = useApiQuery<PagedResponse<DurableJob>>(
    "admin-jobs",
    async (signal) =>
      asPaged(
        await api.get("/admin/jobs", { pageSize: 100 }, signal),
      ),
    tab === "jobs",
  );
  const connections = useApiQuery<PagedResponse<AiConnection>>(
    "admin-ai-connections",
    async (signal) =>
      asPaged(await api.get("/admin/ai-connections", undefined, signal)),
    tab === "health" || tab === "ai",
  );
  const aiProfiles = useApiQuery<PagedResponse<AiTaskProfile>>(
    "admin-ai-profiles",
    async (signal) =>
      asPaged(await api.get("/admin/ai-task-profiles", undefined, signal)),
    tab === "ai",
  );
  const aiBudget = useApiQuery<AiBudget>(
    "admin-ai-budget",
    (signal) => api.get("/admin/settings/budgets", undefined, signal),
    tab === "ai",
  );
  const aiUsage = useApiQuery<AiUsage>(
    "admin-ai-usage",
    (signal) => api.get("/admin/usage", undefined, signal),
    tab === "ai",
  );
  const aiMetrics = useApiQuery<AiMetrics>(
    "admin-ai-metrics",
    (signal) => api.get("/admin/ai-metrics", { days: 30 }, signal),
    tab === "ai",
  );
  const aiPricing = useApiQuery<PagedResponse<AiPricingSnapshot>>(
    "admin-ai-pricing",
    async (signal) =>
      asPaged(await api.get("/admin/pricing-snapshots", undefined, signal)),
    tab === "ai",
  );
  const backups = useApiQuery<BackupListResponse>(
    "admin-backups",
    (signal) =>
      api.get("/admin/backups", { pageSize: 10 }, signal),
    tab === "health",
  );
  const staff = useApiQuery<PagedResponse<StaffAccount>>(
    "admin-staff",
    async (signal) =>
      asPaged(await api.get("/staff", { pageSize: 200 }, signal)),
    tab === "staff",
  );
  const roles = useApiQuery<PagedResponse<RoleDefinition>>(
    "admin-roles",
    async (signal) => asPaged(await api.get("/roles", undefined, signal)),
    tab === "staff",
  );

  function setTab(value: AdminTab) {
    const next = new URLSearchParams(searchParams);
    next.set("tab", value);
    setSearchParams(next, { replace: true });
  }

  return (
    <div className="page admin-page">
      <PageHeader
        eyebrow="システム管理"
        title="管理"
        description="AI接続、職員アカウント、保存容量を管理します。"
        actions={
          health.data?.maintenanceMode ? (
            <Badge tone="warning">メンテナンスモード</Badge>
          ) : undefined
        }
      />
      <Tabs
        value={tab}
        onChange={setTab}
        label="管理項目"
        tabs={[
          { value: "health", label: "システム状態" },
          { value: "ai", label: "AI設定" },
          {
            value: "staff",
            label: "職員アカウント",
            count: staff.data?.totalApproximate,
          },
          { value: "storage", label: "保存容量" },
          { value: "jobs", label: "処理状況" },
        ]}
      />
      {tab === "health" ? (
        <HealthView
          query={health}
          connections={connections}
          backups={backups}
        />
      ) : null}
      {tab === "ai" ? (
        <AiConfigurationView
          connections={connections}
          profiles={aiProfiles}
          budget={aiBudget}
          usage={aiUsage}
          metrics={aiMetrics}
          pricing={aiPricing}
        />
      ) : null}
      {tab === "staff" ? <StaffView query={staff} roles={roles} /> : null}
      {tab === "storage" ? <StorageView query={storage} /> : null}
      {tab === "jobs" ? <JobsView query={jobs} /> : null}
    </div>
  );
}

function StaffView({
  query,
  roles,
}: {
  query: ReturnType<typeof useApiQuery<PagedResponse<StaffAccount>>>;
  roles: ReturnType<typeof useApiQuery<PagedResponse<RoleDefinition>>>;
}) {
  const [search, setSearch] = useState("");
  const [editorOpen, setEditorOpen] = useState(false);
  const [editing, setEditing] = useState<StaffAccount>();
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [selectedRoles, setSelectedRoles] = useState<StaffRole[]>(["teacher"]);
  const [temporaryPassword, setTemporaryPassword] = useState("");
  const [resetTarget, setResetTarget] = useState<StaffAccount>();
  const [statusTarget, setStatusTarget] = useState<StaffAccount>();
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();

  const visible = query.data?.items.filter((account) => {
    const normalized = search.trim().toLocaleLowerCase("ja");
    return (
      !normalized ||
      account.displayName.toLocaleLowerCase("ja").includes(normalized) ||
      account.username.toLocaleLowerCase("ja").includes(normalized) ||
      account.roles.some((role) =>
        staffRoleLabels[role].toLocaleLowerCase("ja").includes(normalized),
      )
    );
  });

  function openCreate() {
    setEditing(undefined);
    setUsername("");
    setDisplayName("");
    setSelectedRoles(["teacher"]);
    setTemporaryPassword("");
    setError(undefined);
    setEditorOpen(true);
  }

  function openEdit(account: StaffAccount) {
    setEditing(account);
    setUsername(account.username);
    setDisplayName(account.displayName);
    setSelectedRoles(account.roles);
    setTemporaryPassword("");
    setError(undefined);
    setEditorOpen(true);
  }

  function toggleRole(role: StaffRole) {
    setSelectedRoles((current) =>
      current.includes(role)
        ? current.filter((value) => value !== role)
        : [...current, role],
    );
  }

  async function saveAccount() {
    setWorking(true);
    setError(undefined);
    try {
      if (editing) {
        await api.patch(`/staff/${encodeURIComponent(editing.id)}`, {
          revision: editing.revision,
          username: username.trim(),
          displayName: displayName.trim(),
          roles: selectedRoles,
        });
        setMessage("職員アカウントを更新しました。");
      } else {
        await api.post(
          "/staff",
          {
            username: username.trim(),
            displayName: displayName.trim(),
            initialPassword: temporaryPassword,
            roles: selectedRoles,
          },
          { idempotencyKey: newIdempotencyKey() },
        );
        setMessage(
          "職員アカウントを作成しました。一時パスワードは24時間以内に一度だけ使用できます。",
        );
      }
      setEditorOpen(false);
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "職員アカウントを保存できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function resetPassword() {
    if (!resetTarget) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/staff/${encodeURIComponent(resetTarget.id)}:resetPassword`,
        {
          revision: resetTarget.revision,
          newPassword: temporaryPassword,
          reasonCode: "administrator_reset",
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      setMessage(
        `${resetTarget.displayName}さんの一時パスワードを更新しました。30分以内に一度だけ使用できます。`,
      );
      setResetTarget(undefined);
      setTemporaryPassword("");
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "パスワードをリセットできませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  async function changeStatus() {
    if (!statusTarget) return;
    const disabling = statusTarget.status === "active";
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        `/staff/${encodeURIComponent(statusTarget.id)}:${
          disabling ? "disable" : "enable"
        }`,
        {
          revision: statusTarget.revision,
          reasonCode: disabling ? "account_disabled" : "account_enabled",
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      setMessage(
        `${statusTarget.displayName}さんのアカウントを${
          disabling ? "無効化" : "再有効化"
        }しました。`,
      );
      setStatusTarget(undefined);
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "アカウント状態を変更できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  if (query.status === "loading" || roles.status === "loading") {
    return <LoadingState label="職員アカウントを読み込んでいます" />;
  }
  if (query.status === "error" || !query.data) {
    return <ErrorState error={query.error} onRetry={query.reload} />;
  }
  if (roles.status === "error" || !roles.data) {
    return <ErrorState error={roles.error} onRetry={roles.reload} />;
  }

  const canSave =
    username.trim() &&
    displayName.trim() &&
    selectedRoles.length > 0 &&
    (editing || temporaryPassword.length >= 12);

  return (
    <div className="stack">
      {message ? (
        <InlineAlert tone="success">
          <p>{message}</p>
        </InlineAlert>
      ) : null}
      {!editorOpen && !resetTarget && !statusTarget && error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
      <Card>
        <div className="card__header staff-toolbar">
          <div>
            <h2>職員アカウント</h2>
            <p>役割、利用状態、パスワードの初回設定を管理します。</p>
          </div>
          <Button leadingIcon="plus" onClick={openCreate}>
            職員を追加
          </Button>
        </div>
        <div className="staff-filter">
          <SearchInput
            value={search}
            onChange={setSearch}
            placeholder="氏名、ユーザー名、役割で検索"
            label="職員を検索"
          />
          <span>{visible?.length ?? 0}件</span>
        </div>
        {visible?.length ? (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>職員</th>
                  <th>役割</th>
                  <th>状態</th>
                  <th>最終ログイン</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {visible.map((account) => (
                  <tr key={account.id}>
                    <td>
                      <strong>{account.displayName}</strong>
                      <small>{account.username}</small>
                    </td>
                    <td>
                      <div className="staff-role-list">
                        {account.roles.map((role) => (
                          <Badge key={role}>{staffRoleLabels[role]}</Badge>
                        ))}
                      </div>
                    </td>
                    <td>
                      <Badge
                        tone={
                          account.status === "active" ? "success" : "neutral"
                        }
                        dot
                      >
                        {account.status === "active" ? "有効" : "無効"}
                      </Badge>
                      {account.mustChangePassword ? (
                        <small className="staff-setup-state">
                          パスワード変更待ち
                        </small>
                      ) : null}
                    </td>
                    <td>{formatDateTime(account.lastLoginAt)}</td>
                    <td>
                      <div className="row-actions">
                        <Button
                          size="small"
                          variant="secondary"
                          onClick={() => openEdit(account)}
                        >
                          編集
                        </Button>
                        <Button
                          size="small"
                          variant="quiet"
                          onClick={() => {
                            setTemporaryPassword("");
                            setError(undefined);
                            setResetTarget(account);
                          }}
                        >
                          パスワード再設定
                        </Button>
                        <Button
                          size="small"
                          variant={
                            account.status === "active" ? "danger" : "quiet"
                          }
                          onClick={() => {
                            setError(undefined);
                            setStatusTarget(account);
                          }}
                        >
                          {account.status === "active" ? "無効化" : "再有効化"}
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <EmptyState
            icon="students"
            title="該当する職員がいません"
            description="検索条件を変えるか、新しい職員アカウントを追加してください。"
          />
        )}
      </Card>

      <Modal
        open={editorOpen}
        onClose={() => !working && setEditorOpen(false)}
        title={editing ? "職員アカウントを編集" : "職員アカウントを追加"}
        description="ユーザー名は大文字・小文字を区別せず、一意である必要があります。"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setEditorOpen(false)}
              disabled={working}
            >
              キャンセル
            </Button>
            <Button
              onClick={() => void saveAccount()}
              disabled={working || !canSave}
            >
              {working ? "保存しています…" : "保存"}
            </Button>
          </>
        }
      >
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
        <div className="form-grid form-grid--2">
          <Field label="表示名" htmlFor="staff-display-name" required>
            <input
              id="staff-display-name"
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              autoComplete="off"
            />
          </Field>
          <Field label="ユーザー名" htmlFor="staff-username" required>
            <input
              id="staff-username"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              autoCapitalize="none"
              autoComplete="off"
            />
          </Field>
        </div>
        {!editing ? (
          <Field
            label="一時パスワード"
            htmlFor="staff-initial-password"
            required
            hint="12文字以上。本人へ安全な方法で伝え、24時間以内に変更してもらいます。"
          >
            <input
              id="staff-initial-password"
              type="password"
              autoComplete="new-password"
              value={temporaryPassword}
              onChange={(event) => setTemporaryPassword(event.target.value)}
            />
          </Field>
        ) : null}
        <fieldset className="staff-role-picker">
          <legend>役割</legend>
          {roles.data.items.map((role) => (
            <label className="setting-check" key={role.name}>
              <input
                type="checkbox"
                checked={selectedRoles.includes(role.name)}
                onChange={() => toggleRole(role.name)}
              />
              <span>
                <strong>{staffRoleLabels[role.name]}</strong>
                <small>{roleDescription(role.name)}</small>
              </span>
            </label>
          ))}
        </fieldset>
      </Modal>

      <Modal
        open={Boolean(resetTarget)}
        onClose={() => !working && setResetTarget(undefined)}
        title="一時パスワードを再設定"
        description={`${resetTarget?.displayName ?? ""}さんの現在のセッションはすべて終了します。`}
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setResetTarget(undefined)}
              disabled={working}
            >
              キャンセル
            </Button>
            <Button
              onClick={() => void resetPassword()}
              disabled={working || temporaryPassword.length < 12}
            >
              {working ? "再設定しています…" : "一時パスワードを再設定"}
            </Button>
          </>
        }
      >
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
        <Field
          label="新しい一時パスワード"
          htmlFor="staff-reset-password"
          required
          hint="30分以内に一度だけ使用できます。初回ログイン後に本人が変更します。"
        >
          <input
            id="staff-reset-password"
            type="password"
            autoComplete="new-password"
            value={temporaryPassword}
            onChange={(event) => setTemporaryPassword(event.target.value)}
          />
        </Field>
      </Modal>

      <Modal
        open={Boolean(statusTarget)}
        onClose={() => !working && setStatusTarget(undefined)}
        title={
          statusTarget?.status === "active"
            ? "職員アカウントを無効化"
            : "職員アカウントを再有効化"
        }
        description={`${statusTarget?.displayName ?? ""}さんの利用状態を変更します。`}
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setStatusTarget(undefined)}
              disabled={working}
            >
              キャンセル
            </Button>
            <Button
              variant={
                statusTarget?.status === "active" ? "danger" : "primary"
              }
              onClick={() => void changeStatus()}
              disabled={working}
            >
              {working
                ? "変更しています…"
                : statusTarget?.status === "active"
                  ? "無効化する"
                  : "再有効化する"}
            </Button>
          </>
        }
      >
        <InlineAlert
          tone={statusTarget?.status === "active" ? "warning" : "info"}
        >
          <p>
            {statusTarget?.status === "active"
              ? "保存と同時に、この職員のログイン中セッションをすべて終了します。最後の有効な管理者は無効化できません。"
              : "再有効化後も、既存のセッションは復元されません。本人が改めてログインします。"}
          </p>
        </InlineAlert>
        {error ? (
          <InlineAlert tone="danger">
            <p>{error}</p>
          </InlineAlert>
        ) : null}
      </Modal>
    </div>
  );
}

function roleDescription(role: StaffRole) {
  switch (role) {
    case "administrator":
      return "職員、設定、保存、すべての採点機能を管理できます。";
    case "teacher":
      return "生徒、ひな形、採点、確定、帳票を操作できます。";
    case "scanOperator":
      return "許可されたテスト実施で答案をアップロードできます。";
    case "readOnlyReviewer":
      return "確定済みの結果と進捗だけを閲覧できます。";
  }
}

function AiConfigurationView({
  connections,
  profiles,
  budget,
  usage,
  metrics,
  pricing,
}: {
  connections: ReturnType<
    typeof useApiQuery<PagedResponse<AiConnection>>
  >;
  profiles: ReturnType<
    typeof useApiQuery<PagedResponse<AiTaskProfile>>
  >;
  budget: ReturnType<typeof useApiQuery<AiBudget>>;
  usage: ReturnType<typeof useApiQuery<AiUsage>>;
  metrics: ReturnType<typeof useApiQuery<AiMetrics>>;
  pricing: ReturnType<
    typeof useApiQuery<PagedResponse<AiPricingSnapshot>>
  >;
}) {
  const connectionItems = connections.data?.items ?? [];
  const geminiConnection = connectionItems.find(
    (item) => item.provider === "geminiDirect",
  );
  const openRouterConnection = connectionItems.find(
    (item) => item.provider === "openRouter",
  );
  const [connectionEditor, setConnectionEditor] = useState<{
    provider: AiProvider;
    connection?: AiConnection;
  }>();
  const [apiKey, setApiKey] = useState("");
  const [modelId, setModelId] = useState(geminiDefaultModel);
  const [timeoutSeconds, setTimeoutSeconds] = useState("75");
  const [concurrencyLimit, setConcurrencyLimit] = useState("2");
  const [probingConnectionId, setProbingConnectionId] = useState<string>();
  const [working, setWorking] = useState(false);
  const [message, setMessage] = useState<string>();
  const [error, setError] = useState<string>();

  function openConnectionEditor(
    provider: AiProvider,
    connection?: AiConnection,
  ) {
    setApiKey("");
    setModelId(
      connection?.modelId || defaultModelForProvider(provider),
    );
    setTimeoutSeconds(String(connection?.timeoutSeconds ?? 75));
    setConcurrencyLimit(String(connection?.concurrencyLimit ?? 2));
    setError(undefined);
    setConnectionEditor({ provider, connection });
  }

  function closeConnectionEditor() {
    if (working) return;
    setApiKey("");
    setConnectionEditor(undefined);
    setError(undefined);
  }

  function selectConnectionProvider(provider: AiProvider) {
    if (connectionEditor?.connection) return;
    setConnectionEditor({ provider });
    setModelId(defaultModelForProvider(provider));
    setError(undefined);
  }

  async function saveConnection() {
    if (!connectionEditor) return;
    const provider = connectionEditor.provider;
    const connection = connectionEditor.connection;
    const selectedModelId = modelId.trim();
    const automaticSetup = provider === "geminiDirect";
    setWorking(true);
    setMessage(undefined);
    setError(undefined);
    try {
      const body = {
        apiKey,
        provider,
        modelId: selectedModelId,
        timeoutSeconds: Number(timeoutSeconds),
        concurrencyLimit: Number(concurrencyLimit),
        revision: connection?.revision,
        testAndEnable: automaticSetup,
      };
      if (connection) {
        await api.put(
          `/admin/ai-connections/${encodeURIComponent(connection.id)}`,
          body,
          { idempotencyKey: newIdempotencyKey() },
        );
      } else {
        await api.post("/admin/ai-connections", body, {
          idempotencyKey: newIdempotencyKey(),
        });
      }
      setApiKey("");
      setConnectionEditor(undefined);
      setMessage(
        automaticSetup
          ? "Geminiの接続を確認して保存しました。全てのAI機能を利用できます。"
          : "OpenRouterのAPIキーを保存しました。「再確認」を押して接続を確認してください。",
      );
      connections.reload();
      profiles.reload();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : `${aiProviderLabel(provider)}接続を保存できませんでした。`,
      );
    } finally {
      setWorking(false);
    }
  }

  async function probeConnection(connection: AiConnection) {
    setWorking(true);
    setProbingConnectionId(connection.id);
    setMessage(undefined);
    setError(undefined);
    try {
      const result = await api.post<AiCapabilityProbeResult>(
        `/admin/ai-connections/${encodeURIComponent(connection.id)}:test`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      if (
        result.state === "passed" &&
        result.authentication &&
        result.modelAvailable &&
        result.imageInput &&
        result.structuredOutput &&
        result.usageMetadata
      ) {
        setMessage(
          connection.provider === "geminiDirect"
            ? "Geminiとの接続と必要な機能を確認しました。全てのAI機能を利用できます。"
            : "OpenRouterとの接続と必要な機能を確認しました。既定のAI機能はGemini設定を使用します。",
        );
      } else if (isDeepSeekV4Flash(connection.modelId)) {
        setError(
          "DeepSeek V4 Flash / 0731 はテキスト専用です。画像入力を使うOoki Graderでは、接続確認により利用不可としてブロックされました。",
        );
      } else {
        const code = result.safeErrorCode
          ? `（${result.safeErrorCode}）`
          : "";
        setError(
          `${aiProviderLabel(connection.provider)}には接続しましたが、認証・モデル・画像入力・構造化出力・使用量情報をすべて確認できないため利用できません${code}。`,
        );
      }
      connections.reload();
      profiles.reload();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : `${aiProviderLabel(connection.provider)}接続を確認できませんでした。`,
      );
      connections.reload();
      profiles.reload();
    } finally {
      setProbingConnectionId(undefined);
      setWorking(false);
    }
  }

  const connectionLoading = connections.status === "loading";
  const profileItems = profiles.data?.items ?? [];
  const aiTasks = [
    "templateExtraction",
    "nameTranscription",
    "initialGrading",
    "adjudication",
  ];

  return (
    <div className="stack">
      {message ? (
        <InlineAlert tone="success">
          <p>{message}</p>
        </InlineAlert>
      ) : null}
      {!connectionEditor && error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
      <InlineAlert tone="info">
        <p>
          Geminiがひな形作成と採点を補助します。AIの結果は、受付開始・答案確定前に先生が確認できます。
        </p>
      </InlineAlert>

      <div className="admin-two-column">
        <AiConnectionCard
          provider="geminiDirect"
          connection={geminiConnection}
          loading={connectionLoading}
          loadError={connections.status === "error" ? connections.error : undefined}
          working={working}
          probing={probingConnectionId === geminiConnection?.id}
          onRetry={connections.reload}
          onEdit={() =>
            openConnectionEditor("geminiDirect", geminiConnection)
          }
          onProbe={probeConnection}
        />

        <Card>
          <div className="card__header">
            <div>
              <h2>直近30日のAI使用量</h2>
              <p>
                Geminiはトークン数と固定価格スナップショット、OpenRouterは返却された実費で集計します。
              </p>
            </div>
          </div>
          {usage.status === "loading" ? (
            <LoadingState compact />
          ) : usage.status === "error" || !usage.data ? (
            <ErrorState error={usage.error} onRetry={usage.reload} compact />
          ) : (
            <div className="stack">
              <dl className="definition-grid definition-grid--compact">
                <div>
                  <dt>リクエスト</dt>
                  <dd>{usage.data.requestCount}件</dd>
                </div>
                <div>
                  <dt>推定費用</dt>
                  <dd>{formatUsdMicros(usage.data.estimatedUsdMicros)}</dd>
                </div>
                <div>
                  <dt>円換算</dt>
                  <dd>{formatJpyMicros(usage.data.estimatedJpyMicros)}</dd>
                </div>
                <div>
                  <dt>集計期間</dt>
                  <dd>
                    {usage.data.from}〜{usage.data.to}
                  </dd>
                </div>
              </dl>
              {usage.data.byModel.map((item) => (
                <div key={`${item.provider}:${item.model}`}>
                  <strong>{item.model}</strong>
                  <small>
                    {item.requestCount}件・{item.totalTokens.toLocaleString()} tokens
                  </small>
                </div>
              ))}
            </div>
          )}
        </Card>
      </div>

      <details className="admin-advanced-details">
        <summary>OpenRouter（任意設定）</summary>
        <div className="stack">
          <InlineAlert tone="info">
            <p>
              通常は既定のGeminiだけで運用できます。学校で別モデルを評価するときだけ、Geminiとは別の接続として追加してください。
            </p>
          </InlineAlert>
          <AiConnectionCard
            provider="openRouter"
            connection={openRouterConnection}
            loading={connectionLoading}
            loadError={
              connections.status === "error" ? connections.error : undefined
            }
            working={working}
            probing={probingConnectionId === openRouterConnection?.id}
            onRetry={connections.reload}
            onEdit={() =>
              openConnectionEditor("openRouter", openRouterConnection)
            }
            onProbe={probeConnection}
          />
        </div>
      </details>

      <Card>
        <div className="card__header">
          <div>
            <h2>利用中のAI機能</h2>
            <p>
              ひな形作成、氏名読み取り、採点、判定の再確認に使う設定です。
            </p>
          </div>
        </div>
        {profiles.status === "loading" ? (
          <LoadingState compact />
        ) : profiles.status === "error" ? (
          <ErrorState error={profiles.error} onRetry={profiles.reload} />
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>AI機能</th>
                  <th>状態</th>
                </tr>
              </thead>
              <tbody>
                {aiTasks.map((taskType) => {
                  const available = profileItems.some(
                    (profile) => {
                      const owningConnection = connectionItems.find(
                        (connection) => connection.id === profile.connectionId,
                      );
                      return (
                        profile.taskType === taskType &&
                        profile.active &&
                        !profile.stale &&
                        owningConnection?.state === "active" &&
                        owningConnection.lastCapabilityProbe?.state === "passed"
                      );
                    },
                  );
                  return (
                    <tr key={taskType}>
                      <td>
                        <strong>{aiTaskLabel(taskType)}</strong>
                      </td>
                      <td>
                        <Badge tone={available ? "success" : "warning"}>
                          {available ? "利用できます" : "APIキーを再設定"}
                        </Badge>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <details className="admin-advanced-details">
        <summary>利用量・費用の詳細設定</summary>
        <div className="stack">
          <AiMetricsPanel query={metrics} />
          {pricing.status === "loading" ? (
            <LoadingState label="AI価格情報を読み込んでいます" />
          ) : pricing.status === "error" ? (
            <ErrorState error={pricing.error} onRetry={pricing.reload} />
          ) : (
            <AiPricingEditor
              connections={connectionItems}
              snapshots={pricing.data?.items ?? []}
              onSaved={(provider, savedModelId) => {
                setMessage(
                  `${aiProviderLabel(provider)} ${savedModelId} の公式価格スナップショットを保存しました。`,
                );
                pricing.reload();
              }}
            />
          )}

          {budget.status === "loading" ? (
            <LoadingState label="AI予算を読み込んでいます" />
          ) : budget.status === "error" || !budget.data ? (
            <ErrorState error={budget.error} onRetry={budget.reload} />
          ) : (
            <AiBudgetEditor
              key={budget.data.revision}
              value={budget.data}
              onSaved={() => {
                setMessage("AI予算の警告値と上限を保存しました。");
                budget.reload();
              }}
            />
          )}
        </div>
      </details>

      <Modal
        open={Boolean(connectionEditor)}
        onClose={closeConnectionEditor}
        title={
          connectionEditor?.connection
            ? `${aiProviderLabel(connectionEditor.provider)} APIキーを交換`
            : "AI接続を追加"
        }
        description={
          connectionEditor?.provider === "geminiDirect"
            ? "入力したキーをすぐに接続確認し、成功した場合だけ暗号化して保存します。"
            : "APIキーを暗号化して保存します。保存後、「再確認」で接続を確認してください。"
        }
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={closeConnectionEditor}
              disabled={working}
            >
              キャンセル
            </Button>
            <Button
              onClick={saveConnection}
              disabled={
                working ||
                !connectionEditor ||
                apiKey.length < 20 ||
                !isValidAiModelId(connectionEditor.provider, modelId.trim()) ||
                Number(timeoutSeconds) < 5 ||
                Number(timeoutSeconds) > 300 ||
                Number(concurrencyLimit) < 1 ||
                Number(concurrencyLimit) > 16
              }
            >
              {working
                ? connectionEditor?.provider === "geminiDirect"
                  ? "確認中…"
                  : "保存中…"
                : connectionEditor?.provider === "geminiDirect"
                  ? "接続を確認して有効化"
                  : "暗号化して保存"}
            </Button>
          </>
        }
      >
        <div className="stack">
          <Field
            label="接続先"
            htmlFor="ai-connection-provider"
            required
            hint={
              connectionEditor?.connection
                ? "既存接続の種類は変更できません。"
                : "通常は既定のGeminiを選びます。"
            }
          >
            <select
              id="ai-connection-provider"
              value={connectionEditor?.provider ?? "geminiDirect"}
              disabled={Boolean(connectionEditor?.connection)}
              onChange={(event) =>
                selectConnectionProvider(event.target.value as AiProvider)
              }
            >
              <option
                value="geminiDirect"
                disabled={
                  !connectionEditor?.connection && Boolean(geminiConnection)
                }
              >
                Gemini（既定）
              </option>
              <option
                value="openRouter"
                disabled={
                  !connectionEditor?.connection && Boolean(openRouterConnection)
                }
              >
                OpenRouter（任意）
              </option>
            </select>
          </Field>
          {connectionEditor?.provider === "openRouter" ? (
            <Field
              label="OpenRouterモデルID"
              htmlFor="ai-connection-model"
              required
              hint="OpenRouterに表示される provider/model の完全なslugを、そのまま入力します。"
            >
              <input
                id="ai-connection-model"
                value={modelId}
                onChange={(event) => setModelId(event.target.value)}
                placeholder="google/gemini-3.1-flash-lite"
                autoCapitalize="none"
                autoCorrect="off"
                spellCheck={false}
              />
            </Field>
          ) : (
            <InlineAlert tone="info">
              <p>
                既定モデル: <strong>{modelId}</strong>
              </p>
            </InlineAlert>
          )}
          {connectionEditor?.provider === "openRouter" &&
          modelId.trim() &&
          !isValidAiModelId("openRouter", modelId.trim()) ? (
            <InlineAlert tone="danger">
              <p>
                モデルIDは省略せず、OpenRouterの正確な provider/model slugで入力してください。
              </p>
            </InlineAlert>
          ) : null}
          {connectionEditor?.provider === "openRouter" &&
          isDeepSeekV4Flash(modelId) ? (
            <InlineAlert tone="warning">
              <p>
                DeepSeek V4 Flash / 0731 はテキスト専用です。画像処理には使用できず、接続確認でも画像非対応としてブロックされます。
              </p>
            </InlineAlert>
          ) : null}
          <Field
            label={`${aiProviderLabel(connectionEditor?.provider)} APIキー`}
            htmlFor="ai-connection-api-key"
            required
            hint="送信後、この画面にAPIキーは保存・再表示されません。"
          >
            <input
              id="ai-connection-api-key"
              type="password"
              autoComplete="new-password"
              value={apiKey}
              onChange={(event) => setApiKey(event.target.value)}
            />
          </Field>
          <details className="admin-advanced-details">
            <summary>詳細設定</summary>
            <div className="stack">
              <Field label="応答待ち時間（秒）" htmlFor="ai-connection-timeout">
                <input
                  id="ai-connection-timeout"
                  type="number"
                  min={5}
                  max={300}
                  value={timeoutSeconds}
                  onChange={(event) => setTimeoutSeconds(event.target.value)}
                />
              </Field>
              <Field label="最大同時処理数" htmlFor="ai-connection-concurrency">
                <input
                  id="ai-connection-concurrency"
                  type="number"
                  min={1}
                  max={16}
                  value={concurrencyLimit}
                  onChange={(event) => setConcurrencyLimit(event.target.value)}
                />
              </Field>
            </div>
          </details>
          {error ? (
            <InlineAlert tone="danger">
              <p>{error}</p>
            </InlineAlert>
          ) : null}
        </div>
      </Modal>

    </div>
  );
}

function AiConnectionCard({
  provider,
  connection,
  loading,
  loadError,
  working,
  probing,
  onRetry,
  onEdit,
  onProbe,
}: {
  provider: AiProvider;
  connection?: AiConnection;
  loading: boolean;
  loadError?: Error;
  working: boolean;
  probing: boolean;
  onRetry: () => void;
  onEdit: () => void;
  onProbe: (connection: AiConnection) => void;
}) {
  const providerLabel = aiProviderLabel(provider);
  const probe = connection?.lastCapabilityProbe;
  const deepSeekTextOnly = Boolean(
    connection && isDeepSeekV4Flash(connection.modelId),
  );

  return (
    <Card>
      <div className="card__header">
        <div>
          <div className="button-row">
            <h2>{providerLabel}接続</h2>
            <Badge tone={provider === "geminiDirect" ? "accent" : "neutral"}>
              {provider === "geminiDirect" ? "既定" : "任意"}
            </Badge>
          </div>
          <p>
            {provider === "geminiDirect"
              ? "通常のひな形作成と採点に使う、推奨の接続です。"
              : "学校が評価したOpenRouterモデルを必要な場合だけ追加します。"}
          </p>
        </div>
        <Button
          variant="secondary"
          onClick={onEdit}
          disabled={loading || working}
        >
          {connection ? "APIキーを交換" : "接続を追加"}
        </Button>
      </div>
      {loading ? (
        <LoadingState compact />
      ) : loadError ? (
        <ErrorState error={loadError} onRetry={onRetry} compact />
      ) : connection ? (
        <div className="stack">
          <dl className="definition-grid definition-grid--compact">
            <div>
              <dt>モデル</dt>
              <dd>{connection.modelId}</dd>
            </div>
            <div>
              <dt>最終接続確認</dt>
              <dd>{formatDateTime(probe?.checkedAt)}</dd>
            </div>
          </dl>
          <div className="button-row" aria-label={`${providerLabel}接続状態`}>
            <span>
              接続状態{" "}
              <StatusBadge status={probe?.state || connection.state} />
            </span>
            <CapabilityBadge label="画像入力" value={probe?.imageInput} />
            <CapabilityBadge
              label="構造化出力"
              value={probe?.structuredOutput}
            />
          </div>
          <div className="button-row">
            <Button onClick={() => onProbe(connection)} disabled={working}>
              {probing ? "再確認中…" : "再確認"}
            </Button>
          </div>
          {deepSeekTextOnly ? (
            <InlineAlert tone="warning">
              <p>
                DeepSeek V4 Flash / 0731 はテキスト専用です。画像処理には使用できず、接続確認でも画像非対応としてブロックされます。
              </p>
            </InlineAlert>
          ) : probe?.safeErrorCode ? (
            <InlineAlert tone="danger">
              <p>
                接続を利用できません（{probe.safeErrorCode}）。APIキー、モデルID、ネットワーク、画像対応を確認してください。
              </p>
            </InlineAlert>
          ) : null}
        </div>
      ) : (
        <EmptyState
          icon="connection"
          title={`${providerLabel}接続が未設定です`}
          description={
            provider === "geminiDirect"
              ? "学校が管理する公式Gemini APIキーを追加してください。"
              : "任意設定です。追加しなくても既定のGeminiで運用できます。"
          }
        />
      )}
    </Card>
  );
}

function CapabilityBadge({
  label,
  value,
}: {
  label: string;
  value?: boolean;
}) {
  return (
    <Badge
      tone={value === true ? "success" : value === false ? "danger" : "neutral"}
    >
      {label}: {value === true ? "対応" : value === false ? "非対応" : "未確認"}
    </Badge>
  );
}

function AiMetricsPanel({
  query,
}: {
  query: ReturnType<typeof useApiQuery<AiMetrics>>;
}) {
  const metrics = query.data;
  const totals = metrics?.totals;
  const categorizedErrors = totals
    ? totals.errors.rateLimited429 +
      totals.errors.provider5Xx +
      totals.errors.schemaOrOutputValidation
    : 0;

  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>AI運用メトリクス</h2>
          <p>
            直近30日の匿名集計です。プロンプト、応答、答案、生徒情報は含みません。
          </p>
        </div>
        <Button
          variant="secondary"
          onClick={query.reload}
          disabled={query.status === "loading"}
        >
          更新
        </Button>
      </div>
      {query.status === "loading" ? (
        <LoadingState compact />
      ) : query.status === "error" || !metrics || !totals ? (
        <ErrorState error={query.error} onRetry={query.reload} compact />
      ) : totals.requestCount === 0 ? (
        <EmptyState
          icon="connection"
          title="集計対象のAI処理はありません"
          description="AI処理が実行されると、待ち時間、成功率、再試行、費用がここに表示されます。"
        />
      ) : (
        <div className="stack">
          <dl className="definition-grid definition-grid--compact">
            <div>
              <dt>リクエスト</dt>
              <dd>{totals.requestCount.toLocaleString()}件</dd>
            </div>
            <div>
              <dt>成功 / 失敗</dt>
              <dd>
                {totals.successCount.toLocaleString()} /{" "}
                {totals.failureCount.toLocaleString()}
              </dd>
            </div>
            <div>
              <dt>再試行</dt>
              <dd>
                {totals.retriedRequestCount.toLocaleString()}件・
                {totals.retryAttemptCount.toLocaleString()}回
              </dd>
            </div>
            <div>
              <dt>送信結果不明</dt>
              <dd>{totals.ambiguousCount.toLocaleString()}件</dd>
            </div>
            <div>
              <dt>キュー待ち 平均 / p95</dt>
              <dd>
                {formatDurationMilliseconds(
                  totals.queueWait.averageMilliseconds,
                )}{" "}
                /{" "}
                {formatDurationMilliseconds(
                  totals.queueWait.p95Milliseconds,
                )}
              </dd>
            </div>
            <div>
              <dt>プロバイダー処理 平均 / p95</dt>
              <dd>
                {formatDurationMilliseconds(
                  totals.providerLatency.averageMilliseconds,
                )}{" "}
                /{" "}
                {formatDurationMilliseconds(
                  totals.providerLatency.p95Milliseconds,
                )}
              </dd>
            </div>
            <div>
              <dt>トークン / 推定費用</dt>
              <dd>
                {totals.tokens.total.toLocaleString()} /{" "}
                {formatUsdMicros(totals.cost.estimatedUsdMicros)}
              </dd>
            </div>
            <div>
              <dt>先生による修正率</dt>
              <dd>
                {totals.teacherCorrection.available &&
                totals.teacherCorrection.rateBasisPoints !== null
                  ? `${(totals.teacherCorrection.rateBasisPoints / 100).toFixed(
                      1,
                    )}%`
                  : "集計待ち"}
              </dd>
            </div>
          </dl>
          <div className="button-row" aria-label="AIリクエスト状態">
            {metrics.stateCounts.map((item) => (
              <span key={item.state}>
                <StatusBadge status={item.state} />{" "}
                {item.count.toLocaleString()}
              </span>
            ))}
          </div>
          {categorizedErrors > 0 ? (
            <InlineAlert tone="warning">
              <p>
                記録中のエラー分類: 429{" "}
                {totals.errors.rateLimited429.toLocaleString()}件、5xx{" "}
                {totals.errors.provider5Xx.toLocaleString()}件、構造・出力検証{" "}
                {totals.errors.schemaOrOutputValidation.toLocaleString()}件。
              </p>
            </InlineAlert>
          ) : null}
          {metrics.sampling.latencySamplesTruncated ? (
            <InlineAlert tone="info">
              <p>
                待ち時間は最新
                {metrics.sampling.latencySampleLimit.toLocaleString()}
                件を標本として表示しています。
              </p>
            </InlineAlert>
          ) : null}
        </div>
      )}
    </Card>
  );
}

function AiPricingEditor({
  connections,
  snapshots,
  onSaved,
}: {
  connections: AiConnection[];
  snapshots: AiPricingSnapshot[];
  onSaved: (provider: AiProvider, modelId: string) => void;
}) {
  const preferredConnection =
    connections.find((item) => item.provider === "geminiDirect") ??
    connections[0];
  const [selectedConnectionId, setSelectedConnectionId] = useState(
    preferredConnection?.id ?? "",
  );
  const selectedConnection = connections.find(
    (item) => item.id === selectedConnectionId,
  );
  const latest = selectedConnection
    ? snapshots.find(
        (item) =>
          item.provider === selectedConnection.provider &&
          item.modelId === selectedConnection.modelId,
      )
    : undefined;
  const [inputRate, setInputRate] = useState("");
  const [outputRate, setOutputRate] = useState("");
  const [thinkingRate, setThinkingRate] = useState("");
  const [sourceUrl, setSourceUrl] = useState("");
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (
      selectedConnectionId &&
      connections.some((item) => item.id === selectedConnectionId)
    ) {
      return;
    }
    setSelectedConnectionId(preferredConnection?.id ?? "");
  }, [connections, preferredConnection?.id, selectedConnectionId]);

  useEffect(() => {
    setInputRate(
      latest
        ? String(latest.inputUsdMicrosPerMillionTokens / 1_000_000)
        : "",
    );
    setOutputRate(
      latest
        ? String(latest.outputUsdMicrosPerMillionTokens / 1_000_000)
        : "",
    );
    setThinkingRate(
      latest
        ? String(latest.thinkingUsdMicrosPerMillionTokens / 1_000_000)
        : "",
    );
    setSourceUrl(latest?.sourceUrl || defaultPricingUrl(selectedConnection));
    setError(undefined);
  }, [latest, selectedConnection]);

  async function save() {
    if (!selectedConnection) return;
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        "/admin/pricing-snapshots",
        {
          provider: selectedConnection.provider,
          modelId: selectedConnection.modelId,
          inputUsdMicrosPerMillionTokens: toMicros(inputRate),
          outputUsdMicrosPerMillionTokens: toMicros(outputRate),
          thinkingUsdMicrosPerMillionTokens: toMicros(thinkingRate),
          sourceUrl,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      onSaved(selectedConnection.provider, selectedConnection.modelId);
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "AI価格情報を保存できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  if (!connections.length) {
    return (
      <Card>
        <div className="card__header">
          <div>
            <h2>AI価格スナップショット</h2>
            <p>価格を登録するAI接続を先に保存してください。</p>
          </div>
        </div>
        <EmptyState
          icon="connection"
          title="価格を登録できるAI接続がありません"
          description="Gemini接続を追加すると、ここで費用を設定できます。"
        />
      </Card>
    );
  }

  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>AI価格スナップショット</h2>
          <p>
            接続ごとに公式価格を確認し、100万トークン当たりのUSD単価を履歴として保存します。
          </p>
        </div>
        <Button
          onClick={save}
          disabled={
            working ||
            !selectedConnection ||
            !inputRate ||
            !outputRate ||
            !thinkingRate ||
            !sourceUrl
          }
        >
          {working ? "保存中…" : "価格を保存"}
        </Button>
      </div>
      <Field label="価格を登録する接続" htmlFor="ai-price-connection">
        <select
          id="ai-price-connection"
          value={selectedConnectionId}
          onChange={(event) => setSelectedConnectionId(event.target.value)}
        >
          {connections.map((connection) => (
            <option key={connection.id} value={connection.id}>
              {aiProviderLabel(connection.provider)} — {connection.modelId}
            </option>
          ))}
        </select>
      </Field>
      <div className="form-grid form-grid--four">
        <Field label="入力 USD / 100万token" htmlFor="ai-price-input">
          <input
            id="ai-price-input"
            type="number"
            min={0}
            step="0.000001"
            value={inputRate}
            onChange={(event) => setInputRate(event.target.value)}
          />
        </Field>
        <Field label="出力 USD / 100万token" htmlFor="ai-price-output">
          <input
            id="ai-price-output"
            type="number"
            min={0}
            step="0.000001"
            value={outputRate}
            onChange={(event) => setOutputRate(event.target.value)}
          />
        </Field>
        <Field
          label="思考 USD / 100万token"
          htmlFor="ai-price-thinking"
          hint={
            selectedConnection?.provider === "openRouter"
              ? "OpenRouterは usage.cost の実費を優先します。思考単価が出力単価に含まれるモデルは 0 を入力します。"
              : "公式価格で思考トークンが出力単価に含まれる場合は、同じ費用を二重に登録しないでください。"
          }
        >
          <input
            id="ai-price-thinking"
            type="number"
            min={0}
            step="0.000001"
            value={thinkingRate}
            onChange={(event) => setThinkingRate(event.target.value)}
          />
        </Field>
        <Field
          label="公式価格URL"
          htmlFor="ai-price-source"
          hint={
            selectedConnection?.provider === "openRouter"
              ? "openrouter.ai のモデル価格ページを指定します。"
              : "Google AIの公式価格ページを指定します。"
          }
        >
          <input
            id="ai-price-source"
            type="url"
            value={sourceUrl}
            onChange={(event) => setSourceUrl(event.target.value)}
          />
        </Field>
      </div>
      {latest ? (
        <small>
          最新: {formatDateTime(latest.effectiveAt)}・
          {aiProviderLabel(selectedConnection?.provider)}・{latest.modelId}
        </small>
      ) : (
        <InlineAlert tone="warning">
          <p>
            この接続の価格が未登録です。費用を確定できないため、有効な予算ガードはAI処理を停止します。
          </p>
        </InlineAlert>
      )}
      {error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
    </Card>
  );
}

function defaultPricingUrl(connection?: AiConnection) {
  if (connection?.provider === "openRouter") {
    return `https://openrouter.ai/${connection.modelId}`;
  }
  return "https://ai.google.dev/gemini-api/docs/pricing";
}

function AiBudgetEditor({
  value,
  onSaved,
}: {
  value: AiBudget;
  onSaved: () => void;
}) {
  const [dailyWarning, setDailyWarning] = useState(
    String(value.dailyWarningUsdMicros / 1_000_000),
  );
  const [dailyHard, setDailyHard] = useState(
    String(value.dailyHardUsdMicros / 1_000_000),
  );
  const [monthlyWarning, setMonthlyWarning] = useState(
    String(value.monthlyWarningUsdMicros / 1_000_000),
  );
  const [monthlyHard, setMonthlyHard] = useState(
    String(value.monthlyHardUsdMicros / 1_000_000),
  );
  const [usdToJpy, setUsdToJpy] = useState(
    String(value.usdToJpyMicros / 1_000_000),
  );
  const [active, setActive] = useState(value.active);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();

  async function save() {
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        "/admin/settings/budgets",
        {
          dailyWarningUsdMicros: toMicros(dailyWarning),
          dailyHardUsdMicros: toMicros(dailyHard),
          monthlyWarningUsdMicros: toMicros(monthlyWarning),
          monthlyHardUsdMicros: toMicros(monthlyHard),
          usdToJpyMicros: toMicros(usdToJpy),
          active,
          revision: value.revision,
        },
        { idempotencyKey: newIdempotencyKey() },
      );
      onSaved();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "AI予算を保存できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>AI予算ガード</h2>
          <p>推定費用が上限に達した新規リクエストを停止するための設定です。</p>
        </div>
        <Button onClick={save} disabled={working}>
          {working ? "保存中…" : "予算を保存"}
        </Button>
      </div>
      <div className="form-grid form-grid--four">
        <Field label="日次警告（USD）" htmlFor="ai-daily-warning">
          <input
            id="ai-daily-warning"
            type="number"
            min={0}
            step="0.01"
            value={dailyWarning}
            onChange={(event) => setDailyWarning(event.target.value)}
          />
        </Field>
        <Field label="日次上限（USD）" htmlFor="ai-daily-hard">
          <input
            id="ai-daily-hard"
            type="number"
            min={0}
            step="0.01"
            value={dailyHard}
            onChange={(event) => setDailyHard(event.target.value)}
          />
        </Field>
        <Field label="月次警告（USD）" htmlFor="ai-monthly-warning">
          <input
            id="ai-monthly-warning"
            type="number"
            min={0}
            step="0.01"
            value={monthlyWarning}
            onChange={(event) => setMonthlyWarning(event.target.value)}
          />
        </Field>
        <Field label="月次上限（USD）" htmlFor="ai-monthly-hard">
          <input
            id="ai-monthly-hard"
            type="number"
            min={0}
            step="0.01"
            value={monthlyHard}
            onChange={(event) => setMonthlyHard(event.target.value)}
          />
        </Field>
        <Field label="USD/JPY換算" htmlFor="ai-usd-jpy">
          <input
            id="ai-usd-jpy"
            type="number"
            min={0.01}
            step="0.01"
            value={usdToJpy}
            onChange={(event) => setUsdToJpy(event.target.value)}
          />
        </Field>
        <label>
          <input
            type="checkbox"
            checked={active}
            onChange={(event) => setActive(event.target.checked)}
          />{" "}
          予算上限を有効にする
        </label>
      </div>
      {error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
    </Card>
  );
}

function toMicros(value: string) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= 0
    ? Math.round(parsed * 1_000_000)
    : 0;
}

function formatUsdMicros(value: number) {
  return new Intl.NumberFormat("ja-JP", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 2,
    maximumFractionDigits: 6,
  }).format(value / 1_000_000);
}

function formatJpyMicros(value: number) {
  return new Intl.NumberFormat("ja-JP", {
    style: "currency",
    currency: "JPY",
    maximumFractionDigits: 2,
  }).format(value / 1_000_000);
}

function formatDurationMilliseconds(value: number | null) {
  if (value === null || value < 0) return "—";
  if (value < 1_000) return `${Math.round(value)}ms`;
  if (value < 60_000) return `${(value / 1_000).toFixed(1)}秒`;
  return `${(value / 60_000).toFixed(1)}分`;
}

function aiProviderLabel(provider?: AiProvider) {
  if (provider === "geminiDirect") return "Gemini";
  if (provider === "openRouter") return "OpenRouter";
  return "AI";
}

function defaultModelForProvider(provider: AiProvider) {
  return provider === "geminiDirect" ? geminiDefaultModel : "";
}

function isDeepSeekV4Flash(modelId: string) {
  const normalized = modelId.trim().toLocaleLowerCase("en-US");
  return (
    normalized === deepSeekV4FlashModel ||
    normalized.startsWith(`${deepSeekV4FlashModel}-`) ||
    normalized.startsWith(`${deepSeekV4FlashModel}:`)
  );
}

function isValidAiModelId(provider: AiProvider, modelId: string) {
  if (provider === "geminiDirect") {
    return /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(modelId);
  }
  return (
    /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\/[A-Za-z0-9][A-Za-z0-9._:@+-]{0,62}$/.test(
      modelId,
    ) &&
    !modelId.toLocaleLowerCase("en-US").startsWith("openrouter/") &&
    !modelId.toLocaleLowerCase("en-US").endsWith(":online")
  );
}

function aiTaskLabel(taskType: string) {
  switch (taskType) {
    case "templateExtraction":
      return "ひな形の作成";
    case "nameTranscription":
      return "氏名の読み取り";
    case "initialGrading":
      return "答案のAI採点";
    case "adjudication":
      return "採点結果の再確認";
    default:
      return taskType;
  }
}

function HealthView({
  query,
  connections,
  backups,
}: {
  query: ReturnType<typeof useApiQuery<AdminHealth>>;
  connections: ReturnType<typeof useApiQuery<PagedResponse<AiConnection>>>;
  backups: ReturnType<typeof useApiQuery<BackupListResponse>>;
}) {
  const [backupWorking, setBackupWorking] = useState<string>();
  const [backupMessage, setBackupMessage] = useState<string>();
  const [backupError, setBackupError] = useState<string>();

  async function createBackup() {
    setBackupWorking("create");
    setBackupMessage(undefined);
    setBackupError(undefined);
    try {
      await api.post(
        "/admin/backups",
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setBackupMessage("手動バックアップを受け付けました。");
      backups.reload();
    } catch (reason) {
      setBackupError(
        reason instanceof Error
          ? reason.message
          : "バックアップを開始できませんでした。",
      );
    } finally {
      setBackupWorking(undefined);
    }
  }

  async function verifyBackup(backupId: string) {
    setBackupWorking("verify");
    setBackupMessage(undefined);
    setBackupError(undefined);
    try {
      await api.post(
        `/admin/backups/${encodeURIComponent(backupId)}:verify`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setBackupMessage("バックアップの完全検証を受け付けました。");
      backups.reload();
    } catch (reason) {
      setBackupError(
        reason instanceof Error
          ? reason.message
          : "バックアップを検証できませんでした。",
      );
    } finally {
      setBackupWorking(undefined);
    }
  }

  async function validateRestorePlan(backupId: string) {
    setBackupWorking("restore-plan");
    setBackupMessage(undefined);
    setBackupError(undefined);
    try {
      const plan = await api.get<BackupRestorePlan>(
        `/admin/backups/${encodeURIComponent(backupId)}/restore-plan`,
      );
      setBackupMessage(
        plan.requiresMigration
          ? `復元可能です。オフライン復元後にDB移行が必要です（必要作業 ${plan.requiredActions.length}件）。`
          : `復元可能です（必要作業 ${plan.requiredActions.length}件）。実行はホスト停止後に OokiGrader.Tool を使用します。`,
      );
    } catch (reason) {
      setBackupError(
        reason instanceof Error
          ? reason.message
          : "復元計画を検証できませんでした。",
      );
    } finally {
      setBackupWorking(undefined);
    }
  }

  if (query.status === "loading") {
    return <LoadingState label="システム状態を確認しています" />;
  }
  if (query.status === "error" || !query.data) {
    return <ErrorState error={query.error} onRetry={query.reload} />;
  }
  const health = query.data;
  const actionableComponents = health.components.filter((component) =>
    ["degraded", "unavailable"].includes(component.state),
  );
  const healthyComponents = health.components.filter(
    (component) => component.state === "healthy",
  );
  const displayState = actionableComponents.some(
    (component) => component.state === "unavailable",
  )
    ? "unavailable"
    : actionableComponents.length
      ? "degraded"
      : "healthy";
  const lastBackup = backups.data?.items[0];
  const backupConfiguration = backups.data?.configuration;
  const lastBackupVerified =
    lastBackup?.integrityResult === "ok"
    && Boolean(lastBackup.verifiedAt);
  return (
    <div className="stack">
      <Card
        className={`health-hero health-hero--${displayState}`}
      >
        <span className="health-hero__icon">
          <Icon
            name={displayState === "healthy" ? "check" : "alert"}
            size={30}
          />
        </span>
        <div>
          <span>ホスト全体の状態</span>
          <h2>
            {displayState === "healthy"
              ? "正常に動作しています"
              : "確認が必要な項目があります"}
          </h2>
          <p>
            最終確認:{" "}
            {formatDateTime(
              health.components
                .map((component) => component.checkedAt)
                .filter(Boolean)
                .sort()
                .at(-1),
            )}
          </p>
        </div>
        <StatusBadge status={displayState} />
      </Card>

      {actionableComponents.length ? (
        <section aria-labelledby="components-title">
          <div className="section-heading">
            <div>
              <h2 id="components-title">対応が必要な項目</h2>
              <p>設定または接続を確認してください。</p>
            </div>
          </div>
          <div className="health-component-grid">
            {actionableComponents.map((component) => (
              <HealthComponentCard component={component} key={component.name} />
            ))}
          </div>
        </section>
      ) : null}

      {healthyComponents.length ? (
        <details className="health-components-details">
          <summary>正常な機能の詳細（{healthyComponents.length}項目）</summary>
          <div className="health-component-grid">
            {healthyComponents.map((component) => (
              <HealthComponentCard component={component} key={component.name} />
            ))}
          </div>
        </details>
      ) : null}

      <div className="admin-two-column">
        <Card>
          <div className="card__header">
            <div>
              <h2>AI接続</h2>
              <p>秘密鍵は表示せず、接続確認の結果だけを示します。</p>
            </div>
          </div>
          {connections.status === "loading" ? (
            <LoadingState compact />
          ) : connections.status === "error" ? (
            <ErrorState
              error={connections.error}
              onRetry={connections.reload}
              compact
            />
          ) : connections.data?.items.length ? (
            <div className="connection-list">
              {connections.data.items.map((connection) => (
                <div key={connection.id}>
                  <span className="connection-list__logo">
                    {connection.provider === "geminiDirect" ? "G" : "OR"}
                  </span>
                  <div>
                    <strong>
                      {connection.provider === "geminiDirect"
                        ? "Gemini 3.5 Flash Lite"
                        : "OpenRouter"}
                    </strong>
                    <small>
                      最終接続確認{" "}
                      {formatDateTime(
                        connection.lastCapabilityProbe?.checkedAt,
                      )}
                    </small>
                  </div>
                  <StatusBadge
                    status={
                      connection.lastCapabilityProbe?.state || "unknown"
                    }
                  />
                </div>
              ))}
            </div>
          ) : (
            <EmptyState
              icon="connection"
              title="AI接続が未設定です"
              description="接続を追加するまで、AI処理は安全に待機します。"
            />
          )}
        </Card>
        <Card>
          <div className="card__header">
            <div>
              <h2>バックアップ</h2>
              <p>最終バックアップ、完全検証、オフライン復元の準備状態です。</p>
            </div>
            {backupConfiguration?.enabled &&
            backupConfiguration.configured &&
            backupConfiguration.encryptionConfirmed &&
            backupConfiguration.destinationAccessible ? (
              <Button
                variant="secondary"
                leadingIcon="plus"
                onClick={() => void createBackup()}
                disabled={Boolean(backupWorking)}
              >
                手動バックアップ
              </Button>
            ) : null}
          </div>
          {backupMessage ? (
            <InlineAlert tone="success">
              <p>{backupMessage}</p>
            </InlineAlert>
          ) : null}
          {backupError ? (
            <InlineAlert tone="danger">
              <p>{backupError}</p>
            </InlineAlert>
          ) : null}
          {backups.status === "loading" ? (
            <LoadingState compact />
          ) : backups.status === "error" ? (
            <ErrorState
              error={backups.error}
              onRetry={backups.reload}
              compact
            />
          ) : lastBackup ? (
            <div className="backup-summary">
              <span
                className={
                  lastBackupVerified
                    ? "backup-summary__icon is-ok"
                    : "backup-summary__icon"
                }
              >
                <Icon name={lastBackupVerified ? "check" : "alert"} />
              </span>
              <div>
                <span>最終バックアップ</span>
                <strong>{formatDateTime(lastBackup.completedAt)}</strong>
                <small>
                  {lastBackupVerified
                    ? `完全検証済み ${formatDateTime(lastBackup.verifiedAt)}`
                    : "完全検証を確認してください"}
                  {backupConfiguration?.destinationAccessible === false
                    ? "・保存先に接続できません"
                    : ""}
                </small>
              </div>
              <StatusBadge status={lastBackup.state} />
            </div>
          ) : (
            <EmptyState
              icon="database"
              title="バックアップは未設定です"
              description="本番運用を始める前に、管理者が保存先を設定してください。"
            />
          )}
          {lastBackup ? (
            <div className="button-row">
              <Button
                size="small"
                variant="secondary"
                onClick={() => void verifyBackup(lastBackup.id)}
                disabled={
                  Boolean(backupWorking)
                  || !backupConfiguration?.destinationAccessible
                  || !["verified", "failed"].includes(lastBackup.state)
                }
              >
                完全検証
              </Button>
              <Button
                size="small"
                variant="quiet"
                onClick={() => void validateRestorePlan(lastBackup.id)}
                disabled={Boolean(backupWorking) || !lastBackupVerified}
              >
                復元計画を確認
              </Button>
            </div>
          ) : null}
          {backupConfiguration?.configured ? (
            <dl className="definition-grid definition-grid--compact">
              <div>
                <dt>保存先</dt>
                <dd>
                  {backupConfiguration.destinationAccessible
                    ? "接続済み"
                    : "未設定"}
                </dd>
              </div>
              <div>
                <dt>次回予定</dt>
                <dd>
                  {formatDateTime(backupConfiguration.nextScheduledAt)}
                </dd>
              </div>
            </dl>
          ) : null}
        </Card>
      </div>
    </div>
  );
}

function HealthComponentCard({ component }: { component: HealthComponent }) {
  return (
    <Card className="health-component">
      <span
        className={`health-component__icon health-component__icon--${component.state}`}
      >
        <Icon name={healthIcon(component.name)} />
      </span>
      <div>
        <span>{componentGroup(component.name)}</span>
        <h3>{component.displayName || component.name}</h3>
        <p>{component.detail || "問題は報告されていません。"}</p>
        <small>{formatDateTime(component.checkedAt)}</small>
      </div>
      <StatusBadge status={component.state} />
    </Card>
  );
}

function StorageView({
  query,
}: {
  query: ReturnType<typeof useApiQuery<AdminStorage>>;
}) {
  const [cleanupOpen, setCleanupOpen] = useState(false);
  const [working, setWorking] = useState(false);
  const [message, setMessage] = useState<string>();
  const [error, setError] = useState<string>();

  async function runCleanup() {
    setWorking(true);
    setError(undefined);
    try {
      await api.post(
        "/admin/retention:run",
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setCleanupOpen(false);
      setMessage(
        "保存期間と容量ポリシーに基づくクリーンアップを受け付けました。",
      );
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : "クリーンアップを開始できませんでした。",
      );
    } finally {
      setWorking(false);
    }
  }

  if (query.status === "loading") {
    return <LoadingState label="保存容量を集計しています" />;
  }
  if (query.status === "error" || !query.data) {
    return <ErrorState error={query.error} onRetry={query.reload} />;
  }
  const storage = query.data;
  const physicalUsed = storage.physicalTotalBytes
    ? storage.physicalTotalBytes - storage.physicalFreeBytes
    : undefined;
  const percentage = storage.quotaBytes
    ? (storage.managedBytes / storage.quotaBytes) * 100
    : 0;

  return (
    <div className="stack">
      {message ? (
        <InlineAlert tone="success">
          <p>{message}</p>
        </InlineAlert>
      ) : null}
      {error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
      <div className="storage-overview-grid">
        <Card className="storage-gauge-card">
          <div className="card__header">
            <div>
              <h2>管理対象の答案画像</h2>
              <p>原本、正規化画像、サムネイル、解答・氏名切り出し</p>
            </div>
            <Badge tone={percentage >= 90 ? "warning" : "success"}>
              {percentage.toFixed(1)}%
            </Badge>
          </div>
          <div className="storage-big-number">
            <strong>{formatBytes(storage.managedBytes)}</strong>
            <span>/ {formatBytes(storage.quotaBytes)}</span>
          </div>
          <div className="quota-scale">
            <span
              className="quota-scale__fill"
              style={{ width: `${Math.min(100, percentage)}%` }}
            />
            {storage.warningBytes ? (
              <i
                className="quota-marker quota-marker--warning"
                style={{
                  left: `${(storage.warningBytes / storage.quotaBytes) * 100}%`,
                }}
              >
                <span>警告 {formatBytes(storage.warningBytes)}</span>
              </i>
            ) : null}
            {storage.proactiveCleanupBytes ? (
              <i
                className="quota-marker quota-marker--cleanup"
                style={{
                  left: `${(storage.proactiveCleanupBytes / storage.quotaBytes) * 100}%`,
                }}
              >
                <span>予防整理</span>
              </i>
            ) : null}
            <i className="quota-marker quota-marker--hard" style={{ left: "100%" }}>
              <span>上限</span>
            </i>
          </div>
          <div className="quota-labels">
            <span>0</span>
            <span>{formatBytes(storage.quotaBytes)}</span>
          </div>
          <div className="storage-policy-note">
            <Icon name="clock" size={18} />
            <span>
              3か月を過ぎた画像は毎日整理されます。容量上限時は古い順に
              {formatBytes(storage.lowWaterBytes)}以下まで整理します。
            </span>
          </div>
          <Button
            variant="secondary"
            onClick={() => setCleanupOpen(true)}
          >
            ポリシーに沿って今すぐ整理
          </Button>
        </Card>
        <Card>
          <div className="card__header">
            <div>
              <h2>物理ディスク</h2>
              <p>Windowsボリューム全体の使用状況</p>
            </div>
          </div>
          {physicalUsed !== undefined && storage.physicalTotalBytes ? (
            <Meter
              label="ディスク使用量"
              value={physicalUsed}
              max={storage.physicalTotalBytes}
              displayValue={`${formatBytes(physicalUsed)} / ${formatBytes(storage.physicalTotalBytes)}`}
              tone={
                storage.physicalFreeBytes < 10_000_000_000
                  ? "warning"
                  : "default"
              }
            />
          ) : (
            <div className="storage-free">
              <span>空き容量</span>
              <strong>{formatBytes(storage.physicalFreeBytes)}</strong>
            </div>
          )}
          <InlineAlert
            tone={
              storage.physicalFreeBytes < 5_000_000_000 ? "danger" : "info"
            }
          >
            <p>
              新しいアップロードには、処理領域に加えて5 GBの緊急予備領域を残します。
            </p>
          </InlineAlert>
          <dl className="definition-grid definition-grid--compact">
            <div>
              <dt>最古の答案画像</dt>
              <dd>{formatDateTime(storage.oldestRetainedAt)}</dd>
            </div>
            <div>
              <dt>次回の整理</dt>
              <dd>{formatDateTime(storage.nextCleanupAt)}</dd>
            </div>
            <div>
              <dt>前回削除</dt>
              <dd>{storage.lastDeletionCount ?? "—"}ファイル</dd>
            </div>
          </dl>
        </Card>
      </div>
      <Card>
        <div className="card__header">
          <div>
            <h2>カテゴリ別の使用量</h2>
            <p>容量上限に含むものと、含まないものを分けています。</p>
          </div>
        </div>
        <div className="storage-breakdown">
          <StorageCategory
            label="提出された原本"
            bytes={storage.originalsBytes}
            quota
            color="teal"
          />
          <StorageCategory
            label="正規化画像・切り出し"
            bytes={storage.derivativesBytes}
            quota
            color="blue"
          />
          <StorageCategory
            label="一時ファイル"
            bytes={storage.temporaryBytes}
            quota
            color="amber"
          />
          <StorageCategory
            label="隔離ファイル"
            bytes={storage.quarantineBytes}
            quota
            color="red"
          />
          <StorageCategory
            label="空欄ひな形"
            bytes={storage.templatesBytes}
            color="purple"
          />
          <StorageCategory
            label="結果PDF"
            bytes={storage.reportsBytes}
            color="green"
          />
          <StorageCategory
            label="ログ"
            bytes={storage.logsBytes}
            color="gray"
          />
        </div>
        <div className="storage-legend">
          <Badge tone="warning">上限に含む</Badge>
          <span>答案画像と、その答案から作った一時的な派生画像</span>
          <Badge tone="neutral">上限に含まない</Badge>
          <span>空欄ひな形、帳票、ログ、データベース、バックアップ</span>
        </div>
      </Card>

      <Modal
        open={cleanupOpen}
        onClose={() => !working && setCleanupOpen(false)}
        title="保存容量の整理を開始しますか？"
        description="保存期間と容量ポリシーに該当する答案画像だけを、古い順に削除します。"
        size="small"
        footer={
          <>
            <Button
              variant="secondary"
              onClick={() => setCleanupOpen(false)}
              disabled={working}
            >
              キャンセル
            </Button>
            <Button
              variant="danger"
              onClick={() => void runCleanup()}
              disabled={working}
            >
              {working ? "受け付けています…" : "整理を開始"}
            </Button>
          </>
        }
      >
        <InlineAlert tone="warning">
          <p>
            削除した答案画像は元に戻せません。採点結果、点数、訂正履歴、帳票は保持されます。
          </p>
        </InlineAlert>
        <p>
          ブラウザーから任意のファイルを選んで削除することはありません。サーバーが保存記録を照合して処理します。
        </p>
      </Modal>
    </div>
  );
}

function StorageCategory({
  label,
  bytes,
  quota = false,
  color,
}: {
  label: string;
  bytes?: number;
  quota?: boolean;
  color: string;
}) {
  return (
    <div>
      <span
        className={`storage-breakdown__swatch storage-breakdown__swatch--${color}`}
      />
      <span>{label}</span>
      <strong>{formatBytes(bytes)}</strong>
      <Badge tone={quota ? "warning" : "neutral"}>
        {quota ? "上限に含む" : "別管理"}
      </Badge>
    </div>
  );
}

function JobsView({
  query,
}: {
  query: ReturnType<typeof useApiQuery<PagedResponse<DurableJob>>>;
}) {
  const [filter, setFilter] = useState("actionable");
  const [workingId, setWorkingId] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [error, setError] = useState<string>();
  const jobs =
    filter === "all"
      ? query.data?.items
      : query.data?.items.filter((job) =>
          ["failed", "retrying", "reconcileRequired", "budgetBlocked"].includes(
            job.state,
          ),
        );

  async function action(job: DurableJob, name: "retry" | "cancel") {
    setWorkingId(job.id);
    setError(undefined);
    try {
      await api.post(
        `/admin/jobs/${encodeURIComponent(job.id)}:${name}`,
        {},
        { idempotencyKey: newIdempotencyKey() },
      );
      setMessage(
        name === "retry"
          ? "安全な再試行を受け付けました。"
          : "待機中の処理をキャンセルしました。",
      );
      query.reload();
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : "操作を完了できませんでした。",
      );
    } finally {
      setWorkingId(undefined);
    }
  }

  return (
    <Card>
      <div className="card__header">
        <div>
          <h2>バックグラウンド処理</h2>
          <p>失敗や再照合が必要な処理を確認します。</p>
        </div>
        <select
          aria-label="ジョブの表示"
          value={filter}
          onChange={(event) => setFilter(event.target.value)}
        >
          <option value="actionable">対応が必要</option>
          <option value="all">すべて</option>
        </select>
      </div>
      {message ? (
        <InlineAlert tone="success">
          <p>{message}</p>
        </InlineAlert>
      ) : null}
      {error ? (
        <InlineAlert tone="danger">
          <p>{error}</p>
        </InlineAlert>
      ) : null}
      {query.status === "loading" ? (
        <LoadingState label="処理状況を読み込んでいます" />
      ) : query.status === "error" ? (
        <ErrorState error={query.error} onRetry={query.reload} />
      ) : jobs?.length ? (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>処理</th>
                <th>状態</th>
                <th>試行</th>
                <th>次回</th>
                <th>エラー</th>
                <th>操作</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={job.id}>
                  <td>
                    <strong>{job.jobType}</strong>
                    <small className="mono">{job.id}</small>
                  </td>
                  <td>
                    <StatusBadge status={job.state} />
                  </td>
                  <td>{job.attempt}</td>
                  <td>{formatDateTime(job.nextAttemptAt)}</td>
                  <td className="job-error">
                    {job.sanitizedError || "—"}
                  </td>
                  <td>
                    <div className="row-actions">
                      <Button
                        size="small"
                        variant="secondary"
                        leadingIcon="retry"
                        disabled={workingId === job.id}
                        onClick={() => void action(job, "retry")}
                      >
                        再試行
                      </Button>
                      {job.state === "queued" ? (
                        <Button
                          size="small"
                          variant="quiet"
                          disabled={workingId === job.id}
                          onClick={() => void action(job, "cancel")}
                        >
                          中止
                        </Button>
                      ) : null}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <EmptyState
          icon="check"
          title="対応が必要な処理はありません"
          description="処理の失敗や再照合が必要になると、ここに表示されます。"
        />
      )}
    </Card>
  );
}

function healthIcon(name: string): "server" | "database" | "storage" | "connection" {
  const lower = name.toLowerCase();
  if (lower.includes("database")) return "database";
  if (lower.includes("file") || lower.includes("disk")) return "storage";
  if (
    lower.includes("internet") ||
    lower.includes("provider") ||
    lower.includes("gemini") ||
    lower.includes("openrouter")
  ) {
    return "connection";
  }
  return "server";
}

function componentGroup(name: string) {
  const lower = name.toLowerCase();
  return lower.includes("provider") ||
    lower.includes("internet") ||
    lower.includes("gemini") ||
    lower.includes("openrouter")
    ? "外部接続"
    : "学校内ホスト";
}
