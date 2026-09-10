import type { Metadata } from 'next'
import { Container } from '@/components/layout/container'
import { siteConfig } from '@/config/site'

export const metadata: Metadata = {
  title: 'Privacy policy',
  description:
    'What Shorepop stores on your device, what it sends to Unity Gaming Services for leaderboards, and how to have your record removed.',
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
            explains what the game does with data.
          </p>

          <p>
            <strong>What the game stores on your device.</strong> Your progress, settings and local
            play journal are saved in the app&apos;s private storage on your phone. They stay there.
            Deleting the app deletes them.
          </p>

          <p>
            <strong>What the game sends over the network.</strong> Shorepop uses Unity Gaming
            Services for its leaderboards. When the game starts it signs you in anonymously: Unity
            creates a random player id that is not linked to your name, email, phone number or any
            account. After a level the game sends your score and a display name the game generated
            (a coastal animal, like &quot;Tidal Otter&quot;) to the leaderboard so you can see how
            your week compares with other players. That is the only information the game sends. If
            the service cannot be reached, the game works normally and the leaderboard shows
            &quot;Not available right now&quot;.
          </p>

          <p>
            Unity processes this on our behalf under its own privacy policy (
            <a
              href="https://unity.com/legal/privacy-policy"
              className="text-berry decoration-berry/40 hover:decoration-berry font-bold underline underline-offset-4"
            >
              https://unity.com/legal/privacy-policy
            </a>
            ). Unity may also receive basic diagnostic information about the game&apos;s
            installation and performance.
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
            <strong>Deleting your data.</strong> Because the leaderboard record is tied only to an
            anonymous id, there is nothing that identifies you to delete, but if you want that
            record removed, email{' '}
            <a
              href={`mailto:${email}`}
              className="text-berry decoration-berry/40 hover:decoration-berry font-bold underline underline-offset-4"
            >
              {email}
            </a>{' '}
            with the player id shown on the game&apos;s Settings screen and we will remove it.
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
