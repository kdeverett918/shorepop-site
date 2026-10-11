using System;
using System.Collections.Generic;
using System.Globalization;
using BloomHarbor;

/// <summary>
/// Deterministic route search over the real <see cref="BoardModel"/> rules, shared by the campaign
/// and Daily Tide suites. Nothing here models a human; a recorded route only proves that the score
/// it reports is reachable from that fixed seed under the implemented rules.
/// </summary>
internal sealed class ScoreRoute
{
    public List<string> Actions = new List<string>();
    // Score after each action, so a target can be evaluated without replaying the route.
    public List<int> Cumulative = new List<int>();
    // Goal units still owed after each action, so a crossing point can be tested against the whole
    // win rule. Zero at every index on a level whose only condition is the score target.
    public List<int> GoalsLeft = new List<int>();
    public int FinalScore, MovesLeftAtEnd;
}

internal static class ScoreRoutes
{
    // A target no route can reach, so a search plays its whole move budget instead of stopping at a win.
    public const int OpenTarget = LevelDefinition.MaximumTargetScore;

    /// <summary>
    /// What one freed collect prop is worth to a search, over and above the score it pays. A level
    /// with a collect goal is not won by score alone, so a search that ranked by score alone would
    /// walk past the last planter for thirty moves and report a level unwinnable that a player
    /// clears without thinking. It is deliberately large enough to beat an ordinary group and small
    /// enough that the policy still takes the big clear when no prop is in reach. On a level with no
    /// goal every term below is zero, so those routes - every level of Arrival Cove among them -
    /// are bit-identical to the ones these searches produced before the collect goal existed.
    /// </summary>
    public const int PropBounty = 500;

    /// <summary>
    /// Board-mechanics-r16. A landed bottle and a washed sand cell earn the same bounty as a freed
    /// prop, since each is one unit of the goal. A bottle is also worth something on its way down:
    /// the search values every row still between the bottles and the shore at
    /// <see cref="BottleRowBounty"/>, counting a bottle not yet launched as a full board height, so a
    /// clear that lowers a bottle outranks an equal clear beside it. Zero on every level without
    /// bottles, so those routes are unchanged.
    /// </summary>
    public const int BottleRowBounty = 60;

    /// <summary>Rows the level's bottles still have to fall, launched or not: the Drop goal's distance to done.</summary>
    public static int BottleDistance(BoardSnapshot snapshot)
    {
        int distance = 0;
        if (snapshot.Goals != null)
            foreach (GoalProgress goal in snapshot.Goals)
                if (goal.Type == GoalType.Drop) distance += (goal.Target - goal.Spawned) * snapshot.Height;
        for (int i = 0; i < snapshot.Cells.Length; i++)
            if (snapshot.Cells[i] != null && snapshot.Cells[i].Bottle) distance += i / snapshot.Width;
        return distance;
    }

    /// <summary>Goal units one action completed: props freed, bottles landed and sand washed.</summary>
    private static int GoalUnits(MoveResult result) { return result.PropsCollected + result.BottlesCollected + result.SandWashed; }

    public static LevelDefinition WithTarget(LevelDefinition definition, int targetScore)
    {
        return new LevelDefinition
        {
            Id = definition.Id, Chapter = definition.Chapter, Width = definition.Width, Height = definition.Height,
            Moves = definition.Moves, ColorCount = definition.ColorCount, CreationTier = definition.CreationTier, Seed = definition.Seed,
            Name = definition.Name, Introduction = definition.Introduction,
            BreezeEnabled = definition.BreezeEnabled, InitialBreezeCharge = definition.InitialBreezeCharge,
            WaveEnabled = definition.WaveEnabled, InitialWaveCharge = definition.InitialWaveCharge,
            TargetScore = targetScore, Goals = definition.Goals, Blockers = definition.Blockers,
            InitialColors = definition.InitialColors,
            // Design uplift S1 #7: Rocket practice's opening rocket. A field this copy misses is a
            // board every calibration and route search silently plays without it.
            Specials = definition.Specials
        };
    }

    /// <summary>Goal units a board still owes: zero on a level won by score alone.</summary>
    public static int Owed(BoardModel board)
    {
        int owed = 0;
        foreach (GoalProgress goal in board.Goals) owed += goal.Remaining;
        return owed;
    }

    /// <summary>The same count read off a snapshot, so the beam never rebuilds a board to ask.</summary>
    public static int Owed(BoardSnapshot snapshot)
    {
        int owed = 0;
        if (snapshot.Goals != null) foreach (GoalProgress goal in snapshot.Goals) owed += goal.Remaining;
        return owed;
    }

    public static MoveResult Act(BoardModel board, string action)
    {
        if (action == "R" || action == "L") return board.UseBreeze(action == "R");
        string[] xy = action.Split(',');
        return board.Tap(int.Parse(xy[0], CultureInfo.InvariantCulture), int.Parse(xy[1], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Index of the action that wins, or -1. Winning is the whole rule: the score target and every
    /// goal, so on a collect level this is the later of reaching the target and freeing the last
    /// prop. A route recorded before goals existed carries no GoalsLeft and reads as score only.
    /// </summary>
    public static int WinIndex(ScoreRoute route, int target)
    {
        for (int i = 0; i < route.Cumulative.Count; i++)
            if (route.Cumulative[i] >= target && (route.GoalsLeft.Count <= i || route.GoalsLeft[i] == 0)) return i;
        return -1;
    }

    /// <summary>Score a route actually finishes with, given a target that ends the attempt on crossing.</summary>
    public static int Crossing(ScoreRoute route, int target)
    {
        int index = WinIndex(route, target);
        return index < 0 ? 0 : route.Cumulative[index];
    }

    /// <summary>Actions up to and including the one that wins.</summary>
    public static List<string> Truncate(ScoreRoute route, int target)
    {
        int index = WinIndex(route, target);
        var actions = new List<string>();
        for (int i = 0; i < route.Actions.Count; i++)
        {
            actions.Add(route.Actions[i]);
            if (index >= 0 && i >= index) break;
        }
        return actions;
    }

    /// <summary>
    /// The reference "decent but not optimal" policy: always take the legal tap worth the most right
    /// now - its points, plus <see cref="PropBounty"/> for each collect prop it frees - breaking ties
    /// by board order. It never plans a combination and never spends Breeze, and on a level with no
    /// collect goal it is exactly the score-greedy policy it has always been.
    /// </summary>
    public static ScoreRoute Greedy(LevelDefinition definition, string[] opening)
    {
        BoardModel board = BoardModel.NewLevel(WithTarget(definition, OpenTarget));
        var route = new ScoreRoute();
        ApplyOpening(board, route, opening);
        int limit = definition.Moves + 16;
        while (board.Status == GameStatus.Playing && route.Actions.Count < limit)
        {
            List<BoardPosition> legal = board.GetLegalMoves();
            if (legal.Count == 0) break;
            int best = int.MinValue; BoardPosition chosen = legal[0];
            foreach (BoardPosition move in legal)
            {
                MoveResult result = board.Tap(move.X, move.Y);
                int worth = result.ScoreDelta + PropBounty * GoalUnits(result);
                if (worth > best) { best = worth; chosen = move; }
                board.Undo();
            }
            Record(board, route, chosen.X + "," + chosen.Y);
        }
        route.MovesLeftAtEnd = board.MovesLeft;
        return route;
    }

    /// <summary>
    /// Score-maximizing beam search over the same rules, played against the definition's real target.
    /// Nodes are ranked by the score already banked plus the remaining moves valued at the reference
    /// policy's average move, so spending a free Breeze to build a larger group competes fairly with
    /// clearing something immediately. An attempt ends the moment it reaches the target, so the search
    /// keeps the highest-scoring **winning** node it saw: that is what the star thresholds measure.
    /// </summary>
    public static ScoreRoute Beam(LevelDefinition definition, string[] opening, int width, int branch, double moveValue)
    {
        BoardModel seed = BoardModel.NewLevel(definition);
        var seedRoute = new ScoreRoute();
        ApplyOpening(seed, seedRoute, opening);
        var beam = new List<Node> { new Node(seed.ExportSnapshot(), seedRoute) };
        ScoreRoute best = seedRoute, won = null;
        int limit = definition.Moves + 16;
        for (int depth = seedRoute.Actions.Count; depth < limit && beam.Count > 0; depth++)
        {
            var children = new List<Node>();
            foreach (Node node in beam)
            {
                BoardModel board = BoardModel.FromSnapshot(node.Snapshot);
                if (board.Status != GameStatus.Playing) continue;
                var options = new List<Option>();
                int distanceBefore = BottleDistance(node.Snapshot);
                foreach (BoardPosition move in board.GetLegalMoves())
                {
                    MoveResult result = board.Tap(move.X, move.Y);
                    BoardSnapshot after = board.ExportSnapshot();
                    // The branch cut ranks a bottle lowered as goal progress, or the tap that brings
                    // one down is pruned before the beam can see where it leads.
                    int lowered = distanceBefore - BottleDistance(after);
                    options.Add(new Option(move.X + "," + move.Y, result.ScoreDelta, GoalUnits(result), after, lowered));
                    board.Undo();
                }
                options.Sort(CompareOptions);
                if (options.Count > branch) options.RemoveRange(branch, options.Count - branch);
                if (board.CanUseBreeze)
                    foreach (bool right in new[] { true, false })
                    {
                        board.UseBreeze(right);
                        options.Add(new Option(right ? "R" : "L", 0, 0, board.ExportSnapshot(), 0));
                        board.Undo();
                    }
                foreach (Option option in options) children.Add(node.Extend(option));
            }
            if (children.Count == 0) break;
            children.Sort((a, b) => CompareNodes(a, b, moveValue));
            beam = new List<Node>();
            var seen = new HashSet<long>();
            foreach (Node child in children)
            {
                if (child.Route.FinalScore > best.FinalScore) best = child.Route;
                if (child.Status == GameStatus.Won)
                { if (won == null || child.Route.FinalScore > won.FinalScore) won = child.Route; continue; }
                if (child.Status != GameStatus.Playing) continue;
                if (beam.Count < width && seen.Add(child.Fingerprint)) beam.Add(child);
            }
        }
        return won ?? best;
    }

    /// <summary>
    /// Targets at which the reference route wins with a margin inside the band, largest first. Each
    /// candidate is a real winning point of that route, so the recorded reference route really ends
    /// there. <paramref name="minimumMargin"/> is 1.10 on a level won by score alone: the target has
    /// to sit at least a tenth under the action that crosses it, or it is a target the route was
    /// always going to sail past. On a collect level the action that ends the attempt is the one
    /// that frees the last prop, not the one that crosses the score, so that floor would be a claim
    /// about the wrong moment; pass 1.0 there and let the upper bound alone hold the target close.
    /// </summary>
    public static List<int> MarginCandidates(ScoreRoute reference, double maximumMargin, int cap, double minimumMargin = 1.10)
    {
        var candidates = new List<int>();
        for (int j = reference.Cumulative.Count - 1; j >= 0; j--)
        {
            // A target the route crosses while props are still standing does not end the attempt
            // there, so it is not a candidate: only settled crossings are real win points.
            if (reference.GoalsLeft.Count > j && reference.GoalsLeft[j] != 0) continue;
            int score = reference.Cumulative[j], previous = j == 0 ? 0 : reference.Cumulative[j - 1];
            int low = Math.Max(previous + 1, (int)Math.Ceiling(score / maximumMargin));
            int high = Math.Min(cap, (int)Math.Floor(score / minimumMargin));
            if (low > high) continue;
            foreach (int candidate in new[] { high, (low + high) / 2, low })
                if (candidate >= low && candidate <= high && !candidates.Contains(candidate)) candidates.Add(candidate);
        }
        candidates.Sort(); candidates.Reverse();
        return candidates;
    }

    private static void ApplyOpening(BoardModel board, ScoreRoute route, string[] opening)
    {
        if (opening == null) return;
        foreach (string action in opening)
        {
            if (!Act(board, action).Accepted) throw new Exception("Prescribed opening action is illegal: " + action);
            route.Actions.Add(action); route.Cumulative.Add(board.Score); route.GoalsLeft.Add(Owed(board));
            route.FinalScore = board.Score;
        }
    }

    private static void Record(BoardModel board, ScoreRoute route, string action)
    {
        if (!Act(board, action).Accepted) throw new Exception("Search selected an illegal action: " + action);
        route.Actions.Add(action); route.Cumulative.Add(board.Score); route.GoalsLeft.Add(Owed(board));
        route.FinalScore = board.Score;
        route.MovesLeftAtEnd = board.MovesLeft;
    }

    // Branch pruning has to see the bounty too, or the tap that frees the last planter is cut
    // before the beam ever ranks it.
    private static int CompareOptions(Option a, Option b)
    {
        int left = a.Delta + PropBounty * a.Props + BottleRowBounty * a.Lowered,
            right = b.Delta + PropBounty * b.Props + BottleRowBounty * b.Lowered;
        if (left != right) return right.CompareTo(left);
        if (a.Delta != b.Delta) return b.Delta.CompareTo(a.Delta);
        return string.CompareOrdinal(a.Action, b.Action);
    }

    private static int CompareNodes(Node a, Node b, double moveValue)
    {
        double left = a.Route.FinalScore + a.MovesLeft * moveValue - a.Owed * (double)PropBounty - a.Distance * (double)BottleRowBounty,
            right = b.Route.FinalScore + b.MovesLeft * moveValue - b.Owed * (double)PropBounty - b.Distance * (double)BottleRowBounty;
        if (left != right) return right.CompareTo(left);
        if (a.Route.FinalScore != b.Route.FinalScore) return b.Route.FinalScore.CompareTo(a.Route.FinalScore);
        return string.CompareOrdinal(a.Signature, b.Signature);
    }

    private sealed class Option
    {
        public readonly string Action; public readonly int Delta, Props, Lowered; public readonly BoardSnapshot Snapshot;
        public Option(string action, int delta, int props, BoardSnapshot snapshot, int lowered)
        { Action = action; Delta = delta; Props = props; Snapshot = snapshot; Lowered = lowered; }
    }

    private sealed class Node
    {
        public readonly BoardSnapshot Snapshot;
        public readonly ScoreRoute Route;
        public readonly int MovesLeft;
        /// <summary>Goal units this node still owes; zero on a level won by score alone.</summary>
        public readonly int Owed;
        /// <summary>Rows its bottles still have to fall; zero on a level without bottles.</summary>
        public readonly int Distance;
        public readonly GameStatus Status;
        public readonly long Fingerprint;
        public readonly string Signature;

        public Node(BoardSnapshot snapshot, ScoreRoute route)
        {
            Snapshot = snapshot; Route = route; MovesLeft = snapshot.MovesLeft; Status = snapshot.Status;
            Owed = ScoreRoutes.Owed(snapshot);
            Distance = BottleDistance(snapshot);
            Fingerprint = Hash(snapshot); Signature = string.Join(">", route.Actions.ToArray());
        }

        public Node Extend(Option option)
        {
            var route = new ScoreRoute
            {
                Actions = new List<string>(Route.Actions), Cumulative = new List<int>(Route.Cumulative),
                GoalsLeft = new List<int>(Route.GoalsLeft),
                FinalScore = option.Snapshot.Score, MovesLeftAtEnd = option.Snapshot.MovesLeft
            };
            route.Actions.Add(option.Action); route.Cumulative.Add(option.Snapshot.Score);
            route.GoalsLeft.Add(Owed(option.Snapshot));
            return new Node(option.Snapshot, route);
        }

        private static long Hash(BoardSnapshot snapshot)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                foreach (Cell cell in snapshot.Cells)
                {
                    hash = (hash ^ cell.Color) * 1099511628211L;
                    hash = (hash ^ (int)cell.Special) * 1099511628211L;
                    // A standing prop and an ordinary crate are different states of the search.
                    hash = (hash ^ (cell.CrateHp * 4 + cell.IceHp + cell.Prop * 16)) * 1099511628211L;
                    // Bottles and sand are state too; a level without them hashes exactly as before.
                    if (cell.Bottle || cell.SandHp > 0) hash = (hash ^ ((cell.Bottle ? 1 : 0) + cell.SandHp * 2 + 7)) * 1099511628211L;
                }
                hash = (hash ^ snapshot.Score) * 1099511628211L;
                hash = (hash ^ snapshot.MovesLeft) * 1099511628211L;
                hash = (hash ^ snapshot.BreezeCharge) * 1099511628211L;
                return (hash ^ snapshot.RngState) * 1099511628211L;
            }
        }
    }
}
