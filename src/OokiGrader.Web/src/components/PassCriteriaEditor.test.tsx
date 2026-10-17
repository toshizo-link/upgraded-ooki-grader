import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { PassCriteriaEditor } from "./PassCriteriaEditor";
import type { TestSessionSummary } from "../types";

const put = vi.hoisted(() => vi.fn().mockResolvedValue({}));
vi.mock("../lib/api", () => ({ api: { put } }));
afterEach(() => { cleanup(); put.mockClear(); });
const session: TestSessionSummary = {
  id: "s1", templateId: "t1", templateVersionId: "v1", testDate: "2026-10-16",
  priority: "economy", state: "open", revision: 7, maximumMistakesToPass: 4,
};
describe("PassCriteriaEditor", () => {
  it("shows an inclusive boundary and saves the per-test setting", async () => {
    const reload = vi.fn();
    render(<PassCriteriaEditor session={session} readOnly={false} onSaved={reload} />);
    expect(screen.getByText("4ミスまで合格")).toBeVisible();
    fireEvent.change(screen.getByLabelText("合格にする最大ミス数"), { target: { value: "3" } });
    expect(screen.getByText("3ミスまで合格")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "合格基準を保存" }));
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
    expect(put).toHaveBeenCalledWith("/test-sessions/s1/pass-criteria", { maximumMistakesToPass: 3, revision: 7 });
  });
  it("clears a setting and rejects fractional values", async () => {
    render(<PassCriteriaEditor session={session} readOnly={false} onSaved={() => {}} />);
    const input = screen.getByLabelText("合格にする最大ミス数");
    fireEvent.change(input, { target: { value: "1.5" } });
    expect(screen.getByRole("button", { name: "合格基準を保存" })).toBeDisabled();
    fireEvent.change(input, { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "合格基準を保存" }));
    await waitFor(() => expect(put).toHaveBeenCalledWith("/test-sessions/s1/pass-criteria", { maximumMistakesToPass: null, revision: 7 }));
  });
});
