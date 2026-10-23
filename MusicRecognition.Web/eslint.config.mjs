// @ts-check
import withNuxt from './.nuxt/eslint.config.mjs'

export default withNuxt(
  { ignores: ['shared/api/generated/**'] },
  {
    rules: {
      // Existing SFC templates use compact Vue attribute formatting throughout.
      'vue/first-attribute-linebreak': 'off',
    },
  },
)
