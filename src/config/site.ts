export const siteConfig = {
  name: 'Shorepop',
  tagline: 'A fox, five coasts, one tap',
  description:
    'Shorepop is a tap-to-blast puzzle game. Tap matching tiles, fire rocket combos and ride the Big Wave through 300 puzzles with a fox companion. Coming soon to Google Play and the App Store.',
  url: 'https://shorepop.com',
  email: 'hello@shorepop.com',
  appId: 'com.shorepop.game',
  ogImage: '/og-image.png',
  owner: 'Dizzywalk Records LLC',
  policyDate: '2026-10-11',
} as const

export const mailtoLaunch = `mailto:${siteConfig.email}?subject=${encodeURIComponent(
  'Tell me when Shorepop launches',
)}`

export const mailtoSupport = `mailto:${siteConfig.email}?subject=${encodeURIComponent(
  'Shorepop support',
)}`

export const mailtoPress = `mailto:${siteConfig.email}?subject=${encodeURIComponent(
  'Shorepop press',
)}`
