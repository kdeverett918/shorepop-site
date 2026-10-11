using System;
using System.Collections.Generic;

namespace BloomHarbor
{
    public enum SpecialType { None, RocketH, RocketV, Bomb, Prism }
    public enum GameStatus { Playing, Won, Lost }
    /// <summary>
    /// Prop is the coast collect goal (design/implementation/goals-r1): the coast's own prop sits
    /// on the board and is freed when a blast clears beside it. Color, Crate and Ice are the
    /// retired counters, kept so a legacy snapshot still deserializes. Drop and Sand are the two
    /// board-mechanics-r16 goals (design/specs/board-mechanics-r16.md): bring message bottles down
    /// to the shore row, and wash every sand patch. Saves serialize these by number, so a new goal
    /// is only ever appended.
    /// </summary>
    public enum GoalType { Color, Crate, Ice, Prop, Drop, Sand }
    /// <summary>
    /// The three earned abilities (design/specs/progression-and-presentation.md, Slice B). Each is a
    /// deterministic board action that spends no move, pays no score and earns no Breeze; the stock
    /// that limits them lives in the player's save, never on the board.
    /// </summary>
    public enum AbilityType { TideSweep, SeedToss, Lantern }
    /// <summary>What a campaign level asks for besides its score target (board-mechanics-r16).
    /// Catalog data only; nothing is saved by this number.</summary>
    public enum LevelObjective { Score, Prop, Drop, Sand }

    /// <summary>One ability applied to a board, recorded in the snapshot for replay and validation.</summary>
    [Serializable]
    public sealed class AbilityUse
    {
        public AbilityType Type;
        public int X, Y;
        public AbilityUse() { }
        public AbilityUse(AbilityType type, int x, int y) { Type = type; X = x; Y = y; }
        public AbilityUse Clone() { return (AbilityUse)MemberwiseClone(); }
    }

    /// <summary>
    /// A campaign coast: one of the five chapters with its own palette intent. Colours are hex
    /// strings ("#RRGGBB") because Core has no Unity dependency; the presentation layer turns them
    /// into tints and the design tokens (design/design-tokens.json, coast_1..5) carry the same
    /// values so a drift between the two is a test failure, not a surprise on device.
    ///
    /// It is also the coast's one name (design uplift S2 #13, ART-04). The tree used to carry two
    /// sets: the round-17 world names on the map and in the catalogue, and the pre-r17 names here, in
    /// the design tokens, the board skins and the Daily Tide, so a QA receipt said "Glasshouse
    /// Garden" about a screen that read "Candy Lagoon". Now <see cref="Name"/> is the r17 world name
    /// (design/specs/worlds-r17.md) and <see cref="NameKey"/> its catalogue key; every screen, card,
    /// sentence and lane draws <see cref="DisplayName"/> (or <see cref="LevelCatalog.CoastDisplayName"/>),
    /// and no other runtime file spells a coast's name (scripts/qa/scan_coast_names.py).
    /// </summary>
    public sealed class CoastDefinition
    {
        public int Id { get; private set; }
        /// <summary>The world's name in English ("Candy Lagoon"): the identity receipts, docs and analytics
        /// readers use, and the English text of <see cref="NameKey"/>. A screen never draws it as is; it
        /// draws <see cref="DisplayName"/>.</summary>
        public string Name { get; private set; }
        /// <summary>The world's slug ("candy-lagoon"): its Resources/Worlds17 folder, its music key and the
        /// stem of <see cref="NameKey"/>. The older map meshes under Resources/Worlds and the board-skin keys
        /// keep their pre-r17 file names; those are file names, never shown.</summary>
        public string Slug { get; private set; }
        /// <summary>The name's catalogue key, world.&lt;slug&gt;.name. LevelCatalog spells each one out, because
        /// the localization audit finds keys by reading call sites.</summary>
        public string NameKey { get; private set; }
        /// <summary>The name in the live locale: what every surface draws. The English <see cref="Name"/>
        /// while no catalogue is loaded, never a bracketed key.</summary>
        public string DisplayName { get { return Strings.Has(Strings.DefaultLocale) ? Strings.Get(NameKey) : Name; } }
        /// <summary>Multiplier over the shared continuous sea backdrop at Play.</summary>
        public string SeaTint { get; private set; }
        /// <summary>The tray's glaze accent and the HUD accent for this coast.</summary>
        public string GlazeAccent { get; private set; }
        /// <summary>Two stops, top then horizon, for the Play sky and the home lighting.</summary>
        public string[] SkyGradient { get; private set; }
        /// <summary>The star-marker gold for this coast.</summary>
        public string StarGold { get; private set; }
        /// <summary>Five tile glazes in the five shared hue families, coast-shifted. Shape marks never change.</summary>
        public string[] TileColorSet { get; private set; }
        /// <summary>The home harbor lighting palette this coast equips when reached, if the player owns it.</summary>
        public int HomePalette { get; private set; }
        /// <summary>First campaign level of this coast.</summary>
        public int FirstLevel { get { return (Id - 1) * LevelCatalog.LevelsPerCoast + 1; } }

        internal CoastDefinition(int id, string slug, string name, string nameKey, string seaTint, string glazeAccent,
            string skyTop, string skyHorizon, string starGold, string[] tileColorSet, int homePalette)
        {
            // The key is spelled out at the table for the audit, so it is checked against the slug here:
            // a slug renamed without its key would otherwise draw another coast's name, or a marker.
            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(name) || nameKey != "world." + slug.Replace('-', '_') + ".name")
                throw new ArgumentException("Coast " + id + " must name itself through world.<slug>.name (slug " + slug + ", key " + nameKey + ").");
            Id = id; Slug = slug; Name = name; NameKey = nameKey; SeaTint = seaTint; GlazeAccent = glazeAccent;
            SkyGradient = new[] { skyTop, skyHorizon }; StarGold = starGold; TileColorSet = tileColorSet; HomePalette = homePalette;
        }
    }

    [Serializable]
    public struct BoardPosition : IEquatable<BoardPosition>
    {
        public int X, Y;
        public BoardPosition(int x, int y) { X = x; Y = y; }
        public bool Equals(BoardPosition other) { return X == other.X && Y == other.Y; }
        public override bool Equals(object obj) { return obj is BoardPosition && Equals((BoardPosition)obj); }
        public override int GetHashCode() { return X * 397 ^ Y; }
    }

    // Treat returned cells as read-only in presentation code. Id survives falling and Breeze.
    [Serializable]
    public sealed class Cell
    {
        public int Id, Color, CrateHp, IceHp;
        /// <summary>A stationary one-hit shell: only a direct blast can open it. Never a collect prop.</summary>
        public bool Shell;
        /// <summary>
        /// The coast collect prop this cell carries, 0 for none and otherwise the coast id whose
        /// prop it is (2 planter, 3 crate, 4 kite, 5 lantern). A prop is a one-hit crate: every
        /// structural rule - grouping, gravity anchoring, Breeze runs, the tap refusal, the prism
        /// and combo exclusions - already treats it as a crate, so only what it credits when it
        /// breaks and how it is drawn differ. Additive: a save written before the collect goal has
        /// no field here and deserializes to zero, which restores as an ordinary crate.
        /// </summary>
        public int Prop;
        public SpecialType Special;
        /// <summary>
        /// A message bottle (board-mechanics-r16): never tapped, never part of a group, never hit by
        /// a blast, but it falls with gravity like any tile and is collected the moment it settles
        /// on the bottom row. Its Color is ignored. Additive: an older save has no field and
        /// deserializes to false.
        /// </summary>
        public bool Bottle;
        /// <summary>
        /// The sand under this board position, 0 or 1. Sand belongs to the position, not to the tile:
        /// the model re-seats it on whichever tile occupies the cell after every fall and weave, and
        /// clearing that tile washes it. Additive: an older save deserializes to zero.
        /// </summary>
        public int SandHp;
        public bool IsCrate { get { return CrateHp > 0; } }
        /// <summary>True for a coast collect prop, which is always a crate as well.</summary>
        public bool IsProp { get { return Prop > 0; } }
        /// <summary>True for a tile a player can match: not a crate and not a bottle.</summary>
        public bool IsMatchable { get { return CrateHp == 0 && !Bottle; } }
        public Cell Clone() { return (Cell)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class GoalDefinition
    {
        public GoalType Type;
        public int Color, Target;
        /// <summary>On a Prop goal, the coast whose collect prop it asks for. Zero on every other type.</summary>
        public int Prop;
        public GoalDefinition() { }
        public GoalDefinition(GoalType type, int target, int color = 0, int prop = 0)
        { Type = type; Target = target; Color = color; Prop = prop; }

        /// <summary>
        /// True for the goals a level is won with alongside its score target: the coast prop, the
        /// bottles and the sand. A level carries at most one of them, so the HUD reads one chip.
        /// </summary>
        public static bool IsCollect(GoalType type)
        { return type == GoalType.Prop || type == GoalType.Drop || type == GoalType.Sand; }
    }

    [Serializable]
    public sealed class GoalProgress
    {
        public GoalType Type;
        public int Color, Target, Collected;
        /// <summary>On a Prop goal, the coast whose collect prop it asks for: the chip's icon and
        /// the band's wording both come from it, so the goal carries its own identity rather than
        /// the HUD inferring one from the level id.</summary>
        public int Prop;
        /// <summary>On a Drop goal, the bottles that have entered the board so far, including the
        /// ones it started with; refill launches the rest one at a time. Zero on every other type,
        /// and in any save written before the Drop goal existed.</summary>
        public int Spawned;
        public int Remaining { get { return Math.Max(0, Target - Collected); } }
        public bool Complete { get { return Collected >= Target; } }
        public GoalProgress Clone() { return (GoalProgress)MemberwiseClone(); }
    }

    [Serializable]
    public sealed class BlockerDefinition
    {
        public int X, Y, CrateHp, IceHp;
        public bool Shell;
        /// <summary>Nonzero makes this blocker the named coast's collect prop; it must be a one-hit crate.</summary>
        public int Prop;
        /// <summary>A message bottle starts here instead of a tile. Never on the bottom row.</summary>
        public bool Bottle;
        /// <summary>Sand under this position (0 or 1). May share the cell with ice or a bottle, never a crate.</summary>
        public int Sand;
        public BlockerDefinition() { }
        public BlockerDefinition(int x, int y, int crateHp = 0, int iceHp = 0, int prop = 0, bool shell = false)
        { X = x; Y = y; CrateHp = crateHp; IceHp = iceHp; Prop = prop; Shell = shell; }
    }

    [Serializable]
    public sealed class LevelDefinition
    {
        public int Id, Chapter, Width = 8, Height = 9, Moves = 28, ColorCount = 4;
        public uint Seed;
        /// <summary>The level's own name. Its coast's name is <see cref="LevelCatalog.CoastDisplayName"/> of
        /// <see cref="Chapter"/>; the English ChapterName copy every definition used to carry was dropped with
        /// design uplift S2 #13 (ART-04), when nothing drew it and it had drifted from the map's names.</summary>
        public string Name, Introduction;
        public bool BreezeEnabled = true;
        public int CreationTier = 3;
        // Starting lesson charge, applied only by NewLevel. Existing snapshots retain their saved charge.
        public int InitialBreezeCharge;
        /// <summary>The Big Wave (design/specs/big-wave-r17.md): every tap charges a meter and a full
        /// meter sweeps the bottom rows at the end of that move. The campaign turns it on from level 5.</summary>
        public bool WaveEnabled;
        /// <summary>Starting wave charge, below <see cref="BoardModel.WaveCapacity"/>. Practice and QA boards only.</summary>
        public int InitialWaveCharge;
        // The score that wins the level. Required > 0. On a level with a collect goal the target
        // is one of two conditions; on every other level it is the only one.
        public int TargetScore;
        // From coast 2 onward a level may also carry exactly one Prop goal: the coast's collect
        // props, all of which must be freed before the score target wins the level (goals-r1,
        // owner direction 2026-09-09). Colour, crate and ice goals stay retired - the catalogs
        // emit none and nothing reads them for winning; crates and ice pay score instead.
        public List<GoalDefinition> Goals = new List<GoalDefinition>();

        /// <summary>The level's collect goal - props, bottles or sand - or null when it is won by
        /// score alone.</summary>
        public GoalDefinition CollectGoal
        {
            get
            {
                if (Goals != null) foreach (var goal in Goals) if (goal != null && GoalDefinition.IsCollect(goal.Type)) return goal;
                return null;
            }
        }
        public List<BlockerDefinition> Blockers = new List<BlockerDefinition>();
        // Optional authored colors: flat row-major array with y=0 at the bottom. -1 uses seeded color.
        public int[] InitialColors;
        /// <summary>
        /// Specials that stand on the opening board, placed by NewLevel after the blockers and before
        /// any pre-level booster (design uplift S1 #7, GP-11: level 5, "Rocket practice", opens with a
        /// row rocket for its teaching hand to point at). Empty on almost every level. Null is read as
        /// empty, so a definition written before the field existed still builds.
        /// </summary>
        public List<SpecialPlacement> Specials = new List<SpecialPlacement>();

        /// <summary>True when the opening board carries an authored special.</summary>
        public bool HasAuthoredSpecials { get { return Specials != null && Specials.Count > 0; } }

        /// <summary>
        /// The same level without its authored specials: the board a build dealt before they were
        /// added. The league server replays a win against it as well, so a client still on the earlier
        /// board keeps scoring once the server runs this catalog. It is strictly the harder board, so
        /// accepting it grants nothing. Every other field is shared with this definition.
        /// </summary>
        public LevelDefinition WithoutAuthoredSpecials()
        {
            var copy = (LevelDefinition)MemberwiseClone();
            copy.Specials = new List<SpecialPlacement>();
            return copy;
        }

        /// <summary>Score threshold for 1, 2 or 3 stars. Stars are earned by score, never by moves left.</summary>
        public int StarScore(int stars) { return StarScoreFor(TargetScore, stars); }

        /// <summary>
        /// Points each unspent move pays on the winning move (amendment of 2026-09-08,
        /// design/specs/score-and-lives.md): the whole budget is worth half the target, so a win
        /// with half the moves unspent is a quarter of the target, one extra star. Rounded down to
        /// a multiple of ten with a floor of ten so the number reads cleanly in the result copy.
        /// </summary>
        public int MoveBonus { get { return MoveBonusFor(TargetScore, Moves); } }

        /// <summary>Same rule for callers that hold only the numbers, such as a calibration search.</summary>
        public static int MoveBonusFor(int targetScore, int moves)
        {
            if (targetScore < 1 || targetScore > MaximumTargetScore)
                throw new ArgumentOutOfRangeException("targetScore", "A level target must be 1.." + MaximumTargetScore + ".");
            if (moves < 1) throw new ArgumentOutOfRangeException("moves", "A level needs at least one move.");
            int each = targetScore / (2 * moves) / 10 * 10;
            return each < MinimumMoveBonus ? MinimumMoveBonus : each;
        }

        /// <summary>The least any unspent move pays, so a low-target level still shows a bonus.</summary>
        public const int MinimumMoveBonus = 10;

        /// <summary>Same thresholds for callers that only hold a target, such as a live board or a saved run.</summary>
        public static int StarScoreFor(int targetScore, int stars)
        {
            if (targetScore < 1 || targetScore > MaximumTargetScore)
                throw new ArgumentOutOfRangeException("targetScore", "A level target must be 1.." + MaximumTargetScore + ".");
            if (stars == 1) return targetScore;
            if (stars == 2) return (int)((long)targetScore * TwoStarNumerator / StarDenominator);
            if (stars == 3) return (int)((long)targetScore * ThreeStarNumerator / StarDenominator);
            throw new ArgumentOutOfRangeException("stars", "Only 1, 2 and 3 stars have a threshold.");
        }

        /// <summary>
        /// The star bands, as hundredths of the target: two stars at 1.18x and three at 1.35x
        /// (owner decision of 2026-09-09, design/implementation/expansion-r1). They were 1.25x and
        /// 1.50x, which only worked while a target was crossed early with most of the budget still
        /// in hand: the leftover-move bonus is half the target spread over the whole budget, so an
        /// early win paid most of the way to the third star on its own. The 300-level campaign is
        /// calibrated so the player can lose, which means crossing the target late with few moves
        /// left and almost no bonus, and at 1.50x the third star was unreachable by the best route
        /// any search could find on 136 of the 300 levels. The arithmetic runs through long so the
        /// bands can move again without moving MaximumTargetScore.
        /// </summary>
        public const int TwoStarNumerator = 118, ThreeStarNumerator = 135, StarDenominator = 100;

        // The star thresholds are computed in long, so this only has to keep a target itself, and
        // the score that beats it, inside int.
        public const int MaximumTargetScore = int.MaxValue / 3;
    }

    /// <summary>One authored special on a level's opening board (LevelDefinition.Specials).</summary>
    [Serializable]
    public sealed class SpecialPlacement
    {
        public int X, Y;
        public SpecialType Type;
        public SpecialPlacement() { }
        public SpecialPlacement(int x, int y, SpecialType type) { X = x; Y = y; Type = type; }
    }

    [Serializable]
    public sealed class CreatedSpecial
    {
        public BoardPosition Position;
        public SpecialType Type;
        public int CellId;
    }

    public sealed class MoveResult
    {
        public bool Accepted, IsBreeze, Reshuffled;
        /// <summary>True when an earned ability produced this result; Ability then names it.</summary>
        public bool IsAbility;
        public AbilityType Ability;
        public string Message = "", Combo = "";
        public Cell[,] Before, After;
        public int ScoreDelta, ClearedCount, CratesDestroyed, IceCleared, BreezeEarned;
        /// <summary>Collect props freed by this action. A subset of CratesDestroyed: a prop is a
        /// crate, pays the same score and breaks by the same rule, and additionally credits the
        /// level's Prop goal.</summary>
        public int PropsCollected;
        /// <summary>Message bottles this action brought to the shore row and collected.</summary>
        public int BottlesCollected;
        /// <summary>Sand patches this action washed: one per cleared tile that stood on sand.</summary>
        public int SandWashed;
        /// <summary>Points the unspent moves paid because this action won the level: MovesLeft
        /// times the board's MoveBonus, already included in ScoreDelta. Zero for every other action.</summary>
        public int MovesBonus;
        public GameStatus Status;
        public List<BoardPosition> ClearedPositions = new List<BoardPosition>();
        /// <summary>Where each freed collect prop stood before gravity, in board order. The HUD
        /// flies one prop from each of these cells to the goal chip.</summary>
        public List<BoardPosition> CollectedProps = new List<BoardPosition>();
        /// <summary>Where each collected bottle stood when it landed on the shore row, in the order
        /// the model collected them. The HUD flies one bottle from each of these cells to the chip.</summary>
        public List<BoardPosition> CollectedBottles = new List<BoardPosition>();
        /// <summary>Every position whose sand this action washed, in board order.</summary>
        public List<BoardPosition> WashedSand = new List<BoardPosition>();
        /// <summary>Cells an ability changed in place without clearing them: a Seed Toss conversion
        /// or a Lantern recolour. Empty for taps and Breeze. Positions are exact; no gravity follows.</summary>
        public List<BoardPosition> ChangedPositions = new List<BoardPosition>();
        public List<CreatedSpecial> CreatedSpecials = new List<CreatedSpecial>();
        public List<string> Events = new List<string>();
        /// <summary>True when this tap filled the wave meter and the Big Wave broke inside it
        /// (big-wave-r17). Everything below is zero or empty otherwise.</summary>
        public bool WaveBroke;
        /// <summary>Charge this tap added to the wave meter, before any break emptied it.</summary>
        public int WaveCharge;
        /// <summary>Points the wave paid, already included in ScoreDelta.</summary>
        public int WaveScore;
        /// <summary>Specials the wave's sweep set off.</summary>
        public int WaveFired;
        /// <summary>The settled board after the tap's own cascade and before the sweep, so the
        /// presentation can show the two phases. Null when no wave broke.</summary>
        public Cell[,] WaveFrom;
        /// <summary>Every position the wave cleared - the sweep and the blasts of the specials it
        /// fired - in <see cref="WaveFrom"/> coordinates. ClearedPositions stays the tap's own.</summary>
        public List<BoardPosition> WaveCleared = new List<BoardPosition>();
    }

    // Public fields and a flat array allow Unity JsonUtility to serialize without a Unity dependency.
    [Serializable]
    public sealed class BoardSnapshot
    {
        public int Version = 1, LevelId, Width, Height, ColorCount, MovesLeft, BreezeCharge, Score, TargetScore, NextId, RocketOrdinal;
        // Points each unspent move pays on the winning move. Additive: a save written before the
        // leftover-move bonus has no field here and deserializes to zero, which restores as "no bonus".
        public int MoveBonus;
        public uint RngState;
        public bool BreezeEnabled;
        public int CreationTierCode;
        public GameStatus Status;
        public Cell[] Cells;
        public GoalProgress[] Goals;
        // Every ability applied during this attempt, in order. A save written before abilities
        // existed has no field here and deserializes to null, which restores as "none used".
        public AbilityUse[] AbilityUses;
        // The pre-level boosters this attempt began with, as BoosterLoadout.Mask
        // (design/specs/monetization.md section 4a). The loadout is part of the run definition, so a
        // replay for league score validation reproduces the same starting board. Additive: a save
        // written before boosters has no field here and deserializes to zero, which restores as
        // "no booster"; an unboosted board writes zero as well.
        public int BoostersApplied;
        // The Big Wave (big-wave-r17). Additive: a save written before it has no fields here and
        // restores as a board without the wave.
        public bool WaveEnabled;
        public int WaveCharge, WavesBroken;
    }

    /// <summary>
    /// One coast's collect prop: the tile that sits on the board from coast two onward and is
    /// freed when a blast clears beside it or a special hits it. Core owns the identity and the
    /// copy because the campaign generator, the win rule and the result band all need it without
    /// a Unity dependency; design/assets/board-skins.json carries the matching art tokens under
    /// the same key, and the Goals QA lane asserts the two agree.
    /// </summary>
    public sealed class CollectPropDefinition
    {
        public int CoastId { get; private set; }
        /// <summary>Stable key shared with the board-skin catalog: planter, crate, kite, lantern.</summary>
        public string Key { get; private set; }
        public string Singular { get; private set; }
        public string Plural { get; private set; }
        /// <summary>What freeing one is called on this coast: freed, opened, lit.</summary>
        public string Verb { get; private set; }
        /// <summary>The HUD chip and result band heading, "Planters freed".</summary>
        public string Label { get { return char.ToUpperInvariant(Plural[0]) + Plural.Substring(1) + " " + Verb; } }
        /// <summary>The level introduction line the coast's first collect level teaches with.</summary>
        public string Introduction { get; private set; }

        internal CollectPropDefinition(int coastId, string key, string singular, string plural, string verb, string introduction)
        { CoastId = coastId; Key = key; Singular = singular; Plural = plural; Verb = verb; Introduction = introduction; }

        /// <summary>"Planters freed 4/6", the exact copy the chip and the result band read.</summary>
        public string Progress(int collected, int target)
        { return Label + " " + collected + "/" + target; }
    }

    /// <summary>
    /// The four coast collect props. Coast one is deliberately absent: Arrival Cove stays score
    /// target only (owner decision of 2026-09-07), so levels 1-12 are untouched by this mechanic.
    /// </summary>
    public static class CollectPropCatalog
    {
        private static readonly CollectPropDefinition[] entries = {
            new CollectPropDefinition(2, "planter", "planter", "planters", "freed",
                "Clay planters sit in the glasshouse. Clear tiles beside one to free it. Free them all and reach the target to win."),
            new CollectPropDefinition(3, "crate", "market crate", "market crates", "opened",
                "Market crates are stacked for the evening. Clear tiles beside one to open it. Open them all and reach the target to win."),
            new CollectPropDefinition(4, "kite", "kite", "kites", "freed",
                "Kites are tangled in the isles. Clear tiles beside one to free it. Free them all and reach the target to win."),
            new CollectPropDefinition(5, "lantern", "lantern", "lanterns", "lit",
                "Paper lanterns wait along the harbor. Clear tiles beside one to light it. Light them all and reach the target to win.")
        };
        /// <summary>The lowest coast that carries a collect prop.</summary>
        public const int FirstCoast = 2;
        public static IReadOnlyList<CollectPropDefinition> All { get { return Array.AsReadOnly(entries); } }
        /// <summary>True when this coast has a collect prop at all.</summary>
        public static bool Has(int coastId) { return coastId >= FirstCoast && coastId < FirstCoast + entries.Length; }
        public static CollectPropDefinition Get(int coastId)
        {
            if (!Has(coastId)) throw new ArgumentOutOfRangeException("coastId", "Coast " + coastId + " has no collect prop.");
            return entries[coastId - FirstCoast];
        }
    }
}
