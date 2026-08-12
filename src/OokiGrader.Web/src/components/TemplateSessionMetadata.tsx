import type { TemplateSummary } from "../types";

function displayValue(value: string | undefined) {
  return value?.trim() || "—";
}

export function TemplateSessionMetadata({
  template,
}: {
  template: Pick<
    TemplateSummary,
    "title" | "subject" | "gradeLabel" | "category" | "course"
  >;
}) {
  return (
    <dl className="reception-template-summary" aria-label="使用するひな形">
      <div className="reception-template-summary__title">
        <dt>試験名</dt>
        <dd>{template.title}</dd>
      </div>
      <div>
        <dt>教科</dt>
        <dd>{displayValue(template.subject)}</dd>
      </div>
      <div>
        <dt>学年</dt>
        <dd>{displayValue(template.gradeLabel)}</dd>
      </div>
      <div>
        <dt>カテゴリ</dt>
        <dd>{displayValue(template.category)}</dd>
      </div>
      <div>
        <dt>コース</dt>
        <dd>{displayValue(template.course)}</dd>
      </div>
    </dl>
  );
}
