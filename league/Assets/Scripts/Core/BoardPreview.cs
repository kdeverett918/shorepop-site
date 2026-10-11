using System;
using System.Collections.Generic;

namespace BloomHarbor
{
    /// <summary>
    /// Detached tap predictions for a settled board. All effects come from the real Tap rules on
    /// a snapshot clone; the source board, random state, counters and Undo stack are never changed.
    /// Coordinates refer to the board before the tap unless explicitly called SettledPosition.
    /// </summary>
    public static class BoardPreview
    {
        public static TapPreview ForTap(BoardModel source, int x, int y)
        {
            if (source == null) throw new ArgumentNullException("source");
            BoardSnapshot before = source.ExportSnapshot();
            BoardModel simulation = BoardModel.FromSnapshot(before);
            MoveResult move = simulation.Tap(x, y);
            BoardSnapshot after = simulation.ExportSnapshot();
            var cleared = new List<PreviewClearedCell>();
            var crateHits = new List<PreviewCrateHit>();
            var created = new List<PreviewCreatedSpecial>();
            var goals = new List<PreviewGoalDelta>();

            if (move.Accepted)
            {
                var settled = new Dictionary<int, BoardPosition>();
                for (int cy = 0; cy < after.Height; cy++)
                    for (int cx = 0; cx < after.Width; cx++)
                    {
                        Cell cell = after.Cells[cy * after.Width + cx];
                        if (cell != null) settled.Add(cell.Id, new BoardPosition(cx, cy));
                    }

                foreach (BoardPosition position in move.ClearedPositions)
                {
                    Cell cell = before.Cells[position.Y * before.Width + position.X];
                    cleared.Add(new PreviewClearedCell(position, cell));
                }

                // Crates can be hit without appearing in ClearedPositions. Follow their stable
                // IDs instead of comparing a post-gravity occupant with the original position.
                for (int cy = 0; cy < before.Height; cy++)
                    for (int cx = 0; cx < before.Width; cx++)
                    {
                        Cell cell = before.Cells[cy * before.Width + cx];
                        if (cell == null || !cell.IsCrate) continue;
                        BoardPosition destination;
                        int hpAfter = settled.TryGetValue(cell.Id, out destination)
                            ? after.Cells[destination.Y * after.Width + destination.X].CrateHp : 0;
                        if (hpAfter < cell.CrateHp)
                            crateHits.Add(new PreviewCrateHit(new BoardPosition(cx, cy), cell.Id, cell.CrateHp, hpAfter, cell.Prop, cell.Shell));
                    }

                foreach (CreatedSpecial special in move.CreatedSpecials)
                {
                    BoardPosition destination;
                    BoardPosition? final = settled.TryGetValue(special.CellId, out destination)
                        ? (BoardPosition?)destination : null;
                    created.Add(new PreviewCreatedSpecial(special.Position, final, special.Type, special.CellId));
                }

                for (int i = 0; i < before.Goals.Length; i++)
                {
                    GoalProgress oldGoal = before.Goals[i], newGoal = after.Goals[i];
                    if (newGoal.Collected != oldGoal.Collected)
                        goals.Add(new PreviewGoalDelta(i, oldGoal, newGoal));
                }
            }

            return new TapPreview(new BoardPosition(x, y), move, before, after, cleared, crateHits, created, goals);
        }

        /// <summary>
        /// Detached weave prediction. Like ForTap it runs the real UseBreeze rule on a snapshot
        /// clone, so crate anchors, run boundaries and row parity are the model's, never restated
        /// here. MovedCells lists the board positions whose occupant changes; a rotation maps each
        /// run onto itself, so the same positions describe the footprint before and after.
        /// </summary>
        public static BreezePreview ForBreeze(BoardModel source, bool right)
        {
            if (source == null) throw new ArgumentNullException("source");
            BoardSnapshot before = source.ExportSnapshot();
            BoardModel simulation = BoardModel.FromSnapshot(before);
            MoveResult move = simulation.UseBreeze(right);
            BoardSnapshot after = simulation.ExportSnapshot();
            var moved = new List<BoardPosition>();

            if (move.Accepted)
                for (int cy = 0; cy < before.Height; cy++)
                    for (int cx = 0; cx < before.Width; cx++)
                    {
                        Cell was = before.Cells[cy * before.Width + cx];
                        Cell now = after.Cells[cy * after.Width + cx];
                        int previous = was == null ? -1 : was.Id, current = now == null ? -1 : now.Id;
                        if (previous != current) moved.Add(new BoardPosition(cx, cy));
                    }

            return new BreezePreview(right, move, before, after, moved);
        }

        /// <summary>
        /// Detached ability prediction. Like ForTap it runs the real UseAbility rule on a snapshot
        /// clone, so the footprint is exact and never restated from the ability's description: a
        /// Tide Sweep's cleared cells (including chained specials and destroyed crates) and crate
        /// hits, a Seed Toss's converted cell, a Lantern's actually recoloured cells. The source
        /// board, its counters, RNG and Undo stack are never touched.
        /// </summary>
        public static AbilityPreview ForAbility(BoardModel source, AbilityType type, int x, int y)
        {
            if (source == null) throw new ArgumentNullException("source");
            BoardSnapshot before = source.ExportSnapshot();
            BoardModel simulation = BoardModel.FromSnapshot(before);
            MoveResult move = simulation.UseAbility(type, x, y);
            BoardSnapshot after = simulation.ExportSnapshot();
            var cleared = new List<PreviewClearedCell>();
            var crateHits = new List<PreviewCrateHit>();
            var created = new List<PreviewCreatedSpecial>();
            var footprint = new List<BoardPosition>();

            if (move.Accepted)
            {
                var settled = new Dictionary<int, BoardPosition>();
                for (int cy = 0; cy < after.Height; cy++)
                    for (int cx = 0; cx < after.Width; cx++)
                    {
                        Cell cell = after.Cells[cy * after.Width + cx];
                        if (cell != null) settled.Add(cell.Id, new BoardPosition(cx, cy));
                    }
                foreach (BoardPosition position in move.ClearedPositions)
                {
                    cleared.Add(new PreviewClearedCell(position, before.Cells[position.Y * before.Width + position.X]));
                    footprint.Add(position);
                }
                for (int cy = 0; cy < before.Height; cy++)
                    for (int cx = 0; cx < before.Width; cx++)
                    {
                        Cell cell = before.Cells[cy * before.Width + cx];
                        if (cell == null || !cell.IsCrate) continue;
                        BoardPosition destination;
                        int hpAfter = settled.TryGetValue(cell.Id, out destination)
                            ? after.Cells[destination.Y * after.Width + destination.X].CrateHp : 0;
                        if (hpAfter < cell.CrateHp)
                        {
                            var position = new BoardPosition(cx, cy);
                            crateHits.Add(new PreviewCrateHit(position, cell.Id, cell.CrateHp, hpAfter, cell.Prop, cell.Shell));
                            if (!footprint.Contains(position)) footprint.Add(position);
                        }
                    }
                foreach (BoardPosition position in move.ChangedPositions)
                    if (!footprint.Contains(position)) footprint.Add(position);
                foreach (CreatedSpecial special in move.CreatedSpecials)
                {
                    BoardPosition destination;
                    BoardPosition? final = settled.TryGetValue(special.CellId, out destination) ? (BoardPosition?)destination : null;
                    created.Add(new PreviewCreatedSpecial(special.Position, final, special.Type, special.CellId));
                }
            }
            return new AbilityPreview(type, new BoardPosition(x, y), move, before, after, footprint, cleared, crateHits, created);
        }

        /// <summary>
        /// Group-only hint: flood fill plus creation family, without simulating gravity/refill.
        /// Rocket direction and all special effects require ForTap; no footprint is estimated here.
        /// </summary>
        public static GroupCreationHint GetGroupHint(BoardModel source, int x, int y)
        {
            if (source == null) throw new ArgumentNullException("source");
            List<BoardPosition> group = source.Status == GameStatus.Playing
                ? source.GetGroup(x, y) : new List<BoardPosition>();
            var creates = source.CreationForGroup(group.Count);
            GroupSpecialKind kind = creates == SpecialType.Prism ? GroupSpecialKind.Prism : creates == SpecialType.Bomb ? GroupSpecialKind.Bomb : creates == SpecialType.RocketH ? GroupSpecialKind.Rocket : GroupSpecialKind.None;
            return new GroupCreationHint(group, kind);
        }

        /// <summary>
        /// Finds the strongest currently legal tap using the real detached preview rules. This
        /// gives a hint system a trustworthy tactical recommendation without mutating the board.
        /// Ties are resolved by cleared cells, goal progress, then board coordinates so the hint is
        /// stable across devices and replay captures.
        /// </summary>
        public static TapPreview FindBestTap(BoardModel source)
        {
            if (source == null) throw new ArgumentNullException("source");
            TapPreview best = null;
            foreach (BoardPosition position in source.GetLegalMoves())
            {
                TapPreview candidate = ForTap(source, position.X, position.Y);
                if (!candidate.Accepted) continue;
                if (best == null || IsBetter(candidate, best)) best = candidate;
            }
            return best;
        }

        private static bool IsBetter(TapPreview candidate, TapPreview current)
        {
            // A hint should finish the level when it can. Raw score is a useful tie-breaker,
            // but choosing a larger non-terminal clear over the move that actually wins makes the
            // advice feel arbitrary on a target or collect-goal board.
            if (candidate.WinsLevel != current.WinsLevel) return candidate.WinsLevel;
            if (candidate.ProjectedStars != current.ProjectedStars) return candidate.ProjectedStars > current.ProjectedStars;
            // On a Drop board a clear that lowers a bottle is worth something before it lands, or the
            // hint would walk past the bottles for the whole budget. Equal to ScoreDelta elsewhere.
            if (candidate.HintWorth != current.HintWorth) return candidate.HintWorth > current.HintWorth;
            if (candidate.ScoreDelta != current.ScoreDelta) return candidate.ScoreDelta > current.ScoreDelta;
            if (candidate.ClearedTileCount != current.ClearedTileCount) return candidate.ClearedTileCount > current.ClearedTileCount;
            if (candidate.GoalDeltas.Count != current.GoalDeltas.Count) return candidate.GoalDeltas.Count > current.GoalDeltas.Count;
            if (candidate.CreatedSpecials.Count != current.CreatedSpecials.Count) return candidate.CreatedSpecials.Count > current.CreatedSpecials.Count;
            if (candidate.TapPosition.Y != current.TapPosition.Y) return candidate.TapPosition.Y < current.TapPosition.Y;
            return candidate.TapPosition.X < current.TapPosition.X;
        }
    }

    public enum GroupSpecialKind { None, Rocket, Bomb, Prism }

    public sealed class GroupCreationHint
    {
        public IReadOnlyList<BoardPosition> Cells { get; private set; }
        public int Count { get { return Cells.Count; } }
        public bool IsLegalGroupTap { get { return Count >= 2; } }
        public GroupSpecialKind Creates { get; private set; }

        internal GroupCreationHint(List<BoardPosition> cells, GroupSpecialKind creates)
        { Cells = cells.AsReadOnly(); Creates = creates; }
    }

    public sealed class BreezePreview
    {
        public bool Right { get; private set; }
        public bool Accepted { get; private set; }
        public string Message { get; private set; }
        public bool Reshuffled { get; private set; }
        public GameStatus Status { get; private set; }
        public int BreezeSpent { get; private set; }
        /// <summary>Board positions whose occupant changes when the weave commits.</summary>
        public IReadOnlyList<BoardPosition> MovedCells { get; private set; }
        // Detached mutable snapshot, owned by this prediction. It never aliases the source.
        public BoardSnapshot AfterSnapshot { get; private set; }

        internal BreezePreview(bool right, MoveResult move, BoardSnapshot before, BoardSnapshot after,
            List<BoardPosition> moved)
        {
            Right = right; Accepted = move.Accepted; Message = move.Message; Reshuffled = move.Reshuffled;
            Status = move.Status; BreezeSpent = before.BreezeCharge - after.BreezeCharge;
            MovedCells = moved.AsReadOnly(); AfterSnapshot = after;
        }
    }

    public sealed class AbilityPreview
    {
        public AbilityType Type { get; private set; }
        public BoardPosition Target { get; private set; }
        public bool Accepted { get; private set; }
        public string Message { get; private set; }
        public bool Reshuffled { get; private set; }
        public GameStatus Status { get; private set; }
        /// <summary>Every cell the ability visibly touches, before gravity: cleared cells, damaged
        /// crates, converted or recoloured cells. Exactly what the board's markers should ring.</summary>
        public IReadOnlyList<BoardPosition> Footprint { get; private set; }
        public IReadOnlyList<PreviewClearedCell> ClearedCells { get; private set; }
        public IReadOnlyList<PreviewCrateHit> CrateHits { get; private set; }
        public IReadOnlyList<PreviewCreatedSpecial> CreatedSpecials { get; private set; }
        public int ClearedTileCount { get; private set; }
        public int CratesDestroyed { get; private set; }
        public int IceCleared { get; private set; }
        /// <summary>Collect props this ability would free, a subset of CratesDestroyed.</summary>
        public int PropsCollected { get; private set; }
        /// <summary>Bottles the sweep's fall would bring ashore, and sand it would wash.</summary>
        public int BottlesCollected { get; private set; }
        public int SandWashed { get; private set; }
        /// <summary>Always zero: abilities pay no score and earn no Breeze. Reported so a HUD can say so.</summary>
        public int ScoreDelta { get; private set; }
        public int MovesSpent { get; private set; }
        // Detached mutable snapshot, owned by this prediction. It never aliases the source.
        public BoardSnapshot AfterSnapshot { get; private set; }

        internal AbilityPreview(AbilityType type, BoardPosition target, MoveResult move, BoardSnapshot before, BoardSnapshot after,
            List<BoardPosition> footprint, List<PreviewClearedCell> cleared, List<PreviewCrateHit> crates, List<PreviewCreatedSpecial> created)
        {
            Type = type; Target = target; Accepted = move.Accepted; Message = move.Message; Reshuffled = move.Reshuffled;
            Status = move.Status; ScoreDelta = move.ScoreDelta; MovesSpent = before.MovesLeft - after.MovesLeft;
            ClearedTileCount = move.ClearedCount; CratesDestroyed = move.CratesDestroyed; IceCleared = move.IceCleared;
            PropsCollected = move.PropsCollected; BottlesCollected = move.BottlesCollected; SandWashed = move.SandWashed;
            Footprint = footprint.AsReadOnly(); ClearedCells = cleared.AsReadOnly(); CrateHits = crates.AsReadOnly();
            CreatedSpecials = created.AsReadOnly(); AfterSnapshot = after;
        }
    }

    public sealed class TapPreview
    {
        public BoardPosition TapPosition { get; private set; }
        public bool Accepted { get; private set; }
        public string Message { get; private set; }
        public string Combo { get; private set; }
        public bool Reshuffled { get; private set; }
        public GameStatus Status { get; private set; }
        public int MovesSpent { get; private set; }
        public int BreezeEarned { get; private set; }
        public int ScoreDelta { get; private set; }
        /// <summary>Score after the predicted tap, including a terminal leftover-move bonus.</summary>
        public int ProjectedScore { get; private set; }
        /// <summary>True only when the predicted tap reaches the target and all goals.</summary>
        public bool WinsLevel { get; private set; }
        /// <summary>
        /// The stars the real board would award after this tap. A non-terminal prediction is zero;
        /// a terminal prediction uses the same score thresholds as <see cref="BoardModel.StarsEarned"/>.
        /// </summary>
        public int ProjectedStars { get; private set; }
        // Matches MoveResult: tile clears exclude destroyed crates; ClearedCells includes both.
        public int ClearedTileCount { get; private set; }
        public int CratesDestroyed { get; private set; }
        public int IceCleared { get; private set; }
        /// <summary>Collect props this tap would free, a subset of CratesDestroyed.</summary>
        public int PropsCollected { get; private set; }
        /// <summary>Where each freed collect prop stands before gravity, in the model's own order.
        /// The HUD flies one prop from each of these cells to the goal chip when the tap commits.</summary>
        public IReadOnlyList<BoardPosition> CollectedProps { get; private set; }
        /// <summary>Message bottles this tap's fall would bring to the shore row, and where each
        /// lands (always the bottom row), in the model's own order.</summary>
        public int BottlesCollected { get; private set; }
        public IReadOnlyList<BoardPosition> CollectedBottles { get; private set; }
        /// <summary>Sand this tap would wash: exactly the cleared cells that stand on sand.</summary>
        public int SandWashed { get; private set; }
        public IReadOnlyList<BoardPosition> WashedSand { get; private set; }
        /// <summary>Rows this tap brings the level's bottles closer to the shore, counting a bottle
        /// not yet launched as a full board height (so a launch reads as one row of progress and a
        /// landing as the rows it fell). Zero on a board without a Drop goal.</summary>
        public int BottleRowsLowered { get; private set; }
        /// <summary>What the hint ranks a tap by: its score plus <see cref="BottleRowWorth"/> for
        /// every bottle row it gains. Exactly <see cref="ScoreDelta"/> on a board without bottles.</summary>
        public int HintWorth { get { return ScoreDelta + BottleRowWorth * BottleRowsLowered; } }
        /// <summary>A bottle row is worth a small group to the hint, the same value the route search uses.</summary>
        public const int BottleRowWorth = 60;
        /// <summary>True when releasing this tap fills the wave meter and the Big Wave breaks inside
        /// it (big-wave-r17). ScoreDelta already includes <see cref="WaveScore"/>.</summary>
        public bool BreaksWave { get; private set; }
        public int WaveScore { get; private set; }
        /// <summary>Charge this tap would add to the wave meter.</summary>
        public int WaveCharge { get; private set; }
        public IReadOnlyList<PreviewClearedCell> ClearedCells { get; private set; }
        public IReadOnlyList<PreviewCrateHit> CrateHits { get; private set; }
        public IReadOnlyList<PreviewCreatedSpecial> CreatedSpecials { get; private set; }
        public IReadOnlyList<PreviewGoalDelta> GoalDeltas { get; private set; }
        // Detached mutable snapshot, owned by this prediction. It never aliases the source.
        public BoardSnapshot AfterSnapshot { get; private set; }

        internal TapPreview(BoardPosition position, MoveResult move, BoardSnapshot before, BoardSnapshot after,
            List<PreviewClearedCell> cleared, List<PreviewCrateHit> crates,
            List<PreviewCreatedSpecial> created, List<PreviewGoalDelta> goals)
        {
            TapPosition = position; Accepted = move.Accepted; Message = move.Message; Combo = move.Combo;
            Reshuffled = move.Reshuffled; Status = move.Status; MovesSpent = before.MovesLeft - after.MovesLeft;
            BreezeEarned = move.BreezeEarned; ScoreDelta = move.ScoreDelta; ClearedTileCount = move.ClearedCount;
            ProjectedScore = after.Score; WinsLevel = move.Status == GameStatus.Won;
            ProjectedStars = WinsLevel ? StarsFor(after.Score, after.TargetScore) : 0;
            CratesDestroyed = move.CratesDestroyed; IceCleared = move.IceCleared; PropsCollected = move.PropsCollected;
            CollectedProps = move.CollectedProps.AsReadOnly();
            BottlesCollected = move.BottlesCollected; CollectedBottles = move.CollectedBottles.AsReadOnly();
            SandWashed = move.SandWashed; WashedSand = move.WashedSand.AsReadOnly();
            BottleRowsLowered = move.Accepted ? BottleDistance(before) - BottleDistance(after) : 0;
            BreaksWave = move.WaveBroke; WaveScore = move.WaveScore; WaveCharge = move.WaveCharge;
            ClearedCells = cleared.AsReadOnly(); CrateHits = crates.AsReadOnly();
            CreatedSpecials = created.AsReadOnly(); GoalDeltas = goals.AsReadOnly(); AfterSnapshot = after;
        }

        /// <summary>Rows a snapshot's bottles still have to fall, launched or not.</summary>
        internal static int BottleDistance(BoardSnapshot snapshot)
        {
            int distance = 0;
            if (snapshot.Goals != null)
                foreach (GoalProgress goal in snapshot.Goals)
                    if (goal.Type == GoalType.Drop) distance += (goal.Target - goal.Spawned) * snapshot.Height;
            for (int i = 0; i < snapshot.Cells.Length; i++)
                if (snapshot.Cells[i] != null && snapshot.Cells[i].Bottle) distance += i / snapshot.Width;
            return distance;
        }

        private static int StarsFor(int score, int target)
        {
            if (score < target) return 0;
            if (score >= LevelDefinition.StarScoreFor(target, 3)) return 3;
            return score >= LevelDefinition.StarScoreFor(target, 2) ? 2 : 1;
        }
    }

    public sealed class PreviewClearedCell
    {
        public BoardPosition Position { get; private set; }
        public int CellId { get; private set; }
        public int Color { get; private set; }
        public SpecialType SpecialBefore { get; private set; }
        public int CrateHpBefore { get; private set; }
        public int IceHpBefore { get; private set; }
        public bool IsDestroyedCrate { get { return CrateHpBefore > 0; } }
        public bool Shell { get; private set; }
        /// <summary>The coast whose collect prop this cell carried, or 0.</summary>
        public int Prop { get; private set; }
        public bool IsProp { get { return Prop > 0; } }
        /// <summary>True when the cleared tile stood on sand, which this clear washes.</summary>
        public bool WashesSand { get; private set; }

        internal PreviewClearedCell(BoardPosition position, Cell before)
        {
            Position = position; CellId = before.Id; Color = before.Color; SpecialBefore = before.Special;
            CrateHpBefore = before.CrateHp; IceHpBefore = before.IceHp; Prop = before.Prop; Shell = before.Shell;
            WashesSand = before.SandHp > 0 && before.CrateHp == 0;
        }
    }

    public sealed class PreviewCrateHit
    {
        public BoardPosition Position { get; private set; }
        public int CellId { get; private set; }
        public int HpBefore { get; private set; }
        public int HpAfter { get; private set; }
        public bool Destroyed { get { return HpAfter == 0; } }
        /// <summary>The coast whose collect prop this crate is, or 0 for an ordinary crate.</summary>
        public int Prop { get; private set; }
        /// <summary>True when this hit frees a collect prop, which is always a single hit.</summary>
        public bool IsProp { get { return Prop > 0; } }

        public bool Shell { get; private set; }

        internal PreviewCrateHit(BoardPosition position, int cellId, int before, int after, int prop, bool shell = false)
        { Position = position; CellId = cellId; HpBefore = before; HpAfter = after; Prop = prop; Shell = shell; }
    }

    public sealed class PreviewCreatedSpecial
    {
        public BoardPosition CreatedPosition { get; private set; }
        public BoardPosition? SettledPosition { get; private set; }
        public SpecialType Type { get; private set; }
        public int CellId { get; private set; }

        internal PreviewCreatedSpecial(BoardPosition created, BoardPosition? settled, SpecialType type, int cellId)
        { CreatedPosition = created; SettledPosition = settled; Type = type; CellId = cellId; }
    }

    public sealed class PreviewGoalDelta
    {
        public int GoalIndex { get; private set; }
        public GoalType Type { get; private set; }
        public int Color { get; private set; }
        /// <summary>On a collect goal, the coast whose prop it counts.</summary>
        public int Prop { get; private set; }
        public int Target { get; private set; }
        public int CollectedBefore { get; private set; }
        public int CollectedAfter { get; private set; }
        public int CollectedDelta { get { return CollectedAfter - CollectedBefore; } }
        public int RemainingBefore { get; private set; }
        public int RemainingAfter { get; private set; }
        public int UsefulDelta { get { return RemainingBefore - RemainingAfter; } }

        internal PreviewGoalDelta(int index, GoalProgress before, GoalProgress after)
        {
            GoalIndex = index; Type = before.Type; Color = before.Color; Prop = before.Prop; Target = before.Target;
            CollectedBefore = before.Collected; CollectedAfter = after.Collected;
            RemainingBefore = before.Remaining; RemainingAfter = after.Remaining;
        }
    }
}
