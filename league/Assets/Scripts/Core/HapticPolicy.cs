using System;

namespace BloomHarbor
{
    /// <summary>What a haptic pulse is being asked to express.</summary>
    public enum HapticMoment
    {
        /// <summary>A refused tap. The shortest tick the platform can make.</summary>
        Refusal,
        /// <summary>An ordinary clear worth feeling.</summary>
        Clear,
        /// <summary>A combo, a created special, or a Breeze.</summary>
        Flourish,
        /// <summary>The level is won. The only moment allowed to be emphatic.</summary>
        Win,
    }

    /// <summary>
    /// Whether a haptic pulse should fire, and how strong.
    ///
    /// Pure C# and side-effect free so the offline suite can exercise the policy without a device,
    /// which matters because the thing most likely to be wrong about haptics cannot be seen in a
    /// screenshot and will not be noticed until a player says the phone "buzzes constantly".
    ///
    /// The rule that earns its place is the rate limit. A blast puzzle cascades: one tap can clear
    /// a group, drop a wave, and clear again. The previous implementation called
    /// <c>Handheld.Vibrate()</c> on every clear of fifteen or more, which on Android is a single
    /// fixed buzz of roughly half a second with no way to shorten it — so a good cascade queued
    /// several overlapping half-second buzzes and the phone felt broken rather than responsive.
    /// Pulses are therefore spaced, and a stronger moment is allowed to interrupt a weaker one
    /// because a win should never be swallowed by the tick that preceded it.
    /// </summary>
    public static class HapticPolicy
    {
        /// <summary>Shortest gap between two pulses of the same or lesser weight.</summary>
        public const double MinimumGapSeconds = .22;

        /// <summary>
        /// A clear has to be worth feeling. Below this the tap already has visual and audible
        /// feedback, and a pulse for every small match is what makes a phone feel noisy.
        /// </summary>
        public const int ClearFeelThreshold = 15;

        /// <summary>
        /// Decides a pulse. <paramref name="lastSeconds"/> is when the previous pulse fired on the
        /// same clock as <paramref name="nowSeconds"/>; pass a negative value when none has.
        /// </summary>
        public static bool ShouldPulse(HapticMoment moment, bool enabled, bool reducedMotion,
            int clearedCount, double nowSeconds, double lastSeconds, HapticMoment lastMoment)
        {
            if (!enabled) return false;
            // Reduced motion is a comfort setting, and a buzz is motion the player did not ask for.
            // A refusal still passes: it is feedback that an action did not happen, not decoration.
            if (reducedMotion && moment != HapticMoment.Refusal) return false;
            if (moment == HapticMoment.Clear && clearedCount < ClearFeelThreshold) return false;
            if (lastSeconds < 0) return true;
            double gap = nowSeconds - lastSeconds;
            // A clock that moved backwards — a new attempt, a reset — is treated as "no recent pulse"
            // rather than as an enormous gap or a negative one.
            if (gap < 0) return true;
            if (gap >= MinimumGapSeconds) return true;
            // Inside the gap, only a genuinely weightier moment may interrupt.
            return moment > lastMoment;
        }

        /// <summary>
        /// The pulse length in seconds. Platforms that can only manage one fixed buzz ignore this;
        /// those that can vary it get a shape rather than a single blunt hit.
        /// </summary>
        public static double DurationSeconds(HapticMoment moment)
        {
            switch (moment)
            {
                case HapticMoment.Refusal: return .012;
                case HapticMoment.Clear: return .020;
                case HapticMoment.Flourish: return .035;
                case HapticMoment.Win: return .070;
                default: return .020;
            }
        }
    }
}
