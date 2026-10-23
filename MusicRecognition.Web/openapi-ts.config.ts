import { defineConfig } from '@hey-api/openapi-ts'

export default defineConfig({
  input: './openapi/score-api.json',
  output: './shared/api/generated',
  plugins: [
    '@hey-api/typescript', '@hey-api/client-fetch',
    { name: 'zod', dates: { offset: true } },
    { name: '@hey-api/sdk', validator: { response: 'zod' } },
    '@tanstack/vue-query',
  ],
})
