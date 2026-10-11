# Durable league delivery — N19

The service is packaged independently under `league/` on the deployment repository branch
`codex/shorepop-michael-league`. Only the league source, its pure Core dependencies and focused
tests are copied. `source-manifest.json` records the exact SHA-256 of each input. No frontend or
legacy verifier service is part of this deployment.

Runtime configuration:

- Project: `aea5f7f0-ebcb-4745-98c5-378bb2ad2feb` (one exact Unity Authentication project).
- `League__DatabaseProvider=postgres`; `League__DatabaseUrl` is supplied privately.
- `League__RepeatPolicy=BEST_PER_LEVEL_PER_WEEK`.
- `League__BoosterPolicy=UNBOOSTED_ONLY`, matching the client's Tailwind exclusion.
- `League__OwnerDecisionReference` records the scoped N19 integration decision.

Storage uses the existing ops PostgreSQL instance and the restricted `shorepop_league` login/schema.
Provisioning creates no database instance. The application cannot create roles or databases. Its
login has an eight-connection limit and each service process has a four-connection pool. PostgreSQL
tables are schema-qualified. The same fixed schema name in the existing `shorepop_verifier_test`
database is reserved for tests; no test score is inserted into production standings.

One transaction takes a stable per-player advisory lock and commits the attempt receipt, permanent
level best, weekly level best and weekly total together. Identical retries return the stored receipt,
even after restart or a week boundary. Conflicting permanent evidence returns 409. Database policy
pins project/repeat/booster settings and refuses mismatched reuse. Weekly reads use a repeatable
snapshot. `/health` probes storage and policy, returning 200 only when ready; it exposes the project,
rules, policies and deployment commit but no credential or player data.

`shorepop-big-wave-v4` replay uses the packaged Core. Level 5 accepts both the current authored
opening rocket and the earlier board without that rocket, with exact terminal score/stars required.
Other illegal or nonwinning routes remain refused. This establishes deterministic replay correctness,
not proof that a human played or that the client earned campaign access.

Every `/v1` route requires a signed, unexpired Unity token with exactly one valid subject, one exact
project claim, and `nbf`. Signing-key retrieval failure returns 503, retains fail-closed behavior
through the retry cooldown, and recovers after a successful refresh. A permanent submission refusal
returns `{ "code": "...", "attemptId": "..." }`; clients keep ambiguous responses retryable.

Build/test commands from the package root:

```powershell
dotnet restore tests/league-server/LeagueServerChecks.csproj --locked-mode
dotnet run --project tests/league-server/LeagueServerChecks.csproj -c Release -- <new-evidence-dir>
dotnet publish server/league/Shorepop.League.csproj -c Release -o <output-dir> -p:RestoreLockedMode=true
```

Set `SHOREPOP_LEAGUE_TEST_POSTGRES=1` and supply `LeagueTestDatabaseUrl` privately to run the durable integration suite. It refuses any
database except `shorepop_verifier_test` before writing. Live smoke uses real Unity identities only
for authenticated empty readback and invalid-submission checks; it creates no production scores.
The service remains subject to free hosting cold starts. Clients retain their durable outbox during
timeouts, 401 refreshes, 429 limits, and 503 outages.
