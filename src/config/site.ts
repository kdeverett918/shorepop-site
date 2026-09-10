export const siteConfig = {
  name: 'Shorepop',
  tagline: 'A fox, five coasts, one tap',
  description:
    'Shorepop is a tap-to-blast puzzle game. Tap groups of matching pieces to hit each score target while a fox walks the coast you bring back to life. Coming soon to Google Play and the App Store.',
  url: 'https://shorepop.com',
  email: 'hello@shorepop.com',
  appId: 'com.shorepop.game',
  ogImage: '/og-image.png',
  owner: 'Kristine Everett',
  policyDate: '2026-09-10',
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
