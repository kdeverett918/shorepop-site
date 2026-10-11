namespace Shorepop.League;

public static class RecordEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapGet(
        "/v1/records/{stableSlot:int}", (int stableSlot, string rules, string? category, LeagueRepository repository) =>
            repository.Best(stableSlot, rules, category ?? LeagueOptions.Category))
        .RequireAuthorization().RequireRateLimiting("player");
}
