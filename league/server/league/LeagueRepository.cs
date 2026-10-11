using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Shorepop.League;

public sealed class LeagueRepository(LeagueOptions options)
{
    public void Initialize()
    {
        options.RequireConfigured();
        if (options.DatabaseProvider == "sqlite") Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        using var connection = Open();
        if (connection is SqliteConnection)
            Execute(connection, null, "PRAGMA journal_mode=WAL;");
        using var transaction = LeagueDatabase.Begin(connection);
        LeagueDatabase.Lock(connection, transaction, "schema:v1");
        string ddl = """
            CREATE TABLE IF NOT EXISTS configuration (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attempts (
              player TEXT NOT NULL, attempt_id TEXT NOT NULL, payload_hash TEXT NOT NULL,
              result_json TEXT NOT NULL, accepted_utc TEXT NOT NULL, PRIMARY KEY(player, attempt_id));
            CREATE TABLE IF NOT EXISTS level_bests (
              player TEXT NOT NULL, rules TEXT NOT NULL, stable_slot INTEGER NOT NULL, category TEXT NOT NULL,
              score INTEGER NOT NULL CHECK(score > 0), PRIMARY KEY(player, rules, stable_slot, category));
            CREATE INDEX IF NOT EXISTS level_top ON level_bests(rules, stable_slot, category, score DESC, player);
            CREATE TABLE IF NOT EXISTS weekly_levels (
              week TEXT NOT NULL, player TEXT NOT NULL, rules TEXT NOT NULL, stable_slot INTEGER NOT NULL,
              category TEXT NOT NULL, score INTEGER NOT NULL CHECK(score > 0),
              PRIMARY KEY(week, player, rules, stable_slot, category));
            CREATE TABLE IF NOT EXISTS weekly_totals (
              week TEXT NOT NULL, player TEXT NOT NULL, rules TEXT NOT NULL, category TEXT NOT NULL,
              score INTEGER NOT NULL CHECK(score >= 0), PRIMARY KEY(week, player, rules, category));
            CREATE INDEX IF NOT EXISTS weekly_rank ON weekly_totals(week, rules, category, score DESC, player);
            """;
        if (connection is NpgsqlConnection) ddl = ddl.Replace("score INTEGER", "score BIGINT");
        Execute(connection, transaction, ddl);
        string policy = options.ProjectId + "|" + options.RepeatPolicy + "|" + options.BoosterPolicy;
        Execute(connection, transaction, "INSERT INTO configuration VALUES ('policy', $value) ON CONFLICT(key) DO NOTHING", ("$value", policy));
        if ((string?)Scalar(connection, transaction, "SELECT value FROM configuration WHERE key='policy'") != policy)
            throw new LeagueRefusal(503, "database_policy_mismatch");
        transaction.Commit();
    }

    public Acceptance Submit(string player, CampaignWin win, DateTime now)
    {
        options.RequireConfigured();
        if (!ValidPlayer(player)) throw new LeagueRefusal(401, "invalid_subject");
        var a = win.Attempt;
        if (a == null || string.IsNullOrEmpty(a.Id) || a.Id.Length != 32 || !Guid.TryParseExact(a.Id, "N", out _) ||
            a.LevelId < 1 || a.LevelId > 300 || a.StableSlot < 1 || a.StableSlot > 300)
            throw new LeagueRefusal(422, "invalid_attempt_id");
        // The same durable attempt can be retried after its numeric display label migrates
        // (schema 13 level 13 -> schema 14 level 61). Hash the permanent identity and evidence.
        string payload = JsonSerializer.Serialize(win with { Attempt = a with { LevelId = 0 } }, WireJson.Options);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        using var connection = Open();
        // SQLite uses BEGIN IMMEDIATE; PostgreSQL takes a stable per-player advisory transaction
        // lock across replicas. The receipt and every ranking delta commit together.
        using var transaction = LeagueDatabase.Begin(connection);
        LeagueDatabase.Lock(connection, transaction, "player:" + player);
        using (var existing = Command(connection, transaction,
            "SELECT payload_hash, result_json FROM attempts WHERE player=$player AND attempt_id=$id", ("$player", player), ("$id", a.Id)))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != hash) throw new LeagueRefusal(409, "attempt_payload_conflict");
                return JsonSerializer.Deserialize<Acceptance>(reader.GetString(1), WireJson.Options)! with { Duplicate = true };
            }
        }
        ReplayValidator.Validate(win, now, options.AllowsTailwind);
        DateTime week = WeekStart(now);
        string weekText = Utc(week);
        var key = new[] { ("$player", (object)player), ("$rules", a.Rules), ("$slot", (object)a.StableSlot), ("$category", LeagueOptions.Category) };
        long priorBest = Number(Scalar(connection, transaction,
            "SELECT score FROM level_bests WHERE player=$player AND rules=$rules AND stable_slot=$slot AND category=$category", key));
        long priorWeeklyBest = Number(Scalar(connection, transaction,
            "SELECT score FROM weekly_levels WHERE week=$week AND player=$player AND rules=$rules AND stable_slot=$slot AND category=$category",
            key.Append(("$week", (object)weekText)).ToArray()));
        long addition = options.RepeatPolicy == "FIRST_CLEAR_PER_LEVEL_EVER"
            ? (priorBest == 0 ? win.Score : 0) : Math.Max(0, win.Score - priorWeeklyBest);
        Execute(connection, transaction, """
            INSERT INTO level_bests VALUES($player,$rules,$slot,$category,$score)
            ON CONFLICT(player,rules,stable_slot,category) DO UPDATE SET score=MAX(score,excluded.score)
            """, key.Append(("$score", (object)win.Score)).ToArray());
        Execute(connection, transaction, """
            INSERT INTO weekly_levels VALUES($week,$player,$rules,$slot,$category,$score)
            ON CONFLICT(week,player,rules,stable_slot,category) DO UPDATE SET score=MAX(score,excluded.score)
            """, key.Append(("$week", (object)weekText)).Append(("$score", (object)win.Score)).ToArray());
        Execute(connection, transaction, """
            INSERT INTO weekly_totals VALUES($week,$player,$rules,$category,$addition)
            ON CONFLICT(week,player,rules,category) DO UPDATE SET score=score+excluded.score
            """, ("$week", weekText), ("$player", player), ("$rules", a.Rules), ("$category", LeagueOptions.Category), ("$addition", addition));
        long total = Number(Scalar(connection, transaction,
            "SELECT score FROM weekly_totals WHERE week=$week AND player=$player AND rules=$rules AND category=$category",
            ("$week", weekText), ("$player", player), ("$rules", a.Rules), ("$category", LeagueOptions.Category)));
        var result = new Acceptance(a.Id, "accepted", false, week, addition, total, (int)Math.Max(priorBest, win.Score));
        Execute(connection, transaction,
            "INSERT INTO attempts VALUES($player,$id,$hash,$result,$now)",
            ("$player", player), ("$id", a.Id), ("$hash", hash), ("$result", JsonSerializer.Serialize(result, WireJson.Options)), ("$now", Utc(now)));
        transaction.Commit();
        return result;
    }

    public RecordBest Best(int stableSlot, string rules, string category)
    {
        options.RequireConfigured();
        if (stableSlot < 1 || stableSlot > 300 || rules != ReplayValidator.RulesVersion || category != LeagueOptions.Category)
            throw new LeagueRefusal(422, "invalid_record_scope");
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT player,score FROM level_bests WHERE rules=$rules AND stable_slot=$slot AND category=$category ORDER BY score DESC,player LIMIT 1",
            ("$rules", rules), ("$slot", stableSlot), ("$category", category));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(stableSlot, rules, category, true, reader.GetInt64(1), PlayerName(reader.GetString(0)))
            : new(stableSlot, rules, category, false, 0, "");
    }

    public WeeklyStanding Weekly(string player, int neighbours, DateTime now)
    {
        options.RequireConfigured();
        if (!ValidPlayer(player)) throw new LeagueRefusal(401, "invalid_subject");
        if (neighbours < 1 || neighbours > 25) throw new LeagueRefusal(422, "invalid_neighbour_count");
        DateTime week = WeekStart(now);
        using var connection = Open();
        using var transaction = LeagueDatabase.Begin(connection, readOnly: true);
        var scope = new[] { ("$week", (object)Utc(week)), ("$rules", ReplayValidator.RulesVersion), ("$category", LeagueOptions.Category) };
        long own = Number(Scalar(connection, transaction,
            "SELECT score FROM weekly_totals WHERE week=$week AND rules=$rules AND category=$category AND player=$player",
            scope.Append(("$player", (object)player)).ToArray()));
        int rank = own == 0 ? 0 : checked(1 + (int)Number(Scalar(connection, transaction, """
            SELECT COUNT(*) FROM weekly_totals WHERE week=$week AND rules=$rules AND category=$category
              AND (score>$score OR (score=$score AND player<$player))
            """, scope.Append(("$score", (object)own)).Append(("$player", (object)player)).ToArray())));
        int offset = Math.Max(0, rank - 1 - neighbours / 2);
        using var command = Command(connection, transaction, """
            SELECT player,score FROM weekly_totals WHERE week=$week AND rules=$rules AND category=$category AND score>0
              ORDER BY score DESC,player LIMIT $limit OFFSET $offset
            """, scope.Append(("$limit", (object)neighbours)).Append(("$offset", (object)offset)).ToArray());
        using var reader = command.ExecuteReader();
        var rows = new List<WeeklyRow>();
        while (reader.Read()) rows.Add(new(offset + rows.Count + 1, PlayerName(reader.GetString(0)), reader.GetInt64(1), "", reader.GetString(0) == player));
        return new(week, own, rank, "", rows);
    }

    public static DateTime WeekStart(DateTime now)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC required", nameof(now));
        return now.Date.AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7));
    }
    public static bool ValidPlayer(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(char.IsAsciiLetterOrDigit);
    private static string PlayerName(string player)
    {
        string[] adjectives = ["Calm", "Sunny", "Brave", "Gentle", "Bright", "Merry", "Swift", "Kind"];
        string[] animals = ["Otter", "Fox", "Puffin", "Seal", "Dolphin", "Turtle", "Heron", "Penguin"];
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(player));
        return adjectives[hash[0] % adjectives.Length] + " " + animals[hash[1] % animals.Length];
    }
    public async Task CheckReadinessAsync(CancellationToken cancel)
    {
        options.RequireConfigured();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var connection = LeagueDatabase.Create(options);
        await connection.OpenAsync(timeout.Token);
        using var command = Command(connection, null, "SELECT value FROM configuration WHERE key='policy';");
        command.CommandTimeout = 3;
        var policy = await command.ExecuteScalarAsync(timeout.Token);
        if ((string?)policy != options.ProjectId + "|" + options.RepeatPolicy + "|" + options.BoosterPolicy)
            throw new LeagueRefusal(503, "database_policy_mismatch");
        using var tables = Command(connection, null, "SELECT 1 FROM attempts WHERE FALSE; SELECT 1 FROM level_bests WHERE FALSE; SELECT 1 FROM weekly_levels WHERE FALSE; SELECT 1 FROM weekly_totals WHERE FALSE;");
        tables.CommandTimeout = 3;
        await tables.ExecuteNonQueryAsync(timeout.Token);
    }
    private DbConnection Open() => LeagueDatabase.Open(options);
    private static string Utc(DateTime date) => date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static long Number(object? value) => value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    private static DbCommand Command(DbConnection c, DbTransaction? t, string sql, params (string, object)[] values)
        => LeagueDatabase.Command(c, t, sql, values);
    private static object? Scalar(DbConnection c, DbTransaction? t, string sql, params (string, object)[] values)
    { using var command = Command(c, t, sql, values); return command.ExecuteScalar(); }
    private static void Execute(DbConnection c, DbTransaction? t, string sql, params (string, object)[] values)
    { using var command = Command(c, t, sql, values); command.ExecuteNonQuery(); }
}
