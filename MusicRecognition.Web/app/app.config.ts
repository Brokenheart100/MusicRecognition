// 全局外观预设：全站组件库（Nuxt UI）的默认配色和圆角风格集中在这里。
// 好处是改一处、全站生效；具体页面要特殊样式时仍可单独覆盖。
// 主色用靛蓝（indigo）、中性色用石板灰（slate），整体是冷静的学习工具风格。
export default defineAppConfig({
  ui: {
// colors 定义全局色板：primary 是主色，neutral 是文字和边框用的中性灰阶。
    colors: { primary: 'indigo', neutral: 'slate' },
// 按钮默认带手型光标和 200ms 过渡动画，鼠标移上去能看出可点击。
    button: { slots: { base: 'cursor-pointer font-medium transition-all duration-200' } },
// 下拉框和输入框都占满整行、用大圆角，保持表单控件观感一致。
    select: { slots: { base: 'w-full rounded-xl' } },
    input: { slots: { base: 'w-full rounded-xl' } },
  },
})
