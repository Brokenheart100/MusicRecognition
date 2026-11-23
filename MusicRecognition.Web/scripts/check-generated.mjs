import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { createClient } from '@hey-api/openapi-ts'
import config from '../openapi-ts.config.ts'
// 构建辅助脚本：检查前端自动生成的 SDK（shared/api/generated）是否和 OpenAPI 快照一致。
// 做法：在临时目录重新生成一份 SDK，再和仓库里的逐文件比较；不一致就报错，提醒重新生成。
// 这样"接口文档改了、SDK 忘了重新生成"的情况能在提交前被发现。
// 在系统临时目录里建一个带随机后缀的目录，既不污染仓库，也避免多次运行互相干扰。
const temp = await mkdtemp(join(tmpdir(), 'pujian-sdk-'))
// 递归列出目录下所有文件，返回排好序的相对路径，保证两次比较顺序一致。
async function files(root, prefix = '') {
  const result = []
  for (const entry of await readdir(join(root, prefix), { withFileTypes: true })) {
    const path = join(prefix, entry.name)
// 是目录就继续往下找，是文件就把路径收进结果。
    result.push(...(entry.isDirectory() ? await files(root, path) : [path]))
  }
  return result.sort()
}
// 生成并比较；无论成功还是失败，finally 里都会清理临时目录。
try {
// 用 openapi-ts.config.ts 的配置，在临时目录里重新生成一份 SDK。
  await createClient({ ...config, output: temp })
// target 是仓库里的 SDK 目录，expected 是刚生成的文件清单，actual 是现有文件清单。
  const target = resolve('shared/api/generated'), expected = await files(temp), actual = await files(target)
// 先比文件名清单：多一个文件或少一个文件都算不一致。
  if (JSON.stringify(expected) !== JSON.stringify(actual)) throw new Error('Generated file list differs; run npm run api:generate')
// 再逐文件逐字节比较内容，任何一处不同都说明生成结果漂移了。
  for (const path of expected) {
    if (!(await readFile(join(temp, path))).equals(await readFile(join(target, path)))) throw new Error(`Generated drift: ${path}`)
  }
// 真正一致时才打印统计，方便在 CI 日志里确认这一步确实跑过。
  console.log(`SDK is reproducible: ${expected.length} files match the OpenAPI snapshot`)
// 清理：只删除本次创建的临时目录，不碰系统里其他文件。
} finally {
  // Only remove the unique temporary directory created by this invocation.
  if (resolve(temp).startsWith(resolve(tmpdir()) + '\\pujian-sdk-') || resolve(temp).startsWith(resolve(tmpdir()) + '/pujian-sdk-')) await rm(temp, { recursive: true })
}
