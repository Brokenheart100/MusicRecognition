declare module 'verovio/wasm' {
  export default function createModule(options?: { print?: (line: string) => void; printErr?: (line: string) => void }): Promise<unknown>
}
declare module 'verovio/esm' {
  export function enableLogToBuffer(value: boolean, module: unknown): void
  export class VerovioToolkit {
    constructor(module: unknown)
    setOptions(options: Record<string, string | number | boolean>): void
    loadData(data: string): boolean
    getMEI(options?: Record<string, unknown>): string
    getLog(): string
    getPageCount(): number
    getVersion(): string
    renderToSVG(page: number, options?: Record<string, unknown>): string
    destroy(): void
  }
}
