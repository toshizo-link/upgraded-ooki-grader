export function formatDate(value?: string | null, options?: Intl.DateTimeFormatOptions) {
  if (!value) return "—";
  const date = /^\d{4}-\d{2}-\d{2}$/.test(value)
    ? new Date(`${value}T00:00:00+09:00`)
    : new Date(value);

  if (Number.isNaN(date.getTime())) return value;

  return new Intl.DateTimeFormat("ja-JP", {
    timeZone: "Asia/Tokyo",
    year: "numeric",
    month: "long",
    day: "numeric",
    ...options,
  }).format(date);
}

export function formatDateTime(value?: string | null) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat("ja-JP", {
    timeZone: "Asia/Tokyo",
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

export function formatPoints(pointsMilli?: number | null) {
  if (pointsMilli === undefined || pointsMilli === null) return "—";
  const value = pointsMilli / 1000;
  return new Intl.NumberFormat("ja-JP", {
    maximumFractionDigits: value % 1 === 0 ? 0 : 3,
  }).format(value);
}

export function formatPercentageBasisPoints(value?: number | null) {
  if (value === undefined || value === null) return "—";
  return `${new Intl.NumberFormat("ja-JP", {
    minimumFractionDigits: 0,
    maximumFractionDigits: 1,
  }).format(value / 100)}%`;
}

export function formatBytes(value?: number | null) {
  if (value === undefined || value === null || Number.isNaN(value)) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let size = value;
  let index = 0;
  while (Math.abs(size) >= 1000 && index < units.length - 1) {
    size /= 1000;
    index += 1;
  }
  return `${new Intl.NumberFormat("ja-JP", {
    maximumFractionDigits: index === 0 ? 0 : 1,
  }).format(size)} ${units[index]}`;
}

export function toDateInput(date = new Date()) {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Asia/Tokyo",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(date);
  const byType = Object.fromEntries(parts.map((part) => [part.type, part.value]));
  return `${byType.year}-${byType.month}-${byType.day}`;
}

export function initials(displayName?: string) {
  if (!displayName) return "職";
  return Array.from(displayName.replace(/\s+/g, "")).slice(0, 2).join("");
}

export function classNames(
  ...values: Array<string | false | null | undefined>
) {
  return values.filter(Boolean).join(" ");
}
