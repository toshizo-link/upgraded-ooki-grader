export const maximumScanPages = 1_000;
const maximumUploadBytes = 250_000_000;

export interface ScanPdfPage {
  file: File;
  sourceFileName: string;
  sourceFileId: string;
  sourcePageNumber: number;
}

// Prepare the whole selection before changing the visible plan. A bad file
// must not silently remove pages from the middle of a student's submission.
export async function prepareScanPdfs(
  files: readonly File[],
  remainingPages: number,
  onProgress?: (message: string) => void,
): Promise<ScanPdfPage[]> {
  for (const file of files) {
    if (file.size > maximumUploadBytes
      || (file.type !== "application/pdf" && !file.name.toLowerCase().endsWith(".pdf"))) {
      throw new Error(`${file.name} は追加できません。PDF（250 MB以下）を選択してください。`);
    }
  }

  const { PDFDocument, EncryptedPDFError } = await import("pdf-lib");
  const pages: ScanPdfPage[] = [];
  for (const file of files) {
    onProgress?.(`${file.name} を読み込んでいます…`);
    const source = await readFile(file)
      .then((bytes) => PDFDocument.load(bytes, { updateMetadata: false }))
      .catch((reason: unknown) => {
        // pdf-lib's ES5 Error subclass does not preserve instanceof.
        throw new Error(reason instanceof Error && reason.message === new EncryptedPDFError().message
          ? `${file.name} はパスワードで保護されています。保護されていないPDFを選択してください。`
          : `${file.name} を読み取れませんでした。PDFとして保存し直すか、再スキャンしてください。`);
      });
    const count = source.getPageCount();
    if (!count) throw new Error(`${file.name} にページがありません。`);
    if (pages.length + count > remainingPages) {
      throw new Error(`一度に取り込めるのは${maximumScanPages}ページまでです。PDFを分けて追加してください。`);
    }

    const sourceFileId = crypto.randomUUID();
    if (count === 1) {
      pages.push({ file, sourceFileName: file.name, sourceFileId, sourcePageNumber: 1 });
      continue;
    }

    const stem = file.name.replace(/\.pdf$/i, "");
    for (let index = 0; index < count; index++) {
      onProgress?.(`${file.name}：${index + 1} / ${count}ページを準備中…`);
      try {
        const output = await PDFDocument.create();
        const [page] = await output.copyPages(source, [index]);
        output.addPage(page!);
        const bytes = await output.save();
        pages.push({
          file: new File([new Uint8Array(bytes)], `${stem} - ${index + 1}ページ.pdf`, {
            type: "application/pdf",
            lastModified: file.lastModified,
          }),
          sourceFileName: file.name,
          sourceFileId,
          sourcePageNumber: index + 1,
        });
      } catch {
        throw new Error(`${file.name} を分割できませんでした。PDFとして保存し直すか、再スキャンしてください。`);
      }
    }
  }
  return pages;
}

function readFile(file: File): Promise<ArrayBuffer> {
  if (typeof file.arrayBuffer === "function") return file.arrayBuffer();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(reader.error);
    reader.readAsArrayBuffer(file);
  });
}
