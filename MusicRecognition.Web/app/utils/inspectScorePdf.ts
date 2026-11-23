// 上传乐谱 PDF 时的"快速体检"：在浏览器里先看一眼文件，有问题立刻提示用户。
// 检查三件事：页数是否超过 30 页、是否加密打不开、是否根本不是 PDF。
// 这里只做友善提示；真正的安全校验由后台 Worker 再做一遍，不能只信前端。
// Client-side input feedback; the worker independently validates the original PDF.
// 检查结果：没问题返回 null；有问题返回错误类型和对应的提示文案 key。
export type PdfInspection =
  | { key: 'pdfTooManyPages'; pages: number }
  | { key: 'pdfEncrypted' }
  | { key: 'pdfUnreadable' }
  | null

export async function inspectScorePdf(file: File): Promise<PdfInspection> {
// 按需加载 PDF 解析库（pdfjs-dist），避免一打开首页就下载大文件、拖慢速度。
  const pdfjs = await import('pdfjs-dist')
// 解析库的后台线程文件从项目自带的 /vendor/pdfjs 目录读取，版本号跟着库版本走。
  pdfjs.GlobalWorkerOptions.workerSrc = `/vendor/pdfjs/${pdfjs.version}/pdf.worker.mjs`
// 把整个文件读进内存再解析，因为解析库不能直接处理浏览器里的文件对象。
  const task = pdfjs.getDocument({ data: await file.arrayBuffer() })
  try {
    const document = await task.promise
// 30 是产品定的上限：页数太多处理很慢，先拦住并提示，不让用户白等。
    return document.numPages > 30 ? { key: 'pdfTooManyPages', pages: document.numPages } : null
  } catch (cause) {
// 报错名是 PasswordException 就是加密 PDF；其他错误一律当成损坏、读不了。
    return (cause as { name?: string }).name === 'PasswordException' ? { key: 'pdfEncrypted' } : { key: 'pdfUnreadable' }
  } finally {
// 无论成功还是失败都释放解析器占用的内存和后台线程。
    await task.destroy()
  }
}
