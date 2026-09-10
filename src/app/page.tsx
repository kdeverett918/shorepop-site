import Image from 'next/image'
import { Container } from '@/components/layout/container'
import { PhoneFrame } from '@/components/phone-frame'
import { GameJsonLd } from '@/components/seo/json-ld'
import { mailtoLaunch } from '@/config/site'
import { coasts } from '@/data/coasts'

const features = [
  '60 campaign puzzles across five coasts, each with a calibrated score target',
  'A fox companion that walks the map between the levels you have earned',
  'Five coasts with their own tile skins, lighting and scenery',
  'Rockets, seed bombs, prism flowers and their combinations',
  'Crates and ice that reward planning',
  'Three stars per level, earned by how far past the target you finish',
  'A bonus for every move you did not need',
  'A personal best per level, called out when you beat it',
  'Three lives shared across the game, one back every 20 minutes',
  'Undo, for as long as the puzzle is running',
  'A daily tide: one fresh puzzle each day, yesterday’s as a replay, a seven-stamp card, and no penalty for missing a day',
  'Five harbor restoration projects',
  'Original music and sound, gentle motion, text size and haptics controls',
  'Saved progress on your device and play without a connection',
]

function ComingSoon({ className = '' }: { className?: string }) {
  return (
    <div className={`flex flex-col items-start gap-4 ${className}`}>
      <p className="text-navy text-base font-bold sm:text-lg">
        Coming soon to Google Play and the App Store.
      </p>
      <a
        href={mailtoLaunch}
        className="bg-berry font-display shadow-lift hover:bg-berry-deep inline-flex items-center rounded-full px-7 py-3.5 text-lg font-semibold text-white transition-colors"
      >
        Email me when it launches
      </a>
      <p className="text-muted text-sm">
        Opens a message to hello@shorepop.com. No mailing list, no tracking, just a reply when the
        game is out.
      </p>
    </div>
  )
}

export default function HomePage() {
  return (
    <>
      <GameJsonLd />

      {/* Hero: sky above, sand below, like the game's own horizon */}
      <section
        className="relative overflow-hidden"
        style={{
          background: 'linear-gradient(180deg, #8fe5f7 0%, #c6f0f9 38%, #ffeacb 72%, #fff6e6 100%)',
        }}
      >
        <Container className="grid items-center gap-12 pt-14 pb-20 lg:grid-cols-[1.05fr_1fr] lg:pt-20 lg:pb-24">
          <div className="max-w-xl">
            <h1 className="text-navy text-5xl leading-[1.02] font-bold sm:text-6xl lg:text-7xl">
              A fox, five coasts, one tap.
            </h1>
            <p className="text-navy/85 mt-6 text-lg leading-relaxed sm:text-xl">
              Tap groups of matching pieces to reach each level&apos;s score target before your
              moves run out. A fox travels the coast with you, and your stars and coins bring a
              small seaside harbor back to life.
            </p>
            <ComingSoon className="mt-8" />
          </div>

          <div className="relative mx-auto flex items-end justify-center gap-4 sm:gap-6">
            <PhoneFrame
              src="/img/map-world-1.webp"
              alt={coasts[0]?.mapAlt ?? 'Arrival Cove map screen'}
              width={210}
              className="translate-y-8 -rotate-3 sm:translate-y-10"
            />
            <PhoneFrame
              src="/img/home-world-1.webp"
              alt="Shorepop home screen at Arrival Cove: the fox on the coast, 570 coins, 18 stars, three lives, the daily challenge card and a Play button for level 7."
              width={250}
              priority
              className="z-10"
            />
          </div>
        </Container>
      </section>

      {/* Five coasts: each panel sits in its own sky */}
      <section aria-labelledby="coasts-heading" className="pt-16">
        <Container>
          <h2 id="coasts-heading" className="text-navy text-3xl font-semibold sm:text-4xl">
            Five coasts, each in its own light.
          </h2>
          <p className="text-navy/80 mt-3 max-w-2xl text-lg">
            A sunlit cove, a glasshouse garden, a market at golden hour, islands above the clouds
            and a lantern harbor at night. Each has its own tile set and scenery, and the fox waits
            at the level you reached until you earn the next one.
          </p>
        </Container>
        <div className="coast-scroll mt-8 flex gap-4 overflow-x-auto px-5 pb-6 sm:px-8 xl:mx-auto xl:grid xl:max-w-[88rem] xl:grid-cols-5 xl:overflow-visible">
          {coasts.map((c) => (
            <figure
              key={c.slug}
              className="shadow-lift w-[78vw] max-w-[360px] shrink-0 overflow-hidden rounded-2xl xl:w-auto xl:max-w-none"
              style={{ background: c.sky, color: c.ink }}
            >
              <Image
                src={`/img/${c.slug}.webp`}
                alt={`${c.name}, coast ${c.number} of 5: ${c.light}.`}
                width={720}
                height={650}
                className="block h-auto w-full"
                sizes="(max-width: 640px) 78vw, (max-width: 1280px) 360px, 20vw"
              />
              <figcaption className="px-5 pt-3 pb-4">
                <span className="font-display text-xl font-semibold">
                  {c.number}. {c.name}
                </span>
                <span className="block text-sm opacity-80">{c.light}</span>
              </figcaption>
            </figure>
          ))}
        </div>
      </section>

      {/* How it plays */}
      <section aria-labelledby="play-heading" className="pt-16">
        <Container className="grid gap-12 lg:grid-cols-[1fr_auto] lg:items-center">
          <div className="max-w-2xl space-y-8">
            <h2 id="play-heading" className="text-navy text-3xl font-semibold sm:text-4xl">
              How it plays
            </h2>
            <div className="text-navy/85 space-y-5 text-lg leading-relaxed">
              <p>
                Bigger groups pay far more than small ones, and every move you did not need is added
                to your score at the end, so the move you finish on decides how many stars you keep.
              </p>
              <p>
                Build rockets, seed bombs and prism flowers from clever matches, then set them off
                together. Crates and ice change how you plan and pay off when you clear them. When
                the board needs a fresh angle, hold Breeze and slide the pieces the way you want.
              </p>
              <p>
                Your stars and coins bring a small seaside harbor back to life, from a garden path
                to a lighthouse and a glasshouse. Every project is earned by playing.
              </p>
              <p className="text-navy font-bold">
                No ads interrupt a puzzle. This version has no ads and no purchases.
              </p>
            </div>
          </div>
          <div className="flex justify-center gap-4 sm:gap-6">
            <PhoneFrame
              src="/img/map-world-3.webp"
              alt={coasts[2]?.mapAlt ?? 'Sunset Market map screen'}
              width={200}
              className="translate-y-6 rotate-2"
            />
            <PhoneFrame
              src="/img/map-world-5.webp"
              alt={coasts[4]?.mapAlt ?? 'Lantern Harbor map screen'}
              width={200}
              className="-rotate-2"
            />
          </div>
        </Container>
      </section>

      {/* What you get */}
      <section aria-labelledby="get-heading" className="pt-16">
        <Container>
          <div className="bg-cream rounded-3xl px-6 py-10 sm:px-10">
            <h2 id="get-heading" className="text-navy text-3xl font-semibold sm:text-4xl">
              What you get
            </h2>
            <ul className="text-navy/90 mt-6 grid gap-x-10 gap-y-3 text-lg sm:grid-cols-2">
              {features.map((f) => (
                <li key={f} className="flex gap-3">
                  <span
                    aria-hidden="true"
                    className="bg-coral mt-[0.55rem] h-2.5 w-2.5 shrink-0 rounded-full"
                  />
                  <span>{f}</span>
                </li>
              ))}
            </ul>
          </div>
        </Container>
      </section>

      {/* Closing */}
      <section aria-labelledby="soon-heading" className="pt-16">
        <Container className="flex flex-col items-start gap-6 sm:flex-row sm:items-center sm:gap-10">
          <Image
            src="/img/icon-512.webp"
            alt="The Shorepop app icon: a five-colour prism flower on a cream block, with a gold star and coral blocks behind it."
            width={128}
            height={128}
            className="shadow-lift rounded-[28px]"
          />
          <div>
            <h2 id="soon-heading" className="text-navy text-3xl font-semibold sm:text-4xl">
              Almost at the shore.
            </h2>
            <ComingSoon className="mt-4" />
          </div>
        </Container>
      </section>
    </>
  )
}
