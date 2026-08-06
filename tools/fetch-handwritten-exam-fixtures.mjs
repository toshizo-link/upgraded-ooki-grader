import { createHash } from "node:crypto";
import { mkdir, readFile, rename, unlink, writeFile } from "node:fs/promises";
import { basename, resolve } from "node:path";
import process from "node:process";

const destination = resolve(
  process.argv[2] || "tmp/handwritten-exam-fixtures",
);
const datasetBase =
  "https://data.mendeley.com/public-files/datasets/sf3kvjwknt/files";
const fixtures = [
  {
    name: "Student_18.pdf",
    fileId: "e7a71b01-dca8-4af5-b76a-7e0fd67816ae",
    bytes: 718_952,
    sha256: "68622bdd43848e17b487ab47a531eaaff578b1b29e9f9239fa90c59d0075c034",
  },
  {
    name: "Student_19.pdf",
    fileId: "77bbed67-8579-4c2b-b80b-9bd980a59103",
    bytes: 976_021,
    sha256: "b49444fb96457a21b3a02c45ca2f8d885e34ff0e15a22debfda93dc2d2b3b854",
  },
  {
    name: "Student_26.pdf",
    fileId: "99ae0962-4d01-48a9-9d74-12fb8549c5dc",
    bytes: 811_381,
    sha256: "d92dfd9886e1363f99f2ce282ff86fc5796cf9e71c9a830367b52caad686bd96",
  },
  {
    name: "Question.txt",
    fileId: "f9370034-b61f-42bc-b2e3-2e64bf677c4c",
    bytes: 4_181,
    sha256: "82ced5174d53505d9bfea65abeea7aabd40fcf4b872f5aa4dc179e2d845402d3",
  },
  {
    name: "answerkey.txt",
    fileId: "854e32a9-1d8e-444d-b77d-8b8f2ac2b436",
    bytes: 1_943,
    sha256: "1d1dea2022715b97928ec245a1157d6df3f9f895514f4542e05e1fd03045b2ef",
  },
  {
    name: "Teacher_manual_marks_Anonymized.csv",
    fileId: "31c31cf0-01de-42f6-a72b-73a013ffacd1",
    bytes: 6_214,
    sha256: "51e194a98970dddf738dd81bdbc51e2d7dea5637666ee1ff7402e7165154b017",
  },
];

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

async function isCurrent(path, expected) {
  try {
    return sha256(await readFile(path)) === expected;
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
}

async function fetchFixture(fixture) {
  if (fixture.name !== basename(fixture.name)) {
    throw new Error(`Unsafe fixture name: ${fixture.name}`);
  }

  const finalPath = resolve(destination, fixture.name);
  if (await isCurrent(finalPath, fixture.sha256)) {
    process.stdout.write(`verified ${fixture.name}\n`);
    return;
  }

  const url = `${datasetBase}/${fixture.fileId}/file_downloaded`;
  const response = await fetch(url, {
    redirect: "follow",
    signal: AbortSignal.timeout(60_000),
  });
  if (!response.ok) {
    throw new Error(`${fixture.name}: HTTP ${response.status}`);
  }

  const bytes = Buffer.from(await response.arrayBuffer());
  const actualHash = sha256(bytes);
  if (bytes.length !== fixture.bytes || actualHash !== fixture.sha256) {
    throw new Error(
      `${fixture.name}: integrity mismatch (${bytes.length} bytes, ${actualHash})`,
    );
  }

  const temporaryPath = `${finalPath}.${process.pid}.part`;
  try {
    await writeFile(temporaryPath, bytes, { flag: "wx", mode: 0o600 });
    await rename(temporaryPath, finalPath);
  } catch (error) {
    await unlink(temporaryPath).catch(() => {});
    throw error;
  }
  process.stdout.write(`downloaded ${fixture.name}\n`);
}

await mkdir(destination, { recursive: true, mode: 0o700 });
for (const fixture of fixtures) {
  await fetchFixture(fixture);
}
