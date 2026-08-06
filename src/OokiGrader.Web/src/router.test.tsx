import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import {
  BrowserRouter,
  Link,
  NavLink,
  Route,
  Routes,
  useParams,
  useSearchParams,
} from "./router";

describe("same-origin router", () => {
  beforeEach(() => {
    window.history.replaceState(null, "", "/");
  });

  it("navigates, matches parameters, and updates query state", () => {
    function StudentPage() {
      const { studentId } = useParams<{ studentId: string }>();
      const [search, setSearch] = useSearchParams();
      return (
        <>
          <h1>生徒 {studentId}</h1>
          <span>タブ {search.get("tab") || "未選択"}</span>
          <button
            type="button"
            onClick={() => setSearch(new URLSearchParams({ tab: "progress" }))}
          >
            推移を表示
          </button>
        </>
      );
    }

    render(
      <BrowserRouter>
        <nav>
          <NavLink to="/" end>
            ホーム
          </NavLink>
          <Link to="/students/S-1042?tab=profile">生徒を開く</Link>
        </nav>
        <Routes>
          <Route path="/" element={<h1>ホーム</h1>} />
          <Route path="/students/:studentId" element={<StudentPage />} />
        </Routes>
      </BrowserRouter>,
    );

    expect(screen.getByRole("link", { name: "ホーム" })).toHaveAttribute(
      "aria-current",
      "page",
    );
    fireEvent.click(screen.getByRole("link", { name: "生徒を開く" }));
    expect(screen.getByRole("heading", { name: "生徒 S-1042" })).toBeVisible();
    expect(screen.getByText("タブ profile")).toBeVisible();

    fireEvent.click(screen.getByRole("button", { name: "推移を表示" }));
    expect(screen.getByText("タブ progress")).toBeVisible();
    expect(window.location.pathname).toBe("/students/S-1042");
    expect(window.location.search).toBe("?tab=progress");
  });

  it("respects clicks already handled by the caller", () => {
    render(
      <BrowserRouter>
        <Link to="/reports" onClick={(event) => event.preventDefault()}>
          帳票
        </Link>
      </BrowserRouter>,
    );
    fireEvent.click(screen.getByRole("link", { name: "帳票" }));
    expect(window.location.pathname).toBe("/");
  });
});
