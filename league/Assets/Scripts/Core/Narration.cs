using System;

namespace BloomHarbor
{
    /// <summary>
    /// One thing the instruction band under the board can say about the player's own move.
    /// The numeric values are save positions (two bits each in <see cref="PlayerData.narrationSeen"/>):
    /// append new cues at the end and never renumber one.
    /// </summary>
    public enum NarrationCue
    {
        /// <summary>The held-tap line that ends in "slide off cancels".</summary>
        HoldCancel = 0,
        /// <summary>"Rocket made!" and "Tap your new rocket."</summary>
        MadeRocket = 1,
        MadeBomb = 2,
        MadePrism = 3,
        /// <summary>"Rocket!" when the player fires one.</summary>
        FiredRocket = 4,
        FiredBomb = 5,
        FiredPrism = 6,
        /// <summary>The six combination names, one cue each, and any name the catalogue does not know.</summary>
        ComboRocketRocket = 7,
        ComboBombRocket = 8,
        ComboBombBomb = 9,
        ComboPrismRocket = 10,
        ComboPrismBomb = 11,
        ComboPrismPrism = 12,
        ComboOther = 13,
        /// <summary>"A beautiful bloom!" after a plain clear of twelve or more.</summary>
        BigClear = 14,
        /// <summary>"Same pieces, new possibilities." after a Breeze.</summary>
        Breeze = 15,
    }

    /// <summary>
    /// How many times the band under the board names each kind of move before it goes quiet
    /// (design review 2026-10-03, AJ-09 / GP-10 and Big 3): the cancel instruction for the first
    /// three holds, and each special, combination, big clear and Breeze for its first two uses.
    /// After that the move speaks through the board itself - the burst, the praise word, the score
    /// pop - and the band keeps only what the player still needs: a refusal, a lesson, an armed
    /// ability's instruction, the Big Wave.
    ///
    /// Pure and allocation-free: the counts live in one <c>long</c> on the save, two bits per cue,
    /// so recording a use on a commit frame touches no heap (the play budget's exact gate) and the
    /// count rides the save the move already makes.
    ///
    /// Every <c>long</c> is a value some build can write - thirty-two two-bit slots fill it, the sign
    /// bit included - so a saved value is read, never refused (S1 #12 review): a count above this
    /// build's cap reads as spent (<see cref="Due"/> is false and <see cref="Use"/> answers false and
    /// writes nothing), and the slots past <see cref="CueCount"/> belong to cues a later build
    /// appended, which this build neither reads nor writes, so they survive its saves for that build
    /// to find again. Appending a cue or raising a cap (to three at most, all two bits hold) therefore
    /// needs no schema bump, and a cosmetic memory can never be why a profile fails to load.
    /// </summary>
    public static class Narration
    {
        public const int BitsPerCue = 2;
        public const int CueCount = 16;
        /// <summary>Holds that show the "slide off cancels" instruction.</summary>
        public const int HoldCancelShows = 3;
        /// <summary>Uses of a special, combination, big clear or Breeze that the band names.</summary>
        public const int FirstUseShows = 2;
        const long CueMask = (1L << BitsPerCue) - 1;

        public static bool IsCue(NarrationCue cue) { return (int)cue >= 0 && (int)cue < CueCount; }

        /// <summary>Showings a cue gets before the band stops naming it.</summary>
        public static int Cap(NarrationCue cue) { return cue == NarrationCue.HoldCancel ? HoldCancelShows : FirstUseShows; }

        /// <summary>Showings already spent on a cue.</summary>
        public static int Seen(long bits, NarrationCue cue)
        {
            if (!IsCue(cue)) return 0;
            return (int)((bits >> (BitsPerCue * (int)cue)) & CueMask);
        }

        /// <summary>Whether the band still names this cue, without spending a showing.</summary>
        public static bool Due(long bits, NarrationCue cue) { return !IsCue(cue) || Seen(bits, cue) < Cap(cue); }

        /// <summary>
        /// Spends one showing of a cue and says whether this use is narrated. A cue already at its
        /// cap is left unchanged and answers false. A value outside the enum always narrates and is
        /// never recorded, so a future cue an old build cannot place is never silently swallowed.
        /// </summary>
        public static bool Use(ref long bits, NarrationCue cue)
        {
            if (!IsCue(cue)) return true;
            int seen = Seen(bits, cue);
            if (seen >= Cap(cue)) return false;
            int shift = BitsPerCue * (int)cue;
            bits = (bits & ~(CueMask << shift)) | ((long)(seen + 1) << shift);
            return true;
        }

        /// <summary>The cue for a special the move created.</summary>
        public static NarrationCue Made(SpecialType special)
        {
            return special == SpecialType.Bomb ? NarrationCue.MadeBomb : special == SpecialType.Prism ? NarrationCue.MadePrism : NarrationCue.MadeRocket;
        }

        /// <summary>The cue for a special the player fired.</summary>
        public static NarrationCue Fired(SpecialType special)
        {
            return special == SpecialType.Bomb ? NarrationCue.FiredBomb : special == SpecialType.Prism ? NarrationCue.FiredPrism : NarrationCue.FiredRocket;
        }

        /// <summary>The cue for the Core's combination name (<c>MoveResult.Combo</c>). The Core writes a
        /// prism's partner by its enum name, so "Prism + RocketH" and "Prism + RocketV" are the same
        /// combination as "Prism + Rocket".</summary>
        public static NarrationCue ForCombo(string combo)
        {
            switch (combo)
            {
                case "Rocket + Rocket": return NarrationCue.ComboRocketRocket;
                case "Bomb + Rocket": return NarrationCue.ComboBombRocket;
                case "Bomb + Bomb": return NarrationCue.ComboBombBomb;
                case "Prism + Rocket":
                case "Prism + RocketH":
                case "Prism + RocketV": return NarrationCue.ComboPrismRocket;
                case "Prism + Bomb": return NarrationCue.ComboPrismBomb;
                case "Prism + Prism": return NarrationCue.ComboPrismPrism;
                default: return NarrationCue.ComboOther;
            }
        }
    }
}
