# shorepop.com

Marketing site for Shorepop, a tap-to-blast puzzle game for Android and iOS by Kristine Everett.

Static Next.js export (`output: 'export'`), published from `out/` by a Render static site. No
server code, no analytics, no forms: the only call to action is a `mailto:` link.

## Pages

| Route      | Purpose                                                            |
| ---------- | ------------------------------------------------------------------ |
| `/`        | Hero, the five coasts, how it plays, the feature list, coming soon |
| `/privacy` | Privacy policy (the URL both app stores point at)                  |
| `/support` | Contact and FAQ                                                    |
| `/press`   | Icon, coast art, in-game screens, description, contact             |

## Develop

```bash
npm ci
npm run dev        # http://localhost:3000
npm run build      # writes ./out
npm run lint
npm run typecheck
npm run format:check
```

## Content sources

Copy and the privacy policy mirror `docs/store/listing-en-US.md` and `docs/store/privacy-policy.md`
in the game repo. Images under `public/img` are downscaled WebP copies of the game's own icon,
world art and QA captures; regenerate them from the game repo rather than editing in place.

## Deploy

Render deploys `main` automatically (see `render.yaml`). Custom domains: `shorepop.com` and
`www.shorepop.com`.
