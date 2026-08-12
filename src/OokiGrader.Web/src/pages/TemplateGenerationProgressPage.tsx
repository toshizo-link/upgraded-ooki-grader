import { useEffect, useState } from "react";
import { Icon } from "../components/Icon";
import {
  Button,
  Card,
  ErrorState,
  InlineAlert,
  LoadingState,
  PageHeader,
} from "../components/ui";
import { ApiError } from "../lib/api";
import {
  answerStyleLabel,
  isActiveBatchStatus,
  pageRangeLabel,
  rememberTemplateGenerationBatchId,
  templateGenerationApi,
  testTypeLabel,
  unitStatusLabel,
  warningMessage,
} from "../lib/templateGeneration";
import { Link, useNavigate, useParams } from "../router";
import type { TemplateGenerationBatch } from "../types";
import { useApiQuery } from "../hooks/useApiQuery";

export function TemplateGenerationProgressPage() {
  const { batchId = "" } = useParams<{ batchId: string }>();
  const navigate = useNavigate();
  const batchQuery = useApiQuery<TemplateGenerationBatch>(
    `template-generation:${batchId}`,
    (signal) => templateGenerationApi.getBatch(batchId, signal),
    Boolean(batchId),
  );
  const [retrying, setRetrying] = useState(false);
  const [starting, setStarting] = useState(false);
  const [actionError, setActionError] = useState<string>();
  const batch = batchQuery.data;

  useEffect(() => {
    if (batchId) rememberTemplateGenerationBatchId(batchId);
  }, [batchId]);

  useEffect(() => {
    if (
      !batch ||
      !isActiveBatchStatus(batch.status) ||
      batchQuery.status === "loading"
    ) {
      return;
    }
    const timer = window.setTimeout(
      () => batchQuery.reload(),
      batchQuery.status === "error" ? 5000 : 2000,
    );
    return () => window.clearTimeout(timer);
  }, [batch, batchQuery.reload, batchQuery.status]);

  async function retryFailedUnits() {
    if (!batch || retrying) return;
    setRetrying(true);
    setActionError(undefined);
    try {
      await templateGenerationApi.retryFailedUnits(
        batch.batchId,
        batch.rowVersion,
      );
      batchQuery.reload();
    } catch (reason) {
      setActionError(actionErrorMessage(reason));
    } finally {
      setRetrying(false);
    }
  }

  async function startDraftBatch() {
    if (!batch || batch.status !== "draft" || starting) return;
    setStarting(true);
    setActionError(undefined);
    try {
      await templateGenerationApi.startGeneration(
        batch.batchId,
        batch.rowVersion,
      );
      batchQuery.reload();
    } catch (reason) {
      setActionError(actionErrorMessage(reason));
    } finally {
      setStarting(false);
    }
  }

  if (!batch && batchQuery.status === "loading") {
    return <LoadingState label="生成状況を読み込んでいます" />;
  }

  if (!batch) {
    return (
      <div className="page">
        <ErrorState error={batchQuery.error} onRetry={batchQuery.reload} />
      </div>
    );
  }

  const completed = batch.completedUnitCount ?? 0;
  const expected = Math.max(batch.expectedUnitCount, 1);
  const progress = Math.min(100, Math.round((completed / expected) * 100));
  const progressCopy = generationProgressCopy(batch);
  const createdTemplates =
    batch.createdTemplates ??
    batch.units
      .filter(
        (unit) => unit.createdTemplateId && unit.createdTemplateVersionId,
      )
      .map((unit) => ({
        templateId: unit.createdTemplateId as string,
        versionId: unit.createdTemplateVersionId as string,
        title: unit.finalTemplateName || `テンプレート ${unit.sequence}`,
      }));

  return (
    <div className="page template-generation-page">
      <PageHeader
        eyebrow="テンプレート生成"
        title="テンプレートを生成しています"
        description="PDFの分割範囲は固定されています。この画面を離れても処理は続きます。"
        backAction={
          <Link className="back-link" to="/templates">
            <Icon name="arrowLeft" size={17} />
            ひな形一覧へ
          </Link>
        }
      />

      {actionError ? (
        <InlineAlert tone="danger" title="操作を開始できませんでした">
          <p>{actionError}</p>
        </InlineAlert>
      ) : null}

      {batchQuery.status === "error" ? (
        <InlineAlert
          tone="warning"
          title="最新の生成状況を取得できませんでした"
          action={
            <Button size="small" variant="secondary" onClick={batchQuery.reload}>
              今すぐ再読み込み
            </Button>
          }
        >
          <p>表示中の情報を残したまま、自動で再接続します。</p>
        </InlineAlert>
      ) : null}

      {batch.status === "failed" ? (
        <InlineAlert
          tone="danger"
          title="生成できなかったテンプレートがあります"
          action={
            <Button
              size="small"
              leadingIcon="retry"
              disabled={retrying}
              onClick={() => void retryFailedUnits()}
            >
              {retrying ? "再試行しています" : "失敗した項目を再試行"}
            </Button>
          }
        >
          <p>
            成功済みの項目は保持されています。すべて成功するまで最終確認には進めません。
          </p>
        </InlineAlert>
      ) : null}

      {batch.status === "draft" ? (
        <InlineAlert
          tone="info"
          title="作成予定が保存されています"
          action={
            <Button
              size="small"
              leadingIcon="spark"
              disabled={starting}
              onClick={() => void startDraftBatch()}
            >
              {starting ? "開始しています" : "テンプレート生成を開始"}
            </Button>
          }
        >
          <p>PDFを再アップロードせず、この作成予定から生成を開始できます。</p>
        </InlineAlert>
      ) : null}

      {batch.status === "needsFinalCheck" ? (
        <InlineAlert
          tone="success"
          title="すべてのテンプレートを生成しました"
          action={
            <Button
              size="small"
              onClick={() =>
                navigate(
                  `/templates/generation/${encodeURIComponent(batch.batchId)}/final-check`,
                )
              }
            >
              最終確認へ
            </Button>
          }
        >
          <p>テスト名と学年を確認してからテンプレートを作成します。</p>
        </InlineAlert>
      ) : null}

      {batch.status === "completed" ? (
        <InlineAlert tone="success" title="テンプレートを作成しました">
          <p>各ひな形を開いて内容を編集し、「受付を開始」から使用できます。</p>
        </InlineAlert>
      ) : null}

      <Card className="template-generation-progress-card">
        <div className="template-progress-heading" role="status" aria-live="polite">
          {isActiveBatchStatus(batch.status) ? (
            <span className="spinner" aria-hidden="true" />
          ) : (
            <span className="template-progress-heading__icon">
              <Icon
                name={
                  batch.status === "failed"
                    ? "alert"
                    : batch.status === "draft"
                      ? "clock"
                      : "check"
                }
                size={22}
              />
            </span>
          )}
          <div>
            <strong>{progressCopy.title}</strong>
            <small>{progressCopy.detail}</small>
          </div>
          <b>{completed} / {batch.expectedUnitCount}</b>
        </div>
        <div
          className="template-generation-progress-bar"
          role="progressbar"
          aria-label="テンプレート生成の進み具合"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={progress}
        >
          <span style={{ width: `${progress}%` }} />
        </div>
      </Card>

      <dl className="template-plan-summary template-progress-summary">
        <div>
          <dt>試験タイプ</dt>
          <dd>{testTypeLabel(batch.testType)}</dd>
        </div>
        <div>
          <dt>教科</dt>
          <dd>{batch.subject}</dd>
        </div>
        {batch.testType === "other" ? (
          <div>
            <dt>問題形式</dt>
            <dd>{answerStyleLabel(batch.answerStyle)}</dd>
          </div>
        ) : null}
        <div>
          <dt>ページ数</dt>
          <dd>{batch.sourcePageCount}ページ</dd>
        </div>
        <div>
          <dt>テンプレート</dt>
          <dd>{batch.expectedUnitCount}件</dd>
        </div>
      </dl>

      <Card className="template-generation-unit-card">
        <div className="template-generation-section-heading">
          <div>
            <h2>生成するテンプレート</h2>
            <p>1件ずつ独立して生成しています。</p>
          </div>
          {isActiveBatchStatus(batch.status) ? (
            <Button size="small" variant="quiet" onClick={batchQuery.reload}>
              状況を更新
            </Button>
          ) : null}
        </div>
        <div className="template-generation-unit-list">
          {batch.units.map((unit) => {
            const warnings = [
              ...(unit.warnings ?? []),
              ...(unit.blockingWarnings ?? []),
            ];
            return (
              <article key={unit.id} className={`template-generation-unit is-${unit.status}`}>
                <span className="template-generation-unit__sequence">
                  {unit.sequence}
                </span>
                <div className="template-generation-unit__copy">
                  <div>
                    <strong>{pageRangeLabel(unit)}</strong>
                    {unit.stepSetIndex ? (
                      <small>
                        STEPセット {unit.stepSetIndex}・固定枝番
                        {unit.deterministicSuffix}
                      </small>
                    ) : null}
                  </div>
                  <span>{unitStatusLabel(unit.status)}</span>
                  {unit.questionCount !== undefined ? (
                    <small>{unit.questionCount}問を抽出</small>
                  ) : null}
                  {unit.orientationCorrectionSummary ? (
                    <small>{unit.orientationCorrectionSummary}</small>
                  ) : unit.appliedRotations?.some(
                      (rotation) => rotation.clockwiseDegrees !== 0,
                    ) ? (
                    <small>ページの向きを自動で補正しました</small>
                  ) : null}
                  {warnings.map((warning, index) => {
                    const code = typeof warning === "string" ? warning : warning.code;
                    return (
                      <small className="field__error" key={`${code}-${index}`}>
                        {typeof warning === "string" && !warning.includes("_")
                          ? warning
                          : warningMessage(code)}
                      </small>
                    );
                  })}
                </div>
                <span className={`template-unit-status template-unit-status--${unit.status}`}>
                  {unit.status === "extracted" || unit.status === "confirmed" ? (
                    <Icon name="check" size={16} />
                  ) : unit.status === "failed" ? (
                    <Icon name="alert" size={16} />
                  ) : (
                    <span className="spinner" aria-hidden="true" />
                  )}
                </span>
              </article>
            );
          })}
        </div>
      </Card>

      {batch.status === "completed" && createdTemplates.length ? (
        <Card className="template-created-links">
          <h2>作成したテンプレート</h2>
          <div>
            {createdTemplates.map((template) => (
              <Link
                className="button button--secondary button--medium"
                key={`${template.templateId}-${template.versionId}`}
                to={`/templates/${encodeURIComponent(template.templateId)}/versions/${encodeURIComponent(template.versionId)}`}
              >
                <span>{template.title}</span>
                <Icon name="arrowRight" size={17} />
              </Link>
            ))}
          </div>
        </Card>
      ) : null}
    </div>
  );
}

export function generationProgressCopy(batch: TemplateGenerationBatch) {
  if (batch.status === "validating") {
    return {
      title: "PDFを分割しています",
      detail: "選択した試験タイプの固定ルールを適用しています。",
    };
  }
  if (batch.status === "draft") {
    return {
      title: "テンプレート生成の開始待ちです",
      detail: "作成画面で固定された分割計画を確認してください。",
    };
  }
  if (batch.units.some((unit) => unit.status === "rotating")) {
    return {
      title: "ページの向きを補正しています",
      detail: "指定されたページだけを端末側で回転しています。",
    };
  }
  if (batch.units.some((unit) => unit.status === "retryingAfterRotation")) {
    return {
      title: "補正後のテンプレートを生成しています",
      detail: "向きを補正したページで1回だけ再試行しています。",
    };
  }
  if (batch.status === "needsFinalCheck") {
    return {
      title: "最終確認を準備しました",
      detail: "テスト名と学年を確認できます。",
    };
  }
  if (batch.status === "confirming") {
    return {
      title: "テンプレートを作成しています",
      detail: "確認済みの内容をまとめて保存しています。",
    };
  }
  if (batch.status === "completed") {
    return {
      title: "すべてのテンプレートを作成しました",
      detail: "各ひな形は個別に編集し、「受付を開始」から使用できます。",
    };
  }
  if (batch.status === "failed") {
    return {
      title: "生成を完了できませんでした",
      detail: "失敗した項目を確認して再試行してください。",
    };
  }
  if (batch.status === "cancelled") {
    return {
      title: "生成は取り消されました",
      detail: "新しく作成する場合は、設定画面からやり直してください。",
    };
  }
  const completed = batch.completedUnitCount ?? 0;
  return {
    title: `テンプレート ${completed} / ${batch.expectedUnitCount} を生成しています`,
    detail: "テストタイプや分割位置のAI判定は行いません。",
  };
}

function actionErrorMessage(reason: unknown) {
  if (reason instanceof ApiError) {
    const code = reason.problem.code || reason.problem.errors?.[0]?.code;
    return code ? warningMessage(code) : reason.message;
  }
  return reason instanceof Error ? reason.message : "再試行を開始できませんでした。";
}
