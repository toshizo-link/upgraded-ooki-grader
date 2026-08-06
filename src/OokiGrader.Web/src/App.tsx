import type { ReactNode } from "react";
import { BrowserRouter, Link, Route, Routes } from "./router";
import { SessionBoundary } from "./auth/SessionBoundary";
import { SessionProvider, useSession } from "./auth/SessionContext";
import { AppShell } from "./components/AppShell";
import { Icon } from "./components/Icon";
import { Card, EmptyState } from "./components/ui";
import { AdminPage } from "./pages/AdminPage";
import { DashboardPage } from "./pages/DashboardPage";
import { ReportsPage } from "./pages/ReportsPage";
import { ResultDetailPage } from "./pages/ResultDetailPage";
import { ReviewPage } from "./pages/ReviewPage";
import { SessionDetailPage } from "./pages/SessionDetailPage";
import { SessionsPage } from "./pages/SessionsPage";
import { StudentDetailPage } from "./pages/StudentDetailPage";
import { StudentsPage } from "./pages/StudentsPage";
import { TemplateCreatePage } from "./pages/TemplateCreatePage";
import { TemplateEditorPage } from "./pages/TemplateEditorPage";
import { TemplatesPage } from "./pages/TemplatesPage";
import type { StaffRole } from "./types";
import "./styles.css";

export function App() {
  return (
    <BrowserRouter>
      <SessionProvider>
        <SessionBoundary>
          <AppShell>
            <AppRoutes />
          </AppShell>
        </SessionBoundary>
      </SessionProvider>
    </BrowserRouter>
  );
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route
        path="/review"
        element={
          <RoleGuard roles={["administrator", "teacher"]}>
            <ReviewPage />
          </RoleGuard>
        }
      />
      <Route
        path="/sessions"
        element={
          <RoleGuard roles={["administrator", "teacher", "scanOperator"]}>
            <SessionsPage />
          </RoleGuard>
        }
      />
      <Route
        path="/sessions/:sessionId"
        element={
          <RoleGuard roles={["administrator", "teacher", "scanOperator"]}>
            <SessionDetailPage />
          </RoleGuard>
        }
      />
      <Route
        path="/templates"
        element={
          <RoleGuard roles={["administrator", "teacher"]}>
            <TemplatesPage />
          </RoleGuard>
        }
      />
      <Route
        path="/templates/new"
        element={
          <RoleGuard roles={["administrator", "teacher"]}>
            <TemplateCreatePage />
          </RoleGuard>
        }
      />
      <Route
        path="/templates/:templateId/versions/:versionId"
        element={
          <RoleGuard roles={["administrator", "teacher"]}>
            <TemplateEditorPage />
          </RoleGuard>
        }
      />
      <Route
        path="/students"
        element={
          <RoleGuard roles={["administrator", "teacher"]}>
            <StudentsPage />
          </RoleGuard>
        }
      />
      <Route
        path="/students/:studentId"
        element={
          <RoleGuard roles={["administrator", "teacher"]}>
            <StudentDetailPage />
          </RoleGuard>
        }
      />
      <Route
        path="/reports"
        element={
          <RoleGuard
            roles={["administrator", "teacher", "readOnlyReviewer"]}
          >
            <ReportsPage />
          </RoleGuard>
        }
      />
      <Route
        path="/results/:submissionId"
        element={
          <RoleGuard
            roles={["administrator", "teacher", "readOnlyReviewer"]}
          >
            <ResultDetailPage />
          </RoleGuard>
        }
      />
      <Route
        path="/admin"
        element={
          <RoleGuard roles={["administrator"]}>
            <AdminPage />
          </RoleGuard>
        }
      />
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  );
}

function RoleGuard({
  roles,
  children,
}: {
  roles: StaffRole[];
  children: ReactNode;
}) {
  const { hasAnyRole } = useSession();
  if (hasAnyRole(...roles)) return children;
  return (
    <div className="page centered-page">
      <Card>
        <EmptyState
          icon="lock"
          title="この画面を表示する権限がありません"
          description="必要な操作がある場合は、学校のシステム管理者に確認してください。"
          action={
            <Link className="button button--primary button--medium" to="/">
              <span>ダッシュボードへ戻る</span>
            </Link>
          }
        />
      </Card>
    </div>
  );
}

function NotFoundPage() {
  return (
    <div className="page centered-page">
      <Card>
        <EmptyState
          icon="search"
          title="ページが見つかりません"
          description="リンクが古いか、表示できないページの可能性があります。"
          action={
            <Link className="button button--primary button--medium" to="/">
              <Icon name="arrowLeft" size={17} />
              <span>ダッシュボードへ戻る</span>
            </Link>
          }
        />
      </Card>
    </div>
  );
}
