import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ProgressChart } from "./ProgressChart";

describe("ProgressChart", () => {
  it("labels a one-result graph without claiming a trend", () => {
    render(
      <ProgressChart
        series={[
          {
            submissionId: "01JRESULT",
            testDate: "2026-07-27",
            testTitle: "漢字確認テスト",
            earnedPointsMilli: 18000,
            possiblePointsMilli: 20000,
            percentageBasisPoints: 9000,
            correct: 18,
            partial: 0,
            incorrect: 2,
            blank: 0,
            resultRevision: 1,
          },
        ]}
      />,
    );

    expect(
      screen.getByText("期間内のテストは1件です。"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("link", {
        name: /漢字確認テスト 90%/,
      }),
    ).toHaveAttribute("href", "/results/01JRESULT");
  });
});
