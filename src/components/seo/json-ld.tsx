import { siteConfig } from '@/config/site'

export function GameJsonLd() {
  const data = {
    '@context': 'https://schema.org',
    '@type': 'VideoGame',
    name: siteConfig.name,
    url: siteConfig.url,
    description: siteConfig.description,
    genre: 'Puzzle',
    gamePlatform: ['Android', 'iOS'],
    applicationCategory: 'Game',
    operatingSystem: 'Android, iOS',
    image: `${siteConfig.url}${siteConfig.ogImage}`,
    author: { '@type': 'Person', name: siteConfig.owner },
  }
  return (
    <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(data) }} />
  )
}
