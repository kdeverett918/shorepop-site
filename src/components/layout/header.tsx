import Image from 'next/image'
import { Container } from './container'

const links = [
  { href: '/support', label: 'Support' },
  { href: '/press', label: 'Press' },
]

export function Header() {
  return (
    <header className="border-navy/10 bg-sand/85 sticky top-0 z-40 border-b backdrop-blur">
      <Container className="flex h-16 items-center justify-between">
        <a href="/" className="flex items-center gap-3" aria-label="Shorepop home">
          <Image
            src="/img/icon-512.webp"
            alt=""
            width={36}
            height={36}
            className="rounded-[10px] shadow-sm"
            priority
          />
          <span className="font-display text-navy text-2xl font-semibold">Shorepop</span>
        </a>
        <nav aria-label="Main">
          <ul className="flex items-center gap-1 sm:gap-2">
            {links.map((l) => (
              <li key={l.href}>
                <a
                  href={l.href}
                  className="text-navy hover:bg-cream rounded-full px-3 py-2 text-base font-bold sm:px-4"
                >
                  {l.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
      </Container>
    </header>
  )
}
