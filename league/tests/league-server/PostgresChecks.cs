using System.Globalization;
using System.Text.Json;
using BloomHarbor;
using Npgsql;
using Shorepop.League;

namespace LeagueServerChecks;

internal static partial class Program
{
    const string VerifierDatabase = "shorepop_verifier_test";
    const string ProductionProject = "aea5f7f0-ebcb-4745-98c5-378bb2ad2feb";
    static readonly string[] PlayerTables = ["attempts", "level_bests", "weekly_levels", "weekly_totals"];
    static int postgresTransientRetries;

    static async Task<bool> PostgresChecks(CampaignWin fixture, CampaignWin other)
    {
        if (Environment.GetEnvironmentVariable("SHOREPOP_LEAGUE_TEST_POSTGRES") != "1")
        {
            Console.WriteLine("SKIP PostgreSQL: set SHOREPOP_LEAGUE_TEST_POSTGRES=1 and LeagueTestDatabaseUrl to the dedicated verifier DB.");
            return false;
        }
        string url = Environment.GetEnvironmentVariable("LeagueTestDatabaseUrl") ?? "";
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("LeagueTestDatabaseUrl is required for PostgreSQL checks.");
        var cfg = new LeagueOptions {
            ProjectId = ProductionProject, DatabaseProvider = "postgres", DatabaseUrl = url,
            RepeatPolicy = "BEST_PER_LEVEL_PER_WEEK", BoosterPolicy = "UNBOOSTED_ONLY",
            OwnerDecisionReference = "LOCAL VERIFIER TEST ONLY"
        };
        Test("PostgreSQL verifier refuses a non-test database before connecting", () =>
        {
            var wrongTarget = new NpgsqlConnectionStringBuilder(LeagueDatabase.NormalizeConnectionString(url)) {
                Database = "shorepop_production_negative_control"
            };
            bool refused = false;
            try { using var unexpected = PgConnect(new LeagueOptions { DatabaseUrl = wrongTarget.ConnectionString }); }
            catch (InvalidOperationException error) { refused = error.Message == "PostgreSQL checks require the dedicated shorepop_verifier_test database."; }
            Check(refused);
        });
        // Establish the database identity before Initialize performs even schema/configuration writes.
        // Never accept a production database URL, regardless of the opt-in flag.
        using (var guard = PgConnect(cfg))
        {
            using var schema = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='shorepop_league')", guard);
            Check((bool)schema.ExecuteScalar()!);
        }
        var db = New(cfg);
        postgresTransientRetries = 0;
        string prefix = "verifyLeague" + Guid.NewGuid().ToString("N");
        int firstScenario = scenarios, firstAssertion = assertions;
        bool passed = false, cleaned = false;
        try
        {
            await TestAsync("PostgreSQL readiness and pinned policy", async () =>
            {
                await db.CheckReadinessAsync(default);
                foreach (Action<LeagueOptions> mismatch in new Action<LeagueOptions>[] {
                    x => x.ProjectId = Project, x => x.RepeatPolicy = "FIRST_CLEAR_PER_LEVEL_EVER",
                    x => x.BoosterPolicy = LeagueOptions.TailwindPolicy
                })
                {
                    var wrong = new LeagueOptions {
                        ProjectId = cfg.ProjectId, DatabaseProvider = cfg.DatabaseProvider, DatabaseUrl = cfg.DatabaseUrl,
                        RepeatPolicy = cfg.RepeatPolicy, BoosterPolicy = cfg.BoosterPolicy, OwnerDecisionReference = cfg.OwnerDecisionReference
                    };
                    mismatch(wrong); RefuseCode(503, "database_policy_mismatch", () => New(wrong));
                }
                await db.CheckReadinessAsync(default);
                Check(PgCountPrefix(cfg, "attempts", prefix) == 0);
            });
            Test("PostgreSQL lost-response restart returns the original receipt in a later week", () =>
            {
                string player = prefix + "Restart";
                var first = db.Submit(player, fixture, Now);
                Check(!first.Duplicate && first.WeeklyAdded == fixture.Score && first.WeeklyScore == fixture.Score);
                NpgsqlConnection.ClearAllPools();
                var restarted = New(cfg);
                var retry = restarted.Submit(player, fixture, Now.AddDays(9));
                Check(retry.Duplicate && retry with { Duplicate = false } == first);
                Check(restarted.Weekly(player, 7, Now.AddDays(9)).WeeklyScore == 0);
                foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 1);
                RefuseCode(409, "attempt_payload_conflict", () => restarted.Submit(player, fixture with { Score = fixture.Score + 1 }, Now));
                Check(restarted.Weekly(player, 7, Now).WeeklyScore == first.WeeklyScore);
                Check(PgCountPlayer(cfg, "attempts", player) == 1);
            });
            await TestAsync("PostgreSQL 20 simultaneous identical submissions settle once", async () =>
            {
                string player = prefix + "Duplicate";
                int retriesBefore = postgresTransientRetries;
                var results = await ConcurrentPostgres(cfg, player, Enumerable.Repeat(fixture, 20).ToArray());
                int originalResponses = results.Count(x => !x.Duplicate);
                Check(results.Length == 20 && (originalResponses == 1 || originalResponses == 0 && postgresTransientRetries > retriesBefore));
                var original = results[0] with { Duplicate = false };
                Check(results.All(x => x with { Duplicate = false } == original));
                foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 1);
                Check(db.Weekly(player, 7, Now).WeeklyScore == fixture.Score);
            });
            await TestAsync("PostgreSQL 20 simultaneous distinct attempts preserve per-level atomic totals", async () =>
            {
                string player = prefix + "Distinct";
                var wins = Enumerable.Range(0, 20).Select(i => Fresh(i % 2 == 0 ? fixture : other)).ToArray();
                var results = await ConcurrentPostgres(cfg, player, wins);
                Check(results.Length == 20 && results.All(x => x.Status == "accepted"));
                Check(results.Sum(x => x.WeeklyAdded) == (long)fixture.Score + other.Score);
                Check(results.Count(x => x.WeeklyAdded > 0) == 2);
                Check(PgCountPlayer(cfg, "attempts", player) == 20);
                Check(PgCountPlayer(cfg, "level_bests", player) == 2 && PgCountPlayer(cfg, "weekly_levels", player) == 2);
                Check(PgCountPlayer(cfg, "weekly_totals", player) == 1);
                Check(db.Weekly(player, 7, Now).WeeklyScore == (long)fixture.Score + other.Score);
            });
            Test("PostgreSQL receipt insertion fault rolls back every score table", () => PostgresRollback(cfg, db, prefix, fixture));
            Test("PostgreSQL L5 current and legacy board receipts accept exact replay and refuse invalid claims", () =>
            {
                var definition = LevelCatalog.Get(LevelCatalog.RocketPracticeLevel);
                Check(definition.HasAuthoredSpecials);
                var current = Win(definition.Id);
                var legacy = Win(definition.Id, board: definition.WithoutAuthoredSpecials(),
                    first: new BoardPosition(LevelCatalog.RocketPracticeX, LevelCatalog.RocketPracticeY));
                foreach (var item in new[] { (Suffix: "Current", Win: current), (Suffix: "Legacy", Win: legacy) })
                {
                    string player = prefix + item.Suffix;
                    var accepted = db.Submit(player, item.Win, Now);
                    Check(!accepted.Duplicate && accepted.WeeklyScore == item.Win.Score);
                    var retry = new LeagueRepository(cfg).Submit(player, item.Win, Now.AddDays(7));
                    Check(retry.Duplicate && retry with { Duplicate = false } == accepted);
                    Refuse(422, () => db.Submit(player, Fresh(item.Win) with { Score = item.Win.Score + 10 }, Now));
                    Refuse(422, () => db.Submit(player, Fresh(item.Win) with {
                        Attempt = item.Win.Attempt! with { Id = Guid.NewGuid().ToString("N"), EvidenceComplete = false }
                    }, Now));
                    Check(PgCountPlayer(cfg, "attempts", player) == 1);
                    Check(db.Weekly(player, 7, Now).WeeklyScore == item.Win.Score);
                }
            });
            await TestAsync("PostgreSQL indexed records and standings remain complete beyond 100 players", async () =>
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, 130), new ParallelOptions { MaxDegreeOfParallelism = 4 }, (i, _) =>
                {
                    Check(!new LeagueRepository(cfg).Submit(prefix + "Indexed" + i.ToString("D3"), fixture, Now).Duplicate);
                    return ValueTask.CompletedTask;
                });
                string player = prefix + "Indexed129";
                var standing = db.Weekly(player, 7, Now);
                Check(standing.WeeklyScore == fixture.Score && standing.Rank > 100);
                Check(standing.Neighbours.Count == 7 && standing.Neighbours.Any(x => x.IsPlayer && x.Rank == standing.Rank));
                var best = db.Best(fixture.Attempt!.StableSlot, ReplayValidator.RulesVersion, LeagueOptions.Category);
                Check(best.Known && best.Score >= fixture.Score);
                Check(PgCountPrefix(cfg, "attempts", prefix + "Indexed") == 130);
                using var connection = PgConnect(cfg);
                using var transaction = connection.BeginTransaction();
                using (var tune = new NpgsqlCommand("SET LOCAL enable_seqscan=off", connection, transaction)) tune.ExecuteNonQuery();
                Check(PgPlan(connection, transaction, "SELECT player,score FROM shorepop_league.level_bests WHERE rules=@rules AND stable_slot=@slot AND category=@category ORDER BY score DESC,player LIMIT 1", fixture.Attempt.StableSlot).Contains("level_top", StringComparison.Ordinal));
                Check(PgPlan(connection, transaction, "SELECT player,score FROM shorepop_league.weekly_totals WHERE week=@week AND rules=@rules AND category=@category AND score>0 ORDER BY score DESC,player LIMIT 7 OFFSET 100", fixture.Attempt.StableSlot).Contains("weekly_rank", StringComparison.Ordinal));
                transaction.Rollback();
            });
            passed = true;
        }
        finally
        {
            using var connection = PgConnect(cfg);
            using var transaction = connection.BeginTransaction();
            foreach (string table in PlayerTables)
            {
                using var command = new NpgsqlCommand("DELETE FROM shorepop_league." + table + " WHERE player LIKE @prefix", connection, transaction);
                command.Parameters.AddWithValue("prefix", prefix + "%"); command.ExecuteNonQuery();
            }
            transaction.Commit();
            cleaned = PlayerTables.All(table => PgCountPrefix(cfg, table, prefix) == 0);
            Check(cleaned);
            File.WriteAllText(Path.Combine(root, "postgres.json"), JsonSerializer.Serialize(new {
                passed, database = VerifierDatabase, schema = "shorepop_league", projectId = ProductionProject,
                scenarios = scenarios - firstScenario, assertions = assertions - firstAssertion,
                cleanup = cleaned, transientStorageRetries = postgresTransientRetries,
                concurrency = "20 simultaneous durable attempts; transient pool/storage failures retried with unchanged evidence within 90 seconds",
                fixtureScope = "Unique test player prefix; no production database writes",
                authentication = "Repository tests only; no deployed bearer acceptance claimed"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed;
    }

    static async Task<Acceptance[]> ConcurrentPostgres(LeagueOptions cfg, string player, CampaignWin[] wins)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan deadline = TimeSpan.FromSeconds(90);
        int retriesBefore = postgresTransientRetries;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = wins.Select(async win => {
            await start.Task;
            while (true)
            {
                try
                {
                    var receipt = await Task.Run(() => new LeagueRepository(cfg).Submit(player, win, Now));
                    Check(timer.Elapsed < deadline);
                    return receipt;
                }
                catch (Exception error) when (timer.Elapsed < deadline &&
                    (error is TimeoutException || error is NpgsqlException { IsTransient: true } ||
                     error is NpgsqlException { InnerException: TimeoutException }))
                {
                    // This is the same durable evidence on every retry, matching the client outbox
                    // contract when bounded provider capacity responds with storage_unavailable.
                    Interlocked.Increment(ref postgresTransientRetries);
                    await Task.Delay(250);
                }
            }
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        Console.WriteLine($"INFO PostgreSQL {wins.Length} simultaneous attempts: {postgresTransientRetries - retriesBefore} transient storage retries, {timer.Elapsed.TotalSeconds:F1}s");
        return results;
    }

    static void PostgresRollback(LeagueOptions cfg, LeagueRepository db, string prefix, CampaignWin fixture)
    {
        string player = prefix + "Rollback", suffix = Guid.NewGuid().ToString("N");
        string trigger = "verifier_receipt_" + suffix, function = "verifier_fault_" + suffix;
        using var connection = PgConnect(cfg);
        try
        {
            using (var command = new NpgsqlCommand($"""
                CREATE FUNCTION shorepop_league.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.player = '{player}' THEN RAISE EXCEPTION 'verifier receipt insertion fault' USING ERRCODE = 'P0001'; END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER {trigger} BEFORE INSERT ON shorepop_league.attempts FOR EACH ROW EXECUTE FUNCTION shorepop_league.{function}();
                """, connection)) command.ExecuteNonQuery();
            // Confirm the trigger is confined to this run's one test subject.
            Check(db.Submit(prefix + "Unaffected", fixture, Now).WeeklyAdded == fixture.Score);
            bool refused = false;
            try { db.Submit(player, fixture, Now); }
            catch (PostgresException error) when (error.SqlState == "P0001") { refused = true; }
            Check(refused);
            foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 0);
        }
        finally
        {
            using var cleanup = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {trigger} ON shorepop_league.attempts; DROP FUNCTION IF EXISTS shorepop_league.{function}();", connection);
            cleanup.ExecuteNonQuery();
        }
        var recovered = db.Submit(player, fixture, Now);
        Check(!recovered.Duplicate && recovered.WeeklyScore == fixture.Score);
        foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 1);
    }

    static NpgsqlConnection PgConnect(LeagueOptions cfg)
    {
        var parsed = new NpgsqlConnectionStringBuilder(LeagueDatabase.NormalizeConnectionString(cfg.DatabaseUrl));
        if (parsed.Database != VerifierDatabase) throw new InvalidOperationException("PostgreSQL checks require the dedicated shorepop_verifier_test database.");
        var connection = new NpgsqlConnection(parsed.ConnectionString);
        try
        {
            connection.Open();
            using var command = new NpgsqlCommand("SELECT current_database()", connection);
            if (connection.Database != VerifierDatabase || (string?)command.ExecuteScalar() != VerifierDatabase)
                throw new InvalidOperationException("Connected PostgreSQL database is not shorepop_verifier_test.");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    static long PgCountPlayer(LeagueOptions cfg, string table, string player) => PgCount(cfg, table, "player=@value", player);
    static long PgCountPrefix(LeagueOptions cfg, string table, string prefix) => PgCount(cfg, table, "player LIKE @value", prefix + "%");
    static long PgCount(LeagueOptions cfg, string table, string filter, string value)
    {
        if (!PlayerTables.Contains(table)) throw new ArgumentException("Unexpected test table.");
        using var connection = PgConnect(cfg);
        using var command = new NpgsqlCommand("SELECT COUNT(*) FROM shorepop_league." + table + " WHERE " + filter, connection);
        command.Parameters.AddWithValue("value", value);
        return (long)command.ExecuteScalar()!;
    }
    static string PgPlan(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, int slot)
    {
        using var command = new NpgsqlCommand("EXPLAIN " + sql, connection, transaction);
        command.Parameters.AddWithValue("rules", ReplayValidator.RulesVersion);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("category", LeagueOptions.Category);
        command.Parameters.AddWithValue("week", LeagueRepository.WeekStart(Now).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var lines = new List<string>(); while (reader.Read()) lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }
    static void RefuseCode(int status, string code, Action action)
    {
        try { action(); }
        catch (LeagueRefusal refusal) { Check(refusal.Status == status && refusal.Code == code); return; }
        throw new Exception("Expected refusal " + status + " " + code);
    }
}
