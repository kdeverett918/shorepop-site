# Shorepop purchase verifier

An ASP.NET 8 service (same stack as `server/league`) that backs the client's `INativePurchaseValidator`
contract in `Assets/Scripts/Services/Commerce/NativePurchaseValidation.cs`. The client side is
`Assets/Scripts/Services/Commerce/HttpNativePurchaseValidator.cs`, which posts the proof with the player's
UGS access token and reads the responses below. It is deployed on Render as `shorepop-verifier`
(`https://shorepop-verifier.onrender.com`, from the `shorepop-site` repo, `verifier/`).

## Who a transaction belongs to

| Product type | Products | Rule |
|---|---|---|
| Consumable | `gg.shorepop.gems.*`, `gg.shorepop.coins.*`, `lives.refill`, `abilities.bundle`, `welcome`, `shellbank` | **Single owner.** The first player to validate owns it; others get 409 `transaction_owned_by_another_player`. Apple only: while the owner has **not acked** (`POST /v1/purchases/ack`), ownership may move (see below). Once acked it never moves. Google consumables never move. |
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

- the JWS carries a token: the player whose derivation equals it may take the transaction over, at any time;
  nobody else ever may (after 24 h included);
- the JWS carries no token: any player may take it over once 24 h have passed since the current owner bound it.

A takeover removes the previous owner's claim (it drops out of their reconcile) and is logged in the
`transfers` table with the reason (`app_account_token` or `unacked_24h`). After an ack, the owner stays
single (409 for everyone else). **Clients must ack right after their durable commit and retry the ack until
it succeeds**: an owner that granted but never acked can lose the transaction to a no-token reclaim after 24 h.

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
RS256, lifetime, one `sub`, and `project_id == UGS_PROJECT_ID`. The `sub` claim is the player. JSON input
is case-insensitive. Output uses the client's PascalCase field names so Unity's `JsonUtility` can read it.

| Route | Body | 200 response |
|---|---|---|
| `POST /v1/purchases/validate` | `NativePurchaseProof` | one `ValidatedNativePurchase` |
| `POST /v1/purchases/ack` | `{"TransactionId":"..."}` (optional `Store`, default `AppleAppStore`; optional `ApplicationId`, must be `com.shorepop.game`) | `{"TransactionId":"...","Acked":true}`; idempotent, first ack time kept |
| `POST /v1/purchases/reconcile` | `{"Store":"AppleAppStore"\|"GooglePlay","ApplicationId":"com.shorepop.game"}` | `ValidatedNativePurchase[]`, all of this player's known transactions (an empty array is an authoritative "none") |
| `GET /healthz` | none | `{status, apple, appleServerApi, google, store}` |

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
  `google_validation_not_configured`** (fail closed). Play stays blocked until the app transfer completes.
- One-time products: `PURCHASED` is accepted, `CANCELLED` is returned with `Refunded=true`, and
  `PENDING` answers 409.
- Subscriptions: `expiryTime` becomes `PurchasedPeriodExpiryUtcTicks`. A revoked subscription appears as
  expired.
- A `testPurchase` or `testPurchaseContext` sets `Environment = "LicenseTest"`.
- The server does not acknowledge or consume purchases; the client's `ConfirmPurchase` does that.
- The Google path has been tested against fake API responses only, never against the live API.

**Subscription fields.** `CurrentSubscriptionExpiryUtcTicks` is the latest expiry among the player's
unrefunded pass transactions. `SubscriptionEntitlementRevision` starts at 1 and goes up every time that
aggregate changes. Both come from the store.

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

`IPurchaseStore` has two implementations:

| Store | Setting | Notes |
|---|---|---|
| SQLite (default) | file at `VERIFIER_DB_PATH`, default `/home/app/data/verifier.db` | See the warning below. |
| In memory | `VERIFIER_STORE=memory` | Lost on every restart. |

**Persistence limit on the free plan.** The service runs on Render's free plan, where the container disk
is ephemeral: the SQLite file is wiped on every deploy and every restart (including idle spin-downs). That
erases every transaction record and player claim. After a reset, a consumable transaction that was already
redeemed can be validated again, and its currency granted again, by a new player. Refunds seen earlier are
also forgotten until reconcile re-queries Apple or Google. Entitlements are not lost, because restore
re-presents the signed transaction.

**Before real revenue, move to one of these:**
- a paid Render instance with a persistent disk mounted at `/home/app/data` (the default `VERIFIER_DB_PATH`), or
- a Postgres store (`DATABASE_URL`). `IPurchaseStore` is ready for it, but that store is not implemented yet.

The database holds raw Apple JWS values and Google purchase tokens, because reconcile needs them to
re-query the stores. Logs never contain tokens, receipts, JWS values or raw transaction ids: they carry
only a 12-hex SHA-256 digest. No CORS middleware is registered.

## Environment

| Variable | Required | Meaning |
|---|---|---|
| `UGS_PROJECT_ID` | yes | UGS project id. `/v1` answers 503 until it is set. |
| `APP_BUNDLE_ID` | no | Must be `com.shorepop.game`, which is the default. |
| `APPLE_ALLOWED_ENVIRONMENTS` | no | Default `Production,Sandbox`. |
| `GOOGLE_PLAY_SA_JSON` | no | Service-account key JSON text. Unset means Google answers 503. |
| `APPLE_IAP_KEY_ID`, `APPLE_IAP_ISSUER_ID`, `APPLE_IAP_PRIVATE_KEY` | no | App Store Server API credentials. |
| `VERIFIER_STORE` | no | `sqlite` (default) or `memory`. |
| `VERIFIER_DB_PATH` | no | SQLite file path. |
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
