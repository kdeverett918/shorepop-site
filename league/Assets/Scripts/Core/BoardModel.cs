using System;
using System.Collections.Generic;

namespace BloomHarbor
{
    /// <summary>Instant deterministic rules. Coordinates: x rightward, y upward, (0,0) bottom left.</summary>
    public sealed class BoardModel
    {
        public Cell[,] Cells { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int LevelId { get; private set; }
        public int MovesLeft { get; private set; }
        public int BreezeCharge { get; private set; }
        public int Score { get; private set; }
        /// <summary>The score that wins this level. Always greater than zero.</summary>
        public int TargetScore { get; private set; }
        /// <summary>Points each unspent move pays on the winning move (LevelDefinition.MoveBonus);
        /// zero only for a board restored from a save that predates the rule.</summary>
        public int MoveBonus { get; private set; }
        public bool BreezeEnabled { get; private set; }
        /// <summary>True when this board has the Big Wave (design/specs/big-wave-r17.md).</summary>
        public bool WaveEnabled { get; private set; }
        /// <summary>The wave meter, 0 to <see cref="WaveCapacity"/> - 1 on a settled board: a tap
        /// that fills it breaks the wave in the same action, which empties it.</summary>
        public int WaveCharge { get; private set; }
        /// <summary>Waves broken so far in this attempt.</summary>
        public int WavesBroken { get; private set; }
        int creationTierCode;
        public int CreationTier => creationTierCode == 0 ? 3 : creationTierCode - 1;
        public SpecialType CreationForGroup(int count) => count >= 9 && CreationTier >= 3 ? SpecialType.Prism :
            count >= 7 && CreationTier >= 2 ? SpecialType.Bomb : count >= 5 && CreationTier >= 1 ? SpecialType.RocketH : SpecialType.None;
        public bool CanUndo { get { return history.Count > 0; } }
        public bool CanUseBreeze { get { return Status == GameStatus.Playing && BreezeEnabled && BreezeCharge >= 20; } }
        public GameStatus Status { get; private set; }
        /// <summary>0 while Score is below the target, otherwise the highest star threshold the score meets.</summary>
        public int StarsEarned
        {
            get
            {
                if (Score < TargetScore) return 0;
                if (Score >= StarScore(3)) return 3;
                return Score >= StarScore(2) ? 2 : 1;
            }
        }
        /// <summary>Threshold for 1, 2 or 3 stars on this board, for score bars and result copy.</summary>
        public int StarScore(int stars) { return LevelDefinition.StarScoreFor(TargetScore, stars); }
        // From coast 2 onward a campaign level carries one Prop goal, the coast's collect props;
        // it is a win condition alongside the score target. Colour, crate and ice goals stay
        // retired: no catalog emits them and a legacy save only restores their display counters.
        public List<GoalProgress> Goals { get; private set; }
        /// <summary>
        /// True while every goal on this board is met, and vacuously true on a board with none.
        /// A level is won when the score reaches the target and this holds, so a board that
        /// outscores its target with props still standing keeps playing.
        /// </summary>
        public bool GoalsComplete
        {
            get { foreach (var goal in Goals) if (!goal.Complete) return false; return true; }
        }
        /// <summary>This board's collect goal - props, bottles or sand - or null when it is won by score alone.</summary>
        public GoalProgress CollectGoal
        {
            get { foreach (var goal in Goals) if (GoalDefinition.IsCollect(goal.Type)) return goal; return null; }
        }
        /// <summary>This board's Drop goal, or null.</summary>
        private GoalProgress DropGoal
        {
            get { foreach (var goal in Goals) if (goal.Type == GoalType.Drop) return goal; return null; }
        }
        /// <summary>Message bottles standing on the board right now.</summary>
        public int BottlesOnBoard
        {
            get
            {
                int count = 0;
                for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                    if (Cells[x, y] != null && Cells[x, y].Bottle) count++;
                return count;
            }
        }
        /// <summary>Board positions that still carry unwashed sand.</summary>
        public int SandOnBoard
        {
            get
            {
                int count = 0;
                for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                    if (Cells[x, y] != null && Cells[x, y].SandHp > 0) count++;
                return count;
            }
        }
        /// <summary>True when this position carries unwashed sand. Sand belongs to the position,
        /// so this is the question a floor layer asks, never the tile's own identity.</summary>
        public bool HasSand(int x, int y) { return InBounds(x, y) && Cells[x, y] != null && Cells[x, y].SandHp > 0; }
        /// <summary>Collect props still standing on the board, whatever the goal asks for.</summary>
        public int PropsOnBoard
        {
            get
            {
                int count = 0;
                for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                    if (Cells[x, y] != null && Cells[x, y].IsProp) count++;
                return count;
            }
        }
        /// <summary>Every ability applied during this attempt, in order. Undo removes the last one.</summary>
        public IReadOnlyList<AbilityUse> AbilityUses { get { return readOnlyAbilityUses; } }
        /// <summary>
        /// The most abilities one attempt can record. Stock lives in the save, not here, so the
        /// board itself never refuses on stock; this sanity ceiling only keeps snapshot counters
        /// bounded (a Tide Sweep refills cells and spends ids without spending a move).
        /// </summary>
        public const int MaximumAbilityUses = 64;
        private int colorCount, nextId, rocketOrdinal;
        // Version 2 protects shell attempts from older readers that would ignore Cell.Shell, and
        // version 3 does the same for bottles and sand (board-mechanics-r16): an older reader would
        // read a bottle as an ordinary tile and drop the sand. Version 4 protects a board with the
        // Big Wave (big-wave-r17), which an older reader would resume without its meter. Boards
        // without any of them keep 1, 2 or 3, and a version 3 board still loads, without the wave.
        private int snapshotVersion = 1;
        /// <summary>The newest snapshot version this model reads and writes.</summary>
        public const int MaximumSnapshotVersion = 4;
        /// <summary>The snapshot version a board with the Big Wave writes.</summary>
        public const int WaveSnapshotVersion = 4;
        /// <summary>
        /// The most bottles afloat at once (board-mechanics-r16). A Drop level starts with at most
        /// this many and refill launches the rest one per action while fewer are standing, so the
        /// player always has a bottle to steer and never a column of them.
        /// </summary>
        public const int MaximumBottlesAfloat = 3;
        /// <summary>The largest bottle count one Drop goal may ask for.</summary>
        public const int MaximumDropTarget = 12;
        private uint rngState;
        private readonly List<AbilityUse> abilityUses = new List<AbilityUse>();
        private readonly IReadOnlyList<AbilityUse> readOnlyAbilityUses;
        private readonly Stack<BoardSnapshot> history = new Stack<BoardSnapshot>();
        private static readonly int[] DX = { 1, 0, -1, 0 };
        private static readonly int[] DY = { 0, 1, 0, -1 };

        private BoardModel() { readOnlyAbilityUses = abilityUses.AsReadOnly(); }

        /// <summary>
        /// The boosters this attempt began with (design/specs/monetization.md section 4a). Never
        /// null; empty for a board created without one. It is carried in the snapshot so a resumed
        /// or replayed run reproduces the same starting board, which is what league score
        /// validation replays against.
        /// </summary>
        public BoosterLoadout Boosters { get; private set; }

        public static BoardModel NewLevel(LevelDefinition definition) { return NewLevel(definition, null); }

        /// <summary>
        /// A level with its pre-level boosters applied. The boosters are applied after the board is
        /// generated and before the legality sweep, and they draw from the board's own RNG, so the
        /// same seed with the same loadout is the same board every time and an empty loadout draws
        /// nothing at all: a board created without boosters is bit-identical to one created by the
        /// single-argument overload.
        /// </summary>
        public static BoardModel NewLevel(LevelDefinition definition, BoosterLoadout loadout)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            if (loadout != null && !BoosterLoadout.IsValidMask(loadout.Mask))
                throw new ArgumentException("Booster loadout carries an unknown booster.", "loadout");
            if (definition.Width < 2 || definition.Height < 2 || definition.Width > 16 || definition.Height > 16 ||
                definition.ColorCount < 2 || definition.ColorCount > 5 || definition.Moves < 1 || definition.Moves > 1000 ||
                definition.TargetScore < 1 || definition.TargetScore > LevelDefinition.MaximumTargetScore ||
                definition.Goals == null || definition.Blockers == null ||
                definition.InitialBreezeCharge < 0 || definition.InitialBreezeCharge > 20 ||
                (!definition.BreezeEnabled && definition.InitialBreezeCharge != 0) || definition.CreationTier < 0 || definition.CreationTier > 3 ||
                definition.InitialWaveCharge < 0 || definition.InitialWaveCharge >= WaveCapacity ||
                (!definition.WaveEnabled && definition.InitialWaveCharge != 0))
                throw new ArgumentException("Invalid level dimensions, colors, moves, or target score.");
            var board = new BoardModel
            {
                Width = definition.Width, Height = definition.Height, LevelId = definition.Id,
                MovesLeft = definition.Moves, colorCount = definition.ColorCount, TargetScore = definition.TargetScore,
                MoveBonus = definition.MoveBonus,
                rngState = definition.Seed == 0 ? 0x9e3779b9u : definition.Seed,
                BreezeEnabled = definition.BreezeEnabled, creationTierCode = definition.CreationTier + 1, Status = GameStatus.Playing,
                BreezeCharge = definition.InitialBreezeCharge,
                WaveEnabled = definition.WaveEnabled, WaveCharge = definition.InitialWaveCharge,
                Goals = new List<GoalProgress>(), nextId = 1,
                Boosters = loadout == null ? new BoosterLoadout() : loadout.Clone()
            };
            board.Cells = new Cell[board.Width, board.Height];
            board.rocketOrdinal = board.Next(2);
            int collectGoals = 0;
            foreach (var goal in definition.Goals)
            {
                if (goal == null || goal.Target < 1 || goal.Color < 0 || goal.Color > 4 ||
                    !Enum.IsDefined(typeof(GoalType), goal.Type) || (goal.Type == GoalType.Color && goal.Color >= board.colorCount))
                    throw new ArgumentException("Invalid goal.");
                // At most one collect goal per level, of any kind: the chip shows one count and the
                // result band reads one line, and a second would have nowhere to be read.
                if (GoalDefinition.IsCollect(goal.Type) && ++collectGoals > 1) throw new ArgumentException("A level carries at most one collect goal.");
                if ((goal.Type == GoalType.Prop) != CollectPropCatalog.Has(goal.Prop))
                    throw new ArgumentException("A collect goal names a coast that has a prop; no other goal names one.");
                if (goal.Type == GoalType.Drop && goal.Target > MaximumDropTarget)
                    throw new ArgumentException("A Drop goal asks for at most " + MaximumDropTarget + " bottles.");
                if (goal.Type == GoalType.Drop || goal.Type == GoalType.Sand) board.snapshotVersion = 3;
                board.Goals.Add(new GoalProgress { Type = goal.Type, Color = goal.Color, Target = goal.Target, Prop = goal.Prop });
            }
            if (definition.InitialColors != null && definition.InitialColors.Length != board.Width * board.Height)
                throw new ArgumentException("InitialColors must have Width * Height entries.");
            for (int y = 0; y < board.Height; y++)
                for (int x = 0; x < board.Width; x++)
                {
                    var cell = board.NewCell();
                    int authored = definition.InitialColors == null ? -1 : definition.InitialColors[y * board.Width + x];
                    if (authored < -1 || authored >= board.colorCount) throw new ArgumentException("Authored color exceeds palette.");
                    if (authored >= 0) cell.Color = authored;
                    board.Cells[x, y] = cell;
                }
            var seen = new HashSet<BoardPosition>();
            foreach (var blocker in definition.Blockers)
            {
                if (blocker == null) throw new ArgumentException("A blocker definition cannot be null.");
                var position = new BoardPosition(blocker.X, blocker.Y);
                if (!board.InBounds(blocker.X, blocker.Y) || !seen.Add(position) || blocker.CrateHp < 0 ||
                    blocker.CrateHp > 2 || blocker.IceHp < 0 || blocker.IceHp > 1 ||
                    (blocker.CrateHp > 0 && blocker.IceHp > 0)) throw new ArgumentException("Invalid or duplicate blocker.");
                // A collect prop is exactly a one-hit crate wearing a coast's identity, so the one
                // adjacency rule the player already learned on crates is what frees it.
                if (blocker.Prop < 0 || blocker.Prop > LevelCatalog.CoastCount ||
                    (blocker.Prop > 0 && (blocker.CrateHp != 1 || !CollectPropCatalog.Has(blocker.Prop))))
                    throw new ArgumentException("A collect prop must be a one-hit crate on a coast that has one.");
                if (blocker.Shell && (blocker.CrateHp != 1 || blocker.IceHp != 0 || blocker.Prop != 0))
                    throw new ArgumentException("A shell must be a one-hit neutral barrier.");
                // A bottle is a tile-sized object that falls; it carries nothing else, and it never
                // starts on the shore row, where it would be collected before the player saw it.
                if (blocker.Bottle && (blocker.CrateHp != 0 || blocker.IceHp != 0 || blocker.Prop != 0 || blocker.Shell || blocker.Y == 0))
                    throw new ArgumentException("A bottle starts alone, above the bottom row.");
                // Sand lies under a tile. A crate never moves, so sand under one could never be washed.
                if (blocker.Sand < 0 || blocker.Sand > 1 || (blocker.Sand > 0 && blocker.CrateHp > 0))
                    throw new ArgumentException("Sand is one layer deep and never lies under a crate.");
                var cell = board.Cells[blocker.X, blocker.Y];
                cell.CrateHp = blocker.CrateHp;
                cell.IceHp = blocker.IceHp;
                cell.Prop = blocker.Prop;
                cell.Shell = blocker.Shell;
                cell.Bottle = blocker.Bottle;
                cell.SandHp = blocker.Sand;
                if (cell.Shell) board.snapshotVersion = Math.Max(board.snapshotVersion, 2);
                if (cell.Bottle || cell.SandHp > 0) board.snapshotVersion = 3;
            }
            // Authored specials (design uplift S1 #7): placed on ordinary tiles before any booster, so a
            // StartRocket booster lands beside the lesson's rocket rather than on it. They draw nothing
            // from the RNG, so every other cell of the board is exactly what it was without them.
            if (definition.Specials != null)
            {
                var placed = new HashSet<BoardPosition>();
                foreach (var special in definition.Specials)
                {
                    if (special == null || !board.InBounds(special.X, special.Y) || special.Type == SpecialType.None ||
                        !Enum.IsDefined(typeof(SpecialType), special.Type) || !placed.Add(new BoardPosition(special.X, special.Y)))
                        throw new ArgumentException("Invalid or duplicate authored special.");
                    var cell = board.Cells[special.X, special.Y];
                    // A special is a tile: never on a crate, a prop, a shell, a bottle or ice.
                    if (!cell.IsMatchable || cell.IceHp > 0 || cell.Bottle)
                        throw new ArgumentException("An authored special stands on an ordinary tile.");
                    cell.Special = special.Type;
                }
            }
            // The goal can only ask for props that are really standing there, so the chip's count
            // is always reachable from the starting board rather than a debt the level cannot pay.
            var collectGoal = board.CollectGoal;
            if (collectGoal != null && collectGoal.Type == GoalType.Prop)
            {
                if (collectGoal.Target > board.PropsOnBoard)
                    throw new ArgumentException("The collect goal asks for more props than the board carries.");
                // One coast per board: the chip shows one icon, so a level cannot mix a planter
                // with a kite and leave the player guessing which one the count is about.
                for (int y = 0; y < board.Height; y++) for (int x = 0; x < board.Width; x++)
                    if (board.Cells[x, y].IsProp && board.Cells[x, y].Prop != collectGoal.Prop)
                        throw new ArgumentException("Every prop on a board belongs to the goal's own coast.");
            }
            else
                for (int y = 0; y < board.Height; y++) for (int x = 0; x < board.Width; x++)
                    if (board.Cells[x, y].IsProp)
                        throw new ArgumentException("A board with no prop goal cannot stand a collect prop.");
            // Bottles: every one on the board is owed to the goal, and refill launches the rest.
            var drop = board.DropGoal;
            int bottles = board.BottlesOnBoard;
            if (drop == null && bottles > 0) throw new ArgumentException("A board with no Drop goal cannot stand a bottle.");
            if (drop != null)
            {
                if (bottles < 1 || bottles > MaximumBottlesAfloat || bottles > drop.Target)
                    throw new ArgumentException("A Drop level starts with one to " + MaximumBottlesAfloat + " bottles, never more than it asks for.");
                // Room to play around the bottles, so a board of bottles and crates can never leave
                // the player without a matchable tile.
                int open = 0;
                for (int y = 0; y < board.Height; y++) for (int x = 0; x < board.Width; x++) if (!board.Cells[x, y].IsCrate) open++;
                if (open < MaximumBottlesAfloat + 2) throw new ArgumentException("A Drop level needs open cells around its bottles.");
                drop.Spawned = bottles;
            }
            if (collectGoal != null && collectGoal.Type == GoalType.Sand && collectGoal.Target > board.SandOnBoard)
                throw new ArgumentException("The Sand goal asks for more sand than the board carries.");
            if (board.WaveEnabled) board.snapshotVersion = WaveSnapshotVersion;
            board.ApplyBoosters();
            board.EnsureLegalMoves();
            ValidateSnapshot(board.ExportSnapshot());
            return board;
        }

        /// <summary>
        /// Puts the chosen boosters on the freshly generated board, in BoosterType order. Called
        /// once, from NewLevel, before the legality sweep. An empty loadout returns without drawing
        /// from the RNG, so an unboosted board is unchanged by this method existing.
        /// </summary>
        private void ApplyBoosters()
        {
            if (Boosters == null || Boosters.IsEmpty) return;
            if (Boosters.Has(BoosterType.StartRocket))
                PlaceBoosterSpecial(Next(2) == 0 ? SpecialType.RocketH : SpecialType.RocketV);
            if (Boosters.Has(BoosterType.StartBomb)) PlaceBoosterSpecial(SpecialType.Bomb);
            // The added moves are part of the budget from the first tap, so the leftover-move bonus
            // pays for them exactly as it pays for the level's own moves; nothing else changes.
            if (Boosters.Has(BoosterType.ExtraMoves)) MovesLeft += BoosterCatalog.ExtraMovesGranted;
        }

        /// <summary>
        /// Places one booster special on an ordinary tile chosen from the board's own RNG. Crates,
        /// collect props and cells that already carry a special are never overwritten, so a booster
        /// can neither destroy a level's authored blockers nor consume another booster's tile.
        /// A board with no ordinary tile left keeps the booster unplaced rather than breaking a rule.
        /// </summary>
        private void PlaceBoosterSpecial(SpecialType special)
        {
            var free = new List<BoardPosition>();
            var clear = new List<BoardPosition>();
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    var cell = Cells[x, y];
                    if (cell == null || !cell.IsMatchable || cell.Special != SpecialType.None) continue;
                    free.Add(new BoardPosition(x, y));
                    // An iced tile can hold a special, but one under no ice is usable on the first
                    // tap, which is what the player paid for; ice is the fallback, not the choice.
                    if (cell.IceHp == 0) clear.Add(new BoardPosition(x, y));
                }
            var candidates = clear.Count > 0 ? clear : free;
            if (candidates.Count == 0) return;
            var position = candidates[Next(candidates.Count)];
            Cells[position.X, position.Y].Special = special;
        }

        // ---- Scoring. Deterministic, documented in docs/core-contract.md, identical in BoardPreview. ----
        /// <summary>Points per tile of the tapped ordinary group, multiplied again by the group size.</summary>
        public const int GroupTileScore = 10;
        /// <summary>Points for each ordinary tile removed by a special or a combination blast.</summary>
        public const int BlastTileScore = 20;
        public const int RocketBonus = 100, BombBonus = 200, PrismBonus = 400, CombinationBonus = 600;
        public const int CrateScore = 150, IceScore = 80;
        /// <summary>
        /// Washing one sand patch pays what clearing one ice pays: both are won by clearing the tile
        /// that sits on the cell itself, never a neighbour, so they are the same effort. A bottle
        /// landing pays what a crate or a freed prop pays, the other "one goal piece done" reward.
        /// A Tide Sweep still pays nothing for either, because abilities pay no score.
        /// </summary>
        public const int SandScore = IceScore, BottleScore = CrateScore;

        // ---- The Big Wave (design/specs/big-wave-r17.md) ----
        /// <summary>Meter capacity. The reference casual player gains 7.97 per tap on average, so a
        /// wave breaks about every seven moves (measured: mean 7.45, median 7, 10th-90th pct 4-10).</summary>
        public const int WaveCapacity = 45;
        /// <summary>Charge on top of the cleared tiles for each special a tap fires, and for a combination.</summary>
        public const int WaveSpecialCharge = 4, WaveComboCharge = 8;
        /// <summary>Rows the wave sweeps, from the shore up.</summary>
        public const int WaveRows = 2;
        /// <summary>Flat points every break pays on top of the tiles it sweeps.</summary>
        public const int WaveBonus = 200;

        /// <summary>A tapped group of n ordinary tiles pays 10 * n * n. Breeze itself pays nothing.</summary>
        public static int GroupScore(int groupSize) { return GroupTileScore * groupSize * groupSize; }

        /// <summary>Flat bonus each activating special pays on top of its cleared tiles.</summary>
        public static int ActivationBonus(SpecialType special)
        {
            if (special == SpecialType.Prism) return PrismBonus;
            if (special == SpecialType.Bomb) return BombBonus;
            return special == SpecialType.None ? 0 : RocketBonus;
        }

        // Upper bound for one action on a board of this area, used for snapshot counter headroom.
        private static long MaximumMoveScore(long area)
        { return GroupTileScore * area * area + (BlastTileScore + PrismBonus + CrateScore + IceScore + SandScore + BottleScore) * area + CombinationBonus; }

        // A wave can follow any tap: a second refill of the board and a second pay-out, blasts included.
        private static long MaximumMoveScore(long area, bool wave)
        { return MaximumMoveScore(area) + (wave ? (BlastTileScore + PrismBonus + CrateScore + IceScore + SandScore + BottleScore) * area + WaveBonus : 0); }
        private static long MaximumCellsPerMove(long area, bool wave) { return wave ? area * 2 : area; }

        public List<BoardPosition> GetGroup(int x, int y)
        {
            var group = new List<BoardPosition>();
            if (!InBounds(x, y) || Cells[x, y] == null || !Cells[x, y].IsMatchable || Cells[x, y].Special != SpecialType.None)
                return group;
            int color = Cells[x, y].Color;
            var visited = new bool[Width, Height];
            group.Add(new BoardPosition(x, y)); visited[x, y] = true;
            for (int i = 0; i < group.Count; i++)
                foreach (var adjacent in Neighbors(group[i]))
                {
                    var cell = Cells[adjacent.X, adjacent.Y];
                    if (!visited[adjacent.X, adjacent.Y] && cell != null && cell.IsMatchable &&
                        cell.Special == SpecialType.None && cell.Color == color)
                    { visited[adjacent.X, adjacent.Y] = true; group.Add(adjacent); }
                }
            return group;
        }

        public List<BoardPosition> GetLegalMoves()
        {
            var moves = new List<BoardPosition>();
            if (Status != GameStatus.Playing) return moves;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    var cell = Cells[x, y];
                    if (cell == null || !cell.IsMatchable) continue;
                    if (cell.Special != SpecialType.None) { moves.Add(new BoardPosition(x, y)); continue; }
                    foreach (var position in Neighbors(new BoardPosition(x, y)))
                    {
                        var neighbor = Cells[position.X, position.Y];
                        if (neighbor != null && neighbor.IsMatchable && neighbor.Special == SpecialType.None && neighbor.Color == cell.Color)
                        { moves.Add(new BoardPosition(x, y)); break; }
                    }
                }
            return moves;
        }

        public MoveResult Tap(int x, int y)
        {
            if (Status != GameStatus.Playing) return Rejected("This level is finished.");
            if (!InBounds(x, y)) return Rejected("Choose a tile inside the board.");
            var origin = Cells[x, y];
            if (origin != null && origin.Shell)
                return Rejected("Shells need a direct rocket, bomb or Tide Sweep hit.");
            if (origin != null && origin.Bottle) return Rejected("Clear the tiles under a bottle to bring it ashore.");
            if (origin == null || origin.IsCrate) return Rejected("Clear tiles beside crates to open them.");
            var group = origin.Special == SpecialType.None ? GetGroup(x, y) : new List<BoardPosition>();
            if (origin.Special == SpecialType.None && group.Count < 2) return Rejected("Tap two or more connected tiles of one color.");

            history.Push(ExportSnapshot());
            var result = new MoveResult { Accepted = true, Before = CloneCells() };
            MovesLeft--;
            var affected = new List<BoardPosition>();
            var affectedSet = new HashSet<BoardPosition>();
            var suppressed = new HashSet<BoardPosition>();
            var source = new BoardPosition(x, y);
            SpecialType created = SpecialType.None;
            int groupSize = 0, specialBonus = 0, comboBonus = 0;
            if (origin.Special == SpecialType.None)
            {
                groupSize = group.Count;
                foreach (var position in group) AddImpact(position, affected, affectedSet);
                created = CreationForGroup(group.Count);
                if (created == SpecialType.RocketH) created = (rocketOrdinal++ % 2 == 0) ? SpecialType.RocketH : SpecialType.RocketV;
            }
            else
            {
                BoardPosition? partner = null;
                foreach (var position in Neighbors(source))
                {
                    var cell = Cells[position.X, position.Y];
                    if (cell != null && !cell.IsCrate && cell.Special != SpecialType.None) { partner = position; break; }
                }
                if (partner.HasValue)
                {
                    suppressed.Add(source); suppressed.Add(partner.Value);
                    comboBonus = CombinationBonus;
                    AddImpact(source, affected, affectedSet); AddImpact(partner.Value, affected, affectedSet);
                    ExpandCombo(source, partner.Value, affected, affectedSet, result);
                }
                else AddImpact(source, affected, affectedSet);
            }
            specialBonus = ResolveImpacts(affected, affectedSet, suppressed, result);
            if (created != SpecialType.None)
            {
                var special = new Cell { Id = nextId++, Color = origin.Color, Special = created };
                Cells[x, y] = special;
                result.CreatedSpecials.Add(new CreatedSpecial { Position = source, Type = created, CellId = special.Id });
                result.Events.Add("Created:" + created);
            }
            // Bottles land during the fall, so the fall comes before the score is added up.
            ApplyGravity(result);
            // Documented deterministic scoring. The tapped ordinary group pays 10 per tile per tile,
            // so growing a group before clearing it is always worth more than clearing it in pieces.
            result.ScoreDelta = GroupScore(groupSize) + BlastTileScore * (result.ClearedCount - groupSize) +
                specialBonus + comboBonus + CrateScore * result.CratesDestroyed + IceScore * result.IceCleared +
                SandScore * result.SandWashed + BottleScore * result.BottlesCollected;
            Score += result.ScoreDelta;
            int previousCharge = BreezeCharge;
            if (BreezeEnabled) BreezeCharge = Math.Min(20, BreezeCharge + result.ClearedCount);
            result.BreezeEarned = BreezeCharge - previousCharge;
            if (WaveEnabled)
            {
                int fired = 0;
                foreach (var e in result.Events) if (e.StartsWith("Special:", StringComparison.Ordinal)) fired++;
                result.WaveCharge = result.ClearedCount + result.CratesDestroyed + WaveSpecialCharge * fired +
                    (string.IsNullOrEmpty(result.Combo) ? 0 : WaveComboCharge);
                WaveCharge = Math.Min(WaveCapacity, WaveCharge + result.WaveCharge);
                if (WaveCharge >= WaveCapacity) BreakWave(result);
            }
            FinishResult(result);
            return result;
        }

        /// <summary>
        /// The Big Wave (big-wave-r17), after the tap's whole cascade and before the win check. The
        /// sweep clears the ordinary tiles of the bottom <see cref="WaveRows"/> rows and fires the
        /// specials there; it never strikes a crate, prop, shell or bottle, never damages the crates
        /// beside its rows and never washes sand. A fired special's own blast keeps every normal rule.
        /// Then the ordinary fall runs, which brings a bottle waiting on row 1 ashore and refills from
        /// the board's RNG, so the wave is exactly as deterministic as a tap.
        /// </summary>
        private void BreakWave(MoveResult result)
        {
            result.WaveFrom = CloneCells();
            var wave = new MoveResult();
            var affected = new List<BoardPosition>();
            var affectedSet = new HashSet<BoardPosition>();
            var gentle = new HashSet<BoardPosition>();
            for (int y = 0; y < Math.Min(WaveRows, Height); y++)
                for (int x = 0; x < Width; x++)
                {
                    var position = new BoardPosition(x, y);
                    AddImpact(position, affected, affectedSet); gentle.Add(position);
                }
            int[,] floor = FloorSand();
            int specialBonus = ResolveImpacts(affected, affectedSet, new HashSet<BoardPosition>(), wave, gentle);
            foreach (var washed in wave.WashedSand) floor[washed.X, washed.Y] = 0;
            ApplyGravity(wave, floor);
            foreach (var e in wave.Events) if (e.StartsWith("Special:", StringComparison.Ordinal)) { result.WaveFired++; result.Events.Add("Wave" + e); }
            result.WaveScore = BlastTileScore * wave.ClearedCount + specialBonus + CrateScore * wave.CratesDestroyed +
                IceScore * wave.IceCleared + SandScore * wave.SandWashed + BottleScore * wave.BottlesCollected + WaveBonus;
            result.WaveBroke = true;
            result.WaveCleared.AddRange(wave.ClearedPositions);
            result.CratesDestroyed += wave.CratesDestroyed; result.IceCleared += wave.IceCleared;
            result.PropsCollected += wave.PropsCollected; result.CollectedProps.AddRange(wave.CollectedProps);
            result.BottlesCollected += wave.BottlesCollected; result.CollectedBottles.AddRange(wave.CollectedBottles);
            result.SandWashed += wave.SandWashed; result.WashedSand.AddRange(wave.WashedSand);
            result.ScoreDelta += result.WaveScore;
            Score += result.WaveScore;
            WaveCharge = 0; WavesBroken++;
            result.Events.Add("Wave");
        }

        public MoveResult UseBreeze(bool right)
        {
            if (!CanUseBreeze) return Rejected("Clear 20 tiles to charge Breeze.");
            history.Push(ExportSnapshot());
            var result = new MoveResult { Accepted = true, IsBreeze = true, Before = CloneCells(), Message = right ? "Breeze right" : "Breeze left" };
            BreezeCharge -= 20;
            // Sand is the floor, not the tile: it stays where it lies while the rows slide over it.
            // Bottles ride the weave like any tile, which is how a Breeze can carry one sideways
            // into an emptier column.
            int[,] floor = FloorSand();
            // Weave adjacent rows in opposite directions so a full board gains new vertical connections.
            for (int y = 0; y < Height; y++)
            {
                bool rowRight = y % 2 == 0 ? right : !right;
                int start = 0;
                for (int end = 0; end <= Width; end++)
                {
                    if (end < Width && !Cells[end, y].IsCrate) continue;
                    int length = end - start;
                    if (length > 1)
                    {
                        if (rowRight)
                        {
                            var wrap = Cells[end - 1, y];
                            for (int x = end - 1; x > start; x--) Cells[x, y] = Cells[x - 1, y];
                            Cells[start, y] = wrap;
                        }
                        else
                        {
                            var wrap = Cells[start, y];
                            for (int x = start; x < end - 1; x++) Cells[x, y] = Cells[x + 1, y];
                            Cells[end - 1, y] = wrap;
                        }
                    }
                    start = end + 1;
                }
            }
            SeatSand(floor);
            result.Events.Add(right ? "Breeze:Right" : "Breeze:Left");
            FinishResult(result);
            return result;
        }

        /// <summary>
        /// Chains every impacted special once, clears the impacted tiles and damages the crates they
        /// hit or sit beside. Shared by taps and Tide Sweep so a swept row behaves exactly like a
        /// rocket's row. Returns the activation bonus the chained specials would pay; the caller
        /// decides whether anything pays (a tap does, an ability never does).
        /// </summary>
        private int ResolveImpacts(List<BoardPosition> affected, HashSet<BoardPosition> affectedSet,
            HashSet<BoardPosition> suppressed, MoveResult result, HashSet<BoardPosition> gentle = null)
        {
            int specialBonus = 0;
            // The wave's sweep is gentle: a swept position that no fired special also strikes is
            // cleared without side damage or washing its sand, and a crate there is left alone.
            HashSet<BoardPosition> struck = gentle == null ? null : new HashSet<BoardPosition>();
            // Every impacted special fires once. Deterministic insertion order avoids hash iteration dependence.
            for (int index = 0; index < affected.Count; index++)
            {
                var position = affected[index];
                var cell = Cells[position.X, position.Y];
                if (cell != null && !cell.IsCrate && cell.Special != SpecialType.None && !suppressed.Contains(position))
                {
                    if (struck == null) ExpandSpecial(position, cell.Special, affected, affectedSet);
                    else
                    {
                        var reach = new List<BoardPosition>();
                        ExpandSpecial(position, cell.Special, reach, new HashSet<BoardPosition>());
                        foreach (var hit in reach) { struck.Add(hit); AddImpact(hit, affected, affectedSet); }
                    }
                    specialBonus += ActivationBonus(cell.Special);
                    result.Events.Add("Special:" + cell.Special);
                }
            }
            var damagedCrates = new HashSet<BoardPosition>();
            foreach (var position in affected)
            {
                var cell = Cells[position.X, position.Y];
                if (cell == null) continue;
                // A bottle is never struck: a blast passes over it and it waits for the fall.
                if (cell.Bottle) continue;
                bool soft = gentle != null && gentle.Contains(position) && !struck.Contains(position);
                // Groups and color-only prisms never include a shell. A shell in affected is a
                // direct rocket/bomb/combo/Tide Sweep hit, including any chained geometric blast.
                if (cell.IsCrate) { if (!soft) damagedCrates.Add(position); continue; }
                if (soft)
                {
                    if (cell.IceHp > 0) { AddGoal(GoalType.Ice, 0, 1); result.IceCleared++; }
                    Cells[position.X, position.Y] = null;
                    result.ClearedPositions.Add(position); result.ClearedCount++;
                    continue;
                }
                foreach (var neighbor in Neighbors(position))
                    if (Cells[neighbor.X, neighbor.Y] != null && Cells[neighbor.X, neighbor.Y].IsCrate &&
                        !Cells[neighbor.X, neighbor.Y].Shell)
                        damagedCrates.Add(neighbor);
                AddGoal(GoalType.Color, cell.Color, 1);
                if (cell.IceHp > 0) { AddGoal(GoalType.Ice, 0, 1); result.IceCleared++; }
                // Clearing the tile that stands on sand washes it, whatever cleared it.
                if (cell.SandHp > 0) { AddGoal(GoalType.Sand, 0, 1); result.SandWashed++; result.WashedSand.Add(position); }
                Cells[position.X, position.Y] = null;
                result.ClearedPositions.Add(position); result.ClearedCount++;
            }
            // Iterate the board rather than the set to keep event order stable across platforms.
            for (int cy = 0; cy < Height; cy++)
                for (int cx = 0; cx < Width; cx++)
                {
                    var position = new BoardPosition(cx, cy);
                    if (!damagedCrates.Contains(position)) continue;
                    var crate = Cells[cx, cy];
                    crate.CrateHp--;
                    if (crate.CrateHp == 0)
                    {
                        bool prop = crate.IsProp;
                        Cells[cx, cy] = null; result.CratesDestroyed++;
                        result.ClearedPositions.Add(position);
                        // A prop pays exactly what a crate pays; only what it credits differs, so
                        // freeing one never changes the score a route banks.
                        if (prop) { result.PropsCollected++; result.CollectedProps.Add(position); AddGoal(GoalType.Prop, 0, 1); }
                        else AddGoal(GoalType.Crate, 0, 1);
                    }
                }
            return specialBonus;
        }

        /// <summary>
        /// Applies an earned ability at a cell. Deterministic, snapshotted for Undo like any other
        /// action, recorded in AbilityUses, and free in every board currency: no move is spent, no
        /// score is paid, no Breeze is earned. Stock is the save's concern; this never checks it.
        /// Tide Sweep clears the whole row y like a horizontal rocket (specials in it chain, crates
        /// in or beside it take one hit). Seed Toss turns the chosen ordinary tile into a seed bomb
        /// in place, keeping its id. Lantern recolours the ordinary tiles of the 3x3 around the
        /// chosen ordinary tile to its colour; crates and specials are left alone.
        /// </summary>
        public MoveResult UseAbility(AbilityType type, int x, int y)
        {
            if (Status != GameStatus.Playing) return Rejected("This level is finished.");
            if (!Enum.IsDefined(typeof(AbilityType), type)) return Rejected("Unknown ability.");
            if (!InBounds(x, y)) return Rejected("Choose a tile inside the board.");
            if (abilityUses.Count >= MaximumAbilityUses) return Rejected("No more abilities can be used this attempt.");
            long area = (long)Width * Height;
            if ((long)nextId + MaximumCellsPerMove(area, WaveEnabled) * MovesLeft + area * (MaximumAbilityUses - abilityUses.Count) > int.MaxValue)
                return Rejected("This board cannot take another ability.");
            var origin = Cells[x, y];
            if (type != AbilityType.TideSweep && (origin == null || !origin.IsMatchable || origin.Special != SpecialType.None))
                return Rejected(type == AbilityType.SeedToss ? "Toss a seed onto an ordinary tile." : "Light a lantern on an ordinary tile.");

            history.Push(ExportSnapshot());
            var result = new MoveResult { Accepted = true, IsAbility = true, Ability = type, Before = CloneCells(), Message = AbilityName(type) };
            var target = new BoardPosition(x, y);
            if (type == AbilityType.TideSweep)
            {
                var affected = new List<BoardPosition>();
                var affectedSet = new HashSet<BoardPosition>();
                for (int cx = 0; cx < Width; cx++) AddImpact(new BoardPosition(cx, y), affected, affectedSet);
                ResolveImpacts(affected, affectedSet, new HashSet<BoardPosition>(), result);
                ApplyGravity(result);
            }
            else if (type == AbilityType.SeedToss)
            {
                origin.Special = SpecialType.Bomb;
                result.ChangedPositions.Add(target);
                result.CreatedSpecials.Add(new CreatedSpecial { Position = target, Type = SpecialType.Bomb, CellId = origin.Id });
                result.Events.Add("Created:" + SpecialType.Bomb);
            }
            else
            {
                int color = origin.Color;
                for (int cy = y - 1; cy <= y + 1; cy++)
                    for (int cx = x - 1; cx <= x + 1; cx++)
                    {
                        if (!InBounds(cx, cy)) continue;
                        var cell = Cells[cx, cy];
                        if (!cell.IsMatchable || cell.Special != SpecialType.None || cell.Color == color) continue;
                        cell.Color = color;
                        result.ChangedPositions.Add(new BoardPosition(cx, cy));
                    }
            }
            abilityUses.Add(new AbilityUse(type, x, y));
            result.Events.Add("Ability:" + type);
            // Abilities pay nothing: ScoreDelta and BreezeEarned stay zero, MovesLeft is untouched.
            FinishResult(result);
            return result;
        }

        public static string AbilityName(AbilityType type)
        {
            if (type == AbilityType.TideSweep) return "Tide Sweep";
            if (type == AbilityType.SeedToss) return "Seed Toss";
            return "Lantern";
        }

        public bool Undo()
        {
            if (history.Count == 0) return false;
            Restore(history.Pop());
            return true;
        }

        public BoardSnapshot ExportSnapshot()
        {
            var snapshot = new BoardSnapshot
            {
                Version = snapshotVersion, LevelId = LevelId, Width = Width, Height = Height, ColorCount = colorCount,
                MovesLeft = MovesLeft, BreezeCharge = BreezeCharge, Score = Score, TargetScore = TargetScore,
                MoveBonus = MoveBonus,
                NextId = nextId, RocketOrdinal = rocketOrdinal, RngState = rngState,
                BreezeEnabled = BreezeEnabled, CreationTierCode = creationTierCode, Status = Status,
                Cells = new Cell[Width * Height], Goals = new GoalProgress[Goals.Count],
                AbilityUses = new AbilityUse[abilityUses.Count],
                BoostersApplied = Boosters == null ? 0 : Boosters.Mask,
                WaveEnabled = WaveEnabled, WaveCharge = WaveCharge, WavesBroken = WavesBroken
            };
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++) snapshot.Cells[y * Width + x] = Cells[x, y] == null ? null : Cells[x, y].Clone();
            for (int i = 0; i < Goals.Count; i++) snapshot.Goals[i] = Goals[i].Clone();
            for (int i = 0; i < abilityUses.Count; i++) snapshot.AbilityUses[i] = abilityUses[i].Clone();
            return snapshot;
        }

        public static BoardModel FromSnapshot(BoardSnapshot snapshot)
        {
            ValidateSnapshot(snapshot);
            var board = new BoardModel(); board.Restore(snapshot);
            if (board.Status == GameStatus.Playing && board.GetLegalMoves().Count == 0)
                throw new ArgumentException("A settled playing snapshot must contain a legal move.");
            return board;
        }

        private static void ValidateSnapshot(BoardSnapshot snapshot)
        {
            // A pre-score save carries no target. Rejecting it routes the old board through the
            // caller's existing "Resume rejected" path instead of resuming it under the new win rule.
            if (snapshot == null || snapshot.Version < 1 || snapshot.Version > MaximumSnapshotVersion || snapshot.Width < 2 || snapshot.Width > 16 ||
                snapshot.Height < 2 || snapshot.Height > 16 || snapshot.ColorCount < 2 || snapshot.ColorCount > 5 ||
                snapshot.MovesLeft < 0 || snapshot.MovesLeft > 1000 + BoosterCatalog.MaximumBoosterMoves ||
                snapshot.BreezeCharge < 0 || snapshot.BreezeCharge > 20 ||
                !BoosterLoadout.IsValidMask(snapshot.BoostersApplied) ||
                snapshot.Score < 0 || snapshot.TargetScore < 1 || snapshot.TargetScore > LevelDefinition.MaximumTargetScore ||
                snapshot.MoveBonus < 0 || snapshot.MoveBonus > LevelDefinition.MaximumTargetScore ||
                snapshot.NextId < 1 || snapshot.RocketOrdinal < 0 || snapshot.RngState == 0 ||
                (!snapshot.BreezeEnabled && snapshot.BreezeCharge != 0) || snapshot.CreationTierCode < 0 || snapshot.CreationTierCode > 4 ||
                snapshot.Cells == null || snapshot.Cells.Length != snapshot.Width * snapshot.Height ||
                !Enum.IsDefined(typeof(GameStatus), snapshot.Status))
                throw new ArgumentException("Invalid board snapshot.");
            // A full meter always breaks inside the tap that filled it, so a saved board never holds one.
            // The wave needs version 4: an older board has no wave fields and loads without it.
            if ((snapshot.WaveEnabled && snapshot.Version < WaveSnapshotVersion) || snapshot.WaveCharge < 0 || snapshot.WaveCharge >= WaveCapacity || snapshot.WavesBroken < 0 ||
                (!snapshot.WaveEnabled && (snapshot.WaveCharge != 0 || snapshot.WavesBroken != 0)))
                throw new ArgumentException("Invalid snapshot wave meter.");
            int area = snapshot.Width * snapshot.Height;
            int abilityUses = snapshot.AbilityUses == null ? 0 : snapshot.AbilityUses.Length;
            if (abilityUses > MaximumAbilityUses) throw new ArgumentException("Snapshot records too many ability uses.");
            if (snapshot.AbilityUses != null)
                foreach (var use in snapshot.AbilityUses)
                    if (use == null || !Enum.IsDefined(typeof(AbilityType), use.Type) ||
                        use.X < 0 || use.Y < 0 || use.X >= snapshot.Width || use.Y >= snapshot.Height)
                        throw new ArgumentException("Invalid snapshot ability use.");
            // Reserve headroom for every possible remaining action. These bounds stay true after a
            // legal move or ability: abilities pay no score, but a sweep refills cells without
            // spending a move, so the id reserve also covers the abilities still allowed.
            long maximumNewCells = MaximumCellsPerMove(area, snapshot.WaveEnabled) * snapshot.MovesLeft + (long)area * (MaximumAbilityUses - abilityUses);
            // The leftover-move bonus is paid at most once, on the winning move, for the moves then unspent.
            long maximumScore = MaximumMoveScore(area, snapshot.WaveEnabled) * snapshot.MovesLeft + (long)snapshot.MoveBonus * snapshot.MovesLeft;
            if ((long)snapshot.NextId + maximumNewCells > int.MaxValue ||
                (long)snapshot.Score + maximumScore > int.MaxValue ||
                (long)snapshot.RocketOrdinal + snapshot.MovesLeft > int.MaxValue ||
                (long)snapshot.WavesBroken + snapshot.MovesLeft > int.MaxValue)
                throw new ArgumentException("Snapshot counters lack headroom for remaining moves.");
            var ids = new HashSet<int>();
            int crateCount = 0, iceCount = 0, propCount = 0, bottleCount = 0, sandCount = 0;
            foreach (var cell in snapshot.Cells)
            {
                if (cell == null || cell.Id < 1 || cell.Id >= snapshot.NextId || !ids.Add(cell.Id) ||
                    cell.Color < 0 || cell.Color >= snapshot.ColorCount || cell.CrateHp < 0 || cell.CrateHp > 2 ||
                    cell.IceHp < 0 || cell.IceHp > 1 || (cell.CrateHp > 0 && (cell.IceHp > 0 || cell.Special != SpecialType.None)) ||
                    !Enum.IsDefined(typeof(SpecialType), cell.Special)) throw new ArgumentException("Invalid snapshot cell.");
                // A prop is a one-hit crate of a coast that has one, in a save exactly as on a
                // fresh board, so a doctored snapshot cannot smuggle in an uncollectable prop.
                if (cell.Prop < 0 || cell.Prop > LevelCatalog.CoastCount ||
                    (cell.Prop > 0 && (cell.CrateHp != 1 || !CollectPropCatalog.Has(cell.Prop))))
                    throw new ArgumentException("Invalid snapshot collect prop.");
                if (cell.Shell && (snapshot.Version < 2 || cell.CrateHp != 1 || cell.IceHp != 0 || cell.Prop != 0))
                    throw new ArgumentException("Invalid snapshot shell.");
                // A bottle is only ever a bottle, and a settled board never holds one on the shore
                // row: the fall that put it there collected it. Sand is one layer and never under a
                // crate. Both need version 3, so an older reader refuses them instead of misreading.
                if (cell.Bottle && (snapshot.Version < 3 || cell.CrateHp != 0 || cell.IceHp != 0 || cell.Prop != 0 ||
                    cell.Shell || cell.Special != SpecialType.None))
                    throw new ArgumentException("Invalid snapshot bottle.");
                if (cell.SandHp < 0 || cell.SandHp > 1 || (cell.SandHp > 0 && (snapshot.Version < 3 || cell.CrateHp > 0)))
                    throw new ArgumentException("Invalid snapshot sand.");
                if (cell.CrateHp > 0) crateCount++;
                if (cell.IceHp > 0) iceCount++;
                if (cell.Prop > 0) propCount++;
                if (cell.Bottle) bottleCount++;
                if (cell.SandHp > 0) sandCount++;
            }
            for (int x = 0; x < snapshot.Width; x++)
                if (snapshot.Cells[x].Bottle) throw new ArgumentException("A settled snapshot cannot hold a bottle on the shore row.");
            // Retired counters. They no longer decide winning; they are still range-checked so a
            // legacy save either restores its display numbers intact or is refused outright.
            int collectGoals = 0;
            GoalProgress dropGoal = null;
            if (snapshot.Goals != null)
                foreach (var goal in snapshot.Goals)
                {
                    if (goal == null || goal.Target < 1 || goal.Collected < 0 || goal.Spawned < 0 ||
                        (long)goal.Collected + maximumNewCells > int.MaxValue || goal.Color < 0 || goal.Color > 4 ||
                        !Enum.IsDefined(typeof(GoalType), goal.Type) || (goal.Type == GoalType.Color && goal.Color >= snapshot.ColorCount))
                        throw new ArgumentException("Invalid snapshot goal.");
                    if ((goal.Type == GoalType.Prop) != CollectPropCatalog.Has(goal.Prop))
                        throw new ArgumentException("Invalid snapshot collect goal coast.");
                    if (GoalDefinition.IsCollect(goal.Type) && ++collectGoals > 1)
                        throw new ArgumentException("A snapshot carries at most one collect goal.");
                    if ((goal.Type == GoalType.Drop || goal.Type == GoalType.Sand) && snapshot.Version < 3)
                        throw new ArgumentException("A Drop or Sand goal needs snapshot version 3.");
                    if (goal.Type != GoalType.Drop && goal.Spawned != 0)
                        throw new ArgumentException("Only a Drop goal launches bottles.");
                    if ((goal.Type == GoalType.Crate && goal.Remaining > crateCount) ||
                        (goal.Type == GoalType.Ice && goal.Remaining > iceCount) ||
                        (goal.Type == GoalType.Prop && goal.Remaining > propCount) ||
                        (goal.Type == GoalType.Sand && goal.Remaining > sandCount))
                        throw new ArgumentException("Snapshot blocker goal exceeds the remaining blockers.");
                    if (goal.Type == GoalType.Drop)
                    {
                        // Every bottle that entered is either standing on the board or collected.
                        if (goal.Target > MaximumDropTarget || goal.Spawned > goal.Target || goal.Collected > goal.Spawned ||
                            goal.Spawned - goal.Collected != bottleCount || bottleCount > MaximumBottlesAfloat)
                            throw new ArgumentException("Snapshot bottles disagree with the Drop goal.");
                        dropGoal = goal;
                    }
                }
            if (dropGoal == null && bottleCount > 0) throw new ArgumentException("A snapshot with bottles needs a Drop goal.");
            // The win rule, restated on the saved numbers: the target and every goal.
            bool goalsMet = true;
            if (snapshot.Goals != null) foreach (var goal in snapshot.Goals) if (!goal.Complete) goalsMet = false;
            bool reachedTarget = snapshot.Score >= snapshot.TargetScore && goalsMet;
            if ((snapshot.Status == GameStatus.Won) != reachedTarget ||
                (snapshot.Status == GameStatus.Playing && snapshot.MovesLeft == 0) ||
                (snapshot.Status == GameStatus.Lost && snapshot.MovesLeft != 0))
                throw new ArgumentException("Snapshot status disagrees with moves, the score target and the goals.");
        }

        private void Restore(BoardSnapshot snapshot)
        {
            snapshotVersion = snapshot.Version;
            Width = snapshot.Width; Height = snapshot.Height; LevelId = snapshot.LevelId; colorCount = snapshot.ColorCount;
            MovesLeft = snapshot.MovesLeft; BreezeCharge = snapshot.BreezeCharge; Score = snapshot.Score;
            TargetScore = snapshot.TargetScore; MoveBonus = snapshot.MoveBonus;
            nextId = snapshot.NextId; rocketOrdinal = snapshot.RocketOrdinal; rngState = snapshot.RngState;
            BreezeEnabled = snapshot.BreezeEnabled; creationTierCode = snapshot.CreationTierCode; Status = snapshot.Status;
            WaveEnabled = snapshot.WaveEnabled; WaveCharge = snapshot.WaveCharge; WavesBroken = snapshot.WavesBroken;
            Boosters = new BoosterLoadout(snapshot.BoostersApplied);
            Cells = new Cell[Width, Height]; Goals = new List<GoalProgress>(); abilityUses.Clear();
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++) Cells[x, y] = snapshot.Cells[y * Width + x].Clone();
            if (snapshot.Goals != null) foreach (var goal in snapshot.Goals) Goals.Add(goal.Clone());
            if (snapshot.AbilityUses != null) foreach (var use in snapshot.AbilityUses) abilityUses.Add(use.Clone());
        }

        private void ExpandCombo(BoardPosition first, BoardPosition second, List<BoardPosition> impacts,
            HashSet<BoardPosition> set, MoveResult result)
        {
            SpecialType a = Cells[first.X, first.Y].Special, b = Cells[second.X, second.Y].Special;
            if (a == SpecialType.Prism || b == SpecialType.Prism)
            {
                if (a == SpecialType.Prism && b == SpecialType.Prism)
                {
                    result.Combo = "Prism + Prism";
                    for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) AddImpact(new BoardPosition(x, y), impacts, set);
                }
                else
                {
                    var other = a == SpecialType.Prism ? b : a;
                    int color = MostCommonColor();
                    result.Combo = "Prism + " + other;
                    for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                    {
                        var cell = Cells[x, y];
                        if (cell.IsMatchable && cell.Color == color)
                        {
                            if (cell.Special == SpecialType.None) cell.Special = other;
                            AddImpact(new BoardPosition(x, y), impacts, set);
                        }
                    }
                }
            }
            else if (a == SpecialType.Bomb && b == SpecialType.Bomb)
            { result.Combo = "Bomb + Bomb"; AddSquare(first, 2, impacts, set); AddSquare(second, 2, impacts, set); }
            else if (a == SpecialType.Bomb || b == SpecialType.Bomb)
            {
                result.Combo = "Bomb + Rocket";
                for (int offset = -1; offset <= 1; offset++)
                {
                    for (int x = 0; x < Width; x++) AddImpact(new BoardPosition(x, first.Y + offset), impacts, set);
                    for (int y = 0; y < Height; y++) AddImpact(new BoardPosition(first.X + offset, y), impacts, set);
                }
            }
            else
            {
                result.Combo = "Rocket + Rocket";
                for (int x = 0; x < Width; x++) AddImpact(new BoardPosition(x, first.Y), impacts, set);
                for (int y = 0; y < Height; y++) AddImpact(new BoardPosition(first.X, y), impacts, set);
            }
            result.Events.Add("Combo:" + result.Combo);
        }

        private void ExpandSpecial(BoardPosition origin, SpecialType special, List<BoardPosition> impacts, HashSet<BoardPosition> set)
        {
            if (special == SpecialType.RocketH)
                for (int x = 0; x < Width; x++) AddImpact(new BoardPosition(x, origin.Y), impacts, set);
            else if (special == SpecialType.RocketV)
                for (int y = 0; y < Height; y++) AddImpact(new BoardPosition(origin.X, y), impacts, set);
            else if (special == SpecialType.Bomb) AddSquare(origin, 1, impacts, set);
            else if (special == SpecialType.Prism)
            {
                int color = MostCommonColor();
                for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                    if (Cells[x, y].IsMatchable && Cells[x, y].Color == color) AddImpact(new BoardPosition(x, y), impacts, set);
            }
        }

        private int MostCommonColor()
        {
            var counts = new int[colorCount];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                if (Cells[x, y] != null && Cells[x, y].IsMatchable) counts[Cells[x, y].Color]++;
            int best = 0;
            for (int color = 1; color < colorCount; color++) if (counts[color] > counts[best]) best = color;
            return best;
        }

        private void AddSquare(BoardPosition origin, int radius, List<BoardPosition> impacts, HashSet<BoardPosition> set)
        {
            for (int y = origin.Y - radius; y <= origin.Y + radius; y++)
                for (int x = origin.X - radius; x <= origin.X + radius; x++) AddImpact(new BoardPosition(x, y), impacts, set);
        }

        private void AddImpact(BoardPosition position, List<BoardPosition> impacts, HashSet<BoardPosition> set)
        { if (InBounds(position.X, position.Y) && set.Add(position)) impacts.Add(position); }

        /// <summary>
        /// The fall after a clear (board-mechanics-r16 order): tiles fall and refill; any bottle
        /// resting on the shore row is collected and its column falls in behind it, until none is;
        /// a new bottle may launch from the top row; and the sand is re-seated on whatever tile now
        /// covers each sandy position. Nothing draws from the RNG unless the level has a Drop goal.
        /// </summary>
        private void ApplyGravity(MoveResult result, int[,] floor = null)
        {
            // The wave passes the sand it read before sweeping, so the positions it cleared keep theirs.
            if (floor == null) floor = FloorSand();
            int firstNew = nextId;
            Fall();
            while (CollectBottles(result)) Fall();
            LaunchBottle(firstNew);
            SeatSand(floor);
        }

        /// <summary>Sand by position, read before tiles move. A cleared position reads zero: the
        /// clear washed it. Crates never stand on sand, so a destroyed crate reads zero too.</summary>
        private int[,] FloorSand()
        {
            var floor = new int[Width, Height];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                floor[x, y] = Cells[x, y] == null ? 0 : Cells[x, y].SandHp;
            return floor;
        }

        private void SeatSand(int[,] floor)
        {
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                if (Cells[x, y] != null) Cells[x, y].SandHp = floor[x, y];
        }

        /// <summary>Collects every bottle standing on the bottom row. True when one was collected.</summary>
        private bool CollectBottles(MoveResult result)
        {
            bool any = false;
            for (int x = 0; x < Width; x++)
            {
                var cell = Cells[x, 0];
                if (cell == null || !cell.Bottle) continue;
                Cells[x, 0] = null; any = true;
                result.BottlesCollected++; result.CollectedBottles.Add(new BoardPosition(x, 0));
                AddGoal(GoalType.Drop, 0, 1);
            }
            return any;
        }

        /// <summary>
        /// Launches at most one bottle per action: while the Drop goal still has bottles to send
        /// and fewer than <see cref="MaximumBottlesAfloat"/> stand on the board, one of the tiles
        /// this fall created on the top row becomes a bottle, chosen from the board's own RNG.
        /// A fall that created nothing on the top row launches nothing; a later one will.
        /// </summary>
        private void LaunchBottle(int firstNew)
        {
            var goal = DropGoal;
            if (goal == null || goal.Spawned >= goal.Target || BottlesOnBoard >= MaximumBottlesAfloat) return;
            var candidates = new List<int>();
            int top = Height - 1;
            for (int x = 0; x < Width; x++)
            {
                var cell = Cells[x, top];
                if (cell != null && cell.Id >= firstNew && cell.IsMatchable && cell.Special == SpecialType.None) candidates.Add(x);
            }
            if (candidates.Count == 0) return;
            Cells[candidates[Next(candidates.Count)], top].Bottle = true;
            goal.Spawned++;
        }

        private void Fall()
        {
            for (int x = 0; x < Width; x++)
            {
                int start = 0;
                for (int end = 0; end <= Height; end++)
                {
                    if (end < Height && (Cells[x, end] == null || !Cells[x, end].IsCrate)) continue;
                    int write = start;
                    for (int read = start; read < end; read++)
                        if (Cells[x, read] != null)
                        {
                            var cell = Cells[x, read]; Cells[x, read] = null; Cells[x, write++] = cell;
                        }
                    while (write < end) Cells[x, write++] = NewCell();
                    start = end + 1;
                }
            }
        }

        private bool EnsureLegalMoves()
        {
            if (GetLegalMoves().Count > 0) return false;
            // Bottles are never recoloured or made into a special: only matchable tiles take part.
            var free = new List<BoardPosition>();
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                if (Cells[x, y].IsMatchable) free.Add(new BoardPosition(x, y));
            for (int attempt = 0; attempt < 24; attempt++)
            {
                for (int i = free.Count - 1; i > 0; i--)
                {
                    var a = free[i]; var b = free[Next(i + 1)];
                    int color = Cells[a.X, a.Y].Color; Cells[a.X, a.Y].Color = Cells[b.X, b.Y].Color; Cells[b.X, b.Y].Color = color;
                }
                if (GetLegalMoves().Count > 0) return true;
            }
            // Pathological palette/layout fallback: visibly restore one legal action, never charge a move.
            foreach (var position in free)
                foreach (var adjacent in Neighbors(position))
                    if (Cells[adjacent.X, adjacent.Y].IsMatchable)
                    { Cells[adjacent.X, adjacent.Y].Color = Cells[position.X, position.Y].Color; return true; }
            if (free.Count > 0)
            { var first = free[0]; Cells[first.X, first.Y].Special = SpecialType.RocketH; return true; }
            throw new InvalidOperationException("A level cannot contain only crates and bottles.");
        }

        /// <summary>
        /// Second wind (monetization M2): a lost board continues with extra moves. Only a Lost
        /// board accepts it; score, target and cells are untouched, and the board is Playing again
        /// with the moves added, which the snapshot rules already allow. A board left without a
        /// legal move by its last clear is reshuffled exactly as an ordinary move would.
        /// </summary>
        public bool GrantExtraMoves(int moves)
        {
            if (moves <= 0 || Status != GameStatus.Lost) return false;
            MovesLeft += moves; Status = GameStatus.Playing;
            EnsureLegalMoves();
            return true;
        }

        /// <summary>Points the unspent moves would pay if the level were won right now.</summary>
        public int LeftoverMovesBonus(int movesLeft) { return movesLeft < 0 ? 0 : MoveBonus * movesLeft; }

        private void FinishResult(MoveResult result)
        {
            // The leftover-move bonus (amendment of 2026-09-08): the action that reaches the target
            // also pays MoveBonus for every move still unspent, before the stars are read. It is
            // part of ScoreDelta so previews report it exactly and Undo restores it with the rest.
            // Both conditions, so the bonus is paid on the action that actually wins: on a collect
            // level that is the later of reaching the target and freeing the last prop.
            bool reached = Score >= TargetScore && GoalsComplete;
            if (reached && Status == GameStatus.Playing)
            {
                result.MovesBonus = LeftoverMovesBonus(MovesLeft);
                result.ScoreDelta += result.MovesBonus;
                Score += result.MovesBonus;
                if (result.MovesBonus > 0) result.Events.Add("MovesBonus:" + result.MovesBonus);
            }
            if (result.PropsCollected > 0) result.Events.Add("Props:" + result.PropsCollected);
            if (result.BottlesCollected > 0) result.Events.Add("Bottles:" + result.BottlesCollected);
            if (result.SandWashed > 0) result.Events.Add("Sand:" + result.SandWashed);
            Status = reached ? GameStatus.Won : MovesLeft <= 0 ? GameStatus.Lost : GameStatus.Playing;
            if (Status == GameStatus.Playing) result.Reshuffled = EnsureLegalMoves();
            if (result.Reshuffled) result.Events.Add("Reshuffled");
            result.Status = Status; result.After = CloneCells();
        }

        private void AddGoal(GoalType type, int color, int amount)
        { foreach (var goal in Goals) if (goal.Type == type && (type != GoalType.Color || goal.Color == color)) goal.Collected += amount; }

        private MoveResult Rejected(string message)
        { return new MoveResult { Accepted = false, Message = message, Status = Status }; }

        private Cell[,] CloneCells()
        {
            var cells = new Cell[Width, Height];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) cells[x, y] = Cells[x, y] == null ? null : Cells[x, y].Clone();
            return cells;
        }

        private Cell NewCell() { return new Cell { Id = nextId++, Color = Next(colorCount) }; }
        private int Next(int max)
        { rngState ^= rngState << 13; rngState ^= rngState >> 17; rngState ^= rngState << 5; return (int)(rngState % (uint)max); }
        private bool InBounds(int x, int y) { return x >= 0 && y >= 0 && x < Width && y < Height; }
        private IEnumerable<BoardPosition> Neighbors(BoardPosition position)
        { for (int i = 0; i < 4; i++) if (InBounds(position.X + DX[i], position.Y + DY[i])) yield return new BoardPosition(position.X + DX[i], position.Y + DY[i]); }
    }
}
