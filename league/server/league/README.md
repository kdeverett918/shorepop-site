# Shorepop league backend

ASP.NET 8 league service with deterministic replay and durable PostgreSQL storage. See [N19 deployment configuration](DEPLOYMENT.md) for the current project, policy, package and storage design. Default `appsettings.json` refuses every `/v1` request with HTTP **503** until configured. No credentials are included. Local tests and live readback have separate evidence receipts.

## Request and response contract

All `/v1` routes require `Authorization: Bearer <Unity Authentication idToken/access token>`. The authenticated `sub` identifies the player; no request field selects a player. JSON uses camelCase, rejects unknown fields, limits request bodies to 256 KiB and nesting to 16, and permits at most 2,000 recorded actions.

`POST /v1/campaign/wins` takes the existing `CampaignWinEvidence` object directly:

```json
{
  "attempt": {
    "id": "32 lowercase hexadecimal characters",
    "levelId": 13,
    "stableSlot": 61,
    "boosters": 0,
    "creditedBoosters": 0,
    "paidCoins": 0,
    "seed": 0,
    "startedUtcTicks": 0,
    "rules": "shorepop-score-collect-shell-v2",
    "evidenceComplete": true,
    "actions": [{ "kind": "tap", "x": 1, "y": 1, "ability": 0, "right": false }]
  },
  "score": 0,
  "stars": 0,
  "completedUtcTicks": 0
}
```

An attempt started with Tailwind boosters also carries `"streakBoosters": <mask>` (the Tailwind part of `creditedBoosters`). The field is omitted when zero, by the client (`CampaignAttempts.LeagueWireJson`) and by the server contract alike, so an unboosted payload and its fingerprint are unchanged.

**Deploy order.** The contract rejects unknown members, so a server built before `streakBoosters` answers any payload that carries it with HTTP 400, which the client treats as retryable: that evidence would sit at the head of the outbox and block every later win. Deploy this server before releasing any client that can send the field. (Today's client sends it only for Tailwind wins, which it does not queue while `LeagueRanksTailwind` is false, so the order matters from the release that flips that flag.)

The example illustrates the wire shape, and intentionally is not valid winning evidence. The server resolves the board by `LevelCatalog.SlotOf(id) == stableSlot`, then checks the real seed, rules, loadout and every action with the linked pure Core implementation. Numeric `levelId` is an informational label, bounded 1–300; the server normalizes it out of the computed payload fingerprint so that an ambiguous schema-13 level-13 submission can be retried after migration to level 61. Stable slot zero is refused. The final board must be **Won** with the exact submitted score and stars. No submitted hash is accepted.

HTTP 200:

```json
{"attemptId":"...","status":"accepted","duplicate":false,"weekStartUtc":"2026-09-07T00:00:00Z","weeklyAdded":1200,"weeklyScore":2400,"levelBest":1200}
```

Identical retries return the original receipt with `duplicate: true`, even across process restarts or a later week. A changed permanent payload for the same `(player, attemptId)` returns **409** `attempt_payload_conflict`. Structurally invalid or nonwinning evidence returns **422**. HTTP 400 covers malformed/unknown JSON fields, 413 oversized bodies, and 429 request limits. Authentication failures return **401** and should be retried after sign-in/token refresh; unavailable signing keys, storage, or configuration return **503**, preserving the client outbox for retry. A 200 response can be lost after COMMIT; replaying the same durable evidence resolves that ambiguity.

`GET /v1/records/{stableSlot}?rules=shorepop-score-collect-shell-v2` resolves its category from server policy and returns:

```json
{"stableSlot":61,"rules":"shorepop-score-collect-shell-v2","category":"unboosted","known":true,"score":1200,"name":"Calm Otter"}
```

No record returns `known: false`, zero score and empty name. The query reads the `(rules, stableSlot, category, score DESC, player)` index, across all players; there is no 100-entry metadata scan. The optional `category` query parameter may repeat the configured category; an explicit mismatch or empty value returns 422. Rules mismatches also return 422. Category is fixed by the configured eligibility policy, never chosen by the submission.

`GET /v1/weekly?neighbours=7` (1–25) returns:

```json
{"weekStartUtc":"2026-09-07T00:00:00Z","weeklyScore":1200,"rank":3,"tier":"","neighbours":[{"rank":3,"name":"Calm Otter","score":1200,"tier":"","isPlayer":true}]}
```

Names are deterministic server-derived adjective/animal pseudonyms, not user-controlled text. No tier system is implemented; `tier` is empty. The configured backend database is authoritative for these routes. It does not publish a secondary UGS leaderboard mirror and never seeds synthetic public standings.

## Explicit configuration and current eligibility

Provide these environment variables; the current scoped integration policy is recorded in DEPLOYMENT.md:

- `League__ProjectId`: actual Unity project UUID; required JWT `project_id` must match exactly.
- `League__DatabaseProvider`: `postgres` for deployment or `sqlite` for local checks.
- `League__DatabaseUrl`: private PostgreSQL URL/connection string, using the restricted league login and pre-provisioned `shorepop_league` schema. Never place it in source.
- `League__DatabasePath`: absolute path to a persistent local SQLite volume. One service deployment may have concurrent threads/processes sharing that local file. Separate database replicas or network filesystems are unsupported.
- `League__RepeatPolicy`: explicitly choose `BEST_PER_LEVEL_PER_WEEK` or `FIRST_CLEAR_PER_LEVEL_EVER`.
- `League__BoosterPolicy`: explicitly choose `UNBOOSTED_ONLY` (the default policy while the owner decides) or `UNBOOSTED_OR_TAILWIND`, which also accepts a board whose every booster is exactly one Tailwind step's free loadout (all credited, no coins; `design/specs/meta-loop-r16.md`). The streak itself is client-claimed and unverifiable here, so choosing it is an owner decision. The client does not queue Tailwind wins at all while `CampaignAttempts.LeagueRanksTailwind` is false, which mirrors `UNBOOSTED_ONLY`; flip that flag only in a client released against a server configured with `UNBOOSTED_OR_TAILWIND`.
- `League__OwnerDecisionReference`: reference to the actual approved decision. The test-only value is not approval.

`BEST_PER_LEVEL_PER_WEEK` adds only improvements over that player's best score on that permanent level in the current week. `FIRST_CLEAR_PER_LEVEL_EVER` adds the score only when the server has no earlier record for that player/rules/slot/category; replay improvements can update Top Scores but add no weekly points. Neither policy is selected in shipped configuration. Database initialization pins project/repeat/booster settings and refuses mismatched reuse; changing policy requires a separately reviewed migration or new database.

Weeks begin **Monday 00:00 UTC**, using server receipt time. Client dates cannot select a league week. Historical rows are retained, and querying a new week returns zero/unplaced until a new accepted attempt qualifies. A retry of an old accepted attempt never creates a new-week addition.

There is no server-authoritative entitlement provider. **Every boosted, credited-booster, ability or revive claim is refused** (the one exception is a Tailwind-only board under `UNBOOSTED_OR_TAILWIND`), including an ability later undone. The pure replay routine supports tap, earned Breeze, ability and Undo, but the submission eligibility guard rejects abilities before replay. Configuration cannot enable them. The server never accepts client balances as proof of ownership.

## Authentication and durability

JWT bearer validation requires RS256 signatures from the fixed Unity JWKS URL, exact issuer, valid expiry/not-before, one nonempty `sub`, and the configured `project_id`. JWKS retrieval uses HTTPS with redirects disabled, a three-second timeout, a 256 KiB cap, an eight-hour cache, and refresh attempts throttled to one per minute. Empty/expired key sets fail closed. Unknown signing keys trigger bounded refresh/retry; no token or private credential is sent to the JWKS endpoint. See [Unity's client authentication contract](https://docs.unity.com/en-us/services-web-apis/client-auth).

SQLite uses WAL, `synchronous=FULL`, a five-second busy timeout, and `BEGIN IMMEDIATE`. One transaction validates/stores the unique attempt result, all-time per-level best, weekly per-level best and weekly total. Server-computed SHA-256 fingerprints enforce permanent-payload conflicts. Transaction failure leaves every ranking table unchanged; no in-memory duplicate cache determines correctness.

`ugs-project-access-policy.json` stages Unity's documented denial of direct Player leaderboard score writes. It has **not been applied**, and applying it must merge with existing project policy rather than replacing unrelated statements. See [Unity's server access control guidance](https://docs.unity.com/en-us/cloud-code/server-access-control). The custom endpoint does not make direct UGS client writes safe by itself.

## Deploy order when board rules change (round 17, the Big Wave)

The server validates a win by replaying it with the **same `Assets/Scripts/Core`** the client ran, and it
accepts only evidence whose `rules` equals `ReplayValidator.RulesVersion`. A client whose Core scores
differently (the Big Wave adds score on every level from 5, and every non-tutorial target was
recalibrated for it) is therefore refused as `replay_mismatch` or `invalid_evidence` by a server built
from older Core.

The rules value is now `shorepop-big-wave-v4`, and it must be identical in
`Assets/Scripts/Services/CampaignAttempts.cs` (`RulesVersion`) and `ReplayValidator.cs`. The deploy
order is fixed:

1. Build and deploy this server from the same commit as the client, so it has the same Core and
   the same rules value.
2. Only then ship a client carrying the new rules.

Records are keyed by rules, so the new rules start their own boards. An older client's queued wins
(rules `shorepop-toy-curriculum-v3`) get a 422 from the new server, and the client files them as
rejection receipts. The client never queues a win from an attempt it resumed under the prior rules
(`CampaignAttempts.LeagueCanRank`).

## Build, local checks and deployment preparation

From the repository root:

```powershell
dotnet restore server/league/Shorepop.League.csproj --locked-mode
dotnet run --project tests/league-server/LeagueServerChecks.csproj -c Release
dotnet publish server/league/Shorepop.League.csproj -c Release -o server/league/publish -p:RestoreLockedMode=true
```

The direct NuGet dependencies are pinned to `Microsoft.AspNetCore.Authentication.JwtBearer` **8.0.31**, `Microsoft.Data.Sqlite` **8.0.31** and `Npgsql` **8.0.9**, including checked-in transitive lock files. Core source is compiled directly from `Assets/Scripts/Core/`; there are no Unity references. `Dockerfile` supplies a repository-root build recipe. PostgreSQL deployment does not require a persistent local volume. TLS terminates at the hosting proxy, and database connections default to certificate verification. `/health` checks database connectivity and pinned policy.

This validates deterministic replay and transactional score accounting. It does not establish human play, server-issued attempt ownership, campaign unlock eligibility, device integrity or entitlement ownership. A client can synthesize an otherwise legal deterministic replay. Live service identity/readback, load capacity, backup/restore and native reconnect behavior require their own scoped evidence. See the N19 receipt for exactly what was exercised.
