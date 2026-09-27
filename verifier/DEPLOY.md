# Deploying shorepop-verifier to Render

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
