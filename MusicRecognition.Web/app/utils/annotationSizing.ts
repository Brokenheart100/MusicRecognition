// PDF points per recognition pixel. Match the export's physical lower bound.
export const preferredAnnotationPixels = (interline: number, pointsPerPixel: number) =>
  Math.max(Math.ceil(4 / pointsPerPixel), Math.min(Math.round(8 / pointsPerPixel), Math.round(interline * 1.2)))

export const automaticFontScales = (preferred: number, minimum: number) =>
  [...new Set([1, .9, .8, .7, .6, 0].map(scale => Math.max(minimum, preferred * scale)))]

// Same count curve as the original-PDF layout. The physical 3 pt floor is
// applied by each renderer, after converting to its own coordinate space.
export const chordFontScale = (count: number) => 1 / (1 + .18 * (Math.max(1, count) - 1))
