using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using BloomHarbor;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shorepop.League;
using Attempt = Shorepop.League.CampaignAttempt;
using ActionDto = Shorepop.League.CampaignAction;

namespace LeagueServerChecks;

internal static partial class Program
{
    static int assertions, scenarios;
    static readonly DateTime Now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
    static readonly string Project = "11111111-1111-4111-8111-111111111111"; // Test identifier, never credentials.
    static string root = "";
    static async Task Main(string[] args)
    {
        root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("tests/league-server/evidence/" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new Exception("Use a new evidence directory.");
        Directory.CreateDirectory(root);
        var fixture = Win(1);
        var other = Win(ReplayValidator.DefinitionForSlot(61).Id);
        Test("PostgreSQL configuration defaults to verified TLS without disclosing supplied credentials", () =>
        {
            foreach (string value in new[] { "postgres://user:dummy@example.invalid/db", "Host=example.invalid;Database=db;Username=user;Password=dummy" })
            {
                var parsed = new Npgsql.NpgsqlConnectionStringBuilder(LeagueDatabase.NormalizeConnectionString(value));
                Check(parsed.SslMode == Npgsql.SslMode.VerifyFull && parsed.MaxPoolSize == 4 && !parsed.IncludeErrorDetail && !parsed.PersistSecurityInfo);
            }
            var explicitMode = new Npgsql.NpgsqlConnectionStringBuilder(LeagueDatabase.NormalizeConnectionString("Host=localhost;Database=test;Username=user;Password=dummy;SSL Mode=Disable"));
            Check(explicitMode.SslMode == Npgsql.SslMode.Disable);
            bool refused = false;
            try { LeagueDatabase.NormalizeConnectionString("postgres://user:private-marker@example.invalid/db?unsupported=private-marker"); }
            catch (ArgumentException error) { refused = !error.ToString().Contains("private-marker"); }
            Check(refused);
        });
        Test("unconfigured and owner decision gates", () =>
        {
            Refuse(503, () => new LeagueRepository(new()).Submit("player", fixture, Now));
            foreach (Action<LeagueOptions> invalidate in new Action<LeagueOptions>[] {
                x => x.RepeatPolicy = "UNCONFIGURED", x => x.BoosterPolicy = "ALLOW_ALL",
                x => x.ProjectId = "", x => x.OwnerDecisionReference = "", x => x.DatabasePath = "relative.sqlite"
            }) { var cfg = Config("gate"); invalidate(cfg); Refuse(503, () => New(cfg)); }
        });
        Test("real SQLite duplicate and lost-response restart", () =>
        {
            var cfg = Config("duplicate"); var db = New(cfg);
            var first = db.Submit("playerA", fixture, Now);
            Check(!first.Duplicate && first.WeeklyAdded == fixture.Score && first.WeeklyScore == fixture.Score);
            var duplicate = db.Submit("playerA", fixture, Now);
            Check(duplicate.Duplicate && duplicate with { Duplicate = false } == first);
            // Model a successful durable commit whose response never arrived, then reopen the DB.
            SqliteConnection.ClearAllPools();
            var restarted = New(cfg);
            var retry = restarted.Submit("playerA", fixture, Now.AddDays(9));
            Check(retry.Duplicate && retry.WeekStartUtc == first.WeekStartUtc && retry.WeeklyScore == first.WeeklyScore);
            Check(Count(cfg, "attempts") == 1 && Count(cfg, "level_bests") == 1 && Count(cfg, "weekly_totals") == 1);
            Refuse(409, () => restarted.Submit("playerA", fixture with { Score = fixture.Score + 1 }, Now));
            Check(restarted.Weekly("playerA", 7, Now).WeeklyScore == fixture.Score);
        });
        Test("one transaction rolls back when intent insertion fails", () =>
        {
            var cfg = Config("rollback"); var db = New(cfg);
            Sql(cfg, "CREATE TRIGGER fail_intent BEFORE INSERT ON attempts BEGIN SELECT RAISE(ABORT, 'test fault before commit'); END;");
            bool threw = false;
            try { db.Submit("playerA", fixture, Now); } catch (SqliteException) { threw = true; }
            Check(threw && Count(cfg, "attempts") == 0 && Count(cfg, "level_bests") == 0 && Count(cfg, "weekly_totals") == 0 && Count(cfg, "weekly_levels") == 0);
            Sql(cfg, "DROP TRIGGER fail_intent;");
            Check(db.Submit("playerA", fixture, Now).WeeklyAdded == fixture.Score);
        });
        Test("numeric level migration preserves semantic idempotency", () =>
        {
            var cfg = Config("migration"); var db = New(cfg);
            var old = other with { Attempt = other.Attempt! with { LevelId = 13 } };
            var first = db.Submit("playerA", old, Now);
            var migrated = old with { Attempt = old.Attempt! with { LevelId = 61 } };
            var retried = db.Submit("playerA", migrated, Now);
            Check(retried.Duplicate && retried.WeeklyAdded == first.WeeklyAdded);
            Check(Count(cfg, "attempts") == 1 && db.Weekly("playerA", 7, Now).WeeklyScore == other.Score);
            // Neither numeric label selects the board. The stable slot and actual seed do.
            ReplayValidator.Validate(migrated, Now);
        });
        Test("weekly reset uses receipt time and duplicate remains in original week", () =>
        {
            var db = New(Config("weekly"));
            Check(db.Submit("playerA", fixture, Now).WeekStartUtc == new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc));
            var monday = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
            Check(db.Weekly("playerA", 7, monday).WeeklyScore == 0);
            Check(db.Submit("playerA", fixture, monday).Duplicate);
            Check(db.Weekly("playerA", 7, monday).WeeklyScore == 0);
            var next = Fresh(fixture); // Old device dates do not select or backdate a league week.
            Check(db.Submit("playerA", next, monday).WeeklyAdded == fixture.Score);
            Check(db.Weekly("playerA", 7, monday).WeeklyScore == fixture.Score);
            Check(db.Best(fixture.Attempt!.StableSlot, ReplayValidator.RulesVersion, "unboosted").Known);
        });
        Test("repeat policies are explicit and database policy is pinned", () =>
        {
            var cfg = Config("repeat"); var db = New(cfg);
            db.Submit("playerA", fixture, Now);
            Check(db.Submit("playerA", Fresh(fixture), Now).WeeklyAdded == 0);
            Check(db.Submit("playerA", other, Now).WeeklyAdded == other.Score);
            Check(db.Weekly("playerA", 7, Now).WeeklyScore == fixture.Score + other.Score);
            cfg.RepeatPolicy = "FIRST_CLEAR_PER_LEVEL_EVER";
            Refuse(503, () => New(cfg));
            var firstCfg = Config("first"); firstCfg.RepeatPolicy = "FIRST_CLEAR_PER_LEVEL_EVER";
            var first = New(firstCfg); first.Submit("playerA", fixture, Now);
            Check(first.Submit("playerA", Fresh(fixture), Now.AddDays(7)).WeeklyAdded == 0);
        });
        Test("stable slots and indexed records survive more than 100 submissions", () =>
        {
            var cfg = Config("large"); var db = New(cfg);
            for (int i = 0; i < 130; i++) db.Submit("player" + i.ToString("D3"), fixture, Now);
            db.Submit("playerLast", other, Now);
            Check(Count(cfg, "attempts") == 131 && Count(cfg, "level_bests") == 131);
            Check(other.Attempt!.StableSlot == 61 && db.Best(61, ReplayValidator.RulesVersion, "unboosted").Score == other.Score);
            Check(!db.Best(13, ReplayValidator.RulesVersion, "unboosted").Known);
            var standing = db.Weekly("player129", 7, Now);
            Check(standing.Rank > 100 && standing.Neighbours.Any(row => row.IsPlayer));
            using var c = Connect(cfg); using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT player,score FROM level_bests WHERE rules='shorepop-score-collect-shell-v2' AND stable_slot=61 AND category='unboosted' ORDER BY score DESC,player LIMIT 1";
            using var r = cmd.ExecuteReader(); bool index = false;
            while (r.Read()) index |= r.GetString(3).Contains("level_top");
            Check(index);
            Refuse(422, () => db.Best(61, "wrong-rules", "unboosted"));
            Refuse(422, () => db.Best(61, ReplayValidator.RulesVersion, "boosted"));
        });
        Test("invalid replay and unverifiable entitlements leave all tables empty", () =>
        {
            var cfg = Config("invalid"); var db = New(cfg); var a = fixture.Attempt!;
            CampaignWin[] bad = [
                fixture with { Score = fixture.Score + 1 }, fixture with { Stars = fixture.Stars == 3 ? 2 : 3 },
                fixture with { Attempt = a with { Seed = a.Seed + 1 } }, fixture with { Attempt = a with { Rules = "forged" } },
                fixture with { Attempt = a with { StableSlot = 0 } }, other with { Attempt = other.Attempt! with { StableSlot = 13 } },
                fixture with { Attempt = a with { EvidenceComplete = false } }, fixture with { Attempt = a with { Actions = [] } },
                fixture with { Attempt = a with { Actions = [new() { Kind = "tap", X = -1, Y = -1 }] } },
                fixture with { Attempt = a with { Actions = [new() { Kind = "undo" }] } },
                fixture with { Attempt = a with { Actions = [new() { Kind = "breeze" }] } },
                fixture with { Attempt = a with { Actions = a.Actions!.SkipLast(1).ToList() } },
                fixture with { Attempt = a with { Boosters = 1, PaidCoins = 90 } },
                fixture with { Attempt = a with { Boosters = 1, CreditedBoosters = 1, PaidCoins = 0 } },
                fixture with { Attempt = a with { Boosters = 8 } },
                fixture with { Attempt = a with { Actions = [new() { Kind = "ability" }] } },
                fixture with { Attempt = a with { Actions = [new() { Kind = "revive" }] } },
                fixture with { Attempt = a with { Actions = [new() { Kind = "ability" }, new() { Kind = "undo" }, .. a.Actions!] } },
                fixture with { CompletedUtcTicks = a.StartedUtcTicks - 1 },
                fixture with { CompletedUtcTicks = Now.AddMinutes(6).Ticks },
                fixture with { Attempt = a with { Id = null! } }, fixture with { Attempt = null }
            ];
            foreach (var win in bad) Refuse(422, () => db.Submit("playerA", win, Now));
            Check(Count(cfg, "attempts") == 0 && Count(cfg, "level_bests") == 0 && Count(cfg, "weekly_totals") == 0);
            Check(ReplayValidator.ApplyAction(BoardModel.NewLevel(LevelCatalog.Get(1)), new() { Kind = "ability", Ability = 0, X = 0, Y = 0 }));
        });
        Test("Tailwind streak loadouts are accepted only under the Tailwind policy", () =>
        {
            // design/specs/meta-loop-r16.md: a board started on exactly one Tailwind step's free
            // boosters. The default policy still refuses it; the opt-in policy replays and accepts it.
            var tailwind = WinWith(1, WinStreak.Mask(3));
            var strict = Config("tailwind-strict"); Refuse(422, () => New(strict).Submit("playerT", tailwind, Now));
            var open = Config("tailwind-open"); open.BoosterPolicy = LeagueOptions.TailwindPolicy;
            var db = New(open);
            var accepted = db.Submit("playerT", tailwind, Now);
            Check(!accepted.Duplicate && accepted.WeeklyAdded == tailwind.Score);
            var t = tailwind.Attempt!;
            foreach (var bad in new[] {
                tailwind with { Attempt = t with { Id = Guid.NewGuid().ToString("N"), StreakBoosters = WinStreak.Mask(1) } },
                tailwind with { Attempt = t with { Id = Guid.NewGuid().ToString("N"), StreakBoosters = 2 } },
                tailwind with { Attempt = t with { Id = Guid.NewGuid().ToString("N"), Boosters = WinStreak.Mask(3) | 4, CreditedBoosters = WinStreak.Mask(3),
                    PaidCoins = BoosterCatalog.Price(BoosterType.ExtraMoves), StreakBoosters = WinStreak.Mask(3) } },
                Fresh(fixture) with { Attempt = fixture.Attempt! with { Id = Guid.NewGuid().ToString("N"), Boosters = 1, CreditedBoosters = 1, PaidCoins = 0 } } })
                Refuse(422, () => db.Submit("playerT", bad, Now));
            // An unboosted attempt serializes without the new field, so its stored payload hash is unchanged.
            Check(!System.Text.Json.JsonSerializer.Serialize(fixture, WireJson.Options).Contains("streakBoosters") &&
                System.Text.Json.JsonSerializer.Serialize(tailwind, WireJson.Options).Contains("streakBoosters"));
        });
        Test("tap undo and earned Breeze replay use actual Core", () =>
        {
            var a = fixture.Attempt!;
            var first = a.Actions![0];
            var withUndo = fixture with { Attempt = a with { Id = Guid.NewGuid().ToString("N"), Actions = [first, new() { Kind = "undo" }, .. a.Actions] } };
            ReplayValidator.Validate(withUndo, Now);
            Check(true);
            var breezeWin = Win(ReplayValidator.DefinitionForSlot(61).Id, breeze: true);
            Check(breezeWin.Attempt!.Actions!.Any(x => x.Kind == "breeze"));
            ReplayValidator.Validate(breezeWin, Now);
        });
        Test("Rocket practice replays with its authored rocket and on the board dealt before it", () =>
        {
            // Design uplift S1 #7: level 5 gained an opening row rocket under the same rules version.
            var definition = LevelCatalog.Get(LevelCatalog.RocketPracticeLevel);
            Check(definition.HasAuthoredSpecials);
            var current = Win(definition.Id);
            Check(current.Attempt!.Actions!.Count > 0);
            ReplayValidator.Validate(current, Now);
            // A client still on the earlier board, whose route opens on the plain tile that now carries the
            // rocket (there it is a legal pair; today the same tap fires the rocket), is still accepted...
            var dealtBefore = definition.WithoutAuthoredSpecials();
            var rocketCell = new BoardPosition(LevelCatalog.RocketPracticeX, LevelCatalog.RocketPracticeY);
            Check(BoardModel.NewLevel(dealtBefore).GetGroup(rocketCell.X, rocketCell.Y).Count >= 2);
            var earlier = Win(definition.Id, board: dealtBefore, first: rocketCell);
            ReplayValidator.Validate(earlier, Now);
            // ...through the fallback alone: on today's board the same actions do not reproduce that win.
            var today = BoardModel.NewLevel(definition); bool legal = true;
            foreach (var action in earlier.Attempt!.Actions!) if (legal) legal = ReplayValidator.ApplyAction(today, action);
            Check(!legal || today.Status != GameStatus.Won || today.Score != earlier.Score);
            // Either board still has to reproduce the claim exactly.
            Refuse(422, () => ReplayValidator.Validate(earlier with { Score = earlier.Score + 10 }, Now));
            Refuse(422, () => ReplayValidator.Validate(current with { Score = current.Score + 10 }, Now));
            // A level without an authored special never falls back to another board.
            Check(!LevelCatalog.Get(1).HasAuthoredSpecials);
            Refuse(422, () => ReplayValidator.Validate(fixture with { Score = fixture.Score + 10 }, Now));
        });
        await TestAsync("concurrent connections deduplicate and repeat deltas stay atomic", async () =>
        {
            var cfg = Config("concurrent"); var db = New(cfg);
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => new LeagueRepository(cfg).Submit("playerA", fixture, Now))));
            Check(results.Count(x => !x.Duplicate) == 1 && Count(cfg, "attempts") == 1);
            var distinct = Enumerable.Range(0, 12).Select(_ => Fresh(fixture)).ToArray();
            await Task.WhenAll(distinct.Select(win => Task.Run(() => new LeagueRepository(cfg).Submit("playerA", win, Now))));
            Check(Count(cfg, "attempts") == 13 && db.Weekly("playerA", 7, Now).WeeklyScore == fixture.Score);
        });
        await TestAsync("JWT signature issuer expiry project subject algorithm and JWKS cache", Authentication);
        await TestAsync("forced JWKS refresh failure remains closed until successful recovery", AuthenticationRefreshRecovery);
        await TestAsync("real HTTP unconfigured 503 and configured unauthenticated 401", HttpGates);
        await TestAsync("authenticated record HTTP binding resolves omitted category from server policy", () => RecordHttpBinding(other));
        bool postgres = await PostgresChecks(fixture, other);
        var summary = new { passed = true, scenarios, assertions, filesystem = root, noUnity = true, postgres, authentication = "Ephemeral local RSA test keys only; no real credentials, no deployed auth acceptance" };
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {scenarios} scenarios / {assertions} assertions. Real SQLite files: {root}");
    }

    static async Task Authentication()
    {
        using RSA rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "local-test-key" };
        var handler = new JsonWebTokenHandler();
        string Token(string issuer, DateTime expires, string project, string subject = "playerA", string algorithm = SecurityAlgorithms.RsaSha256)
            => handler.CreateToken(new SecurityTokenDescriptor {
                Issuer = issuer, Expires = expires, NotBefore = expires.AddHours(-1), IssuedAt = expires.AddHours(-1),
                Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("project_id", project)]),
                SigningCredentials = new SigningCredentials(key, algorithm)
            });
        var parameters = UgsAuthentication.Parameters(); parameters.IssuerSigningKey = key;
        var valid = await handler.ValidateTokenAsync(Token(UgsAuthentication.Issuer, DateTime.UtcNow.AddMinutes(20), Project), parameters);
        Check(valid.IsValid && UgsAuthentication.ValidClaims(new ClaimsPrincipal(valid.ClaimsIdentity), Project));
        foreach (var token in new[] {
            Token("https://wrong.example", DateTime.UtcNow.AddMinutes(20), Project),
            Token(UgsAuthentication.Issuer, DateTime.UtcNow.AddMinutes(-5), Project),
            Token(UgsAuthentication.Issuer, DateTime.UtcNow.AddMinutes(20), Project, algorithm: SecurityAlgorithms.RsaSha512)
        }) Check(!(await handler.ValidateTokenAsync(token, parameters)).IsValid);
        var wrongProject = await handler.ValidateTokenAsync(Token(UgsAuthentication.Issuer, DateTime.UtcNow.AddMinutes(20), Guid.NewGuid().ToString()), parameters);
        Check(wrongProject.IsValid && !UgsAuthentication.ValidClaims(new ClaimsPrincipal(wrongProject.ClaimsIdentity), Project));
        Check(!UgsAuthentication.ValidClaims(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x"), new Claim("nbf", "1")])), Project));
        Check(!UgsAuthentication.ValidClaims(new ClaimsPrincipal(valid.ClaimsIdentity), ""));
        var missingSubject = new ClaimsPrincipal(new ClaimsIdentity([new Claim("project_id", Project), new Claim("nbf", "1")]));
        Check(!UgsAuthentication.ValidClaims(missingSubject, Project));
        var duplicateProject = new ClaimsIdentity(valid.ClaimsIdentity); duplicateProject.AddClaim(new Claim("project_id", Project));
        Check(!UgsAuthentication.ValidClaims(new ClaimsPrincipal(duplicateProject), Project));
        var noExpiryHandler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        string noExpiry = noExpiryHandler.CreateToken(JsonSerializer.Serialize(new { iss = UgsAuthentication.Issuer, sub = "playerA", project_id = Project, nbf = 1 }), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        Check(!(await handler.ValidateTokenAsync(noExpiry, parameters)).IsValid);
        using RSA rogue = RSA.Create(2048);
        parameters.IssuerSigningKey = new RsaSecurityKey(rogue);
        Check(!(await handler.ValidateTokenAsync(Token(UgsAuthentication.Issuer, DateTime.UtcNow.AddMinutes(20), Project), parameters)).IsValid);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key); jwk.Alg = "RS256"; jwk.Use = "sig";
        string keys = JsonSerializer.Serialize(new { keys = new[] { jwk } });
        var transport = new FakeJwks(keys); var cache = new UgsJwksCache(new HttpClient(transport));
        Check((await cache.GetConfigurationAsync(default)).SigningKeys.Count == 1);
        Check((await cache.GetConfigurationAsync(default)).SigningKeys.Count == 1 && transport.Calls == 1);
        cache.RequestRefresh(); await cache.GetConfigurationAsync(default); Check(transport.Calls == 1);
        var unavailable = new UgsJwksCache(new HttpClient(new FakeJwks("bad", HttpStatusCode.ServiceUnavailable)));
        bool failed = false; try { await unavailable.GetConfigurationAsync(default); } catch (HttpRequestException) { failed = true; }
        Check(failed && unavailable.Unavailable);
        var timeout = new UgsJwksCache(new HttpClient(new FakeJwks("", delay: true)));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        failed = false; try { await timeout.GetConfigurationAsync(default); } catch (OperationCanceledException) { failed = true; }
        Check(failed && timer.Elapsed < TimeSpan.FromSeconds(5));
    }
    sealed class FakeJwks(string content, HttpStatusCode status = HttpStatusCode.OK, bool delay = false) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Check(request.RequestUri!.AbsoluteUri == UgsAuthentication.Jwks);
            if (delay) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(status) { Content = new StringContent(content) };
        }
    }
    static async Task HttpGates()
    {
        // Loopback-only child process; no window, credentials, UGS call, or deployment.
        foreach (bool configured in new[] { false, true })
        {
            var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            port.Start(); int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.ArgumentList.Add(typeof(LeagueRepository).Assembly.Location);
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:" + number);
            start.Environment["League__ProjectId"] = configured ? Project : "";
            start.Environment["League__DatabaseProvider"] = "sqlite";
            start.Environment["League__DatabaseUrl"] = "";
            start.Environment["League__DatabasePath"] = configured ? Path.Combine(root, "http.sqlite") : "";
            start.Environment["League__RepeatPolicy"] = configured ? "BEST_PER_LEVEL_PER_WEEK" : "UNCONFIGURED";
            start.Environment["League__BoosterPolicy"] = configured ? "UNBOOSTED_ONLY" : "UNCONFIGURED";
            start.Environment["League__OwnerDecisionReference"] = configured ? "LOCAL HTTP TEST ONLY" : "";
            using var child = System.Diagnostics.Process.Start(start)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync(); Task<string> errors = child.StandardError.ReadToEndAsync();
            bool started = false;
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + number), Timeout = TimeSpan.FromSeconds(2) };
                // A busy CI host can accept the connection before its first health response is ready.
                // Retry only startup probes, with a total deadline; endpoint assertions stay strict.
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!child.HasExited && !startup.IsCancellationRequested)
                {
                    try { using var health = await client.GetAsync("/health", startup.Token); started = true; break; }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) { }
                    if (startup.IsCancellationRequested) break;
                    await Task.Delay(100);
                }
                Check(started);
                using var readiness = await client.GetAsync("/health");
                Check((int)readiness.StatusCode == (configured ? 200 : 503));
                using var readinessBody = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
                Check(readinessBody.RootElement.GetProperty("status").GetString() == (configured ? "ready" : "unavailable"));
                Check(readinessBody.RootElement.GetProperty("storage").GetString() == "sqlite");
                using var response = await client.GetAsync("/v1/weekly?neighbours=7");
                Check((int)response.StatusCode == (configured ? 401 : 503));
                Check((await response.Content.ReadAsStringAsync()).Contains(configured ? "authentication_required" : "league_unconfigured"));
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                string childLog = await output + await errors;
                File.WriteAllText(Path.Combine(root, configured ? "http-configured.log" : "http-unconfigured.log"), childLog);
                if (!started) Console.WriteLine("HTTP startup did not become ready:\n" + childLog);
            }
        }
    }
    static CampaignWin Win(int level, bool breeze = false, LevelDefinition? board = null, BoardPosition? first = null)
    {
        var definition = board ?? LevelCatalog.Get(level);
        var live = BoardModel.NewLevel(definition); var actions = new List<ActionDto>();
        bool usedBreeze = false;
        // A forced opening tap, kept by the beam fallback below as well.
        if (first.HasValue)
        {
            Check(live.Tap(first.Value.X, first.Value.Y).Accepted);
            actions.Add(new() { Kind = "tap", X = first.Value.X, Y = first.Value.Y });
        }
        while (live.Status == GameStatus.Playing && actions.Count < 100)
        {
            if (breeze && !usedBreeze && live.CanUseBreeze)
            {
                Check(live.UseBreeze(true).Accepted); actions.Add(new() { Kind = "breeze", Right = true }); usedBreeze = true; continue;
            }
            int best = int.MinValue; BoardPosition? choice = null;
            foreach (var move in live.GetLegalMoves())
            {
                var result = live.Tap(move.X, move.Y); int value = result.ScoreDelta + result.PropsCollected * 500;
                if (value > best) { choice = move; best = value; }
                Check(live.Undo());
            }
            if (!choice.HasValue) break;
            Check(live.Tap(choice.Value.X, choice.Value.Y).Accepted);
            actions.Add(new() { Kind = "tap", X = choice.Value.X, Y = choice.Value.Y });
        }
        if (live.Status != GameStatus.Won)
        {
            var opening = breeze ? actions.TakeWhile(x => x.Kind != "breeze").Concat(actions.Where(x => x.Kind == "breeze").Take(1))
                .Select(x => x.Kind == "breeze" ? "R" : x.X + "," + x.Y).ToArray()
                : first.HasValue ? new[] { first.Value.X + "," + first.Value.Y } : Array.Empty<string>();
            var route = ScoreRoutes.Beam(definition, opening, 96, 10, 150);
            live = BoardModel.NewLevel(definition); actions.Clear();
            foreach (string action in route.Actions)
            {
                Check(ScoreRoutes.Act(live, action).Accepted);
                if (action == "R" || action == "L") actions.Add(new() { Kind = "breeze", Right = action == "R" });
                else { var xy = action.Split(','); actions.Add(new() { Kind = "tap", X = int.Parse(xy[0]), Y = int.Parse(xy[1]) }); }
            }
        }
        Check(live.Status == GameStatus.Won);
        return new() { Attempt = new Attempt {
            Id = Guid.NewGuid().ToString("N"), LevelId = level, StableSlot = LevelCatalog.SlotOf(level), Seed = definition.Seed,
            Rules = ReplayValidator.RulesVersion, EvidenceComplete = true, StartedUtcTicks = Now.AddMinutes(-5).Ticks, Actions = actions
        }, Score = live.Score, Stars = live.StarsEarned, CompletedUtcTicks = Now.AddMinutes(-1).Ticks };
    }
    /// <summary>A greedy win on a board started with a Tailwind loadout, claimed as Tailwind.</summary>
    static CampaignWin WinWith(int level, int boosters)
    {
        var definition = LevelCatalog.Get(level); var board = BoardModel.NewLevel(definition, new BoosterLoadout(boosters)); var actions = new List<ActionDto>();
        while (board.Status == GameStatus.Playing && actions.Count < 100)
        {
            int best = int.MinValue; BoardPosition? choice = null;
            foreach (var move in board.GetLegalMoves())
            {
                var result = board.Tap(move.X, move.Y); int value = result.ScoreDelta;
                if (value > best) { choice = move; best = value; }
                Check(board.Undo());
            }
            if (!choice.HasValue) break;
            Check(board.Tap(choice.Value.X, choice.Value.Y).Accepted);
            actions.Add(new() { Kind = "tap", X = choice.Value.X, Y = choice.Value.Y });
        }
        Check(board.Status == GameStatus.Won);
        return new() { Attempt = new Attempt {
            Id = Guid.NewGuid().ToString("N"), LevelId = level, StableSlot = LevelCatalog.SlotOf(level), Seed = definition.Seed,
            Boosters = boosters, CreditedBoosters = boosters, StreakBoosters = boosters, PaidCoins = 0,
            Rules = ReplayValidator.RulesVersion, EvidenceComplete = true, StartedUtcTicks = Now.AddMinutes(-5).Ticks, Actions = actions
        }, Score = board.Score, Stars = board.StarsEarned, CompletedUtcTicks = Now.AddMinutes(-1).Ticks };
    }
    static CampaignWin Fresh(CampaignWin win) => win with { Attempt = win.Attempt! with { Id = Guid.NewGuid().ToString("N") } };
    static LeagueOptions Config(string name) => new() { ProjectId = Project, DatabasePath = Path.Combine(root, name + ".sqlite"),
        RepeatPolicy = "BEST_PER_LEVEL_PER_WEEK", BoosterPolicy = "UNBOOSTED_ONLY", OwnerDecisionReference = "LOCAL TEST ONLY: not owner approval" };
    static LeagueRepository New(LeagueOptions cfg) { var db = new LeagueRepository(cfg); db.Initialize(); return db; }
    static SqliteConnection Connect(LeagueOptions cfg) { var c = new SqliteConnection("Data Source=" + cfg.DatabasePath); c.Open(); return c; }
    static long Count(LeagueOptions cfg, string table) { using var c = Connect(cfg); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM " + table; return (long)cmd.ExecuteScalar()!; }
    static void Sql(LeagueOptions cfg, string sql) { using var c = Connect(cfg); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    static void Check(bool condition) { Interlocked.Increment(ref assertions); if (!condition) throw new Exception("Assertion failed"); }
    static void Refuse(int status, Action action) { try { action(); } catch (LeagueRefusal r) { Check(r.Status == status); return; } throw new Exception("Expected refusal " + status); }
    static void Test(string name, Action action) { action(); scenarios++; Console.WriteLine("PASS " + name); }
    static async Task TestAsync(string name, Func<Task> action) { await action(); scenarios++; Console.WriteLine("PASS " + name); }
}
