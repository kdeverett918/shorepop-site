import { siteConfig } from '@/config/site'
import { Container } from './container'

export function Footer() {
  return (
    <footer className="bg-navy text-sand mt-20">
      <Container className="flex flex-col gap-6 py-10 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-sand/80 text-sm">© 2026 {siteConfig.owner}</p>
        <nav aria-label="Footer">
          <ul className="flex flex-wrap items-center gap-x-6 gap-y-2 text-sm font-bold">
            <li>
              <a href="/privacy" className="hover:text-sky">
                Privacy
              </a>
            </li>
            <li>
              <a href="/support" className="hover:text-sky">
                Support
              </a>
            </li>
            <li>
              <a href="/press" className="hover:text-sky">
                Press
              </a>
            </li>
            <li>
              <a href={`mailto:${siteConfig.email}`} className="hover:text-sky">
                {siteConfig.email}
              </a>
            </li>
          </ul>
        </nav>
      </Container>
    </footer>
  )
}
