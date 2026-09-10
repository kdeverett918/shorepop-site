import type { MetadataRoute } from 'next'
import { siteConfig } from '@/config/site'

export const dynamic = 'force-static'

export default function sitemap(): MetadataRoute.Sitemap {
  const lastModified = new Date(siteConfig.policyDate)
  return [
    { url: siteConfig.url, lastModified, changeFrequency: 'monthly', priority: 1 },
    { url: `${siteConfig.url}/support`, lastModified, changeFrequency: 'monthly', priority: 0.7 },
    { url: `${siteConfig.url}/press`, lastModified, changeFrequency: 'monthly', priority: 0.6 },
    { url: `${siteConfig.url}/privacy`, lastModified, changeFrequency: 'yearly', priority: 0.5 },
  ]
}
