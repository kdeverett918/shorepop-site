using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Shorepop.Verifier;

/// <summary>Transport-only proof returned to its original, still-authorized player. Never log this value.</summary>
public sealed record RevenueProof(string Store, string ApplicationId, string ProductId, string LedgerKey,
    string Receipt, string Signature, DateTimeOffset ExpiresUtc)
{
    public override string ToString() => "RevenueProof { Receipt = [redacted], Signature = [redacted] }";
}

/// <summary>Proof-cache erasure only. Canonical settlement identities and state are retained.</summary>
public sealed record RevenueProofErasureResult(int ClearedProofCount, bool AlreadySuppressed, long SuppressedAtUnixMs);

/// <summary>Durable acceptance for remaining account-data cleanup, not confirmation of completed deletion.</summary>
public sealed record AccountDeletionRequestResult(string RequestId, string Status, long RequestedAtUnixMs,
    int ClearedProofCount, bool AlreadySuppressed, bool AlreadyRequested);

/// <summary>
/// An encrypted, short-lived recovery copy of a cryptographically checked native proof. Save is called
/// only after the binding verifier has validated the receipt and signature against a settled purchase.
/// Both save and retrieval independently check the current canonical ledger on the same database
/// transaction/connection, with the settlement lock held. Other entitlement claimants cannot retrieve it.
/// </summary>
public sealed class RevenueProofStore
{
    private const int MaximumProofBytes = 64 * 1024;
    private readonly string connectionString;
    private readonly byte[] key;
    private sealed record Payload(string Receipt, string Signature);
    private sealed record Identity(string Store, string App, string Transaction, string Player, string Product, string Ledger, string RevenueOrder);

    public RevenueProofStore(string databaseUrlOrConnectionString, byte[] encryptionKey32)
    {
        if (encryptionKey32 == null || encryptionKey32.Length != 32)
            throw new ArgumentException("Revenue proof encryption requires a 32-byte key.");
        key = encryptionKey32.ToArray();
        connectionString = PostgresPurchaseStore.NormalizeConnectionString(databaseUrlOrConnectionString);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        PostgresPurchaseStore.Lock(connection, tx, "schema", "v1");
        using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS shorepop_verifier.revenue_proofs(
              store TEXT NOT NULL, app_id TEXT NOT NULL, ledger_key TEXT NOT NULL, player_id TEXT NOT NULL, product_id TEXT NOT NULL,
              ciphertext BYTEA NOT NULL, nonce BYTEA NOT NULL CHECK(octet_length(nonce)=12),
              tag BYTEA NOT NULL CHECK(octet_length(tag)=16), created_ms BIGINT NOT NULL, expires_ms BIGINT NOT NULL,
              PRIMARY KEY(store,app_id,ledger_key), CHECK(octet_length(ciphertext)<=65536), CHECK(expires_ms>created_ms));
            CREATE INDEX IF NOT EXISTS revenue_proofs_expiry ON shorepop_verifier.revenue_proofs(expires_ms);
            ALTER TABLE shorepop_verifier.revenue_proofs ADD COLUMN IF NOT EXISTS revenue_order_id TEXT NOT NULL DEFAULT '';
            CREATE TABLE IF NOT EXISTS shorepop_verifier.revenue_proof_suppressions(
              store TEXT NOT NULL, app_id TEXT NOT NULL, player_id TEXT NOT NULL, suppressed_ms BIGINT NOT NULL,
              PRIMARY KEY(store,app_id,player_id));
            CREATE TABLE IF NOT EXISTS shorepop_verifier.privacy_deletion_requests(
              store TEXT NOT NULL, app_id TEXT NOT NULL, player_id TEXT NOT NULL, request_id UUID NOT NULL UNIQUE,
              analytics_install_id UUID NULL, requested_ms BIGINT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
              firebase_app_instance_id TEXT NULL,
              PRIMARY KEY(store,app_id,player_id));
            ALTER TABLE shorepop_verifier.privacy_deletion_requests ADD COLUMN IF NOT EXISTS firebase_app_instance_id TEXT NULL;
            """, connection, tx);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public static string LedgerKey(string store, string app, string transaction) =>
        "tx-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store + "\n" + app + "\n" + transaction))).ToLowerInvariant();

    public void Save(PurchaseRecord verifiedRecord, string playerId, string receipt, string signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(receipt)) throw new VerifierRefusal(422, "revenue_proof_missing");
        if (receipt.Length + (signature?.Length ?? 0) > MaximumProofBytes)
            throw new VerifierRefusal(422, "revenue_proof_too_large");
        string ledger = LedgerKey(verifiedRecord.Store, verifiedRecord.ApplicationId, verifiedRecord.TransactionId);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        PostgresPurchaseStore.Lock(connection, tx, verifiedRecord.Store, verifiedRecord.ApplicationId);
        var identity = Authorized(connection, tx, playerId, verifiedRecord.Store, verifiedRecord.ApplicationId, ledger, verifiedRecord.ProductId);
        if (identity == null) throw new VerifierRefusal(404, "revenue_proof_unavailable");
        // The caller checked the signature. Bind the signed receipt's order as well as the native
        // token: subscriptions can retain the token across multiple economic renewal orders.
        string receiptOrder = ReceiptOrder(identity, receipt);
        if (receiptOrder.Length == 0 || (identity.RevenueOrder.Length > 0 && receiptOrder != identity.RevenueOrder))
            throw new VerifierRefusal(404, "revenue_proof_unavailable");
        long created = now.ToUnixTimeMilliseconds(), expires = now.AddDays(30).ToUnixTimeMilliseconds();
        using (var existing = Command(connection, tx, """
            SELECT player_id,product_id,created_ms,expires_ms,revenue_order_id FROM shorepop_verifier.revenue_proofs
            WHERE store=@s AND app_id=@a AND ledger_key=@l
            """, identity))
        {
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != playerId || reader.GetString(1) != identity.Product)
                    throw new VerifierRefusal(404, "revenue_proof_unavailable");
                string previousOrder = reader.GetString(4);
                if (previousOrder == receiptOrder || previousOrder.Length == 0)
                {
                    created = reader.GetInt64(2); expires = reader.GetInt64(3);
                }
                else if (identity.RevenueOrder.Length == 0)
                    throw new VerifierRefusal(404, "revenue_proof_unavailable");
            }
        }
        if (expires <= now.ToUnixTimeMilliseconds())
        {
            ClearExpired(connection, tx, now);
            tx.Commit();
            return;
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new Payload(receipt, signature ?? ""));
        try
        {
            if (plaintext.Length > MaximumProofBytes) throw new VerifierRefusal(422, "revenue_proof_too_large");
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ciphertext = new byte[plaintext.Length];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(identity));
            using var command = Command(connection, tx, """
                INSERT INTO shorepop_verifier.revenue_proofs(store,app_id,ledger_key,player_id,product_id,ciphertext,nonce,tag,created_ms,expires_ms,revenue_order_id)
                VALUES(@s,@a,@l,@p,@prod,@cipher,@nonce,@tag,@created,@expires,@order)
                ON CONFLICT(store,app_id,ledger_key) DO UPDATE SET ciphertext=@cipher,nonce=@nonce,tag=@tag,
                  created_ms=@created,expires_ms=@expires,revenue_order_id=@order
                """, identity);
            command.Parameters.AddWithValue("cipher", ciphertext); command.Parameters.AddWithValue("nonce", nonce);
            command.Parameters.AddWithValue("tag", tag); command.Parameters.AddWithValue("created", created); command.Parameters.AddWithValue("expires", expires);
            command.Parameters.AddWithValue("order", receiptOrder);
            command.ExecuteNonQuery();
            ClearExpired(connection, tx, now);
            tx.Commit();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public RevenueProof? Get(string playerId, string store, string app, string ledgerKey, string productId, DateTimeOffset now)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        PostgresPurchaseStore.Lock(connection, tx, store, app);
        var identity = Authorized(connection, tx, playerId, store, app, ledgerKey, productId);
        if (identity == null || identity.RevenueOrder.Length == 0) return null;
        byte[] nonce, ciphertext, tag;
        long expires;
        using (var command = Command(connection, tx, """
            SELECT ciphertext,nonce,tag,expires_ms FROM shorepop_verifier.revenue_proofs
            WHERE store=@s AND app_id=@a AND ledger_key=@l AND player_id=@p AND product_id=@prod AND revenue_order_id=@order
            """, identity))
        {
            command.Parameters.AddWithValue("order", identity.RevenueOrder);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            ciphertext = reader.GetFieldValue<byte[]>(0); nonce = reader.GetFieldValue<byte[]>(1);
            tag = reader.GetFieldValue<byte[]>(2); expires = reader.GetInt64(3);
        }
        if (expires <= now.ToUnixTimeMilliseconds() || ciphertext.Length == 0)
        {
            ClearExpired(connection, tx, now);
            tx.Commit();
            return null;
        }
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(identity));
            var payload = JsonSerializer.Deserialize<Payload>(plaintext);
            if (payload == null || string.IsNullOrEmpty(payload.Receipt) || ReceiptOrder(identity, payload.Receipt) != identity.RevenueOrder) return null;
            tx.Commit();
            return new RevenueProof(store, app, productId, ledgerKey, payload.Receipt, payload.Signature, DateTimeOffset.FromUnixTimeMilliseconds(expires));
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            // Wrong keys, damaged ciphertext or a changed bound identity never return partial proof.
            throw new VerifierRefusal(503, "revenue_proof_unavailable");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>Remove expired ciphertext while retaining the first-save tombstone against replay renewal.</summary>
    public int PurgeExpired(DateTimeOffset now)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        int count = ClearExpired(connection, tx, now);
        tx.Commit();
        return count;
    }

    /// <summary>
    /// Permanently suppress proof recovery for this exact player and application, even when no proof
    /// exists yet. This erases only cached attribution proof bytes, never settlement claims, acknowledgments,
    /// refunds, subscription revisions or canonical store tokens/JWS. Authentication belongs to the caller.
    /// </summary>
    public RevenueProofErasureResult EraseForPlayer(string playerId, string store, string applicationId, DateTimeOffset now)
    {
        if (!VerifierOptions.ValidPlayer(playerId) || !ShorepopCatalog.MatchesApplication(store, applicationId))
            throw new VerifierRefusal(422, "revenue_proof_erasure_request_invalid");
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        PostgresPurchaseStore.Lock(connection, tx, store, applicationId);
        var result = EraseForPlayer(connection, tx, playerId, store, applicationId, now);
        tx.Commit();
        return result;
    }

    /// <summary>
    /// Accepts an authenticated player's deletion request durably and atomically suppresses cached proofs.
    /// Remaining provider cleanup stays pending for support. Repeats preserve the request identity and
    /// original analytics identifier; conflicting metadata is refused without changing the stored request.
    /// </summary>
    public AccountDeletionRequestResult RequestAccountDeletion(string playerId, string store, string applicationId,
        string? analyticsInstallId, DateTimeOffset now, string? firebaseAppInstanceId = null)
    {
        if (!VerifierOptions.ValidPlayer(playerId) || !ShorepopCatalog.MatchesApplication(store, applicationId))
            throw new VerifierRefusal(422, "privacy_deletion_request_invalid");
        Guid? analyticsId = null;
        if (analyticsInstallId != null)
        {
            if (!Guid.TryParseExact(analyticsInstallId, "D", out var parsed))
                throw new VerifierRefusal(422, "privacy_deletion_request_invalid");
            analyticsId = parsed;
        }
        if (firebaseAppInstanceId != null)
        {
            if (firebaseAppInstanceId.Length is < 32 or > 64 || !firebaseAppInstanceId.All(Uri.IsHexDigit))
                throw new VerifierRefusal(422, "privacy_deletion_request_invalid");
            firebaseAppInstanceId = firebaseAppInstanceId.ToLowerInvariant();
        }
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        PostgresPurchaseStore.Lock(connection, tx, store, applicationId);
        Guid requestId = Guid.NewGuid();
        long requestedAt = now.ToUnixTimeMilliseconds();
        bool alreadyRequested = false;
        using (var command = new NpgsqlCommand("""
            SELECT request_id,analytics_install_id,requested_ms,firebase_app_instance_id FROM shorepop_verifier.privacy_deletion_requests
            WHERE store=@s AND app_id=@a AND player_id=@p
            """, connection, tx))
        {
            command.Parameters.AddWithValue("s", store); command.Parameters.AddWithValue("a", applicationId);
            command.Parameters.AddWithValue("p", playerId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                Guid? originalAnalyticsId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                string? originalFirebaseId = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (originalAnalyticsId != analyticsId || originalFirebaseId != firebaseAppInstanceId)
                    throw new VerifierRefusal(422, "privacy_deletion_metadata_conflict");
                requestId = reader.GetGuid(0); requestedAt = reader.GetInt64(2); alreadyRequested = true;
            }
        }
        if (!alreadyRequested)
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO shorepop_verifier.privacy_deletion_requests
                  (store,app_id,player_id,request_id,analytics_install_id,requested_ms,status,firebase_app_instance_id)
                VALUES(@s,@a,@p,@id,@analytics,@now,'pending',@firebase)
                """, connection, tx);
            command.Parameters.AddWithValue("s", store); command.Parameters.AddWithValue("a", applicationId);
            command.Parameters.AddWithValue("p", playerId); command.Parameters.AddWithValue("id", requestId);
            command.Parameters.AddWithValue("analytics", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)analyticsId ?? DBNull.Value);
            command.Parameters.AddWithValue("firebase", NpgsqlTypes.NpgsqlDbType.Text, (object?)firebaseAppInstanceId ?? DBNull.Value);
            command.Parameters.AddWithValue("now", requestedAt);
            command.ExecuteNonQuery();
        }
        var erasure = EraseForPlayer(connection, tx, playerId, store, applicationId, now);
        tx.Commit();
        return new AccountDeletionRequestResult(requestId.ToString("D"), "accepted", requestedAt,
            erasure.ClearedProofCount, erasure.AlreadySuppressed, alreadyRequested);
    }

    private static RevenueProofErasureResult EraseForPlayer(NpgsqlConnection connection, NpgsqlTransaction tx,
        string playerId, string store, string applicationId, DateTimeOffset now)
    {
        bool alreadySuppressed;
        using (var command = new NpgsqlCommand("""
            INSERT INTO shorepop_verifier.revenue_proof_suppressions(store,app_id,player_id,suppressed_ms)
            VALUES(@s,@a,@p,@now) ON CONFLICT(store,app_id,player_id) DO NOTHING
            """, connection, tx))
        {
            command.Parameters.AddWithValue("s", store); command.Parameters.AddWithValue("a", applicationId);
            command.Parameters.AddWithValue("p", playerId); command.Parameters.AddWithValue("now", now.ToUnixTimeMilliseconds());
            alreadySuppressed = command.ExecuteNonQuery() == 0;
        }
        int cleared;
        using (var command = new NpgsqlCommand("""
            UPDATE shorepop_verifier.revenue_proofs SET ciphertext=''::bytea
            WHERE store=@s AND app_id=@a AND player_id=@p AND octet_length(ciphertext)>0
            """, connection, tx))
        {
            command.Parameters.AddWithValue("s", store); command.Parameters.AddWithValue("a", applicationId);
            command.Parameters.AddWithValue("p", playerId);
            cleared = command.ExecuteNonQuery();
        }
        long suppressedAt;
        using (var command = new NpgsqlCommand("""
            SELECT suppressed_ms FROM shorepop_verifier.revenue_proof_suppressions
            WHERE store=@s AND app_id=@a AND player_id=@p
            """, connection, tx))
        {
            command.Parameters.AddWithValue("s", store); command.Parameters.AddWithValue("a", applicationId);
            command.Parameters.AddWithValue("p", playerId);
            suppressedAt = (long)command.ExecuteScalar()!;
        }
        return new RevenueProofErasureResult(cleared, alreadySuppressed, suppressedAt);
    }

    private static Identity? Authorized(NpgsqlConnection connection, NpgsqlTransaction tx, string player, string store, string app, string ledger, string product)
    {
        if (string.IsNullOrEmpty(player) || !ShorepopCatalog.MatchesApplication(store, app) || !ShorepopCatalog.ProductIds.Contains(product) ||
            ledger.Length != 67 || !ledger.StartsWith("tx-", StringComparison.Ordinal)) return null;
        using (var suppression = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM shorepop_verifier.revenue_proof_suppressions
              WHERE store=@s AND app_id=@a AND player_id=@p)
            """, connection, tx))
        {
            suppression.Parameters.AddWithValue("s", store); suppression.Parameters.AddWithValue("a", app);
            suppression.Parameters.AddWithValue("p", player);
            if ((bool)suppression.ExecuteScalar()!) return null;
        }
        using var command = new NpgsqlCommand("""
            SELECT p.transaction_id,p.refunded,p.revenue_refunded,p.revenue_transaction_id,
              (SELECT first_claim.player_id FROM shorepop_verifier.claims first_claim
               WHERE first_claim.store=p.store AND first_claim.app_id=p.app_id AND first_claim.transaction_id=p.transaction_id
               ORDER BY first_claim.seq LIMIT 1)
            FROM shorepop_verifier.purchases p JOIN shorepop_verifier.claims c
              ON c.store=p.store AND c.app_id=p.app_id AND c.transaction_id=p.transaction_id
            WHERE p.store=@s AND p.app_id=@a AND p.product_id=@prod AND c.player_id=@p
            """, connection, tx);
        command.Parameters.AddWithValue("s", store); command.Parameters.AddWithValue("a", app);
        command.Parameters.AddWithValue("p", player); command.Parameters.AddWithValue("prod", product);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string transaction = reader.GetString(0);
            if (LedgerKey(store, app, transaction) == ledger && !reader.GetBoolean(1) && !reader.GetBoolean(2) &&
                !reader.IsDBNull(4) && reader.GetString(4) == player)
                return new Identity(store, app, transaction, player, product, ledger, reader.GetString(3));
        }
        return null;
    }

    private static byte[] Aad(Identity identity) => JsonSerializer.SerializeToUtf8Bytes(new[]
        { identity.Store, identity.App, identity.Transaction, identity.Player, identity.Product });

    private static string ReceiptOrder(Identity identity, string receipt)
    {
        if (identity.Store == ShorepopCatalog.AppleStore) return identity.Transaction;
        try
        {
            using var json = JsonDocument.Parse(receipt, new JsonDocumentOptions { MaxDepth = 8 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("orderId", out var order) && order.ValueKind == JsonValueKind.String
                ? order.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction tx, string text, Identity identity)
    {
        var command = new NpgsqlCommand(text, connection, tx);
        command.Parameters.AddWithValue("s", identity.Store); command.Parameters.AddWithValue("a", identity.App);
        command.Parameters.AddWithValue("l", identity.Ledger); command.Parameters.AddWithValue("p", identity.Player);
        command.Parameters.AddWithValue("prod", identity.Product);
        return command;
    }

    private static int ClearExpired(NpgsqlConnection connection, NpgsqlTransaction tx, DateTimeOffset now)
    {
        using var command = new NpgsqlCommand("UPDATE shorepop_verifier.revenue_proofs SET ciphertext=''::bytea WHERE expires_ms<=@now AND octet_length(ciphertext)>0", connection, tx);
        command.Parameters.AddWithValue("now", now.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }
}
