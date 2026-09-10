import type { Metadata } from 'next'
import Image from 'next/image'
import { Container } from '@/components/layout/container'
import { PhoneFrame } from '@/components/phone-frame'
import { mailtoPress, siteConfig } from '@/config/site'
import { coasts } from '@/data/coasts'

export const metadata: Metadata = {
  title: 'Press and brand',
  description:
    'Shorepop press kit: the app icon, the five-coast art, in-game screens, a one-paragraph description and contact details.',
  alternates: { canonical: '/press' },
}

const linkClass =
  'font-bold text-berry underline decoration-berry/40 underline-offset-4 hover:decoration-berry'

const facts = [
  ['Name', 'Shorepop'],
  ['Store name', 'Shorepop: Blast Puzzle'],
  ['Developer', siteConfig.owner],
  ['Platforms', 'Android and iOS (coming soon)'],
  ['Genre', 'Puzzle, casual, single player, offline'],
  ['Price', 'Free. This version has no ads and no purchases.'],
  ['Age rating', '4+'],
  ['App id', siteConfig.appId],
  ['Contact', siteConfig.email],
]

export default function PressPage() {
  return (
    <Container className="py-14">
      <div className="max-w-2xl">
        <h1 className="text-navy text-4xl font-semibold sm:text-5xl">Press and brand</h1>
        <p className="text-navy/90 mt-5 text-lg leading-relaxed">
          Shorepop is a tap-to-blast puzzle game for phones. You tap groups of matching pieces to
          reach each level&apos;s score target before your moves run out; bigger groups pay far
          more, and every move you did not need is added to your score at the end. Rockets, seed
          bombs and prism flowers come from clever matches, and crates and ice reward planning. A
          fox travels the coast with you across five coasts, each with its own light, scenery and
          tile set, and your stars and coins bring a small seaside harbor back to life. Sixty
          campaign puzzles, a daily tide, three shared lives and saved progress on your device. This
          version has no ads and no purchases.
        </p>
        <p className="text-navy/90 mt-4 text-lg">
          Press questions and review builds:{' '}
          <a href={mailtoPress} className={linkClass}>
            {siteConfig.email}
          </a>
        </p>
      </div>

      <h2 className="text-navy mt-14 text-3xl font-semibold">Fact sheet</h2>
      <dl className="mt-5 grid max-w-2xl gap-x-8 gap-y-2 text-lg sm:grid-cols-[10rem_1fr]">
        {facts.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-navy font-bold">{k}</dt>
            <dd className="text-navy/85 sm:mb-1">{v}</dd>
          </div>
        ))}
      </dl>

      <h2 className="text-navy mt-14 text-3xl font-semibold">Icon</h2>
      <div className="mt-5 flex flex-wrap items-center gap-6">
        <Image
          src="/img/icon-512.webp"
          alt="The Shorepop app icon: a five-colour prism flower on a cream block, with a gold star and coral blocks behind it, on harbor teal."
          width={160}
          height={160}
          className="shadow-lift rounded-[36px]"
        />
        <ul className="space-y-1 text-lg">
          <li>
            <a href="/icon-512.png" className={linkClass} download>
              Download icon (PNG, 512 px)
            </a>
          </li>
          <li>
            <a href="/og-image.png" className={linkClass} download>
              Download social card (PNG, 1200 by 630)
            </a>
          </li>
        </ul>
      </div>

      <h2 className="text-navy mt-14 text-3xl font-semibold">The five coasts</h2>
      <p className="text-navy/85 mt-3 max-w-2xl text-lg">
        Left to right: Arrival Cove, Glasshouse Garden, Sunset Market, Cloudfall Isles and Lantern
        Harbor. Every coast is procedural art rendered from the game&apos;s own sources.
      </p>
      <figure className="shadow-lift mt-5 overflow-hidden rounded-2xl">
        <Image
          src="/img/world-strip.webp"
          alt="Strip of the five coasts side by side: Arrival Cove in the morning, Glasshouse Garden under glass domes, Sunset Market at golden hour, Cloudfall Isles above the clouds, and Lantern Harbor at night."
          width={1600}
          height={327}
          className="block h-auto w-full"
          sizes="(max-width: 1152px) 100vw, 1152px"
        />
      </figure>
      <ul className="mt-4 flex flex-wrap gap-x-6 gap-y-1 text-base">
        {coasts.map((c) => (
          <li key={c.slug}>
            <a href={`/img/${c.slug}.webp`} className={linkClass} download>
              {c.name} panel
            </a>
          </li>
        ))}
        <li>
          <a href="/img/world-strip.webp" className={linkClass} download>
            Full strip
          </a>
        </li>
      </ul>

      <h2 className="text-navy mt-14 text-3xl font-semibold">In-game screens</h2>
      <p className="text-navy/85 mt-3 max-w-2xl text-lg">
        Captures from the current internal test build at 360 by 640. The home screen and the map at
        each coast.
      </p>
      <div className="mt-6 flex flex-wrap gap-6">
        <PhoneFrame
          src="/img/home-world-1.webp"
          alt="Shorepop home screen at Arrival Cove: the fox on the coast, coin and star counts, three lives, the daily challenge card and a Play button."
          width={180}
        />
        {coasts.map((c) => (
          <PhoneFrame key={c.slug} src={c.map} alt={c.mapAlt} width={180} />
        ))}
      </div>
      <ul className="mt-4 flex flex-wrap gap-x-6 gap-y-1 text-base">
        <li>
          <a href="/img/home-world-1.webp" className={linkClass} download>
            Home screen
          </a>
        </li>
        {coasts.map((c) => (
          <li key={c.slug}>
            <a href={c.map} className={linkClass} download>
              {c.name} map
            </a>
          </li>
        ))}
      </ul>
    </Container>
  )
}
