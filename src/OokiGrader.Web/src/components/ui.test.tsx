import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { EmptyState, InlineAlert, StatusBadge, Tabs } from "./ui";

describe("StatusBadge", () => {
  it("renders the durable Japanese status instead of an indefinite spinner", () => {
    render(<StatusBadge status="awaitingAi" />);

    expect(screen.getByText("AI処理待ち")).toBeInTheDocument();
  });

  it("makes retention deletion explicit", () => {
    render(<StatusBadge status="scanDeleted" />);

    expect(screen.getByText("画像削除済み")).toBeInTheDocument();
  });

  it("presents legacy batch work as ordinary AI grading", () => {
    render(<StatusBadge status="geminiBatchRunning" />);

    expect(screen.getByText("AI採点中")).toBeInTheDocument();
    expect(screen.queryByText(/一括|Batch/)).not.toBeInTheDocument();
  });

  it("localizes durable submission states returned by the API", () => {
    const view = render(<StatusBadge status="awaiting_grading" />);
    expect(view.container).toHaveTextContent("AI採点待ち");

    view.rerender(<StatusBadge status="grading" />);
    expect(view.container).toHaveTextContent("AI採点中");

    view.rerender(<StatusBadge status="voided" />);
    expect(view.container).toHaveTextContent("処理対象外");
  });

  it("does not expose an unknown internal state code", () => {
    render(<StatusBadge status="new_internal_state" />);

    expect(screen.getByText("状態を確認中")).toBeInTheDocument();
    expect(screen.queryByText("new_internal_state")).not.toBeInTheDocument();
  });
});

describe("EmptyState", () => {
  it("uses a real heading and explanatory copy", () => {
    render(
      <EmptyState
        title="答案はまだありません"
        description="テスト実施から答案をアップロードしてください。"
      />,
    );

    expect(
      screen.getByRole("heading", { name: "答案はまだありません" }),
    ).toBeInTheDocument();
    expect(
      screen.getByText("テスト実施から答案をアップロードしてください。"),
    ).toBeInTheDocument();
  });
});

describe("InlineAlert", () => {
  it("announces action errors immediately", () => {
    render(
      <InlineAlert tone="danger">保存できませんでした。</InlineAlert>,
    );

    expect(screen.getByRole("alert")).toHaveTextContent(
      "保存できませんでした。",
    );
  });
});

describe("Tabs", () => {
  it("supports arrow-key navigation with a single tab stop", () => {
    let selected = "first";
    const { rerender } = render(
      <Tabs
        value={selected}
        onChange={(value) => {
          selected = value;
        }}
        label="表示内容"
        tabs={[
          { value: "first", label: "最初" },
          { value: "second", label: "次" },
        ]}
      />,
    );
    const first = screen.getByRole("tab", { name: "最初" });
    const second = screen.getByRole("tab", { name: "次" });

    expect(first).toHaveAttribute("tabindex", "0");
    expect(second).toHaveAttribute("tabindex", "-1");
    fireEvent.keyDown(first, { key: "ArrowRight" });
    expect(selected).toBe("second");
    expect(second).toHaveFocus();

    rerender(
      <Tabs
        value={selected}
        onChange={(value) => {
          selected = value;
        }}
        label="表示内容"
        tabs={[
          { value: "first", label: "最初" },
          { value: "second", label: "次" },
        ]}
      />,
    );
    expect(screen.getByRole("tab", { name: "次" })).toHaveAttribute(
      "tabindex",
      "0",
    );
  });
});
