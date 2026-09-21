import type { Metadata } from 'next'
import { Container } from '@/components/layout/container'
import { siteConfig } from '@/config/site'

export const metadata: Metadata = {
  title: 'Privacy policy',
  description:
    'How Shorepop handles local game progress, optional support messages, and requests to delete your data.',
  alternates: { canonical: '/privacy' },
}

const email = siteConfig.email

export default function PrivacyPage() {
  return (
    <Container className="py-14">
      <article className="max-w-2xl">
        <h1 className="text-navy text-4xl font-semibold sm:text-5xl">Shorepop privacy policy</h1>
        <p className="text-muted mt-2 text-sm font-bold">Updated {siteConfig.policyDate}</p>

        <div className="text-navy/90 mt-8 space-y-6 text-lg leading-relaxed">
          <p>
            <strong>Shorepop</strong> is a puzzle game made by Kristine Everett. This policy
            describes the current test release, build 13, and how we handle game data and support
            messages.
          </p>

          <p>
            <strong>What the game stores on your device.</strong> Your progress, settings and local
            play journal are saved in the app&apos;s private storage on your phone. The game does
            not upload these records. Device backups may retain a copy according to your Apple or
            Google account settings; Shorepop does not operate a cloud-save service.
          </p>

          <p>
            <strong>Online services.</strong> Online rankings are unavailable in this test release.
            The game does not sign you in to Unity Gaming Services or send your scores to a
            leaderboard. You do not need an account or an internet connection to play.
          </p>

          <p>
            <strong>Analytics and diagnostics.</strong> This release has no configured game
            analytics destination. Unity analytics submission, Unity Connect, cloud diagnostics and
            advertising services are disabled. Apple or Google may separately process store,
            installation or diagnostic information under their own policies and your platform
            settings. TestFlight feedback and crash reports you share through the testing platform
            may be made available to us to investigate problems.
          </p>

          <p>
            <strong>Support messages.</strong> If you email us, we receive your email address,
            message and any attachments you choose to send. Our email provider processes this
            correspondence so we can respond and investigate your request. Please send only the
            information needed to explain the issue.
          </p>

          <p>
            <strong>What the game does not do.</strong> It shows no ads and sells nothing in this
            version. It does not ask for your location, contacts, photos, camera or microphone. It
            does not use your data for advertising or profiling, and it does not track you across
            other apps or websites.
          </p>

          <p>
            <strong>Children.</strong> Shorepop is intended for players 13 and older. We do not
            knowingly collect personal information from children.
          </p>

          <p>
            <strong>Deleting your data.</strong> Delete the app to remove its local game data, or
            use Android&apos;s app-storage settings to clear its data. Offloading an app on iOS
            retains its data. Manage any device-backup copies through your platform settings. There
            is no active Shorepop leaderboard account to delete in this release. To ask about your
            data or request deletion of support correspondence, email{' '}
            <a
              href={`mailto:${email}`}
              className="text-berry decoration-berry/40 hover:decoration-berry font-bold underline underline-offset-4"
            >
              {email}
            </a>
            . No game player ID is required.
          </p>

          <p>
            <strong>Changes.</strong> We will update this page when the game&apos;s data practices
            change, with the date at the top.
          </p>

          <p>
            <strong>Contact.</strong>{' '}
            <a
              href={`mailto:${email}`}
              className="text-berry decoration-berry/40 hover:decoration-berry font-bold underline underline-offset-4"
            >
              {email}
            </a>
          </p>
        </div>
      </article>
    </Container>
  )
}
