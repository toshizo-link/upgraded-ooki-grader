import {
  useEffect,
  useState,
  type FormEvent,
  type ReactNode,
} from "react";
import type { StudentDetail } from "../types";
import { Button, Field } from "./ui";

export interface StudentFormValues {
  studentNumber: string;
  familyName: string;
  givenName: string;
  familyNameKana: string;
  givenNameKana: string;
  displayName: string;
  gradeLabel: string;
  classLabel: string;
  course: string;
  notes: string;
}

const blankValues: StudentFormValues = {
  studentNumber: "",
  familyName: "",
  givenName: "",
  familyNameKana: "",
  givenNameKana: "",
  displayName: "",
  gradeLabel: "",
  classLabel: "",
  course: "",
  notes: "",
};

function fromStudent(student?: StudentDetail): StudentFormValues {
  if (!student) return blankValues;
  return {
    studentNumber: student.studentNumber || "",
    familyName: student.familyName || "",
    givenName: student.givenName || "",
    familyNameKana: student.familyNameKana || "",
    givenNameKana: student.givenNameKana || "",
    displayName: student.displayName || "",
    gradeLabel: student.gradeLabel || "",
    classLabel: student.classLabel || "",
    course: student.course || "",
    notes: student.notes || "",
  };
}

export function StudentForm({
  initial,
  onSubmit,
  onCancel,
  submitting,
  submitLabel = "保存",
  error,
  footerPrefix,
}: {
  initial?: StudentDetail;
  onSubmit: (values: StudentFormValues) => void | Promise<void>;
  onCancel?: () => void;
  submitting?: boolean;
  submitLabel?: string;
  error?: string;
  footerPrefix?: ReactNode;
}) {
  const [values, setValues] = useState(() => fromStudent(initial));
  const [touchedDisplayName, setTouchedDisplayName] = useState(
    Boolean(initial?.displayName),
  );

  useEffect(() => {
    setValues(fromStudent(initial));
    setTouchedDisplayName(Boolean(initial?.displayName));
  }, [initial]);

  function set<K extends keyof StudentFormValues>(
    key: K,
    value: StudentFormValues[K],
  ) {
    setValues((current) => {
      const next = { ...current, [key]: value };
      if (
        !touchedDisplayName &&
        (key === "familyName" || key === "givenName")
      ) {
        next.displayName = [next.familyName, next.givenName]
          .filter(Boolean)
          .join(" ");
      }
      return next;
    });
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    void onSubmit({
      ...values,
      studentNumber: values.studentNumber.trim(),
      displayName: values.displayName.trim(),
    });
  }

  return (
    <form className="student-form" onSubmit={handleSubmit}>
      <div className="form-grid form-grid--2">
        <Field
          label="生徒番号"
          htmlFor="student-number"
          required
          hint="学校内で重複しない番号"
        >
          <input
            id="student-number"
            value={values.studentNumber}
            onChange={(event) => set("studentNumber", event.target.value)}
            required
            autoComplete="off"
          />
        </Field>
        <Field label="表示名" htmlFor="display-name" required>
          <input
            id="display-name"
            value={values.displayName}
            onChange={(event) => {
              setTouchedDisplayName(true);
              set("displayName", event.target.value);
            }}
            required
          />
        </Field>
      </div>
      <fieldset>
        <legend>氏名</legend>
        <div className="form-grid form-grid--2">
          <Field label="姓" htmlFor="family-name" required>
            <input
              id="family-name"
              value={values.familyName}
              onChange={(event) => set("familyName", event.target.value)}
              required
              autoComplete="family-name"
            />
          </Field>
          <Field label="名" htmlFor="given-name" required>
            <input
              id="given-name"
              value={values.givenName}
              onChange={(event) => set("givenName", event.target.value)}
              required
              autoComplete="given-name"
            />
          </Field>
          <Field label="姓（カナ）" htmlFor="family-name-kana" required>
            <input
              id="family-name-kana"
              value={values.familyNameKana}
              onChange={(event) => set("familyNameKana", event.target.value)}
              required
              inputMode="text"
            />
          </Field>
          <Field label="名（カナ）" htmlFor="given-name-kana" required>
            <input
              id="given-name-kana"
              value={values.givenNameKana}
              onChange={(event) => set("givenNameKana", event.target.value)}
              required
              inputMode="text"
            />
          </Field>
        </div>
      </fieldset>
      <fieldset>
        <legend>所属（任意）</legend>
        <div className="form-grid form-grid--3">
          <Field label="学年" htmlFor="grade-label">
            <input
              id="grade-label"
              value={values.gradeLabel}
              onChange={(event) => set("gradeLabel", event.target.value)}
              placeholder="例：中学2年"
            />
          </Field>
          <Field label="クラス" htmlFor="class-label">
            <input
              id="class-label"
              value={values.classLabel}
              onChange={(event) => set("classLabel", event.target.value)}
              placeholder="例：2-A"
            />
          </Field>
          <Field label="コース" htmlFor="course">
            <input
              id="course"
              value={values.course}
              onChange={(event) => set("course", event.target.value)}
              placeholder="例：英語A"
            />
          </Field>
        </div>
      </fieldset>
      <Field
        label="職員向けメモ"
        htmlFor="student-notes"
        hint="スキャン担当者には表示されません。"
      >
        <textarea
          id="student-notes"
          rows={3}
          value={values.notes}
          onChange={(event) => set("notes", event.target.value)}
        />
      </Field>
      {error ? (
        <p className="form-error" role="alert">
          {error}
        </p>
      ) : null}
      <div className="form-actions">
        {footerPrefix}
        <span className="form-actions__spacer" />
        {onCancel ? (
          <Button type="button" variant="secondary" onClick={onCancel}>
            キャンセル
          </Button>
        ) : null}
        <Button type="submit" disabled={submitting}>
          {submitting ? "保存しています…" : submitLabel}
        </Button>
      </div>
    </form>
  );
}
