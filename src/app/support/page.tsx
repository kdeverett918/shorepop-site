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
    a: 'Progress is saved in the app’s private storage on your device. Shorepop does not provide a general cloud-save service. Versions with online services can also keep separate player, ranking and purchase records. Deleting the app removes local progress but does not automatically delete those online records. Device-backup copies are managed through your Apple or Google settings.',
  },
  {
    q: 'When does the daily tide reset?',
    a: 'Once a day, at midnight UTC. Each day brings one fresh puzzle, with yesterday’s available as a replay. Missing a day carries no penalty.',
  },
  {
    q: 'Are there ads or purchases?',
    a: 'Advertising and optional in-app purchases are being introduced across releases. Availability depends on your platform and game version; some older test builds have neither. Available prices and purchase terms appear in the in-game store. Rewarded ads require an eligible completed ad to grant the displayed reward.',
  },
  {
    q: 'Does the game need a connection?',
    a: 'You can play the core puzzles offline. Ads, purchases, purchase restoration, online rankings and other online services need a connection when they are available in your version.',
  },
  {
    q: 'How do I get help with a purchase?',
    a: 'Check that you are using the same Apple or Google account. Use Restore purchases in game Settings when that option is available. Contact us with your platform, game version and store order number if an item is missing. Do not send your password, full card details or raw receipt token. Manage subscriptions and refund requests through the store where you bought them; deleting the app does not cancel a subscription.',
  },
  {
    q: 'How do I change privacy choices?',
    a: 'Use Share stats in game Settings for analytics, diagnostics and campaign measurement, and Privacy choices when offered for advertising consent. Your device also provides advertising and tracking controls. These choices do not remove ads or prevent processing needed to fulfil a purchase. Available controls depend on your version and region.',
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

        <h2 id="delete-account" className="text-navy mt-12 text-3xl font-semibold">
          Delete my account and data
        </h2>
        <p className="text-navy/90 mt-4 text-lg leading-relaxed">
          If your version includes Delete account in Settings, choose it. Read the confirmation and
          submit your request while connected to the internet. After it is accepted, local progress
          is removed, your current player session ends and account and online-record deletion
          begins. Remaining service cleanup may need support verification and processing; keep your
          request identifier if you contact us. We will confirm when that cleanup is completed.
          Deletion does not cancel a subscription: manage it through Apple or Google.
        </p>
        <p className="text-navy/90 mt-4 text-lg leading-relaxed">
          Delete the app to remove local progress, settings and play records. On Android, you can
          also clear the app&apos;s data in its storage settings. Offloading on iOS keeps your data.
          Manage any device-backup copies through your Apple or Google settings. This does not
          automatically delete online records or cancel subscriptions. If you cannot access the
          game, use an older release, or want to delete support emails, request deletion by
          contacting{' '}
          <a
            href={`mailto:${siteConfig.email}?subject=Shorepop%20data%20deletion`}
            className={linkClass}
          >
            {siteConfig.email}
          </a>{' '}
          with the subject “Shorepop data deletion.” Include your platform and the records
          concerned, plus a player identifier or store order number if you have one. We may need
          more information to locate records and verify your request. Requests are handled through
          support; some purchase or other records may be retained for verification, refunds,
          security or legal obligations. The full details are in the{' '}
          <a href="/privacy" className={linkClass}>
            privacy policy
          </a>
          .
        </p>
      </div>
    </Container>
  )
}
