using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Shorepop.League;

/// <summary>Provider mechanics only. Ranking and replay decisions stay in LeagueRepository.</summary>
public static class LeagueDatabase
{
    public static DbConnection Create(LeagueOptions options) => options.DatabaseProvider == "postgres"
        ? new NpgsqlConnection(NormalizeConnectionString(options.DatabaseUrl))
        : new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5, Pooling = true
        }.ToString());

    public static DbConnection Open(LeagueOptions options)
    {
        DbConnection connection = Create(options);
        try
        {
            connection.Open();
            if (connection is SqliteConnection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public static DbTransaction Begin(DbConnection connection, bool readOnly = false) => connection is SqliteConnection sqlite
        ? sqlite.BeginTransaction(deferred: readOnly)
        : connection.BeginTransaction(readOnly ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted);

    public static void Lock(DbConnection connection, DbTransaction transaction, string identity)
    {
        if (connection is not NpgsqlConnection) return;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("shorepop_league:" + identity));
        using var command = Command(connection, transaction, "SELECT pg_advisory_xact_lock(@key)",
            ("@key", (object)BinaryPrimitives.ReadInt64BigEndian(hash)));
        command.ExecuteNonQuery();
    }

    public static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string, object)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        sql = sql.Replace('$', '@');
        if (connection is NpgsqlConnection)
        {
            // The fixed allowlist is SQL owned here; no request-controlled identifiers are interpolated.
            sql = Regex.Replace(sql, @"\b(configuration|attempts|level_bests|weekly_levels|weekly_totals)\b", "shorepop_league.$1");
            sql = sql.Replace("MAX(score,excluded.score)", "GREATEST(score,excluded.score)");
            // ON CONFLICT requires an unambiguous target-table value on PostgreSQL.
            if (sql.Contains("UPDATE SET score=GREATEST"))
            {
                string target = sql.Contains("INSERT INTO shorepop_league.level_bests") ? "level_bests" : "weekly_levels";
                sql = sql.Replace("GREATEST(score,excluded.score)", $"GREATEST({target}.score,excluded.score)");
            }
            sql = sql.Replace("UPDATE SET score=score+excluded.score", "UPDATE SET score=weekly_totals.score+excluded.score");
        }
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name.Replace('$', '@'); parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    // Parse provider URLs without exposing credentials in failures. TLS verification is the default;
    // deployed restricted roles have an eight-connection cap, and each process uses at most four.
    public static string NormalizeConnectionString(string value)
    {
        try
        {
            NpgsqlConnectionStringBuilder builder;
            if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value); var auth = uri.UserInfo.Split(':', 2);
                if (auth.Length != 2 || string.IsNullOrWhiteSpace(uri.Host) || uri.AbsolutePath.Length <= 1 || uri.Fragment.Length > 0) throw new FormatException();
                builder = new() { Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port,
                    Database = Uri.UnescapeDataString(uri.AbsolutePath[1..]), Username = Uri.UnescapeDataString(auth[0]),
                    Password = Uri.UnescapeDataString(auth[1]), SslMode = SslMode.VerifyFull };
                foreach (string item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = item.Split('=', 2); if (pair.Length != 2) throw new FormatException();
                    string setting = Uri.UnescapeDataString(pair[1]);
                    switch (Uri.UnescapeDataString(pair[0]).ToLowerInvariant())
                    {
                        case "sslmode": builder.SslMode = Enum.Parse<SslMode>(setting.Replace("-", ""), true); break;
                        case "connect_timeout": builder.Timeout = int.Parse(setting, CultureInfo.InvariantCulture); break;
                        case "application_name": builder.ApplicationName = setting; break;
                        default: throw new FormatException();
                    }
                }
            }
            else
            {
                builder = new(value);
                if (!builder.ContainsKey("SSL Mode")) builder.SslMode = SslMode.VerifyFull;
            }
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) || string.IsNullOrWhiteSpace(builder.Username)) throw new FormatException();
            builder.IncludeErrorDetail = false; builder.LogParameters = false; builder.PersistSecurityInfo = false;
            builder.SearchPath = "pg_catalog"; builder.NoResetOnClose = false;
            builder.MaxPoolSize = Math.Min(builder.MaxPoolSize, 4);
            builder.Timeout = builder.Timeout == 0 ? 10 : Math.Min(builder.Timeout, 10);
            builder.CommandTimeout = builder.CommandTimeout == 0 ? 15 : Math.Min(builder.CommandTimeout, 15);
            return builder.ConnectionString;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        { throw new ArgumentException("Invalid PostgreSQL connection configuration."); }
    }
}
