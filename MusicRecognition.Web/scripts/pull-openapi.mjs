import { writeFile } from 'node:fs/promises'
// 构建辅助脚本：从正在运行的 API 拉取 OpenAPI 文档，存成仓库里的接口快照。
// 用法：npm run api:pull -- <API 根地址>，地址取自 Aspire 启动后显示的端点。
const origin = process.argv[2]
// 第一个参数是 API 根地址；缺失或不是 http(s) 开头就报错退出。
if (!origin || !/^https?:\/\//.test(origin)) throw new Error('Usage: npm run api:pull -- <Aspire API origin>')
const response = await fetch(new URL('/openapi/v1.json', origin))
if (!response.ok) throw new Error(`OpenAPI HTTP ${response.status}`)
const spec = await response.json()
// Runtime Aspire ports are not part of the portable contract.
// 把服务器地址改成相对路径，避免把本机端口写进仓库快照。
spec.servers = [{ url: '/' }]
// 写回仓库里的接口快照，缩进 2 个空格、末尾换行，方便阅读和对比。
await writeFile(new URL('../openapi/score-api.json', import.meta.url), JSON.stringify(spec, null, 2) + '\n')
console.log('Updated portable OpenAPI snapshot')
