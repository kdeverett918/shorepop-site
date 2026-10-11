# Shorepop purchase verifier

An ASP.NET 8 service (same stack as `server/league`) that backs the client's `INativePurchaseValidator`
contract in `Assets/Scripts/Services/Commerce/NativePurchaseValidation.cs`. The client side is
`Assets/Scripts/Services/Commerce/HttpNativePurchaseValidator.cs`, which posts the proof with the player's
UGS access token and reads the responses below. The current Michael-account deployment is
`shorepop-verifier-michael` at `https://shorepop-verifier-michael.onrender.com`
(Render `srv-db5h5s942hec738v1cd0`, dedicated `codex/shorepop-michael-verifier` branch).

**2026-10-11 deployed identity:** the service targets Michael's Apple app
`com.dizzywalk.shorepop` and Unity project `aea5f7f0-ebcb-4745-98c5-378bb2ad2feb`.
Android retains `com.shorepop.game`. PostgreSQL, Play validation and Michael's verified
In-App Purchase key are configured. Sandbox authentication is verified; Apple production
currently returns401 on the unreleased app. This is not a transaction receipt.
The former SQLite service and its `main` auto-deploy setting remain untouched.
See `DEPLOY.md` for exact source/live identities. Configuration does not establish
provider authentication or an actual purchase settlement.

## Operational endpoints

`GET /healthz` checks the durable database/schema with a three-second deadline and returns503
when unavailable, alongside build/configuration flags. It never calls a provider purchase API.

`POST /v1/privacy/account-deletion` requires the current player's Unity bearer token. Input is
`Store`, `ApplicationId`, optional GUID `AnalyticsInstallId`, and optional32–64 hex-character
`FirebaseAppInstanceId`. The authenticated subject is authoritative; no input player ID is used.
The response contains stable `RequestId`, `Status=accepted`, `RequestedAtUnixMs`, `PlayerId`,
`Store`, `ApplicationId`, and `SettlementRecordsRetained=true`. Acceptance atomically queues
provider cleanup and permanently suppresses/erases optional encrypted attribution proofs.
It does not assert that provider deletion is complete. Delete Unity Auth only after acceptance.
Repeating different identity metadata returns422 without changing the original request.

`POST /v1/privacy/revenue-proofs/erase` uses the same authentication and exact app scope for
proof-cache erasure alone. Both routes preserve necessary canonical settlement/replay records.
The support operator must verify optional client-supplied analytics identifiers before erasing
provider data. Never delete claims/acks or reset the ledger to fulfill a privacy request.

## Who a transaction belongs to

| Product type | Products | Rule |
|---|---|---|
| Consumable | `gg.shorepop.gems.*`, `gg.shorepop.coins.*`, `lives.refill`, `abilities.bundle`, `welcome`, `shellbank` | **Single owner.** The first player to validate owns it; others get 409 `transaction_owned_by_another_player`. Apple only: while the owner has **not acked** (`POST /v1/purchases/ack`), ownership may move: to the token-matched player at any time, to anyone 24 h after the last claim. Once acked it never moves. Google consumables never move. |
| Non-consumable | `gg.shorepop.noads`, `gg.shorepop.starter` | **Follows the store account, bounded.** Up to 5 distinct players per original transaction (`claimant_limit_reached` beyond). If the verified JWS carries an `appAccountToken`, a new player must match it or present a restore. |
| Subscription | `gg.shorepop.pass.monthly` | Same as non-consumable; renewals share the original transaction's claimant set and cap. |

### appAccountToken

The client sets StoreKit's `appAccountToken` at purchase time to a UUID derived from its UGS player id, and
also sends it as `AppAccountToken` in the proof. The server **only trusts the copy inside the verified JWS**
(`appAccountToken` in the signed payload) and compares it with its **own** derivation of the authenticated
player (`sub`). The request field is informational: a mismatch with the server's derivation is logged
(client derivation bug) and never changes a decision.

Derivation (RFC 4122 UUID version 5, SHA-1), which the client must reproduce exactly:

- namespace: `6ba7b811-9dad-11d1-80b4-00c04fd430c8` (the RFC 4122 URL namespace), as its 16 bytes in network order;
- name: the UTF-8 bytes of `"shorepop:ugs:" + playerId` (UGS player id exactly as issued, case preserved);
- `SHA1(namespace bytes + name bytes)`, first 16 bytes; `byte[6] = (byte[6] & 0x0F) | 0x50`; `byte[8] = (byte[8] & 0x3F) | 0x80`;
- written lowercase, hyphenated 8-4-4-4-12.
- Test vector: player `abc123` -> `0f752aa9-33c5-54b1-b936-68d3b28f2043` (identical to Python `uuid.uuid5(uuid.NAMESPACE_URL, "shorepop:ugs:abc123")`).

Implementation: `AppAccountTokens.cs`.

### Consumable reclaim (Apple)

A consumable validated by player A whose app died before its durable commit, then reinstalled as a new
anonymous player B, used to answer 409 forever (charged, never granted). Now, while no claimant has acked:

- the JWS carries a token: the player whose derivation equals it may take the transaction over at any time;
- any player (token or not, matching or not) may take it over once 24 h have passed since the last claim
  (reason `unacked_24h_mismatch` when the JWS carries a token that does not match, `unacked_24h` when it carries none).
  This covers the reinstall case: the new anonymous player's derived token can never match the old one.

A takeover removes the previous owner's claim (it drops out of their reconcile) and is logged in the
`transfers` table with the reason (`app_account_token`, `unacked_24h_mismatch` or `unacked_24h`). After an ack, the owner stays
single (409 for everyone else). **Clients must ack right after their durable commit and retry the ack until
it succeeds**: an owner that granted but never acked can lose the transaction to any player 24 h after the last claim.

### Entitlements (non-consumable, subscription)

A player who already claims the transaction (or any transaction under the same original transaction) is
always served. A new player is served when:

- the verified JWS carries no token, or its token equals the player's derivation, or
- **restore**: the JWS is for the same original transaction, Apple signed it (`signedDate`) within the last
  24 h, and it is newer than every JWS the verifier has stored for that original transaction. A StoreKit
  restore on the same Apple account yields such a JWS; a replay of a JWS someone already presented does not
  (409 `app_account_token_mismatch`).

Then the cap: at most 5 distinct players per original transaction; the 6th gets 409
`claimant_limit_reached` (logged). The token-matched player is never capped.

Every current claimant is recorded (`claims` table, first claimant first, with bind and ack times). The
transaction's store state is shared by all claimants: a refund seen through any claimant is sticky for all
of them. `SubscriptionEntitlementRevision` is kept per player and only goes up.

The type comes from Apple's signed `type` field (`Consumable`, `Non-Consumable`,
`Auto-Renewable Subscription`), which a client cannot alter without breaking the signature. It is never
looser than the catalog (`IapCatalog.All` in `UnityIapProvider.cs`, mirrored in `Contracts.cs`): a product
the catalog lists as consumable stays single-owner even if Apple's type says otherwise. Google responses
carry no type, so Google uses the catalog.

## Endpoints

Every `/v1` route requires `Authorization: Bearer <UGS player access token>`. The token is validated
exactly like the league server validates it: Unity JWKS, issuer `https://player-auth.services.api.unity.com`,
RS256, lifetime, one `sub`, and one exact `project_id` from `UGS_PROJECT_ID` or the optional
comma-separated `UGS_ADDITIONAL_PROJECT_IDS` allowlist. Wildcards, empty entries and invalid UUIDs
are refused. Additional IDs support a bounded project migration; they do not link player identities
between projects or transfer purchase ownership. The `sub` claim remains the player; because it is
not project-prefixed in the ledger, do not accept two projects unless their subject namespace
and intended ownership continuity have been independently established. The new Michael service
accepts only Michael's project, with no additional project IDs. JSON input
is case-insensitive. Output uses the client's PascalCase field names so Unity's `JsonUtility` can read it.

| Route | Body | 200 response |
|---|---|---|
| `POST /v1/purchases/validate` | `NativePurchaseProof` | one `ValidatedNativePurchase` |
| `POST /v1/purchases/ack` | `{"TransactionId":"..."}` (optional `Store`, default `AppleAppStore`; optional `ApplicationId`, must match that store: Apple `com.dizzywalk.shorepop`, Google `com.shorepop.game`) | `{"TransactionId":"...","Acked":true}`; idempotent, first ack time kept |
| `POST /v1/purchases/reconcile` | `{Store,ApplicationId}` using Apple `com.dizzywalk.shorepop` or Google `com.shorepop.game` | `ValidatedNativePurchase[]`, all of this player's known transactions (an empty array is an authoritative "none") |
| `POST /v1/purchases/revenue-proof` | `{Store,ApplicationId,ProductId,LedgerTransactionKey}` | Authenticated original claimant's unexpired attribution proof plus verified economic metadata; never a purchase grant or downstream tracking receipt. |
| `GET /healthz` | none | `{status, apple, appleServerApi, google, store, build, revenueProofRecovery}` |

Errors are returned as `{"Code":"..."}`:

| Status | Codes |
|---|---|
| 400 | `malformed_json`, `body_required` |
| 401 | `authentication_required` |
| 404 | `transaction_not_found` (ack for a transaction the verifier has not seen) |
| 409 | `transaction_owned_by_another_player` (consumable owned by another player; also ack by a non-claimant), `app_account_token_mismatch` (token-bound entitlement, no restore proof), `claimant_limit_reached` (6th player on one entitlement), `purchase_pending` (Google) |
| 422 | `unknown_store`, `application_mismatch`, `unknown_product`, `transaction_missing`, `apple_signed_transaction_required`, `apple_signature_invalid`, `bundle_mismatch`, `product_mismatch`, `transaction_mismatch`, `quantity_not_one`, `environment_not_allowed`, `type_mismatch`, `expiry_missing`, `purchase_not_found`, `line_items_invalid`, `purchase_state_unknown` |
| 429 | Rate limit: 20 requests per minute per player, 32 concurrent requests per server |
| 503 | `verifier_unconfigured`, `google_validation_not_configured`, `authentication_keys_unavailable`, `apple_server_api_unavailable`, `apple_server_response_unverifiable`, `storage_unavailable`, `stored_transaction_unverifiable` |

## What each store check proves

### Verified economic metadata

`ValidatedNativePurchase` additionally returns `RevenueVerified`, `GrossRevenueMicros`,
`RevenueCurrency`, `RevenueTransactionId`, `RevenuePurchasedAtUnixMs` and `RevenueRefunded`.
Apple values come from the signature-verified JWS (`price` milliunits multiplied by
1000). Google gross paid totals come from `orders.get`, bound to the verified order,
purchase token and product; net developer proceeds are not used. Missing or temporarily
unavailable economic data never substitutes a current catalog price and does not prevent
a valid grant. Later reconciliation can fill it. Revenue identity is the Apple transaction
or Play order ID, never a Google purchase token.

Google allows a full monetary refund without revoking the entitlement. `RevenueRefunded`
therefore suppresses positive tracking for that economic order independently of `Refunded`,
which remains the store entitlement state. A later paid subscription renewal has its own
economic identity. Unknown prices, sandbox purchases and refund metadata must never be
reported as positive production revenue.

### Attribution proof recovery

The recovery endpoint exists only with Postgres plus `VERIFIER_REVENUE_PROOF_KEY` configured.
It returns `{Store,ProductId,LedgerTransactionKey,Receipt,Signature}` and the economic fields
above. The ledger key is `tx-` plus lowercase SHA-256 of UTF-8 `store + "\n" + app + "\n" +
nativeTransaction`. The requesting authenticated player must still be the current first
claimant, match product/app/store, and own the original proof record. Other entitlement
claimants receive no proof. Fully refunded economic orders return their refund metadata
with empty proof, allowing the client to discard an unsent positive tracking event.

Only verified StoreKit 2 JWS or Google purchase JSON with a valid app licensing RSA signature
is retained. Google package/product/token are bound to the verified purchase before storage.
The recovery cache encrypts receipt/signature using AES-256-GCM and identity-bound associated
data. Recovery expires exactly 30 days after first retention of that economic order;
replay never extends it. A distinct verified subscription renewal order may start its own
window, replacing the token's prior proof only when the canonical order matches. A
startup/every-12-hour cleanup clears expired ciphertext and retains only the identity/expiry
tombstone. Keep the encryption key available across deployments; do not rotate it without
migrating live envelopes. Existing canonical ledger JWS/token retention used for store
reconciliation is separate from this encrypted cache.

Clients request recovery only when attribution collection is allowed. The endpoint itself
does not emit analytics or contact Tenjin. A client SDK handoff is not a provider receipt.

**Apple (StoreKit 2).** The service verifies `AppleSignedTransaction` (a JWS) as follows:
- The header must say `alg: ES256` and carry exactly three `x5c` certificates.
- The leaf must be signed by the intermediate, and the intermediate by a *configured* root. That root is
  the Apple Root CA - G3 embedded from `Resources/AppleRootCA-G3.cer` (SHA-256
  `63343ABF…3E9179`, byte-identical to the real root in Apple's own library tests). The presented
  `x5c[2]` is parsed but never trusted.
- Issuer names must chain, and every certificate must be valid at the time of the check.
- The intermediate must be a CA carrying OID `1.2.840.113635.100.6.2.1`. The leaf must not be a CA and
  must carry OID `1.2.840.113635.100.6.11.1`.
- The ES256 signature is checked with the leaf key.
- Payload checks:
  - `bundleId == com.shorepop.game`, and `productId` and `transactionId` equal the claim.
  - `quantity == 1`, and the type matches the product.
  - `environment` is in `APPLE_ALLOWED_ENVIRONMENTS` (default `Production,Sandbox`; Xcode is refused)
    and is returned as `Environment`.
  - `revocationDate` sets `Refunded` (a refund never clears once set).
  - `expiresDate` becomes `PurchasedPeriodExpiryUtcTicks` for `gg.shorepop.pass.monthly`.

OCSP revocation of Apple's signing certificates is **not** checked. Apple's library makes that check optional.

**Google Play.** Purchases are checked with `purchases.productsv2.getproductpurchasev2` or
`purchases.subscriptionsv2.get`, authenticated with the service account in `GOOGLE_PLAY_SA_JSON`.
- If `GOOGLE_PLAY_SA_JSON` is unset, every Google request answers **503
  `google_validation_not_configured`** (fail closed). Check current provider access rather than historical transfer notes.
- One-time products: `PURCHASED` is accepted, `CANCELLED` is returned with `Refunded=true`, and
  `PENDING` answers 409.
- Subscriptions: `expiryTime` becomes `PurchasedPeriodExpiryUtcTicks`. A revoked subscription appears as
  expired.
- A `testPurchase` or `testPurchaseContext` sets `Environment = "LicenseTest"`.
- The server does not acknowledge or consume purchases; the client's `ConfirmPurchase` does that.
- The interpreter has test response coverage. The 2026-10-10 live invalid-token probe reached
  Publisher validation with the configured service account; a real license-test purchase is still separate evidence.

**Subscription fields.** `CurrentSubscriptionExpiryUtcTicks` is the latest expiry among the player's
unrefunded pass transactions. `SubscriptionEntitlementRevision` goes up every time that
aggregate changes. PostgreSQL seeds a new row from the fixed server configuration
`VERIFIER_SUBSCRIPTION_REVISION_EPOCH` (at least 1); existing rows keep their persisted
revisions regardless of restart or configuration changes. The staged cutover epoch is
`1791672606408`, above the legacy SQLite counter range. Preserve it across deployments;
never lower the baseline for future rows. Both subscription fields come from the store.

## Reconcile and the App Store Server API

- **With** `APPLE_IAP_KEY_ID`, `APPLE_IAP_ISSUER_ID` and `APPLE_IAP_PRIVATE_KEY` (the `.p8` PEM text;
  literal `\n` is accepted), reconcile calls two App Store Server API endpoints for each stored transaction:
  - **Get Transaction Info** picks up refunds and revocations.
  - For the pass, **Get All Subscription Statuses** claims renewals that share the same
    `originalTransactionId` for the same player.

  Every response JWS goes through the same chain verification as client proofs.
- **Without** those keys, reconcile re-verifies each stored signed transaction (at its own `signedDate`)
  and returns the stored state. A refund made after the purchase stays invisible until the key is set.
  App Store Server Notifications V2 are not implemented yet.
- **Google** reconcile re-queries every stored token.

## Storage

`IPurchaseStore` has three implementations:

| Store | Setting | Notes |
|---|---|---|
| SQLite (default) | file at `VERIFIER_DB_PATH`, default `/home/app/data/verifier.db` | See the warning below. |
| In memory | `VERIFIER_STORE=memory` | Lost on every restart. |
| PostgreSQL | `VERIFIER_STORE=postgres`, `VERIFIER_DATABASE_URL` | Durable isolated `shorepop_verifier` schema; database transaction locks serialize claim, acknowledgment and subscription updates across processes. |

**Persistence limit on the free plan.** The service runs on Render's free plan, where the container disk
is ephemeral: the SQLite file is wiped on every deploy and every restart (including idle spin-downs). That
erases every transaction record and player claim. After a reset, a consumable transaction that was already
redeemed can be validated again, and its currency granted again, by a new player. Refunds seen earlier are
also forgotten until reconcile re-queries Apple or Google. Entitlements are not lost, because restore
re-presents the signed transaction.

**Before real revenue, move to one of these:**
- a paid Render instance with a persistent disk mounted at `/home/app/data` (the default `VERIFIER_DB_PATH`), or
- the Postgres store (`VERIFIER_DATABASE_URL`). The schema must be provisioned before startup;
  the scoped login creates its tables and indexes. It never falls back to SQLite on an error.

Postgres tests use `SHOREPOP_TEST_POSTGRES`, pointing to a separate test database.
The implementation accepts a Postgres URL or Npgsql connection string and defaults
to required TLS for URLs. Keep credentials out of logs and command arguments.

The database holds raw Apple JWS values and Google purchase tokens, because reconcile needs them to
re-query the stores. Logs never contain tokens, receipts, JWS values or raw transaction ids: they carry
only a 12-hex SHA-256 digest. No CORS middleware is registered.

## Environment

| Variable | Required | Meaning |
|---|---|---|
| `UGS_PROJECT_ID` | yes | UGS project id. `/v1` answers 503 until it is set. |
| `UGS_ADDITIONAL_PROJECT_IDS` | no | Up to eight exact additional project UUIDs, comma-separated, during a project migration. Does not map players or ownership. |
| `GOOGLE_PACKAGE_ID` | no | Must be `com.shorepop.game`, which is the default. `APP_BUNDLE_ID` remains an Android-only legacy fallback. |
| `APPLE_BUNDLE_ID` | no | Must be `com.dizzywalk.shorepop`, the new Michael-team app. No fallback to the old Apple bundle. |
| `APPLE_ALLOWED_ENVIRONMENTS` | no | Default `Production,Sandbox`. |
| `GOOGLE_PLAY_SA_JSON` | no | Service-account key JSON text. Unset means Google answers 503. |
| `APPLE_IAP_KEY_ID`, `APPLE_IAP_ISSUER_ID`, `APPLE_IAP_PRIVATE_KEY` | no | App Store Server API credentials. |
| `VERIFIER_STORE` | no | `sqlite` (default), `memory`, or `postgres`; unknown values fail startup. |
| `VERIFIER_DB_PATH` | no | SQLite file path. |
| `VERIFIER_DATABASE_URL` | when postgres | Dedicated PostgreSQL login connection URL/string. Never sent to clients. |
| `VERIFIER_SUBSCRIPTION_REVISION_EPOCH` | for SQLite cutover | Fixed positive initial PostgreSQL revision baseline; preserve across deployments. Existing rows retain their revisions. |
| `VERIFIER_REVENUE_PROOF_KEY` | for proof recovery | Base64-encoded 32-byte AES key, stable across deployments. |
| `GOOGLE_PLAY_LICENSE_KEY` | for Google proof retention | App licensing RSA public key from Play Console, used to verify original purchase signatures. |
| `PORT` | no | Set by Render. |

## Build and test

```
dotnet build server/verifier/Shorepop.Verifier.csproj -nologo -v q
dotnet run --project server/verifier/tests/Shorepop.Verifier.Tests.csproj -c Release   # exit 0 = all pass
```

The tests use two sets of vectors:
- Apple's MIT-licensed vectors from `app-store-server-library-python`, under
  `tests/Resources/apple-library`. They include Apple's test root, which is used only in tests.
- A locally generated chain with Apple's certificate shape.
