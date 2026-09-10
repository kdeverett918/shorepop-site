import type { Metadata } from 'next'
import { Container } from '@/components/layout/container'
import { mailtoSupport, siteConfig } from '@/config/site'

export const metadata: Metadata = {
  title: 'Support',
  description:
    'Get help with Shorepop: contact, answers about lives, saved progress, the daily tide, ads and purchases, and how to delete your data.',
  alternates: { canonical: '/support' },
}

const faq = [
  {
    q: 'How do lives work?',
    a: 'You have three lives, shared across the whole game. Losing a level costs one. A life comes back every 20 minutes, up to three.',
  },
  {
    q: 'Where is my progress saved?',
    a: 'On your device, in the app’s private storage. There is no account to make. Deleting the app deletes your progress.',
  },
  {
    q: 'When does the daily tide reset?',
    a: 'Once a day, at midnight UTC. Each day brings one fresh puzzle, with yesterday’s available as a replay. Missing a day carries no penalty.',
  },
  {
    q: 'Are there ads or purchases?',
    a: 'No. The current version has no ads and no purchases.',
  },
  {
    q: 'Does the game need a connection?',
    a: 'No. You can play without one. The leaderboard needs a connection and shows "Not available right now" when it cannot reach the service.',
  },
]

const linkClass =
  'font-bold text-berry underline decoration-berry/40 underline-offset-4 hover:decoration-berry'

export default function SupportPage() {
  return (
    <Container className="py-14">
      <div className="max-w-2xl">
        <h1 className="text-navy text-4xl font-semibold sm:text-5xl">Support</h1>
        <p className="text-navy/90 mt-5 text-lg leading-relaxed">
          Something confusing, broken, or just a question? Email{' '}
          <a href={mailtoSupport} className={linkClass}>
            {siteConfig.email}
          </a>
          . Include your phone model and, if it helps, a screenshot.
        </p>

        <h2 className="text-navy mt-12 text-3xl font-semibold">Common questions</h2>
        <dl className="divide-border mt-6 divide-y">
          {faq.map((item) => (
            <div key={item.q} className="py-5">
              <dt className="font-display text-navy text-xl font-semibold">{item.q}</dt>
              <dd className="text-navy/85 mt-2 text-lg leading-relaxed">{item.a}</dd>
            </div>
          ))}
        </dl>

        <h2 className="text-navy mt-12 text-3xl font-semibold">Delete my data</h2>
        <p className="text-navy/90 mt-4 text-lg leading-relaxed">
          Your progress lives only on your phone, so deleting the app deletes it. The leaderboard
          record is tied to an anonymous id, not to you. If you want that record removed anyway,
          email{' '}
          <a href={mailtoSupport} className={linkClass}>
            {siteConfig.email}
          </a>{' '}
          with the player id shown on the game&apos;s Settings screen and we will remove it. The
          full details are in the{' '}
          <a href="/privacy" className={linkClass}>
            privacy policy
          </a>
          .
        </p>
      </div>
    </Container>
  )
}
