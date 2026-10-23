export default defineAppConfig({
  ui: {
    colors: { primary: 'indigo', neutral: 'slate' },
    button: { slots: { base: 'cursor-pointer font-medium transition-all duration-200' } },
    select: { slots: { base: 'w-full rounded-xl' } },
    input: { slots: { base: 'w-full rounded-xl' } },
  },
})
