import { PDFDocument } from "pdf-lib";
import { describe, expect, it } from "vitest";
import { createScanPdf, readPdfBytes } from "../test/pdfFixtures";
import { naturalSortScanItems } from "./orderedScans";
import { prepareScanPdfs } from "./scanPdfs";

describe("scan PDF preparation", () => {
  it("splits every page without changing dimensions, rotation, or numeric order", async () => {
    const multiple = await createScanPdf("scan-2.pdf", 12);
    const single = await createScanPdf("scan-10.pdf");
    const pages = naturalSortScanItems(await prepareScanPdfs([single, multiple], 1_000));

    expect(pages).toHaveLength(13);
    expect(pages[12]?.file).toBe(single);
    expect(pages.slice(0, 12).map((page) => page.sourcePageNumber))
      .toEqual(Array.from({ length: 12 }, (_, index) => index + 1));
    for (let index = 0; index < 12; index++) {
      const page = pages[index]!;
      const document = await PDFDocument.load(await readPdfBytes(page.file));
      expect(document.getPageCount()).toBe(1);
      expect(document.getPage(0).getSize()).toEqual({ width: 300 + index, height: 400 });
      expect(document.getPage(0).getRotation().angle).toBe(index % 2 ? 90 : 0);
      expect(page.file.name).toBe(`scan-2 - ${index + 1}ページ.pdf`);
    }
  });

  it("keeps different PDFs with the same filename in separate contiguous groups", async () => {
    const pages = await prepareScanPdfs([
      await createScanPdf("scan.pdf", 2),
      await createScanPdf("scan.pdf", 2),
    ], 1_000);
    const sorted = naturalSortScanItems(pages);
    expect(sorted.map((page) => page.sourcePageNumber)).toEqual([1, 2, 1, 2]);
    expect(sorted[0]?.sourceFileId).toBe(sorted[1]?.sourceFileId);
    expect(sorted[2]?.sourceFileId).toBe(sorted[3]?.sourceFileId);
    expect(sorted[0]?.sourceFileId).not.toBe(sorted[2]?.sourceFileId);
  });

  it("rejects the whole selection when a PDF is broken or the total page limit is exceeded", async () => {
    const good = await createScanPdf("good.pdf", 2);
    const broken = new File(["broken"], "broken.pdf", { type: "application/pdf" });
    await expect(prepareScanPdfs([good, broken], 1_000)).rejects.toThrow("broken.pdf を読み取れませんでした");
    await expect(prepareScanPdfs([good], 1)).rejects.toThrow("1000ページまで");
  });

  it("identifies password-protected PDFs without ignoring encryption", async () => {
    const document = await PDFDocument.create();
    document.addPage();
    document.context.trailerInfo.Encrypt = document.context.register(document.context.obj({ Filter: "Standard" }));
    const encrypted = new File([new Uint8Array(await document.save())], "locked.pdf", { type: "application/pdf" });
    await expect(prepareScanPdfs([encrypted], 1_000)).rejects.toThrow("パスワードで保護されています");
  });
});
