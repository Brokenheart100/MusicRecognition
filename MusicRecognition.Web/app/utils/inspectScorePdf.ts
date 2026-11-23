// Client-side input feedback; the worker independently validates the original PDF.
export type PdfInspection =
  | { key: 'pdfTooManyPages'; pages: number }
  | { key: 'pdfEncrypted' }
  | { key: 'pdfUnreadable' }
  | null

export async function inspectScorePdf(file: File): Promise<PdfInspection> {
  const pdfjs = await import('pdfjs-dist')
  pdfjs.GlobalWorkerOptions.workerSrc = `/vendor/pdfjs/${pdfjs.version}/pdf.worker.mjs`
  const task = pdfjs.getDocument({ data: await file.arrayBuffer() })
  try {
    const document = await task.promise
    return document.numPages > 30 ? { key: 'pdfTooManyPages', pages: document.numPages } : null
  } catch (cause) {
    return (cause as { name?: string }).name === 'PasswordException' ? { key: 'pdfEncrypted' } : { key: 'pdfUnreadable' }
  } finally {
    await task.destroy()
  }
}
