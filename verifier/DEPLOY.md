# Deploying shorepop-verifier to Render

## Current deployment (2026-10-10; supersedes the historical steps below)

**Owner account correction:** publication must use Michael's App Store Connect team.
The earlier Apple key and Sandbox probe below belong to the prior account and are
historical evidence only. Do not deploy them as Michael's credentials. Michael's
In-App Purchase key is pending; his App Store Connect Admin key is a different key
type and must not be substituted. The new Apple bundle is `com.dizzywalk.shorepop`,
team `UH99QQFW52`, App Store Connect app `6821483469`.
Android remains `com.shorepop.game`. `APPLE_BUNDLE_ID` and `GOOGLE_PACKAGE_ID`
are independent exact identities. The candidate defaults to these values and
refuses mismatched configuration. Old Apple test rows are retained untouched but
are not accepted by the new verifier's validation, reconciliation or recovery
routes. This is a new Michael-team app, not an update to the former TestFlight app.

The preserved legacy Render service is `srv-das74am0tbcc73e40tu0`,
`https://shorepop-verifier.onrender.com`, free web compute in Oregon. Render builds
`kdeverett918/shorepop-site`, branch `main`, directory `verifier`, with automatic
deploys enabled. Do not push this candidate to `main` or restart the legacy service.
The prepared replacement is a separate free web service named
`shorepop-verifier-michael`, on branch `codex/shorepop-michael-verifier`, directory
`verifier`, with automatic deployments disabled. `server/verifier` in the game repo
is the source mirror. The new service ID and URL must come from creation readback;
the prepared name is not evidence of a live endpoint.

The existing Play service account reached the purchase-token validation API with
the expected invalid-token response. Its JSON was configured as `GOOGLE_PLAY_SA_JSON`.
This proves API access, not a real transaction settlement. A dedicated
`shorepop_verifier` schema and restricted `shorepop_verifier_app` login were created
on the existing studio Postgres instance, without a new paid resource. The login
can create in that schema, cannot create in `public`, and has an eight-connection
limit. `VERIFIER_STORE=postgres` and `VERIFIER_DATABASE_URL` were staged in Render;
secret readbacks matched. No deploy was triggered by that configuration step.
The earlier **In-App Purchase** key staged in the old service belongs to the prior
Apple account. The associated Sandbox success and Production 401 are historical
observations and do not establish Michael-team access. The replacement service
must use separately verified Michael-team IAP credentials. Sanitized receipts are
in the game repo at `outputs/launch-ops/20261010/backend`.

### Separate service; preserved legacy ledger

The previous deploy uses free-plan ephemeral SQLite. A deployment or spin-down may
discard claims. The service has no bulk export endpoint, and Render free web
services have neither SSH nor dashboard shell. Missing logs do not establish an
empty ledger. Preserve that service and route the new client to a separate endpoint
with its own durable PostgreSQL ledger. The Apple bundle is new, so old Apple
receipts are refused by exact bundle validation; the old deployed verifier did not
have Google validation credentials. No legacy claim import, reset, deletion or
anonymous-account linkage is performed. PostgreSQL protects new claims; it cannot
reconstruct ownership already lost from SQLite.

### Unity project migration

The authorized target is Michael's project `aea5f7f0-ebcb-4745-98c5-378bb2ad2feb`.
The legacy project is `26418e78-9eb7-42c5-955c-b4c141443252`. The candidate accepts
one exact signed `project_id` from the primary `UGS_PROJECT_ID` or the optional
`UGS_ADDITIONAL_PROJECT_IDS` allowlist. It retains the fixed Unity issuer/JWKS,
RS256 signature, token lifetime, single-subject and single-project checks.
The separate Michael service sets `UGS_PROJECT_ID` to Michael's ID and leaves
`UGS_ADDITIONAL_PROJECT_IDS` empty. The old service retains its old project settings.
The optional allowlist is not used for this release: the ledger keys use raw `sub`,
so admitting both projects would require separate proof that subject namespaces and
ownership continuity are compatible. No anonymous identities or stored claimants
are mapped between projects.

The staged fixed `VERIFIER_SUBSCRIPTION_REVISION_EPOCH=1791672606408` uses UTC Unix
milliseconds as the new-row revision baseline. It lets clients with earlier SQLite
counter revisions accept an authoritative shorter entitlement after cutover. PostgreSQL
persists and increments each player's revision; restarts and different configured
baselines never rewrite an existing row. Keep the staged baseline fixed and never lower
it for future rows. This handles revision ordering, not missing ownership or prior grants.

`VERIFIER_REVENUE_PROOF_KEY` and the Play app licensing public key are also staged.
The first is a private AES-256 key for the attribution recovery cache and must survive
deployments unchanged unless live envelopes are migrated. The cache's 30-day window is
per verified economic order; a replay cannot extend its expiry. Production Apple API
access and store notification endpoints remain separate outstanding work.

### Current verification and release sequence

1. Build the verifier and run its console suite. Run PostgreSQL tests with
   `SHOREPOP_TEST_POSTGRES` pointing to a separate test database. The launch packet
   helper provisions one on the existing instance without printing credentials.
2. Independently review atomic claims, acknowledgments, sticky refunds and
   subscription revisions.
3. Mirror only verifier files into `shorepop-site/verifier`, commit/push the dedicated
   branch, and create the reviewed separate free service. Never push this release
   to the legacy service's `main` branch. The integrator owns release approval.
4. Follow the new Render service until live. `/healthz` must show the exact candidate `build`,
   `store=postgres_durable`, and `google=configured`. `appleServerApi=true` means
   credentials are present; actual provider authentication is separate evidence.
5. Check unauthenticated purchase calls return 401. Complete an authorized store
   sandbox/license-test purchase and duplicate/resume/restore checks on the
   selected emulator. Tests and invalid-token probes are not purchases.

After durable settlement begins, a code rollback must retain the Postgres store
and compatible schema; do not return to ephemeral SQLite.

## Historical setup record (2026-09-26; not current instructions)

**Not deployed yet (2026-09-26).** Render builds from GitHub, and `server/verifier/` exists only in the
uncommitted working tree. The lead commits; agents do not. Local Docker is installed, but its daemon was
not running, so no image could be built and pushed either.

## Steps for the lead

1. **Test.** Run the suite; it must print `RESULT passed=46 failed=0` and exit 0:

   ```
   dotnet run --project server/verifier/tests/Shorepop.Verifier.Tests.csproj -c Release
   ```

2. **Commit and push.** Commit `server/verifier/` (the `bin/` and `obj/` folders are gitignored), then
   push the branch Render will build. `render.yaml` says `master`; change it if you deploy another branch.

3. **Create the service with the Render API.** Use the "Kristine Projects" workspace (owner
   `tea-d60ol5ali9vc73fdlplg`, the same one as the `shorepop` site). Render must have GitHub access to the
   private `kdeverett918/harbor-sprouts` repo. The key is `RENDER_API_KEY` in
   `~/dizzywalk-ops/.env`; never echo it.

   ```bash
   KEY=$(grep '^RENDER_API_KEY=' ~/dizzywalk-ops/.env | cut -d= -f2- | tr -d '"\r')
   curl -s -X POST https://api.render.com/v1/services \
     -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" -d '{
       "type": "web_service",
       "name": "shorepop-verifier",
       "ownerId": "tea-d60ol5ali9vc73fdlplg",
       "repo": "https://github.com/kdeverett918/harbor-sprouts",
       "branch": "master",
       "rootDir": "server/verifier",
       "autoDeploy": "no",
       "serviceDetails": {
         "runtime": "docker",
         "plan": "free",
         "region": "oregon",
         "healthCheckPath": "/healthz",
         "envSpecificDetails": { "dockerfilePath": "./Dockerfile", "dockerContext": "." }
       },
       "envVars": [
         { "key": "UGS_PROJECT_ID", "value": "26418e78-9eb7-42c5-955c-b4c141443252" },
         { "key": "APP_BUNDLE_ID", "value": "com.shorepop.game" },
         { "key": "APPLE_ALLOWED_ENVIRONMENTS", "value": "Production,Sandbox" }
       ]
     }' | python -c "import sys,json; d=json.load(sys.stdin); s=d.get('service',d); print(s.get('id'), s.get('serviceDetails',{}).get('url'), d.get('message',''))"
   ```

   - `UGS_PROJECT_ID` is the public project id from `design/specs/ugs-project.md`.
   - Leave `GOOGLE_PLAY_SA_JSON` unset for now. Google then answers 503, which is intended while Play is
     blocked on the app transfer. `~/dizzywalk-ops/.env` has a `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`; set its
     text as `GOOGLE_PLAY_SA_JSON` only once that account has Shorepop access after the transfer.
   - Add `APPLE_IAP_KEY_ID`, `APPLE_IAP_ISSUER_ID` and `APPLE_IAP_PRIVATE_KEY` when an App Store Connect
     In-App Purchase key exists (Users and Access > Integrations > In-App Purchase). Without them, reconcile
     cannot see refunds made after purchase.

4. **Deploy and wait.** Run `render deploys list <srv-id> -o json --confirm` until the deploy shows `live`.

5. **Check the deploy.** Replace `<url>` with the service URL:

   ```
   curl -s https://<url>/healthz
   # {"status":"ok","apple":"jws_chain_g3","appleServerApi":false,"google":"not_configured","store":"sqlite_ephemeral"}
   curl -s -o /dev/null -w "%{http_code}\n" -X POST https://<url>/v1/purchases/validate -d '{}' -H 'content-type: application/json'
   # 401
   ```

## Before real revenue

- **Storage.** Free-plan SQLite resets on every deploy, so transaction ownership is forgotten. Add a
  persistent disk or a Postgres store.
- **Client.** Done: `Assets/Scripts/Services/Commerce/HttpNativePurchaseValidator.cs` POSTs to this URL
  with the UGS access token.
- **Money review.** The verifier and the client wiring both need an independent money-review pass.
