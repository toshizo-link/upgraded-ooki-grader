import { createHash } from "node:crypto";
import { mkdir, readFile, rename, unlink, writeFile } from "node:fs/promises";
import { basename, resolve } from "node:path";
import process from "node:process";

const destination = resolve(
  process.argv[2] || "tmp/japanese-handwriting-fixtures",
);
const dataset = "llm-jp/jawildtext";
const revision = "627ca7ea7c224ffe1accff8737991fc2240784fa";
const configuration = "handwriting_ocr";
const split = "train";
const fixtures = [
  {
    rowIndex: 29,
    imageId: "51",
    filename: "0051_01_2_2_1_h.jpg",
    tool: "paper_other",
    width: 4_032,
    height: 2_268,
    bytes: 425_311,
    sha256: "0239932e51aad04001834ae953541434f07232c2c71ad4bcc0bd3358e6d68aa1",
    metadataSha256:
      "97440e4bd4d0e77c4ab7aafa577743fe38b29d37c577f81536e6edc1d2f09e6c",
  },
  {
    rowIndex: 95,
    imageId: "182",
    filename: "0128_45_1_2_3_h.jpg",
    tool: "paper_plain",
    width: 3_024,
    height: 4_032,
    bytes: 1_511_207,
    sha256: "32336f4bf9c16db8734d204181a31cbb2d26a927087e16601efe5a8b9c040d2c",
    metadataSha256:
      "340c4277121d7276e874a72803fcfde2a0f37c650206c9d862f9be833f809290",
  },
];

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

async function isCurrent(path, expected) {
  if (!expected) return false;
  try {
    return sha256(await readFile(path)) === expected;
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
}

async function fetchBytes(url, label) {
  const response = await fetch(url, {
    redirect: "follow",
    signal: AbortSignal.timeout(60_000),
  });
  if (!response.ok) {
    throw new Error(`${label}: HTTP ${response.status}`);
  }
  return Buffer.from(await response.arrayBuffer());
}

async function writeAtomic(path, bytes) {
  const temporaryPath = `${path}.${process.pid}.part`;
  try {
    await writeFile(temporaryPath, bytes, { flag: "wx", mode: 0o600 });
    await rename(temporaryPath, path);
  } catch (error) {
    await unlink(temporaryPath).catch(() => {});
    throw error;
  }
}

async function fetchFixture(fixture) {
  if (fixture.filename !== basename(fixture.filename)) {
    throw new Error(`Unsafe fixture name: ${fixture.filename}`);
  }

  const rowsUrl = new URL(
    "https://datasets-server.huggingface.co/rows",
  );
  rowsUrl.searchParams.set("dataset", dataset);
  rowsUrl.searchParams.set("config", configuration);
  rowsUrl.searchParams.set("split", split);
  rowsUrl.searchParams.set("offset", String(fixture.rowIndex));
  rowsUrl.searchParams.set("length", "1");
  const rowsBytes = await fetchBytes(rowsUrl, fixture.filename);
  const rows = JSON.parse(rowsBytes.toString("utf8")).rows;
  if (!Array.isArray(rows) || rows.length !== 1) {
    throw new Error(`${fixture.filename}: expected one dataset row`);
  }

  const entry = rows[0];
  const row = entry.row;
  if (
    entry.row_idx !== fixture.rowIndex ||
    row?.subset !== configuration ||
    row?.image_id !== fixture.imageId ||
    row?.filename !== fixture.filename ||
    row?.tool !== fixture.tool ||
    row?.image?.width !== fixture.width ||
    row?.image?.height !== fixture.height ||
    !Array.isArray(row?.polygons) ||
    row.polygons.length === 0
  ) {
    throw new Error(`${fixture.filename}: dataset identity changed`);
  }

  const metadata = Buffer.from(
    `${JSON.stringify(
      {
        dataset,
        revision,
        configuration,
        split,
        rowIndex: fixture.rowIndex,
        imageId: fixture.imageId,
        filename: fixture.filename,
        tool: fixture.tool,
        width: fixture.width,
        height: fixture.height,
        polygons: row.polygons,
      },
      null,
      2,
    )}\n`,
    "utf8",
  );
  const metadataHash = sha256(metadata);
  if (
    fixture.metadataSha256 &&
    metadataHash !== fixture.metadataSha256
  ) {
    throw new Error(
      `${fixture.filename}: annotation integrity mismatch (${metadataHash})`,
    );
  }

  const imagePath = resolve(destination, fixture.filename);
  const metadataPath = resolve(
    destination,
    `${fixture.filename}.metadata.json`,
  );
  let imageBytes;
  if (await isCurrent(imagePath, fixture.sha256)) {
    imageBytes = await readFile(imagePath);
  } else {
    imageBytes = await fetchBytes(row.image.src, fixture.filename);
  }
  const imageHash = sha256(imageBytes);
  if (
    (fixture.bytes && imageBytes.length !== fixture.bytes) ||
    (fixture.sha256 && imageHash !== fixture.sha256)
  ) {
    throw new Error(
      `${fixture.filename}: image integrity mismatch ` +
        `(${imageBytes.length} bytes, ${imageHash})`,
    );
  }

  if (!(await isCurrent(imagePath, imageHash))) {
    await writeAtomic(imagePath, imageBytes);
  }
  if (!(await isCurrent(metadataPath, metadataHash))) {
    await writeAtomic(metadataPath, metadata);
  }
  process.stdout.write(
    `verified ${fixture.filename} ` +
      `(${imageBytes.length} bytes, ${imageHash}, metadata ${metadataHash})\n`,
  );
}

await mkdir(destination, { recursive: true, mode: 0o700 });
for (const fixture of fixtures) {
  await fetchFixture(fixture);
}
