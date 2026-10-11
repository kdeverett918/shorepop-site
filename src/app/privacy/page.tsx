import type { Metadata } from 'next'
import { Container } from '@/components/layout/container'
import { siteConfig } from '@/config/site'

export const metadata: Metadata = {
  title: 'Privacy policy',
  description:
    'How Shorepop handles game progress, online services, advertising, purchases, analytics and privacy requests.',
  alternates: { canonical: '/privacy' },
}

const email = siteConfig.email
const mailtoEmail = 'mailto:' + email
const linkClass =
  'font-bold text-berry underline decoration-berry/40 underline-offset-4 hover:decoration-berry'
const headingClass = 'font-display text-navy text-2xl font-semibold'

export default function PrivacyPage() {
  return (
    <Container className="py-14">
      <article className="max-w-2xl">
        <h1 className="text-navy text-4xl font-semibold sm:text-5xl">Shorepop privacy policy</h1>
        <p className="text-muted mt-2 text-sm font-bold">Updated {siteConfig.policyDate}</p>

        <div className="text-navy/90 mt-8 space-y-6 text-lg leading-relaxed">
          <p>
            <strong>Shorepop</strong> is a puzzle game made by {siteConfig.owner}. This policy
            covers the game, this website and support messages. Advertising, purchases and online
            services are being introduced across our releases. The features and services available
            to you depend on your platform and game version; older test versions may not include
            them.
          </p>

          <h2 className={headingClass}>Game progress and online services</h2>
          <p>
            The game saves progress, currencies, settings, unfinished boards, rewards and purchase
            entitlements in private storage on your device. Device backups may keep copies under
            your Apple or Google account settings. Shorepop does not provide a general cloud-save
            service.
          </p>
          <p>
            Online features can use Unity Gaming Services to create a player identifier and sign you
            in without asking you to create a username or password. Authentication credentials are
            stored on your device. Where rankings are available, player identifiers, scores,
            attempts and verification results are sent to the ranking service, and a generated
            display name and score can appear to other players. These online records are separate
            from your local save.
          </p>

          <h2 className={headingClass}>Analytics and diagnostics</h2>
          <p>
            We use Unity Analytics, Google Analytics for Firebase and PostHog to understand game
            sessions, tutorial and level outcomes, progression, feature use, rewards, advertising
            and purchases. Events can include an installation or player identifier, timestamps, game
            version, platform, settings and progress. These identifiers are pseudonymous: they can
            connect activity from the same installation without requiring your name.
          </p>
          <p>
            Sentry helps us investigate crashes and errors using diagnostic details such as the game
            build, operating system, device information, error traces and session health. Service
            providers also receive network information, including IP addresses, when the app
            connects to them. TestFlight or Google Play may separately provide testing, installation
            and diagnostic information under their own settings and policies.
          </p>

          <h2 className={headingClass}>Advertising and measuring campaigns</h2>
          <p>
            Versions with advertising use AppLovin MAX and participating networks, including Google
            AdMob, to show ads and deliver rewards for eligible completed ads. These providers may
            process device and app information, advertising or vendor identifiers when available, IP
            addresses, approximate location, ad interactions, revenue and privacy choices. This
            supports ad delivery, measurement, fraud prevention and, where permitted, personalized
            advertising. The participating networks can vary by platform, region and ad.
          </p>
          <p>
            Tenjin helps us understand which campaigns bring players to Shorepop. It can receive
            installation identifiers, device and campaign information, game milestones, ad revenue
            and verified purchase information. Campaign measurement can involve sharing these events
            with the relevant advertising platform, such as AppLovin, Google, Meta or TikTok, where
            that connection is enabled. Advertising partners may combine information with activity
            from other apps or websites according to your choices and their policies.
          </p>

          <h2 className={headingClass}>Purchases</h2>
          <p>
            Where offered, purchases are processed by Apple or Google through the in-game store. We
            do not receive your full payment-card number. Unity IAP and our purchase-verification
            service process player and product identifiers, transaction identifiers, store receipts
            or purchase tokens, purchase value and currency, and entitlement, subscription and
            refund status. We use these records to verify purchases, deliver and restore eligible
            items, reconcile refunds and prevent duplicate or invalid grants. Purchase and revenue
            information may also be used for the analytics and campaign measurement described above.
          </p>

          <h2 className={headingClass}>Your privacy choices</h2>
          <p>
            In versions that provide <strong>Share stats</strong> in Settings, use it to change game
            analytics, diagnostics and campaign measurement collection. Advertising choices are
            separate: use
            <strong> Privacy choices</strong> in Settings when it is available, the consent message
            shown for your region, and your device&apos;s advertising and tracking settings. On iOS,
            you can manage the app&apos;s tracking permission in system Settings. Turning off stats
            or personalized advertising does not remove ads or stop processing needed for a purchase
            or another online feature you request.
          </p>
          <p>
            Depending on where you live, you may have rights to access, correct or delete personal
            data, withdraw consent, or object to or opt out of particular processing, including
            targeted advertising or sharing for that purpose. Contact us at{' '}
            <a href={mailtoEmail} className={linkClass}>
              {email}
            </a>{' '}
            to make a request or ask about your options. You may also have the right to complain to
            your local data-protection authority.
          </p>

          <h2 className={headingClass}>Support and this website</h2>
          <p>
            If you email us, we receive your address, message and any attachments you send. We and
            our email provider use them to respond and investigate your request. Please do not send
            passwords, full card details or raw purchase tokens. This website uses ordinary hosting
            services, which receive connection and request information when you visit. The website
            does not currently include an advertising pixel or an analytics script.
          </p>

          <h2 className={headingClass}>Retention and deletion</h2>
          <p>
            Local data remains until it is removed from your device or overwritten. Deleting the
            app, or clearing its storage on Android, removes its local data; offloading an app on
            iOS keeps that data. Manage backup copies through your platform account settings.
            Uninstalling does not automatically erase online records or cancel a subscription.
          </p>
          <p>
            There is no single automatic deletion period for all Shorepop data. Purchase and
            entitlement records can be retained for verification, restoration, refund handling,
            fraud prevention and legal obligations. Analytics, diagnostic and support records are
            handled according to their purpose and the relevant service&apos;s retention settings. A
            short-lived purchase-recovery copy, where used, does not set the retention period for
            the underlying transaction record.
          </p>
          <p>
            If your version includes Delete account in Settings, choose it to initiate deletion from
            the game. A connection is required. After your request is accepted, the game removes
            local progress, ends your current player session and begins account and service-record
            deletion. Remaining online cleanup may require verification and processing by support;
            keep your request identifier to check its status. Deletion does not cancel an Apple or
            Google subscription. Necessary transaction, refund, security and legal records can be
            retained as explained above.
          </p>
          <p>
            If you cannot access the game, use an older version, or want to request deletion of
            support correspondence, email{' '}
            <a href={mailtoEmail} className={linkClass}>
              {email}
            </a>{' '}
            with the subject “Shorepop data deletion.” Tell us your platform and the records
            concerned; include a player identifier or store order number if you have one. We may
            need additional information to locate records and verify the request. We will explain
            any retained records and let you know when the request is completed. Our{' '}
            <a href="/support" className={linkClass}>
              support page
            </a>{' '}
            has further guidance.
          </p>

          <h2 className={headingClass}>Service providers</h2>
          <p>
            The providers above help operate the game and may process information outside your
            country. Their privacy information explains their processing, safeguards and choices:
          </p>
          <ul className="list-disc space-y-2 pl-6">
            <li>
              <a href="https://legal.applovin.com/privacy/" className={linkClass}>
                AppLovin
              </a>{' '}
              and its{' '}
              <a href="https://legal.applovin.com/applovin-partners-privacy/" className={linkClass}>
                advertising partners
              </a>
            </li>
            <li>
              <a href="https://policies.google.com/privacy" className={linkClass}>
                Google
              </a>{' '}
              (AdMob, consent services, Google Play and{' '}
              <a href="https://firebase.google.com/support/privacy" className={linkClass}>
                Firebase
              </a>
              )
            </li>
            <li>
              <a href="https://tenjin.com/privacy/" className={linkClass}>
                Tenjin
              </a>
            </li>
            <li>
              <a href="https://posthog.com/privacy" className={linkClass}>
                PostHog
              </a>
            </li>
            <li>
              <a href="https://sentry.io/privacy/" className={linkClass}>
                Sentry
              </a>
            </li>
            <li>
              <a
                href="https://unity.com/legal/game-player-and-app-user-privacy-policy"
                className={linkClass}
              >
                Unity
              </a>{' '}
              (authentication, online services, analytics and purchases)
            </li>
            <li>
              <a href="https://www.apple.com/legal/privacy/" className={linkClass}>
                Apple
              </a>{' '}
              (App Store and TestFlight)
            </li>
            <li>
              <a href="https://render.com/privacy" className={linkClass}>
                Render
              </a>{' '}
              (website and service hosting)
            </li>
          </ul>

          <h2 className={headingClass}>Children, questions and changes</h2>
          <p>
            If you are a parent or guardian concerned that a child has provided personal information
            to Shorepop, contact{' '}
            <a href={mailtoEmail} className={linkClass}>
              {email}
            </a>{' '}
            so we can investigate. For any privacy question, use the same address. We will update
            this page as data practices change and show the revision date above.
          </p>
        </div>
      </article>
    </Container>
  )
}
