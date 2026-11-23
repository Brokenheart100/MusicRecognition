const minimumPreferredPoints = 7
const maximumPreferredPoints = 10

export const reservationFontPoints = [4.5, 4, 3.5, 3] as const

export const preferredAnnotationSize = (reference: number, unitsPerPoint: number) =>
  Math.max(minimumPreferredPoints * unitsPerPoint, Math.min(maximumPreferredPoints * unitsPerPoint, reference * 1.55))

// Match the original-PDF layout's physical sizing, rounded to recognition pixels.
export const preferredAnnotationPixels = (interline: number, pointsPerPixel: number) =>
  Math.max(Math.ceil(minimumPreferredPoints / pointsPerPixel), Math.round(preferredAnnotationSize(interline, 1 / pointsPerPixel)))

export const chordFontScale = (count: number) => 1 / (1 + .08 * (Math.max(1, count) - 1))
