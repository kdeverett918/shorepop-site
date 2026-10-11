using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shorepop.League;

// Field names mirror CampaignWinEvidence, without linking UnityEngine or trusting a client hash.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CampaignWin
{
    public CampaignAttempt? Attempt { get; init; }
    public int Score { get; init; }
    public int Stars { get; init; }
    public long CompletedUtcTicks { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CampaignAttempt
{
    public string Id { get; init; } = "";
    public int LevelId { get; init; }
    public int StableSlot { get; init; }
    public int Boosters { get; init; }
    public int CreditedBoosters { get; init; }
    public int PaidCoins { get; init; }
    // The part of CreditedBoosters the Tailwind win streak gave (design/specs/meta-loop-r16.md).
    // Omitted when zero, so an unboosted attempt serializes - and hashes - exactly as before.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int StreakBoosters { get; init; }
    public uint Seed { get; init; }
    public long StartedUtcTicks { get; init; }
    public string Rules { get; init; } = "";
    public bool EvidenceComplete { get; init; }
    public List<CampaignAction>? Actions { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CampaignAction
{
    public string Kind { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int Ability { get; init; }
    public bool Right { get; init; }
}
public sealed record Acceptance(string AttemptId, string Status, bool Duplicate, DateTime WeekStartUtc,
    long WeeklyAdded, long WeeklyScore, int LevelBest);
public sealed record RecordBest(int StableSlot, string Rules, string Category, bool Known, long Score, string Name);
public sealed record WeeklyRow(int Rank, string Name, long Score, string Tier, bool IsPlayer);
public sealed record WeeklyStanding(DateTime WeekStartUtc, long WeeklyScore, int Rank, string Tier, IReadOnlyList<WeeklyRow> Neighbours);
public sealed record ErrorBody(string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttemptId = null);
public sealed class LeagueRefusal(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
}
