using System;
using System.Collections.Generic;

namespace BloomHarbor
{
    /// <summary>
    /// The falling motion the board view gives a committed change (TileVisual's round 16 gravity drop),
    /// as numbers the tap gate can reason about without a renderer. Core cannot see the Feel layer, so
    /// the host hands the values over (FeelTokens.Fall); the offline tap-gate suite reads the same ones.
    /// </summary>
    public struct FallProfile
    {
        /// <summary>Cells per second squared, terminal and starting speed in cells per second, the share
        /// of the impact speed a hard landing rebounds with, and the impact speed below which it does not.</summary>
        public float Gravity, TerminalSpeed, StartSpeed, Bounce, BounceMinSpeed;
        /// <summary>The board row a fresh or rebuilt tile is dropped in from (BoardView.Show: 10 - y cells above its cell).</summary>
        public float SpawnRow;
        /// <summary>A tile that moves sideways or upward (a Breeze weave, a reshuffle) glides instead of
        /// falling; how long until that glide reads as at rest.</summary>
        public float GlideSeconds;
        /// <summary>Added to every settle: a frame-stepped fall lands a frame or so either side of the closed form.</summary>
        public float SlackSeconds;
    }

    /// <summary>The live board a <see cref="TapGate"/> asks about. Implemented by the Play screen and by
    /// the offline suite, so both run the same decisions.</summary>
    public interface ITapBoard
    {
        /// <summary>Bumped by every committed change to the board, so a cached prediction knows it is old.</summary>
        int Revision { get; }
        /// <summary>The id of the tile the model holds at (x, y), or -1.</summary>
        int ModelId(int x, int y);
        /// <summary>Where the model holds tile <paramref name="id"/> now, or false when it has left the board.</summary>
        bool Find(int id, out int x, out int y);
        /// <summary>The columns a tap at (x, y) would change on the live board (the Core's own detached
        /// prediction, never a restated rule), with the ids it would clear added to <paramref name="cleared"/>
        /// and, when <paramref name="aim"/> is given, what the tap acts on written to it
        /// (<see cref="TapGate.PredictFootprint"/>). Zero for a tap the Core refuses.</summary>
        int Footprint(int x, int y, List<int> cleared, TapAim aim);
    }

    public enum TapVerdict { Resolve, Queued, Coalesced, Dropped }

    /// <summary>
    /// What a tap acts on, from the Core's own rules: the tiles of the group it clears, or the special it
    /// fires, and what its release makes. A queued tap keeps the one it was made against, so it can tell
    /// when an earlier tap has changed it (<see cref="TapGate.NextReady"/>).
    /// </summary>
    public sealed class TapAim
    {
        /// <summary>The ids of the tiles the tap acts on: an ordinary tap's whole group
        /// (<see cref="BoardModel.GetGroup"/>), or a special's own tile.</summary>
        public readonly List<int> Group = new List<int>(24);
        /// <summary>The special the release creates, with both rocket directions as one kind (the Core
        /// alternates them by its own count), or None.</summary>
        public SpecialType Makes;
        /// <summary>The combination a special's release fires (<see cref="TapPreview.Combo"/>), or null.</summary>
        public string Combo;

        public void Clear() { Group.Clear(); Makes = SpecialType.None; Combo = null; }

        public void CopyFrom(TapAim other)
        {
            Group.Clear();
            if (other == null) { Makes = SpecialType.None; Combo = null; return; }
            Group.AddRange(other.Group); Makes = other.Makes; Combo = other.Combo;
        }

        /// <summary>
        /// True when a tap made against this aim still acts on what the player chose, on a board whose
        /// prediction is <paramref name="live"/>: every tile of the group is still in it, and the release
        /// makes the same special or fires the same combination. A group that has lost a tile, or now makes
        /// something else, is not the one the player tapped. One that has only gained a tile still is: a
        /// queued tap resolves only once every column its clear touches is at rest, so the newcomer has
        /// landed beside the group in view and pops with it, as a group always does.
        /// </summary>
        public bool Matches(TapAim live)
        {
            if (live == null || live.Makes != Makes || !string.Equals(live.Combo, Combo, StringComparison.Ordinal)) return false;
            for (int i = 0; i < Group.Count; i++) if (!live.Group.Contains(Group[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// Design uplift S2-2 (AJ-01): the board's input gate, per column instead of one lock for the board.
    ///
    /// Before this, every committed move locked the whole board for a constant 0.32 s (0.10 s under
    /// reduced motion) and a tap inside that window was dropped, so a player tapping at a human cadence
    /// landed about one tap in three. Now each move holds only the columns it changed, each until its own
    /// tiles have come to rest (and never less than the old constant, which stays the feel floor), and:
    ///
    /// <list type="bullet">
    /// <item>a tap whose whole footprint is in settled, unreserved columns resolves at once, while other
    /// columns are still falling;</item>
    /// <item>a tap on a column that is still moving is queued (at most <see cref="Capacity"/>) against the tile
    /// the model rests at the touched cell, the tile the player watches land there, together with what the
    /// tap acts on at that moment (its group, or the special, and what the release makes: a
    /// <see cref="TapAim"/>). It resolves in order once that tile and its footprint have come to rest; when
    /// an earlier queued tap moves the tile, the tap follows it. It is dropped as stale, never re-aimed and
    /// never answered with a refusal, when the tile has left the board or its group is no longer the one
    /// the player tapped: a tile gone from it, or a different special made or combination fired;</item>
    /// <item>a tap on a tile that is still dropping in above the board (a fresh or rebuilt tile, in its first
    /// fraction of a second) is dropped: the player cannot have seen it;</item>
    /// <item>a tap on a moving tile the model rests with no group is dropped at once (as a tap on a moving
    /// board always was): the Core would refuse it on the board the player is watching settle, and a group
    /// an earlier queued tap might give it later is not one they saw;</item>
    /// <item>a second tap on a tile a queued tap already names, or will clear, is coalesced into it;</item>
    /// <item>a queued tap waits at most <see cref="WindowSeconds"/>; after that the player has moved on.</item>
    /// </list>
    ///
    /// A combination's hit-stop freezes every tile on the board (BoardView.Hold), so it pushes back every
    /// column still settling and every tile still dropping in, not only the new move's.
    ///
    /// Nothing here touches a rule: every tap that resolves goes through the Core's own Tap, in the order
    /// it resolves, and that order is what the campaign record and the league replay store. So the same
    /// sequence of resolved taps gives the same board and score, however they were timed.
    ///
    /// <see cref="LegacyGlobalLock"/> restores the old behaviour (one board-wide lock, one buffered tap) and
    /// <see cref="FollowAnyGroup"/> the first S2-2 build's re-check, each for a known-bad half of an
    /// assertion; nothing in a player sets either.
    /// </summary>
    public sealed class TapGate
    {
        /// <summary>
        /// Taps held while their tiles settle. Measured on every campaign level from 13 (offline tap-gate suite,
        /// ten taps 125 ms apart, bursts without a Big Wave, stale groups dropped, 2026-10-10): 2 slots met the
        /// 9-of-10 bar on 82.7% of levels, 3 on 86.9%, 4 on 91.7%, 5 on 92.9%, 6 on 93.2%. More than one,
        /// because each queued tap is bound to its tile and its group and is dropped once either has changed,
        /// so a deeper queue never spends a move on a stale position (what kept the pre-S2-2 buffer at one
        /// tap); four, because past it each slot buys about a point or less while the taps it holds resolve
        /// further behind the finger.
        /// </summary>
        public const int Capacity = 4;
        /// <summary>How long a queued tap may wait. Past this the player has visibly moved on.</summary>
        public const float WindowSeconds = 1.2f;
        /// <summary>The old buffer's replay window (0.4 s), kept only for <see cref="LegacyGlobalLock"/>.</summary>
        public const float LegacyBufferWindow = .4f;

        sealed class Entry
        {
            public int X, Y, Tile, Reserve, Footprint, Revision;
            public float At;
            public readonly List<int> Cleared = new List<int>(24);
            /// <summary>What the tap acted on when the player made it.</summary>
            public readonly TapAim Aim = new TapAim();
        }

        readonly float[] until;
        readonly float[] settle;
        readonly Entry[] queue = new Entry[Capacity];
        readonly List<int> scratch = new List<int>(24);
        readonly TapAim scratchAim = new TapAim(), liveAim = new TapAim();
        readonly Dictionary<int, int> origins = new Dictionary<int, int>(160);
        /// <summary>Tiles still dropping in above the board: id, and when half the tile is on the board.</summary>
        readonly Dictionary<int, float> entering = new Dictionary<int, float>(160);
        readonly List<int> dropped = new List<int>(80);
        readonly List<int> keys = new List<int>(160);
        int count;
        float frozenUntil;
        float legacyUntil;
        int legacyX = -1, legacyY = -1;
        float legacyAt;

        public FallProfile Fall;
        /// <summary>QA known-bad only: one board-wide lock after every move and one buffered tap, exactly
        /// as before S2-2. A player never sets it.</summary>
        public bool LegacyGlobalLock;
        /// <summary>QA known-bad only: checks a queued tap as the first S2-2 build did. A tap on a tile with no
        /// group is held while another waits ahead of it, and at its turn a queued tap needs only that its
        /// tile has some group, and fires on whatever group that is now. A player never sets it.</summary>
        public bool FollowAnyGroup;
        public int Width { get; private set; }

        // Counters for QA receipts. Offered = every tap handed to Offer.
        public int Offered, ResolvedNow, ResolvedQueued, Queued, Coalesced, Stale, Expired, Overflow, NoTile, Unseen, Cleared;

        public TapGate(int width = 8)
        {
            if (width < 1 || width > 30) throw new ArgumentOutOfRangeException("width");
            Width = width; until = new float[width]; settle = new float[width];
            for (int i = 0; i < Capacity; i++) queue[i] = new Entry();
        }

        public int Count { get { return LegacyGlobalLock ? (legacyX >= 0 ? 1 : 0) : count; } }
        public int AllColumns { get { return (1 << Width) - 1; } }

        /// <summary>Columns whose tiles are still moving at <paramref name="now"/>.</summary>
        public int BusyMask(float now)
        {
            if (LegacyGlobalLock) return now < legacyUntil ? AllColumns : 0;
            int mask = 0;
            for (int x = 0; x < Width; x++) if (now < until[x]) mask |= 1 << x;
            return mask;
        }

        /// <summary>Columns a queued tap has claimed: its own column and its predicted footprint. A fresh
        /// tap that would touch one of them waits behind it instead of overtaking it.</summary>
        public int ReservedMask
        {
            get
            {
                int mask = 0;
                if (!LegacyGlobalLock) for (int i = 0; i < count; i++) mask |= queue[i].Reserve;
                return mask;
            }
        }

        /// <summary>True when a queued tap names tile <paramref name="id"/> or will clear it: a tap on it now
        /// would be coalesced.</summary>
        public bool Claims(int id)
        {
            if (LegacyGlobalLock) return false;
            for (int i = 0; i < count; i++) if (queue[i].Tile == id || queue[i].Cleared.Contains(id)) return true;
            return false;
        }

        /// <summary>True while tile <paramref name="id"/> is still dropping in above the board at
        /// <paramref name="now"/> (a fresh or rebuilt tile in its first fraction of a second): the player
        /// cannot have seen it yet, so a tap is never bound to it.</summary>
        public bool Entering(int id, float now)
        {
            return !LegacyGlobalLock && id >= 0 && entering.TryGetValue(id, out float visible) && now < visible;
        }

        /// <summary>True while any column is moving or any tap is waiting: the board as a whole is not
        /// at rest, so Undo, Breeze, abilities and lessons wait.</summary>
        public bool Busy(float now) { return BusyMask(now) != 0 || Count > 0; }

        /// <summary>A new board: every hold and every queued tap goes.</summary>
        public void Reset()
        {
            Array.Clear(until, 0, until.Length);
            entering.Clear();
            count = 0; frozenUntil = 0; legacyUntil = 0; legacyX = legacyY = -1;
        }

        /// <summary>Drops every queued tap: an overlay opened, or the attempt ended.</summary>
        public void ClearQueue()
        {
            Cleared += Count;
            count = 0; legacyX = legacyY = -1;
        }

        public void ResetCounters() { Offered = ResolvedNow = ResolvedQueued = Queued = Coalesced = Stale = Expired = Overflow = NoTile = Unseen = Cleared = 0; }

        /// <summary>Holds every column until <paramref name="untilTime"/> (never shortens a hold).</summary>
        public void HoldAll(float untilTime)
        {
            if (LegacyGlobalLock) { legacyUntil = Math.Max(legacyUntil, untilTime); return; }
            for (int x = 0; x < Width; x++) until[x] = Math.Max(until[x], untilTime);
        }

        /// <summary>
        /// A hit-stop from <paramref name="now"/> for <paramref name="seconds"/>. BoardView.Hold freezes every
        /// tile on the board and extends a freeze already running rather than adding to it, so each column
        /// still settling, and each tile still dropping in, lands later by exactly the freeze this adds.
        /// </summary>
        public void Freeze(float now, float seconds)
        {
            if (LegacyGlobalLock || seconds <= 0) return;
            float end = now + seconds, from = Math.Max(frozenUntil, now);
            if (end <= from) return;
            float added = end - from;
            frozenUntil = end;
            for (int x = 0; x < Width; x++) if (until[x] > now) until[x] += added;
            keys.Clear();
            foreach (var pair in entering) if (pair.Value > now) keys.Add(pair.Key);
            for (int i = 0; i < keys.Count; i++) entering[keys[i]] += added;
        }

        /// <summary>
        /// Holds each column the view moves from <paramref name="before"/> to <paramref name="after"/>, starting
        /// <paramref name="delay"/> from <paramref name="now"/> (a hit-stop freezes the tiles first; a freeze
        /// already running delays it as much), until its own tiles have come to rest and at least
        /// <paramref name="floor"/> seconds. Returns the moved columns.
        /// </summary>
        public int HoldMove(Cell[,] before, Cell[,] after, float now, float delay, float floor)
        {
            if (LegacyGlobalLock)
            {
                // The pre-S2-2 lock: assigned, not extended, the moment the move commits.
                legacyUntil = now + floor;
                return AllColumns;
            }
            delay = Math.Max(delay, frozenUntil - now);
            PruneEntering(now);
            int mask = Settle(before, after, settle);
            // A tile dropped in from the spawn row starts above the board, out of the player's sight, and is
            // half on the board once it has fallen past the top edge.
            float visible = DropSeconds(Fall.SpawnRow - (after.GetLength(1) - .5f), Fall, out _);
            if (visible > 0) for (int i = 0; i < dropped.Count; i++) entering[dropped[i]] = now + delay + visible;
            for (int x = 0; x < Width; x++)
                if ((mask & (1 << x)) != 0) until[x] = Math.Max(until[x], now + delay + Math.Max(floor, settle[x]));
            return mask;
        }

        /// <summary>
        /// Holds what a committed move moves on screen: its own fall, after <paramref name="hitStop"/> (a
        /// combination's freeze of the whole board); and when it broke the Big Wave, the sweep's relayout
        /// <paramref name="waveDelay"/> later plus a board-wide hold of <paramref name="waveHold"/>. A move that
        /// carries no before-board holds every column for the floor, as the old lock did.
        /// </summary>
        public void HoldCommitted(MoveResult result, Cell[,] after, float now, float floor, float hitStop, float waveDelay, float waveHold)
        {
            if (result == null || !result.Accepted) return;
            Freeze(now, hitStop);
            if (result.Before == null || after == null) { HoldAll(now + hitStop + floor); return; }
            if (result.WaveBroke && result.WaveFrom != null)
            {
                HoldMove(result.Before, result.WaveFrom, now, hitStop, floor);
                if (LegacyGlobalLock) { HoldAll(now + waveHold); return; }
                HoldMove(result.WaveFrom, after, now, waveDelay, floor);
                HoldAll(now + waveHold);
            }
            else HoldMove(result.Before, after, now, hitStop, floor);
        }

        /// <summary>
        /// The columns a tap at (x, y) would change on <paramref name="board"/>, from the Core's own detached
        /// prediction (<see cref="BoardPreview.ForTap"/>), with the ids it would clear added to
        /// <paramref name="cleared"/>; and, when <paramref name="aim"/> is given, what the tap acts on: the
        /// group <see cref="BoardModel.GetGroup"/> finds there (or the special itself) and what the prediction
        /// says the release makes. <paramref name="reuse"/> is a prediction already made for this cell on this
        /// board (a held press). Zero, and an empty aim, when the Core refuses the tap.
        /// </summary>
        public static int PredictFootprint(BoardModel board, int x, int y, List<int> cleared, TapAim aim = null, TapPreview reuse = null)
        {
            if (aim != null) aim.Clear();
            if (board == null || x < 0 || y < 0 || x >= board.Width || y >= board.Height) return 0;
            var preview = reuse != null && reuse.TapPosition.X == x && reuse.TapPosition.Y == y ? reuse : BoardPreview.ForTap(board, x, y);
            if (!preview.Accepted) return 0;
            if (cleared != null) foreach (var cell in preview.ClearedCells) cleared.Add(cell.CellId);
            if (aim != null)
            {
                var origin = board.Cells[x, y];
                if (origin != null && origin.Special != SpecialType.None) aim.Group.Add(origin.Id);
                else foreach (var p in board.GetGroup(x, y)) aim.Group.Add(board.Cells[p.X, p.Y].Id);
                var makes = preview.CreatedSpecials.Count > 0 ? preview.CreatedSpecials[0].Type : SpecialType.None;
                aim.Makes = makes == SpecialType.RocketV ? SpecialType.RocketH : makes;
                aim.Combo = string.IsNullOrEmpty(preview.Combo) ? null : preview.Combo;
            }
            return ChangedColumns(board.Cells, preview.AfterSnapshot);
        }

        /// <summary>
        /// A tap at (x, y). <paramref name="tile"/> is the tile the model rests at that cell (-1 when it holds
        /// none). The caller has already refused taps the board cannot take at all (an overlay, a finished
        /// attempt); this only decides between now, later and never. <paramref name="unseen"/> says the tile
        /// was still dropping in above the board when the finger came down (a press checked then; its
        /// release arrives later).
        /// </summary>
        public TapVerdict Offer(int x, int y, int tile, float now, ITapBoard board, bool unseen = false)
        {
            Offered++;
            if (x < 0 || x >= Width) { NoTile++; return TapVerdict.Dropped; }
            if (LegacyGlobalLock)
            {
                if (now >= legacyUntil) { ResolvedNow++; return TapVerdict.Resolve; }
                // One buffered tap: a newer one replaces it.
                if (legacyX >= 0) Overflow++;
                legacyX = x; legacyY = y; legacyAt = now; Queued++;
                return TapVerdict.Queued;
            }
            if (tile < 0) { NoTile++; return TapVerdict.Dropped; }
            // The model already rests a fresh tile here, but on screen it is still above the board: whatever
            // the player touched, it was not this tile.
            if (unseen || Entering(tile, now)) { Unseen++; return TapVerdict.Dropped; }
            ExpireQueued(now);
            for (int i = 0; i < count; i++)
                if (queue[i].Tile == tile || queue[i].Cleared.Contains(tile)) { Coalesced++; return TapVerdict.Coalesced; }

            int column = 1 << x, blocked = BusyMask(now) | ReservedMask;
            int footprint = -1, revision = board.Revision;
            scratch.Clear();
            if ((blocked & column) == 0)
            {
                // Nothing moving and nothing waiting: no prediction needed.
                if (blocked == 0) { ResolvedNow++; return TapVerdict.Resolve; }
                footprint = board.Footprint(x, y, scratch, scratchAim);
                if ((footprint & blocked) == 0) { ResolvedNow++; return TapVerdict.Resolve; }
            }
            if (count >= Capacity) { Overflow++; return TapVerdict.Dropped; }
            var entry = queue[count++];
            entry.Tile = tile; entry.At = now;
            // The prediction is made where the named tile rests, which is where the tap resolves.
            int tx = x, ty = y;
            if (!board.Find(tile, out tx, out ty)) { tx = x; ty = y; }
            entry.X = tx; entry.Y = ty;
            entry.Cleared.Clear();
            if (footprint < 0 || tx != x || ty != y) footprint = board.Footprint(tx, ty, entry.Cleared, entry.Aim);
            else { entry.Cleared.AddRange(scratch); entry.Aim.CopyFrom(scratchAim); }
            // A tile the model rests with no group: the Core would refuse this tap on the board the player is
            // watching settle, so it goes now instead of holding a slot. A group an earlier queued tap might
            // give the tile later is not one the player saw (the first S2-2 build held such a tap whenever
            // another waited ahead of it: FollowAnyGroup).
            if (footprint == 0 && (count == 1 || !FollowAnyGroup)) { count--; Stale++; return TapVerdict.Dropped; }
            entry.Footprint = footprint; entry.Revision = revision;
            entry.Reserve = column | (1 << tx) | footprint;
            Queued++;
            return TapVerdict.Queued;
        }

        /// <summary>
        /// The next queued tap that can resolve now, in order, and the cell it resolves on: wherever its tile
        /// now rests (an earlier queued tap may have moved it). The head is dropped as stale when that tile
        /// has left the board, or when the board has changed since the tap was made and the tile's live group
        /// is not the one the player tapped (no move at all, a tile gone from it, or a different special made
        /// or combination fired: <see cref="TapAim.Matches"/>); it waits while the tile's column moves, and
        /// resolves once its footprint on the live board is clear of moving columns.
        /// </summary>
        public bool NextReady(float now, ITapBoard board, out int x, out int y)
        {
            x = y = -1;
            if (LegacyGlobalLock)
            {
                if (legacyX < 0 || now < legacyUntil) return false;
                int bx = legacyX, by = legacyY; float at = legacyAt;
                legacyX = legacyY = -1;
                if (now - at > LegacyBufferWindow) { Expired++; return false; }
                x = bx; y = by; ResolvedQueued++;
                return true;
            }
            ExpireQueued(now);
            while (count > 0)
            {
                var head = queue[0];
                if (!board.Find(head.Tile, out int tx, out int ty)) { Stale++; RemoveAt(0); continue; }
                int busy = BusyMask(now);
                if ((busy & (1 << tx)) != 0) return false;
                if (head.Revision != board.Revision || tx != head.X || ty != head.Y)
                {
                    head.X = tx; head.Y = ty;
                    head.Cleared.Clear();
                    head.Footprint = board.Footprint(tx, ty, head.Cleared, liveAim);
                    head.Revision = board.Revision;
                    head.Reserve = (1 << tx) | head.Footprint;
                    // An earlier tap has changed the board since this one was made. The tap was aimed at a
                    // group (or a special) as it stood then; firing on whatever the tile belongs to now would
                    // spend a move on something the player never chose.
                    if (head.Footprint != 0 && !FollowAnyGroup && !head.Aim.Matches(liveAim)) { Stale++; RemoveAt(0); continue; }
                }
                // The group the player aimed at no longer exists (the tile landed alone, or beside a crate):
                // dropped quietly, not answered with a refusal for a tap made a moment ago.
                if (head.Footprint == 0) { Stale++; RemoveAt(0); continue; }
                if ((head.Footprint & busy) != 0) return false;
                x = tx; y = ty; ResolvedQueued++;
                RemoveAt(0);
                return true;
            }
            return false;
        }

        void ExpireQueued(float now)
        {
            for (int i = count - 1; i >= 0; i--)
                if (now - queue[i].At > WindowSeconds) { Expired++; RemoveAt(i); }
        }

        void RemoveAt(int index)
        {
            var removed = queue[index];
            for (int i = index; i < count - 1; i++) queue[i] = queue[i + 1];
            queue[count - 1] = removed;
            count--;
        }

        void PruneEntering(float now)
        {
            keys.Clear();
            foreach (var pair in entering) if (pair.Value <= now) keys.Add(pair.Key);
            for (int i = 0; i < keys.Count; i++) entering.Remove(keys[i]);
        }

        /// <summary>
        /// What the view does to each column going from <paramref name="before"/> to <paramref name="after"/>:
        /// fills <paramref name="seconds"/> with how long each column takes to come to rest and returns the
        /// mask of columns whose visible content changes. Mirrors BoardView.Show: a tile keeps its piece while
        /// its look is unchanged and falls from where it stood; a new tile, or one whose look changed (a crate
        /// hit, a special made, a reshuffled colour), is rebuilt and dropped in from the spawn row.
        /// </summary>
        public int Settle(Cell[,] before, Cell[,] after, float[] seconds)
        {
            int width = after.GetLength(0), height = after.GetLength(1);
            Array.Clear(seconds, 0, seconds.Length);
            origins.Clear(); dropped.Clear();
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    var b = before[x, y];
                    if (b != null) origins[b.Id] = x * 64 + y;
                }
            int mask = 0;
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    var a = after[x, y]; var b = before[x, y];
                    if ((a == null ? -1 : a.Id) != (b == null ? -1 : b.Id) || Signature(a) != Signature(b)) mask |= 1 << x;
                    if (a == null) continue;
                    float t;
                    if (!origins.TryGetValue(a.Id, out int origin)) { t = FallSeconds(Fall.SpawnRow - y, Fall); dropped.Add(a.Id); }
                    else
                    {
                        int ox = origin / 64, oy = origin % 64;
                        if (Signature(before[ox, oy]) != Signature(a)) { t = FallSeconds(Fall.SpawnRow - y, Fall); dropped.Add(a.Id); }
                        else if (ox != x || oy < y) t = Fall.GlideSeconds;
                        else t = oy > y ? FallSeconds(oy - y, Fall) : 0;
                    }
                    if (t > 0) t += Fall.SlackSeconds;
                    if (x < seconds.Length && t > seconds[x]) seconds[x] = t;
                }
            return mask;
        }

        /// <summary>Columns whose content differs between a board and a detached prediction of it (TapPreview.AfterSnapshot).</summary>
        public static int ChangedColumns(Cell[,] before, BoardSnapshot after)
        {
            if (before == null || after == null || after.Cells == null) return 0;
            int mask = 0;
            for (int x = 0; x < after.Width; x++)
                for (int y = 0; y < after.Height; y++)
                {
                    var a = after.Cells[y * after.Width + x]; var b = before[x, y];
                    if ((a == null ? -1 : a.Id) != (b == null ? -1 : b.Id) || Signature(a) != Signature(b)) { mask |= 1 << x; break; }
                }
            return mask;
        }

        /// <summary>What the player sees on a cell: colour, special, crate, ice, collect prop, shell and
        /// bottle. Two cells with the same signature are indistinguishable whatever their ids; the board
        /// view keys its tile pieces on it (BoardView.Signature forwards here).</summary>
        public static int Signature(Cell c)
        {
            return c == null ? -1 : c.Color + ((int)c.Special) * 10 + c.CrateHp * 100 + c.IceHp * 1000 + c.Prop * 10000 + (c.Shell ? 100000 : 0) + (c.Bottle ? 1000000 : 0);
        }

        /// <summary>Seconds a tile dropped <paramref name="cells"/> takes to come to rest under
        /// <paramref name="p"/>: accelerate to terminal speed, then the one small rebound a hard landing makes.</summary>
        public static float FallSeconds(float cells, FallProfile p)
        {
            float t = DropSeconds(cells, p, out float impact);
            if (t > 0 && impact > p.BounceMinSpeed) t += 2 * impact * p.Bounce / p.Gravity;
            return t;
        }

        /// <summary>Seconds a tile dropped from rest takes to cover <paramref name="cells"/> under
        /// <paramref name="p"/> (no rebound), and the speed it has then. Zero with no fall.</summary>
        public static float DropSeconds(float cells, FallProfile p, out float speed)
        {
            speed = 0;
            if (cells <= 0 || p.Gravity <= 0) return 0;
            float toTerminal = Math.Max(0, (p.TerminalSpeed - p.StartSpeed) / p.Gravity);
            float terminalCells = p.StartSpeed * toTerminal + .5f * p.Gravity * toTerminal * toTerminal;
            if (cells <= terminalCells)
            {
                float t = (-p.StartSpeed + (float)Math.Sqrt(p.StartSpeed * p.StartSpeed + 2 * p.Gravity * cells)) / p.Gravity;
                speed = p.StartSpeed + p.Gravity * t;
                return t;
            }
            speed = p.TerminalSpeed;
            return toTerminal + (cells - terminalCells) / p.TerminalSpeed;
        }
    }
}
