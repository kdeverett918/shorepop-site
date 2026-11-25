using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shorepop.League;

namespace LeagueServerChecks;

internal static partial class Program
{
    static void SqliteErasureChecks(CampaignWin fixture, CampaignWin other)
    {
        Test("SQLite player erasure removes every score scope and permanently refuses durable replay", () =>
        {
            var cfg = Config("erasure-complete"); var db = New(cfg);
            const string player = "eraseTarget", retained = "eraseRetained";
            Check(!db.Submit(player, fixture, Now).Duplicate);
            Check(!db.Submit(player, other, Now).Duplicate);
            Check(!db.Submit(player, Fresh(fixture), Now.AddDays(7)).Duplicate);
            Check(!db.Submit(player, Fresh(other), Now.AddDays(7)).Duplicate);
            Check(!db.Submit(player, Fresh(fixture), Now.AddDays(7)).Duplicate);
            Check(!db.Submit(player, Fresh(fixture), Now.AddDays(14)).Duplicate);
            Check(!db.Submit(retained, fixture, Now).Duplicate);
            Check(!db.Submit(retained, other, Now).Duplicate);
            var retainedRows = PlayerTables.ToDictionary(table => table, table => SqliteErasureRows(cfg, table, retained));
            var before = db.InspectErasure(player);
            Check(!before.Suppressed && before.Attempts == 6 && before.LevelBests == 2 &&
                before.WeeklyLevels == 5 && before.WeeklyTotals == 3);

            string caseReference = Guid.NewGuid().ToString("D");
            DateTime erasedAt = Now.AddDays(15);
            var erased = db.ErasePlayer(player, caseReference, erasedAt);
            Check(!erased.AlreadyErased && erased.Attempts == 6 && erased.LevelBests == 2 &&
                erased.WeeklyLevels == 5 && erased.WeeklyTotals == 3);
            foreach (string table in PlayerTables)
            {
                Check(SqliteErasureRows(cfg, table, player).Length == 0);
                Check(SqliteErasureRows(cfg, table, retained).SequenceEqual(retainedRows[table]));
            }
            Check(Count(cfg, "erased_subjects") == 1);
            var tombstone = SqliteErasureTombstone(cfg, player);
            Check(tombstone.HasValue && tombstone.Value.CaseReference == caseReference &&
                DateTime.Parse(tombstone.Value.ErasedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) == erasedAt);
            var after = db.InspectErasure(player);
            Check(after.Suppressed && after.Attempts == 0 && after.LevelBests == 0 &&
                after.WeeklyLevels == 0 && after.WeeklyTotals == 0);

            var restarted = New(cfg);
            foreach (var replay in new[] { fixture, other, Fresh(fixture), Fresh(other) })
                RefuseCode(422, "player_erased", () => restarted.Submit(player, replay, erasedAt.AddMinutes(1)));
            foreach (string retryCase in new[] { caseReference, Guid.NewGuid().ToString("D") })
            {
                var repeat = restarted.ErasePlayer(player, retryCase, erasedAt.AddHours(1));
                Check(repeat.AlreadyErased && repeat.Attempts == 0 && repeat.LevelBests == 0 &&
                    repeat.WeeklyLevels == 0 && repeat.WeeklyTotals == 0);
                Check(Count(cfg, "erased_subjects") == 1 && SqliteErasureTombstone(cfg, player) == tombstone);
            }
            foreach (string table in PlayerTables)
            {
                Check(SqliteErasureRows(cfg, table, player).Length == 0);
                Check(SqliteErasureRows(cfg, table, retained).SequenceEqual(retainedRows[table]));
            }
            Check(!restarted.InspectErasure(retained).Suppressed);
            Check(restarted.Weekly(retained, 7, Now).WeeklyScore == (long)fixture.Score + other.Score);
        });

        Test("SQLite erasure matches case-sensitive player subjects exactly", () =>
        {
            var cfg = Config("erasure-case-sensitive"); var db = New(cfg);
            Check(!db.Submit("playerA", fixture, Now).Duplicate);
            Check(!db.Submit("playerA", other, Now).Duplicate);
            Check(!db.Submit("playera", fixture, Now).Duplicate);
            var retainedRows = PlayerTables.ToDictionary(table => table, table => SqliteErasureRows(cfg, table, "playera"));
            var erased = db.ErasePlayer("playerA", Guid.NewGuid().ToString("D"), Now.AddMinutes(1));
            Check(!erased.AlreadyErased && erased.Attempts == 2 && erased.LevelBests == 2 &&
                erased.WeeklyLevels == 2 && erased.WeeklyTotals == 1);
            Check(db.InspectErasure("playerA").Suppressed && !db.InspectErasure("playera").Suppressed);
            Check(SqliteErasureTombstone(cfg, "playerA").HasValue && !SqliteErasureTombstone(cfg, "playera").HasValue);
            foreach (string table in PlayerTables)
            {
                Check(SqliteErasureRows(cfg, table, "playerA").Length == 0);
                Check(SqliteErasureRows(cfg, table, "playera").SequenceEqual(retainedRows[table]));
            }
            RefuseCode(422, "player_erased", () => db.Submit("playerA", fixture, Now.AddMinutes(2)));
            Check(db.Submit("playera", fixture, Now.AddMinutes(2)).Duplicate);
            Check(db.Submit("playera", other, Now.AddMinutes(2)).WeeklyAdded == other.Score);
            Check(db.Weekly("playera", 7, Now).WeeklyScore == (long)fixture.Score + other.Score);
            Check(Count(cfg, "erased_subjects") == 1);
        });

        Test("SQLite erasure fault rolls back score removal and tombstone together", () =>
        {
            var cfg = Config("erasure-rollback"); var db = New(cfg);
            const string player = "eraseRollback", retained = "eraseRollbackRetained";
            var accepted = db.Submit(player, fixture, Now);
            Check(!accepted.Duplicate && !db.Submit(retained, other, Now).Duplicate);
            var originalRows = PlayerTables.ToDictionary(table => table, table => SqliteErasureRows(cfg, table, player));
            var retainedRows = PlayerTables.ToDictionary(table => table, table => SqliteErasureRows(cfg, table, retained));
            foreach (string table in PlayerTables) Check(originalRows[table].Length == 1);
            string caseReference = Guid.NewGuid().ToString("D");
            Sql(cfg, """
                CREATE TRIGGER fail_erasure_total BEFORE DELETE ON weekly_totals
                WHEN OLD.player = 'eraseRollback'
                BEGIN SELECT RAISE(ABORT, 'verifier erasure deletion fault'); END;
                """);
            try
            {
                bool refused = false;
                try { db.ErasePlayer(player, caseReference, Now.AddMinutes(1)); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19) { refused = true; }
                Check(refused);
                foreach (string table in PlayerTables)
                {
                    Check(SqliteErasureRows(cfg, table, player).SequenceEqual(originalRows[table]));
                    Check(SqliteErasureRows(cfg, table, retained).SequenceEqual(retainedRows[table]));
                }
                Check(Count(cfg, "erased_subjects") == 0 && !SqliteErasureTombstone(cfg, player).HasValue);
                var state = db.InspectErasure(player);
                Check(!state.Suppressed && state.Attempts == 1 && state.LevelBests == 1 &&
                    state.WeeklyLevels == 1 && state.WeeklyTotals == 1);
                var retry = db.Submit(player, fixture, Now.AddMinutes(2));
                Check(retry.Duplicate && retry with { Duplicate = false } == accepted);
            }
            finally { Sql(cfg, "DROP TRIGGER IF EXISTS fail_erasure_total;"); }

            var recovered = New(cfg).ErasePlayer(player, caseReference, Now.AddMinutes(3));
            Check(!recovered.AlreadyErased && recovered.Attempts == 1 && recovered.LevelBests == 1 &&
                recovered.WeeklyLevels == 1 && recovered.WeeklyTotals == 1);
            foreach (string table in PlayerTables)
            {
                Check(SqliteErasureRows(cfg, table, player).Length == 0);
                Check(SqliteErasureRows(cfg, table, retained).SequenceEqual(retainedRows[table]));
            }
            Check(Count(cfg, "erased_subjects") == 1 && SqliteErasureTombstone(cfg, player)?.CaseReference == caseReference);
            RefuseCode(422, "player_erased", () => db.Submit(player, fixture, Now.AddMinutes(4)));
        });

        Test("SQLite invalid erasure case reference leaves all stored rows intact", () =>
        {
            var cfg = Config("erasure-invalid-reference"); var db = New(cfg);
            const string player = "eraseInvalidReference";
            Check(!db.Submit(player, fixture, Now).Duplicate);
            var originalRows = PlayerTables.ToDictionary(table => table, table => SqliteErasureRows(cfg, table, player));
            foreach (string caseReference in new[] { "", "not-a-case-guid" })
                Refuse(422, () => db.ErasePlayer(player, caseReference, Now.AddMinutes(1)));
            foreach (string table in PlayerTables)
                Check(SqliteErasureRows(cfg, table, player).SequenceEqual(originalRows[table]));
            Check(Count(cfg, "erased_subjects") == 0 && !db.InspectErasure(player).Suppressed);
        });
    }

    static string[] SqliteErasureRows(LeagueOptions cfg, string table, string player)
    {
        if (!PlayerTables.Contains(table)) throw new ArgumentException("Unexpected test table.");
        using var connection = Connect(cfg); using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM " + table + " WHERE player=$player";
        command.Parameters.AddWithValue("$player", player);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount]; reader.GetValues(values);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    static (string CaseReference, string ErasedUtc)? SqliteErasureTombstone(LeagueOptions cfg, string player)
    {
        // Independently compute the documented project-scoped, case-sensitive subject digest.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cfg.ProjectId + ":" + player)));
        using var connection = Connect(cfg); using var command = connection.CreateCommand();
        command.CommandText = "SELECT case_reference,erased_utc FROM erased_subjects WHERE subject_hash=$hash";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }
}
