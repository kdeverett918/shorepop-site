using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shorepop.League;

/// <summary>Private operator process, not an HTTP route. Exact identity enters on stdin only.</summary>
public static class LeaguePrivacyCli
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record Input(string Player, string CaseReference);

    public static int Run(string operation)
    {
        try
        {
            // The wrapper limits input too; keep identifiers out of command arguments and logs.
            char[] buffer = new char[2049];
            int count = Console.In.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length) throw new LeagueRefusal(422, "invalid_erasure_input");
            var input = JsonSerializer.Deserialize<Input>(new string(buffer, 0, count), WireJson.Options);
            if (input == null || !LeagueRepository.ValidPlayer(input.Player) || !Guid.TryParse(input.CaseReference, out _))
                throw new LeagueRefusal(422, "invalid_erasure_input");
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var options = configuration.GetSection("League").Get<LeagueOptions>() ?? new();
            options.RequireConfigured();
            if (options.DatabaseProvider != "postgres" || options.ProjectId != "aea5f7f0-ebcb-4745-98c5-378bb2ad2feb")
                throw new LeagueRefusal(503, "invalid_operator_scope");
            var repository = new LeagueRepository(options);
            // Inspection never initializes schema or mutates state. Deployment owns migrations.
            repository.CheckReadinessAsync(default).GetAwaiter().GetResult();
            string database;
            using (var connection = LeagueDatabase.Open(options))
            using (var command = LeagueDatabase.Command(connection, null, "SELECT current_user"))
            {
                if ((string?)command.ExecuteScalar() != "shorepop_league")
                    throw new LeagueRefusal(503, "invalid_operator_role");
                database = connection.Database;
            }
            ErasureReceipt? erasure = operation == "privacy-erase"
                ? repository.ErasePlayer(input.Player, input.CaseReference, DateTime.UtcNow) : null;
            var readback = repository.InspectErasure(input.Player);
            var scope = new { projectId = options.ProjectId, schema = "shorepop_league", role = "shorepop_league",
                database, repeatPolicy = options.RepeatPolicy, boosterPolicy = options.BoosterPolicy };
            Console.WriteLine(JsonSerializer.Serialize(new { scope, erasure, readback }, WireJson.Options));
            return 0;
        }
        catch (LeagueRefusal error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { code = error.Code, status = error.Status }, WireJson.Options));
            return 2;
        }
        catch (Exception error) when (error is JsonException or Npgsql.NpgsqlException or TimeoutException or ArgumentException or OperationCanceledException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { code = "operator_unavailable", kind = error.GetType().Name }, WireJson.Options));
            return 2;
        }
    }
}
