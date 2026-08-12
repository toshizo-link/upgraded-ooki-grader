import { Card, EmptyState, ErrorState, LoadingState } from "../components/ui";
import { useRuntimeCapabilities } from "../hooks/useRuntimeCapabilities";
import { Link } from "../router";
import { TemplateCreatePage } from "./TemplateCreatePage";

export function TemplateGenerationCreateRoute() {
  const capabilities = useRuntimeCapabilities();

  if (capabilities.status === "loading") {
    return <LoadingState label="テンプレート生成の利用状況を確認しています" />;
  }

  if (capabilities.status === "error") {
    return (
      <div className="page">
        <ErrorState error={capabilities.error} onRetry={capabilities.reload} />
      </div>
    );
  }

  if (capabilities.data?.ai.templateGeneration.enabled !== true) {
    return (
      <div className="page centered-page">
        <Card>
          <EmptyState
            icon="lock"
            title="テンプレート生成は現在停止しています"
            description="管理者が生成機能を再開するまで、新しい作成は開始できません。進行中の生成結果や最終確認は引き続き表示できます。"
            action={
              <Link className="button button--secondary button--medium" to="/templates">
                ひな形一覧へ戻る
              </Link>
            }
          />
        </Card>
      </div>
    );
  }

  return <TemplateCreatePage />;
}
