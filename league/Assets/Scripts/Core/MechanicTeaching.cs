using System;
using System.Collections.Generic;

namespace BloomHarbor
{
    // Stable persisted bit positions. Never renumber when a lesson is added.
    public enum MechanicLesson
    {
        Match, RocketH, RocketV, Bomb, Prism, Crate, Ice, Shell, Collect, Breeze,
        RocketRocket, BombRocket, BombBomb, PrismRocket, PrismBomb, PrismPrism,
        TideSweep, SeedToss, Lantern, MakeRocket, MakeBomb, MakePrism,
        // Board-mechanics-r16, appended: message bottles and sand.
        Bottle, Sand,
        // Big-wave-r17, appended.
        Wave
    }

    public static class MechanicTeaching
    {
        public const int Count = 25;
        public const int All = (1 << Count) - 1;
        public static int Bit(MechanicLesson kind) => 1 << (int)kind;
        static readonly string[] titleKeys = { Strings.Key("mechanic.Match.title"), Strings.Key("mechanic.RocketH.title"), Strings.Key("mechanic.RocketV.title"), Strings.Key("mechanic.Bomb.title"), Strings.Key("mechanic.Prism.title"), Strings.Key("mechanic.Crate.title"), Strings.Key("mechanic.Ice.title"), Strings.Key("mechanic.Shell.title"), Strings.Key("mechanic.Collect.title"), Strings.Key("mechanic.Breeze.title"), Strings.Key("mechanic.RocketRocket.title"), Strings.Key("mechanic.BombRocket.title"), Strings.Key("mechanic.BombBomb.title"), Strings.Key("mechanic.PrismRocket.title"), Strings.Key("mechanic.PrismBomb.title"), Strings.Key("mechanic.PrismPrism.title"), Strings.Key("mechanic.TideSweep.title"), Strings.Key("mechanic.SeedToss.title"), Strings.Key("mechanic.Lantern.title"), Strings.Key("mechanic.MakeRocket.title"), Strings.Key("mechanic.MakeBomb.title"), Strings.Key("mechanic.MakePrism.title"), Strings.Key("mechanic.Bottle.title"), Strings.Key("mechanic.Sand.title"), Strings.Key("mechanic.Wave.title") };
        static readonly string[] copyKeys = { Strings.Key("mechanic.Match.copy"), Strings.Key("mechanic.RocketH.copy"), Strings.Key("mechanic.RocketV.copy"), Strings.Key("mechanic.Bomb.copy"), Strings.Key("mechanic.Prism.copy"), Strings.Key("mechanic.Crate.copy"), Strings.Key("mechanic.Ice.copy"), Strings.Key("mechanic.Shell.copy"), Strings.Key("mechanic.Collect.copy"), Strings.Key("mechanic.Breeze.copy"), Strings.Key("mechanic.RocketRocket.copy"), Strings.Key("mechanic.BombRocket.copy"), Strings.Key("mechanic.BombBomb.copy"), Strings.Key("mechanic.PrismRocket.copy"), Strings.Key("mechanic.PrismBomb.copy"), Strings.Key("mechanic.PrismPrism.copy"), Strings.Key("mechanic.TideSweep.copy"), Strings.Key("mechanic.SeedToss.copy"), Strings.Key("mechanic.Lantern.copy"), Strings.Key("mechanic.MakeRocket.copy"), Strings.Key("mechanic.MakeBomb.copy"), Strings.Key("mechanic.MakePrism.copy"), Strings.Key("mechanic.Bottle.copy"), Strings.Key("mechanic.Sand.copy"), Strings.Key("mechanic.Wave.copy") };
        public static string TitleKey(MechanicLesson kind) => titleKeys[(int)kind];
        public static string CopyKey(MechanicLesson kind) => copyKeys[(int)kind];
        public static MechanicLesson ForAbility(AbilityType type) => (MechanicLesson)((int)MechanicLesson.TideSweep + (int)type);
        public static bool IsAbility(MechanicLesson kind) => kind >= MechanicLesson.TideSweep && kind <= MechanicLesson.Lantern;
        public static bool Seen(int mask, MechanicLesson kind) => (mask & Bit(kind)) != 0;
        public static MechanicLesson? Special(SpecialType type)
        {
            if (type == SpecialType.None) return null;
            return (MechanicLesson)((int)type);
        }
        public static MechanicLesson? Combination(string combo)
        {
            switch (combo)
            {
                case "Rocket + Rocket": return MechanicLesson.RocketRocket;
                case "Bomb + Rocket": return MechanicLesson.BombRocket;
                case "Bomb + Bomb": return MechanicLesson.BombBomb;
                case "Prism + RocketH": case "Prism + RocketV": return MechanicLesson.PrismRocket;
                case "Prism + Bomb": return MechanicLesson.PrismBomb;
                case "Prism + Prism": return MechanicLesson.PrismPrism;
                default: return null;
            }
        }
        // Availability is read from the actual settled board, never its campaign number.
        public static MechanicLesson? Next(BoardModel board, int seen)
        {
            if (board == null || board.Status != GameStatus.Playing) return null;
            if (!Seen(seen, MechanicLesson.Match)) return MechanicLesson.Match;
            // The Big Wave is met on the first board that has it (level 5), before anything else it
            // shows, because its meter is on screen from the first tap.
            if (board.WaveEnabled && !Seen(seen, MechanicLesson.Wave)) return MechanicLesson.Wave;
            foreach (var p in board.GetLegalMoves())
            {
                var preview = board.Cells[p.X,p.Y].Special == SpecialType.None ? null : BoardPreview.ForTap(board, p.X, p.Y);
                var combo = preview == null ? null : Combination(preview.Combo);
                if (combo.HasValue && !Seen(seen, combo.Value)) return combo;
                var special = Special(board.Cells[p.X, p.Y].Special);
                if (special.HasValue && !Seen(seen, special.Value)) return special;
                var hint = BoardPreview.GetGroupHint(board, p.X, p.Y);
                if (hint != null && hint.Creates != GroupSpecialKind.None)
                {
                    var make = hint.Creates == GroupSpecialKind.Prism ? MechanicLesson.MakePrism : hint.Creates == GroupSpecialKind.Bomb ? MechanicLesson.MakeBomb : MechanicLesson.MakeRocket;
                    if (!Seen(seen, make)) return make;
                }
            }
            for (int y = 0; y < board.Height; y++) for (int x = 0; x < board.Width; x++)
            {
                var c = board.Cells[x,y]; if (c == null) continue;
                MechanicLesson? obstacle = c.Shell ? MechanicLesson.Shell : c.Prop > 0 ? MechanicLesson.Collect : c.Bottle ? MechanicLesson.Bottle :
                    c.CrateHp > 0 ? MechanicLesson.Crate : c.IceHp > 0 ? MechanicLesson.Ice : (MechanicLesson?)null;
                if (obstacle.HasValue && !Seen(seen, obstacle.Value)) return obstacle;
                // Sand is the floor under whatever stands on it, so an iced tile on sand teaches both.
                if (c.SandHp > 0 && !Seen(seen, MechanicLesson.Sand)) return MechanicLesson.Sand;
            }
            if (board.CanUseBreeze && !Seen(seen, MechanicLesson.Breeze)) return MechanicLesson.Breeze;
            return null;
        }
        /// <summary>
        /// The board mechanics a level can introduce with the one-time intro card before play (lead
        /// decision 2026-09-23): the Big Wave now, bottles and sand for players who never met them,
        /// and each world's twist as it is appended. Order is the order they are introduced in.
        /// </summary>
        public static readonly MechanicLesson[] BoardIntroductions = { MechanicLesson.Wave, MechanicLesson.Bottle, MechanicLesson.Sand };

        /// <summary>True when this board carries the mechanic an intro card would present.</summary>
        public static bool OnBoard(BoardModel board, MechanicLesson kind)
        {
            if (board == null) return false;
            if (kind == MechanicLesson.Wave) return board.WaveEnabled;
            if (kind == MechanicLesson.Bottle)
            { var goal = board.CollectGoal; return board.BottlesOnBoard > 0 || (goal != null && goal.Type == GoalType.Drop); }
            if (kind == MechanicLesson.Sand) return board.SandOnBoard > 0;
            return false;
        }

        /// <summary>
        /// The mechanic the intro card should present on this freshly dealt board, or null: the first
        /// of <see cref="BoardIntroductions"/> the board carries and the save has not seen. Only a board
        /// nobody has touched yet asks, so a resumed or half-played attempt never gets a card.
        /// </summary>
        public static MechanicLesson? Introduction(BoardModel board, int seen)
        {
            if (board == null || board.Status != GameStatus.Playing || board.CanUndo || board.Score != 0) return null;
            foreach (var kind in BoardIntroductions)
                if (OnBoard(board, kind) && !Seen(seen, kind)) return kind;
            return null;
        }

        public static bool Demonstrated(MechanicLesson kind, MoveResult move, SpecialType tapped)
        {
            if (move == null || !move.Accepted) return false;
            if (IsAbility(kind)) return move.IsAbility && ForAbility(move.Ability) == kind;
            if (kind == MechanicLesson.Match) return !move.IsAbility && !move.IsBreeze && tapped == SpecialType.None && move.ClearedCount >= 2;
            if (kind == MechanicLesson.Breeze) return move.IsBreeze;
            if (kind >= MechanicLesson.RocketRocket && kind <= MechanicLesson.PrismPrism) return Combination(move.Combo) == kind;
            if (kind >= MechanicLesson.RocketH && kind <= MechanicLesson.Prism) return Special(tapped) == kind && string.IsNullOrEmpty(move.Combo);
            if (kind >= MechanicLesson.MakeRocket)
                foreach (var created in move.CreatedSpecials)
                    if ((kind == MechanicLesson.MakeRocket && (created.Type == SpecialType.RocketH || created.Type == SpecialType.RocketV)) ||
                        (kind == MechanicLesson.MakeBomb && created.Type == SpecialType.Bomb) || (kind == MechanicLesson.MakePrism && created.Type == SpecialType.Prism)) return true;
            if (kind == MechanicLesson.Ice) return move.IceCleared > 0;
            if (kind == MechanicLesson.Collect) return move.CollectedProps.Count > 0;
            if (kind == MechanicLesson.Bottle) return move.BottlesCollected > 0;
            if (kind == MechanicLesson.Sand) return move.SandWashed > 0;
            if (kind == MechanicLesson.Wave) return move.WaveBroke;
            if ((kind == MechanicLesson.Crate || kind == MechanicLesson.Shell) && move.Before != null && move.After != null)
                foreach (var c in move.Before)
                    if (c != null && c.CrateHp > 0 && c.Shell == (kind == MechanicLesson.Shell) && c.Prop == 0)
                    {
                        int remaining=0;
                        foreach(var after in move.After)if(after!=null && after.Id==c.Id){remaining=after.CrateHp;break;}
                        if(remaining<c.CrateHp)return true;
                    }
            return false;
        }
        public static MechanicPractice Practice(MechanicLesson kind) => new MechanicPractice(kind);

        /// <summary>
        /// The fresh-save quick start, in order: match, make a rocket, fire it. The last step names the
        /// row rocket, but it is played on the board the second step left (<see cref="ContinueQuickStart"/>),
        /// so its lesson follows the rocket the player actually made.
        /// </summary>
        public static readonly MechanicLesson[] QuickStart = { MechanicLesson.Match, MechanicLesson.MakeRocket, MechanicLesson.RocketH };

        /// <summary>
        /// The quick start's next step (design uplift S1 #7, GP-11). A rocket step continues on the board
        /// the previous step left, outlining the rocket that step's move just made: "make a rocket, then
        /// fire it" on one board, the way the genre teaches it, instead of a fresh board with a rocket the
        /// player never made. The lesson follows the rocket's real orientation, so the title can never
        /// name a row rocket over a column one. Every other step, and a rocket step whose previous move
        /// made no rocket that still stands, gets its own stand-alone practice board.
        /// </summary>
        public static MechanicPractice ContinueQuickStart(MechanicPractice previous, MechanicLesson planned)
        {
            if ((planned == MechanicLesson.RocketH || planned == MechanicLesson.RocketV) && previous != null &&
                previous.Completed && previous.LastMove != null && previous.Board != null)
                foreach (var made in previous.LastMove.CreatedSpecials)
                {
                    if (made.Type != SpecialType.RocketH && made.Type != SpecialType.RocketV) continue;
                    var cell = previous.Board.Cells[made.Position.X, made.Position.Y];
                    if (cell == null || cell.Id != made.CellId || cell.Special != made.Type) continue;
                    return new MechanicPractice((MechanicLesson)(int)made.Type,
                        BoardModel.FromSnapshot(previous.Board.ExportSnapshot()), made.Position.X, made.Position.Y);
                }
            return Practice(planned);
        }
    }

    // Completely isolated rules sandbox: this type cannot access PlayerData, inventory or settlement.
    public sealed class MechanicPractice
    {
        public readonly MechanicLesson Kind;
        public BoardModel Board { get; private set; }
        public bool Completed { get; private set; }
        public int TargetX { get; private set; } = 1;
        public int TargetY { get; private set; } = 1;
        /// <summary>The last accepted action on this board, or null: what the completed frame shows happened.</summary>
#nullable disable // null until the first accepted action; the league server compiles Core with nullable on
        public MoveResult LastMove { get; private set; }
#nullable restore
        /// <summary>A practice played on an existing board (a quick-start continuation), aimed at one cell.</summary>
        internal MechanicPractice(MechanicLesson kind, BoardModel board, int targetX, int targetY)
        {
            if((int)kind<0 || (int)kind>=MechanicTeaching.Count)throw new ArgumentOutOfRangeException("kind");
            if(board==null)throw new ArgumentNullException("board");
            Kind=kind;Board=board;TargetX=targetX;TargetY=targetY;
        }
        public MechanicPractice(MechanicLesson kind)
        {
            if((int)kind<0 || (int)kind>=MechanicTeaching.Count)throw new ArgumentOutOfRangeException("kind");
            Kind = kind;
            var level = new LevelDefinition { Id=1900+(int)kind, Width=4, Height=4, ColorCount=3, Moves=20,
                Seed=0xA73401u, TargetScore=100000, BreezeEnabled=true, InitialBreezeCharge=20, InitialColors=new int[16] };
            for (int y=0;y<4;y++) for(int x=0;x<4;x++) level.InitialColors[y*4+x]=(x+y)%3;
            level.InitialColors[5]=level.InitialColors[6]=0;
            if (kind>=MechanicLesson.MakeRocket && kind<=MechanicLesson.MakePrism)
            {
                int count=kind==MechanicLesson.MakeRocket?5:kind==MechanicLesson.MakeBomb?7:9;
                for(int y=0;y<4;y++)for(int x=0;x<4;x++)level.InitialColors[y*4+x]=1+(x+y)%2;
                for(int i=0;i<count;i++)level.InitialColors[(i/3)*4+i%3]=0;
                TargetX=0;TargetY=0;
            }
            if(kind==MechanicLesson.Crate || kind==MechanicLesson.Collect || kind==MechanicLesson.Shell)
                level.Blockers.Add(new BlockerDefinition(1,2,1,0,kind==MechanicLesson.Collect?2:0,kind==MechanicLesson.Shell));
            if(kind==MechanicLesson.Ice)level.Blockers.Add(new BlockerDefinition(1,1,0,1));
            if(kind==MechanicLesson.Collect)level.Goals.Add(new GoalDefinition(GoalType.Prop,1,0,2));
            // A bottle over a three-tile group: the marked tap brings it straight to the shore row.
            if(kind==MechanicLesson.Bottle)
            {
                level.InitialColors[0]=1;level.InitialColors[1]=level.InitialColors[2]=0;level.InitialColors[3]=2;
                level.Blockers.Add(new BlockerDefinition(1,1){Bottle=true});
                level.Goals.Add(new GoalDefinition(GoalType.Drop,1));
                TargetX=1;TargetY=0;
            }
            // Sand under the marked pair: clearing it washes both patches.
            if(kind==MechanicLesson.Sand)
            {
                level.Blockers.Add(new BlockerDefinition(1,1){Sand=1});
                level.Blockers.Add(new BlockerDefinition(2,1){Sand=1});
                level.Goals.Add(new GoalDefinition(GoalType.Sand,2));
            }
            // The meter two short of full: the marked pair's tap breaks the wave through the real rules.
            if(kind==MechanicLesson.Wave){level.WaveEnabled=true;level.InitialWaveCharge=BoardModel.WaveCapacity-2;}
            Board=BoardModel.NewLevel(level);
            if(kind>=MechanicLesson.RocketH && kind<=MechanicLesson.Prism) Board.Cells[1,1].Special=(SpecialType)(int)kind;
            if(kind==MechanicLesson.Shell) Board.Cells[1,1].Special=SpecialType.RocketV;
            if(kind>=MechanicLesson.RocketRocket && kind<=MechanicLesson.PrismPrism)
            {
                Board.Cells[1,1].Special=kind>=MechanicLesson.PrismRocket?SpecialType.Prism:kind==MechanicLesson.RocketRocket?SpecialType.RocketH:SpecialType.Bomb;
                Board.Cells[2,1].Special=kind==MechanicLesson.PrismPrism?SpecialType.Prism:kind==MechanicLesson.BombBomb||kind==MechanicLesson.PrismBomb?SpecialType.Bomb:SpecialType.RocketV;
            }
        }
        public MoveResult Act(int x,int y)
        {
            SpecialType tapped=Board.Cells[x,y].Special;
            var result=MechanicTeaching.IsAbility(Kind)?Board.UseAbility((AbilityType)((int)Kind-(int)MechanicLesson.TideSweep),x,y)
                :Kind==MechanicLesson.Breeze?Board.UseBreeze(true):Board.Tap(x,y);
            if(result.Accepted)LastMove=result;
            if(MechanicTeaching.Demonstrated(Kind,result,tapped))Completed=true;
            return result;
        }
    }
}
