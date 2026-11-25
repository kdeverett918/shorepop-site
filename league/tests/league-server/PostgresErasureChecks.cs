using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shorepop.League;

namespace LeagueServerChecks;

internal static partial class Program
{
    static async Task PostgresErasureChecks(LeagueOptions cfg, LeagueRepository db, string prefix,
        CampaignWin fixture, CampaignWin other, List<string> erasurePlayers)
    {
        Test("PostgreSQL case-bound erasure deletes all player rows and permanently refuses durable retries", () =>
        {
            string player = prefix + "EraseCaseA", otherPlayer = prefix + "EraseCasea";
            erasurePlayers.Add(player);
            string caseReference = Guid.NewGuid().ToString("D");
            db.Submit(player, fixture, Now);
            db.Submit(player, other, Now);
            db.Submit(otherPlayer, fixture, Now);
            var before = db.InspectErasure(player);
            Check(!before.Suppressed && before.Attempts == 2 && before.LevelBests == 2 &&
                before.WeeklyLevels == 2 && before.WeeklyTotals == 1);
            var receipt = db.ErasePlayer(player, caseReference, Now);
            Check(!receipt.AlreadyErased && receipt.Attempts == 2 && receipt.LevelBests == 2 &&
                receipt.WeeklyLevels == 2 && receipt.WeeklyTotals == 1);
            AssertPostgresErased(cfg, db, player, fixture);
            foreach (string repeatedCase in new[] { caseReference, Guid.NewGuid().ToString("D") })
            {
                var repeat = new LeagueRepository(cfg).ErasePlayer(player, repeatedCase, Now.AddDays(7));
                Check(repeat.AlreadyErased && repeat.Attempts == 0 && repeat.LevelBests == 0 &&
                    repeat.WeeklyLevels == 0 && repeat.WeeklyTotals == 0);
            }
            // The same player spelling with a different case is a different UGS subject.
            foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, otherPlayer) == 1);
            Check(PgTombstoneCount(cfg, otherPlayer) == 0 && !db.InspectErasure(otherPlayer).Suppressed);
            Check(db.Submit(otherPlayer, fixture, Now).Duplicate);
            Check(db.Weekly(otherPlayer, 7, Now).WeeklyScore == fixture.Score);
            using var connection = PgConnect(cfg);
            using var command = new NpgsqlCommand("SELECT subject_hash,case_reference,erased_utc FROM shorepop_league.erased_subjects WHERE subject_hash=@hash", connection);
            string expectedHash = PostgresErasureHash(cfg.ProjectId, player);
            command.Parameters.AddWithValue("hash", expectedHash);
            using var reader = command.ExecuteReader();
            Check(reader.Read() && reader.GetString(0) == expectedHash && reader.GetString(1) == caseReference);
            Check(reader.GetString(0) != player && reader.GetString(1) != player && reader.GetString(2) != player);
            Check(!reader.Read());
        });
        await TestAsync("PostgreSQL simultaneous submit and erasure leave no player rows and refuse future attempts", async () =>
        {
            string player = prefix + "EraseRace";
            erasurePlayers.Add(player);
            db.Submit(player, fixture, Now);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string caseReference = Guid.NewGuid().ToString("D");
            var submissions = Enumerable.Range(0, 10).Select(_ => Fresh(fixture)).Select(async win =>
            {
                await start.Task;
                try
                {
                    var receipt = await ErasureOperation(() => new LeagueRepository(cfg).Submit(player, win, Now), timer);
                    Check(receipt.Status == "accepted");
                    return true;
                }
                catch (LeagueRefusal refusal) when (refusal.Status == 422 && refusal.Code == "player_erased")
                {
                    Check(true);
                    return false;
                }
            }).ToArray();
            var erasure = Task.Run(async () =>
            {
                await start.Task;
                return await ErasureOperation(() => new LeagueRepository(cfg).ErasePlayer(player, caseReference, Now), timer);
            });
            start.SetResult();
            await Task.WhenAll(submissions.Cast<Task>().Append(erasure));
            Check(submissions.Length == 10 && submissions.All(task => task.IsCompletedSuccessfully));
            Check(timer.Elapsed < TimeSpan.FromSeconds(90));
            AssertPostgresErased(cfg, db, player, fixture);
            var repeat = db.ErasePlayer(player, Guid.NewGuid().ToString("D"), Now);
            Check(repeat.AlreadyErased && repeat.Attempts == 0 && repeat.LevelBests == 0 && repeat.WeeklyLevels == 0 && repeat.WeeklyTotals == 0);
            Console.WriteLine($"INFO PostgreSQL erasure race: {submissions.Count(task => task.Result)} accepted before erasure, {submissions.Count(task => !task.Result)} refused, {timer.Elapsed.TotalSeconds:F1}s");
        });
        Test("PostgreSQL erasure fault rolls back the tombstone and every deleted score row", () =>
        {
            string player = prefix + "EraseRollback", suffix = Guid.NewGuid().ToString("N");
            erasurePlayers.Add(player);
            db.Submit(player, fixture, Now);
            string trigger = "verifier_erase_" + suffix, function = "verifier_erase_fault_" + suffix;
            using var connection = PgConnect(cfg);
            try
            {
                using (var command = new NpgsqlCommand($"""
                    CREATE FUNCTION shorepop_league.{function}() RETURNS trigger LANGUAGE plpgsql AS $body$
                    BEGIN
                        IF OLD.player = '{player}' THEN RAISE EXCEPTION 'verifier erasure fault' USING ERRCODE = 'P0001'; END IF;
                        RETURN OLD;
                    END;
                    $body$;
                    CREATE TRIGGER {trigger} BEFORE DELETE ON shorepop_league.weekly_totals FOR EACH ROW EXECUTE FUNCTION shorepop_league.{function}();
                    """, connection)) command.ExecuteNonQuery();
                bool refused = false;
                try { db.ErasePlayer(player, Guid.NewGuid().ToString("D"), Now); }
                catch (PostgresException error) when (error.SqlState == "P0001") { refused = true; }
                Check(refused);
                foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 1);
                Check(PgTombstoneCount(cfg, player) == 0);
                var state = db.InspectErasure(player);
                Check(!state.Suppressed && state.Attempts == 1 && state.LevelBests == 1 && state.WeeklyLevels == 1 && state.WeeklyTotals == 1);
                Check(db.Submit(player, fixture, Now).Duplicate && db.Weekly(player, 7, Now).WeeklyScore == fixture.Score);
            }
            finally
            {
                using var cleanup = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {trigger} ON shorepop_league.weekly_totals; DROP FUNCTION IF EXISTS shorepop_league.{function}();", connection);
                cleanup.ExecuteNonQuery();
            }
            var recovered = db.ErasePlayer(player, Guid.NewGuid().ToString("D"), Now);
            Check(!recovered.AlreadyErased && recovered.Attempts == 1 && recovered.LevelBests == 1 && recovered.WeeklyLevels == 1 && recovered.WeeklyTotals == 1);
            AssertPostgresErased(cfg, db, player, fixture);
        });
        await TestAsync("PostgreSQL operator process validates project and case scope and erases only its exact subject", async () =>
        {
            string player = prefix + "EraseOperator", caseReference = Guid.NewGuid().ToString("D");
            erasurePlayers.Add(player);
            db.Submit(player, fixture, Now);
            var wrongScope = await ErasureCli(cfg, "privacy-erase", player, caseReference, Project);
            Check(wrongScope.ExitCode == 2);
            using (var body = JsonDocument.Parse(wrongScope.Output))
                Check(body.RootElement.GetProperty("code").GetString() == "invalid_operator_scope");
            var wrongCase = await ErasureCli(cfg, "privacy-erase", player, "not-a-case-guid");
            Check(wrongCase.ExitCode == 2);
            using (var body = JsonDocument.Parse(wrongCase.Output))
                Check(body.RootElement.GetProperty("code").GetString() == "invalid_erasure_input");
            foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 1);
            Check(PgTombstoneCount(cfg, player) == 0);
            var inspect = await ErasureCli(cfg, "privacy-inspect", player, caseReference);
            Check(inspect.ExitCode == 0);
            using (var body = JsonDocument.Parse(inspect.Output))
            {
                Check(body.RootElement.GetProperty("scope").GetProperty("database").GetString() == VerifierDatabase);
                Check(body.RootElement.GetProperty("erasure").ValueKind == JsonValueKind.Null);
                var state = body.RootElement.GetProperty("readback");
                Check(!state.GetProperty("suppressed").GetBoolean() && state.GetProperty("attempts").GetInt32() == 1);
            }
            var erase = await ErasureCli(cfg, "privacy-erase", player, caseReference);
            Check(erase.ExitCode == 0);
            using (var body = JsonDocument.Parse(erase.Output))
            {
                var scope = body.RootElement.GetProperty("scope");
                Check(scope.GetProperty("database").GetString() == VerifierDatabase && scope.GetProperty("schema").GetString() == "shorepop_league" &&
                    scope.GetProperty("role").GetString() == "shorepop_league" && scope.GetProperty("projectId").GetString() == ProductionProject);
                var receipt = body.RootElement.GetProperty("erasure");
                Check(!receipt.GetProperty("alreadyErased").GetBoolean() && receipt.GetProperty("attempts").GetInt32() == 1 &&
                    receipt.GetProperty("levelBests").GetInt32() == 1 && receipt.GetProperty("weeklyLevels").GetInt32() == 1 && receipt.GetProperty("weeklyTotals").GetInt32() == 1);
                Check(body.RootElement.GetProperty("readback").GetProperty("suppressed").GetBoolean());
            }
            AssertPostgresErased(cfg, db, player, fixture);
            foreach (var result in new[] { wrongScope, wrongCase, inspect, erase })
                Check(!result.Output.Contains(player, StringComparison.Ordinal) && !result.Errors.Contains(player, StringComparison.Ordinal) &&
                    !result.Output.Contains(caseReference, StringComparison.Ordinal) && !result.Errors.Contains(caseReference, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "postgres-erasure-cli.json"), JsonSerializer.Serialize(new {
                passed = true, database = VerifierDatabase, wrongProjectExit = wrongScope.ExitCode,
                invalidCaseExit = wrongCase.ExitCode, inspectExit = inspect.ExitCode, eraseExit = erase.ExitCode,
                rawSubjectOrCaseInOutput = false, productionWrites = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    static async Task<(int ExitCode, string Output, string Errors)> ErasureCli(LeagueOptions cfg, string operation, string player,
        string caseReference, string? projectOverride = null)
    {
        // The normal compiled operator entry point receives identity only through stdin.
        // PgConnect has already enforced the verifier database before this suite runs.
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(LeagueRepository).Assembly.Location);
        start.ArgumentList.Add(operation);
        start.Environment["League__ProjectId"] = projectOverride ?? cfg.ProjectId;
        start.Environment["League__DatabaseProvider"] = "postgres";
        start.Environment["League__DatabaseUrl"] = cfg.DatabaseUrl;
        start.Environment["League__DatabasePath"] = "";
        start.Environment["League__RepeatPolicy"] = cfg.RepeatPolicy;
        start.Environment["League__BoosterPolicy"] = cfg.BoosterPolicy;
        start.Environment["League__OwnerDecisionReference"] = cfg.OwnerDecisionReference;
        using var child = System.Diagnostics.Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteAsync(JsonSerializer.Serialize(new { player, caseReference }));
        child.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw new Exception("Operator verifier process exceeded 30 seconds.");
        }
        return (child.ExitCode, await output, await errors);
    }

    static async Task<T> ErasureOperation<T>(Func<T> operation, System.Diagnostics.Stopwatch timer)
    {
        while (true)
        {
            try
            {
                var result = await Task.Run(operation);
                Check(timer.Elapsed < TimeSpan.FromSeconds(90));
                return result;
            }
            catch (Exception error) when (timer.Elapsed < TimeSpan.FromSeconds(90) &&
                (error is TimeoutException || error is NpgsqlException { IsTransient: true } ||
                 error is NpgsqlException { InnerException: TimeoutException }))
            {
                Interlocked.Increment(ref postgresTransientRetries);
                await Task.Delay(250);
            }
        }
    }

    static void AssertPostgresErased(LeagueOptions cfg, LeagueRepository db, string player, CampaignWin fixture)
    {
        foreach (string table in PlayerTables) Check(PgCountPlayer(cfg, table, player) == 0);
        Check(PgTombstoneCount(cfg, player) == 1);
        var state = db.InspectErasure(player);
        Check(state.Suppressed && state.Attempts == 0 && state.LevelBests == 0 && state.WeeklyLevels == 0 && state.WeeklyTotals == 0);
        RefuseCode(422, "player_erased", () => db.Submit(player, fixture, Now));
        RefuseCode(422, "player_erased", () => db.Submit(player, Fresh(fixture), Now.AddDays(7)));
        Check(db.Weekly(player, 7, Now).WeeklyScore == 0 && db.Weekly(player, 7, Now).Rank == 0);
        Check(new LeagueRepository(cfg).InspectErasure(player).Suppressed);
    }

    // Independent test oracle: no production helper is reused for either verification or cleanup.
    static string PostgresErasureHash(string project, string player) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project + ":" + player)));

    static long PgTombstoneCount(LeagueOptions cfg, string player)
    {
        using var connection = PgConnect(cfg);
        using var command = new NpgsqlCommand("SELECT COUNT(*) FROM shorepop_league.erased_subjects WHERE subject_hash=@hash", connection);
        command.Parameters.AddWithValue("hash", PostgresErasureHash(cfg.ProjectId, player));
        return (long)command.ExecuteScalar()!;
    }
}
