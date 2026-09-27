# Shorepop purchase verifier

An ASP.NET 8 service (same stack as `server/league`) that backs the client's `INativePurchaseValidator`
contract in `Assets/Scripts/Services/Commerce/NativePurchaseValidation.cs`. It has not been reviewed by
the money-review lane, and no client `INativePurchaseValidator` HTTP implementation exists yet:
`NativePurchaseConfiguration.Validator` is still unassigned, so checkout stays disabled.

## Endpoints

Every `/v1` route requires `Authorization: Bearer <UGS player access token>`. The token is validated
exactly like the league server validates it: Unity JWKS, issuer `https://player-auth.services.api.unity.com`,
RS256, lifetime, one `sub`, and `project_id == UGS_PROJECT_ID`. The `sub` claim is the player. JSON input
is case-insensitive. Output uses the client's PascalCase field names so Unity's `JsonUtility` can read it.

| Route | Body | 200 response |
|---|---|---|
| `POST /v1/purchases/validate` | `NativePurchaseProof` | one `ValidatedNativePurchase` |
| `POST /v1/purchases/reconcile` | `{"Store":"AppleAppStore"\|"GooglePlay","ApplicationId":"com.shorepop.game"}` | `ValidatedNativePurchase[]`, all of this player's known transactions (an empty array is an authoritative "none") |
| `GET /healthz` | none | `{status, apple, appleServerApi, google, store}` |

Errors are returned as `{"Code":"..."}`:

| Status | Codes |
|---|---|
| 400 | `malformed_json`, `body_required` |
| 401 | `authentication_required` |
| 409 | `transaction_owned_by_another_player` (the first player to validate a transaction owns it), `purchase_pending` (Google) |
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
  - For the pass, **Get All Subscription Statuses** binds renewals that share the same
    `originalTransactionId` to the same player.

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

**On Render free the container disk is ephemeral.** Transaction-to-player bindings reset on every deploy
or restart. After a reset, a replayed transaction can be bound to a new player. Before revenue depends on
replay protection, attach a persistent disk (paid plan) or add a Postgres store (`DATABASE_URL`). The
interface is ready for it; that store is not implemented.

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
