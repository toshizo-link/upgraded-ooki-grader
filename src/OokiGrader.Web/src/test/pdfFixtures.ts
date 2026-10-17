import { degrees, PDFDocument } from "pdf-lib";

export async function createScanPdf(name: string, pageCount = 1): Promise<File> {
  const document = await PDFDocument.create();
  for (let index = 0; index < pageCount; index++) {
    const page = document.addPage([300 + index, 400]);
    page.setRotation(degrees(index % 2 ? 90 : 0));
    page.drawText(`Page ${index + 1}`, { x: 20, y: 50 });
  }
  return new File([new Uint8Array(await document.save())], name, { type: "application/pdf" });
}

export function readPdfBytes(file: Blob): Promise<ArrayBuffer> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(reader.error);
    reader.readAsArrayBuffer(file);
  });
}
