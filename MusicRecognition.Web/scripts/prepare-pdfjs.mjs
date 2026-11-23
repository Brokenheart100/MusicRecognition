import { cp, mkdir, readFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
// 构建辅助脚本：把 PDF 预览需要的文件从依赖包（pdfjs-dist）复制到 public/vendor 下。
// 浏览器只能直接访问 public 里的文件，所以要预先复制，并按版本号分目录，升级依赖后旧缓存自然失效。

// 从依赖包自带的 package.json 里读出当前版本号，用来拼目标目录。
const packageFile = fileURLToPath(import.meta.resolve('pdfjs-dist/package.json'))
const { version } = JSON.parse(await readFile(packageFile, 'utf8'))
const root = dirname(packageFile)
// 目标是 public/vendor/pdfjs/<版本号>/：浏览器只能访问 public 下的文件。
const target = fileURLToPath(new URL(`../public/vendor/pdfjs/${version}/`, import.meta.url))
// 目录不存在就创建（recursive 允许一次创建多层）。
await mkdir(target, { recursive: true })
// worker 脚本负责在后台线程解析 PDF，预览必须有它。
await cp(join(root, 'build/pdf.worker.mjs'), join(target, 'pdf.worker.mjs'))
// 标准字体和 wasm 目录整体复制：PDF 排版和压缩计算都会用到。
for (const directory of ['standard_fonts', 'wasm']) {
  await cp(join(root, directory), join(target, directory), { recursive: true })
}
