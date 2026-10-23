// Client-side input feedback; the worker independently validates the original PDF.
export async function inspectScorePdf(file: File): Promise<string | null> {
  const pdfjs = await import('pdfjs-dist')
  pdfjs.GlobalWorkerOptions.workerSrc = `/vendor/pdfjs/${pdfjs.version}/pdf.worker.mjs`
  const task = pdfjs.getDocument({ data: await file.arrayBuffer() })
  try {
    const document = await task.promise
    return document.numPages > 30 ? `这份 PDF 有 ${document.numPages} 页，目前最多支持 30 页。` : null
  } catch (cause) {
    return (cause as { name?: string }).name === 'PasswordException'
      ? '这份 PDF 已加密，请先导出未加密的 PDF 再上传。'
      : '无法读取这份 PDF，请检查文件是否完整后再试。'
  } finally {
    await task.destroy()
  }
}
