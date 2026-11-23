// 简谱数字写多大，由这里统一算：既不能小到看不清，也不能大到压住音符。
// 思路是先定一个理想字号区间（7~10 磅），再按当前 PDF 的缩放比例换算成像素。
// 换算跟着乐谱行距和纸张缩放走，所以同一份标注在不同大小的纸上看起来一样大。
const minimumPreferredPoints = 7
const maximumPreferredPoints = 10

// 为标注预留排版空间时依次尝试的候选字号（单位：磅），从大到小排列。
export const reservationFontPoints = [4.5, 4, 3.5, 3] as const

// 按参考尺寸（乐谱行距）估一个理想字号：参考值乘 1.55，但限制在 7~10 磅之间。
export const preferredAnnotationSize = (reference: number, unitsPerPoint: number) =>
  Math.max(minimumPreferredPoints * unitsPerPoint, Math.min(maximumPreferredPoints * unitsPerPoint, reference * 1.55))

// 把字号换算成识别原图的像素：至少保证最小字号放得下，再四舍五入到整像素。
// Match the original-PDF layout's physical sizing, rounded to recognition pixels.
export const preferredAnnotationPixels = (interline: number, pointsPerPixel: number) =>
  Math.max(Math.ceil(minimumPreferredPoints / pointsPerPixel), Math.round(preferredAnnotationSize(interline, 1 / pointsPerPixel)))

// 同一个位置叠了几个音（和弦）时字号要缩小：每多一个音约乘 0.93，保证并排写得下。
export const chordFontScale = (count: number) => 1 / (1 + .08 * (Math.max(1, count) - 1))
