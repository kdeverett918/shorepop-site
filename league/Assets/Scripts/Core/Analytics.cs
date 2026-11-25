using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BloomHarbor
{
    /// <summary>The events the game is allowed to report. One place, so a name cannot drift between
    /// the call site and the dashboard that counts it, and so a typo fails a test rather than
    /// quietly producing a metric nobody notices is missing for a month. docs/analytics-events.md is
    /// the dictionary; tests/analytics fails when the two disagree.</summary>
    public static class AnalyticsEvents
    {
        public const string SessionStart = "session_start";
        public const string SessionEnd = "session_end";
        public const string InstallMetadata = "install_metadata";

        public const string LevelStart = "level_start";
        public const string LevelEnd = "level_end";
        public const string LevelQuit = "level_quit";
        public const string CoastCleared = "coast_cleared";

        public const string EconomyEarn = "economy_earn";
        public const string EconomySpend = "economy_spend";

        public const string BoosterReserved = "booster_reserved";
        public const string BoosterSpent = "booster_spent";

        public const string LivesDepleted = "lives_depleted";
        public const string LivesRefilled = "lives_refilled";

        public const string StoreOpened = "store_opened";
        public const string PurchaseStarted = "purchase_started";
        public const string PurchaseCompleted = "purchase_completed";
        public const string PurchaseFailed = "purchase_failed";

        public const string AdOffered = "ad_offered";
        public const string AdStarted = "ad_started";
        public const string AdRewarded = "ad_rewarded";
        public const string AdFailed = "ad_failed";

        public const string DailyGiftClaimed = "daily_gift_claimed";
        public const string DailyChallengeStarted = "daily_challenge_started";
        public const string DailyChallengeCompleted = "daily_challenge_completed";

        public const string TutorialStep = "tutorial_step";
        public const string HarborProjectBought = "harbor_project_bought";

        // The first-session funnel: first board, first tap, first win, first Home, first harbor
        // purchase, each reported once per install. Separate from TutorialStep, which is the help
        // pages being offered, because mixing the two makes the funnel's denominators meaningless.
        public const string FtueStep = "ftue_step";

        // The meta loop (design/specs/meta-loop-r16.md): the Tailwind streak moving, and a chest
        // opened on Home. Together they answer whether the streak brings players back to the next
        // level and whether a chest is claimed the session it is earned.
        public const string StreakChanged = "streak_changed";
        public const string ChestOpened = "chest_opened";

        // The fishing mini-game. Its own pair rather than level_start/level_end with a mode flag,
        // so a campaign dashboard never has to remember to filter the fishing out of its funnel.
        public const string FishingCast = "fishing_cast";
        public const string FishingCatch = "fishing_catch";
        // Fishing WP4 (2026-10-09): the rest of the fishing funnel -- the screen opened and closed,
        // the strike, the empty-stock router, every ticket faucet, the album and the odds sheet --
        // and the level-gated unlock. Keys were checked against the relay's substring stripping
        // (ip, mac, token, auth, location): none carries one.
        public const string FishingOpen = "fishing_open";
        public const string FishingHook = "fishing_hook";
        public const string FishingEmpty = "fishing_empty";
        public const string FishingTicketsGranted = "fishing_tickets_granted";
        public const string FishingAlbum = "fishing_album";
        public const string FishingClose = "fishing_close";
        public const string FishingOddsViewed = "fishing_odds_viewed";

        // Session guidance (design/specs/session-guidance.md §6). These four answer which prompts
        // earn their place: a prompt with a high dismissal rate and a low accept rate should be
        // deleted rather than reworded, and that decision needs both numbers.
        public const string PromptShown = "prompt_shown";
        public const string PromptAccepted = "prompt_accepted";
        public const string PromptDismissed = "prompt_dismissed";
        public const string TeachingCardShown = "teaching_card_shown";
        // The FeatureGate (design uplift S2 #6, UX-07): the win that opens a Home feature (the same name and shape the
        // fishing lane declared for D-FISH-6), and how the player answered the fox's spotlight on it.
        public const string FeatureUnlocked = "feature_unlocked";
        public const string FeatureSpotlight = "feature_spotlight";

        // The Big Wave (design/specs/big-wave-r17.md): one per break on a campaign board. Answers how
        // often it breaks, how much it pays and how often it is the move that wins the level.
        public const string WaveBreak = "wave_break";

        // The one-time mechanic intro card before play (lead decision 2026-09-23): whether a new
        // mechanic's card is shown, tried in its practice board or skipped straight to play.
        public const string MechanicIntro = "mechanic_intro";

        // The restoration coast (design/specs/coast-r17.md): the fox's post-win hop to the new
        // frontier, and a landmark relit or restyled with stars. Together they answer whether the
        // coast's arrival lands and whether stars are spent on landmarks or hoarded.
        public const string CoastArrival = "coast_arrival";
        public const string LandmarkRelit = "landmark_relit";

        // Consent that does not tax LTV (studio standard, 2026-09-26): the effective analytics answer
        // and where it came from, once per change. Carries a region class, never a country.
        public const string ConsentResult = "consent_result";

        // Impression-level ad revenue, for value-based UA. Reported only from a real ad network's paid
        // callback (BloomApp.ReportAdRevenue), with native precision and test provenance.
        public const string AdRevenue = "ad_revenue";
        // Profit round lane 3 (owner-approved catalog, 2026-09-26): the offer funnel (a card or row shown,
        // accepted, dismissed), the checkout it opens, the lose-screen continue ladder and its deficit
        // bridge, and the Shell Bank. Together they answer which offers earn their place and what the
        // fail screen sells.
        public const string OfferShown = "offer_shown";
        public const string OfferAccepted = "offer_accepted";
        public const string OfferDismissed = "offer_dismissed";
        public const string CheckoutStart = "checkout_start";
        public const string ContinueOffered = "continue_offered";
        public const string ContinueUsed = "continue_used";
        public const string DeficitBridgeShown = "deficit_bridge_shown";
        public const string ShellBankDeposit = "shell_bank_deposit";
        public const string ShellBankBreak = "shell_bank_break";
        // Profit round lane 5 (2026-09-26): local reminders. The permission ask (after the first win, never at
        // boot, never rewarded) and its answer, each reminder put on the device, and each one tapped.
        public const string NotificationPermissionPrompted = "notification_permission_prompted";
        public const string NotificationPermissionGranted = "notification_permission_granted";
        public const string NotificationPermissionDenied = "notification_permission_denied";
        public const string NotificationScheduled = "notification_scheduled";
        public const string NotificationOpened = "notification_opened";
        // WO-20261111-353: OS-verified notification delivery read back from active shade StatusBarNotifications.
        public const string PushFired = "push_fired";
        // Fleet standard v3 (WO-20261104-108): the in-app store review ask lifecycle (requested, launched, failed, skipped).
        public const string ReviewPrompt = "review_prompt";
        // Wave 4 attribution, remote difficulty and restore-purchases telemetry.
        public const string AdValueMilestone = "ad_value_milestone";
        public const string MetaCombo = "meta_combo";
        public const string DifficultyAssigned = "difficulty_assigned";
        public const string DifficultyFallback = "difficulty_fallback";
        public const string RestorePurchasesResult = "restore_purchases_result";

        // Canonical v4 lifecycle, FTUE, retention and VTE record events (WO-20261020-93).
        public const string FirstOpen = "first_open";
        public const string TutorialStart = "tutorial_start";
        public const string TutorialComplete = "tutorial_complete";
        public const string FirstPurchase = "first_purchase";
        public const string D1Return = "d1_return";
        public const string AttributionMilestone = "attribution_milestone";

        // Day-0 signal stack atomic events (WO-20261114-83, game-launch-ops ref 36 §A).
        public const string InterstitialSurvived = "interstitial_survived";
        public const string StoreSurfaceClose = "store_surface_close";
        public const string AudioToggle = "audio_toggle";
        public const string MetaAction = "meta_action";
        public const string ResourceEmpty = "resource_empty";

        // Day-0 profile, golden event (WO-20261116-69 / 0ba3d3ae), composites and shadow value (ref 36 §B, §C, §I).
        public const string D0Profile = "d0_profile";
        public const string D0Golden = "d0_golden";
        public const string D0ReturnProgress = "d0_return_progress";
        public const string D0FrictionSurvived = "d0_friction_survived";
        public const string D0RvReturn = "d0_rv_return";
        public const string D0ShopperIntent = "d0_shopper_intent";
        public const string D0Value = "d0_value";

        // Revenue Pulse analytics rows (WO-20261119-63 / 95523951, game-launch-ops ref 37).
        public const string PulseOpen = "pulse_open";
        public const string RevPulseBuy = "rev_pulse_buy";
        public const string QualRung = "qual_rung";
        public const string RevUnit = "rev_unit";

        /// <summary>Every event above, for the suite and for the validator.</summary>
        public static readonly string[] All =
        {
            SessionStart, SessionEnd, InstallMetadata,
            LevelStart, LevelEnd, LevelQuit, CoastCleared,
            EconomyEarn, EconomySpend,
            BoosterReserved, BoosterSpent,
            LivesDepleted, LivesRefilled,
            StoreOpened, PurchaseStarted, PurchaseCompleted, PurchaseFailed,
            AdOffered, AdStarted, AdRewarded, AdFailed,
            DailyGiftClaimed, DailyChallengeStarted, DailyChallengeCompleted,
            TutorialStep, HarborProjectBought,
            FtueStep,
            StreakChanged, ChestOpened,
            FishingCast, FishingCatch,
            FishingOpen, FishingHook, FishingEmpty, FishingTicketsGranted, FishingAlbum, FishingClose, FishingOddsViewed,
            PromptShown, PromptAccepted, PromptDismissed, TeachingCardShown,
            FeatureUnlocked, FeatureSpotlight,
            WaveBreak, MechanicIntro,
            CoastArrival, LandmarkRelit,
            ConsentResult, AdRevenue,
            OfferShown, OfferAccepted, OfferDismissed, CheckoutStart,
            ContinueOffered, ContinueUsed, DeficitBridgeShown, ShellBankDeposit, ShellBankBreak,
            NotificationPermissionPrompted, NotificationPermissionGranted, NotificationPermissionDenied, NotificationScheduled, NotificationOpened, PushFired,
            ReviewPrompt,
            AdValueMilestone, MetaCombo, DifficultyAssigned, DifficultyFallback, RestorePurchasesResult,
            FirstOpen, TutorialStart, TutorialComplete, FirstPurchase, D1Return, AttributionMilestone,
            InterstitialSurvived, StoreSurfaceClose, AudioToggle, MetaAction, ResourceEmpty,
            D0Profile, D0Golden, D0ReturnProgress, D0FrictionSurvived, D0RvReturn, D0ShopperIntent, D0Value,
            PulseOpen, RevPulseBuy, QualRung, RevUnit,
        };
    }

    /// <summary>How a parameter's string is to be read by a provider that types its columns.</summary>
    public enum AnalyticsType { Int, Number, Bool, Text }

    /// <summary>One declared parameter of one event.</summary>
    public sealed class AnalyticsField
    {
        public readonly string Key;
        public readonly AnalyticsType Type;
        public readonly bool Required;

        public AnalyticsField(string key, AnalyticsType type, bool required)
        {
            Key = key; Type = type; Required = required;
        }
    }

    /// <summary>
    /// The parameters each event may carry, and their types. A provider with a typed event schema
    /// (UGS Analytics among them) rejects a whole event whose parameter is undeclared or of the
    /// wrong type, and it does so on the server, where nobody is looking. Checking here instead
    /// turns that into a counted local drop the suite can see, and gives the dashboard setup one
    /// list to copy.
    ///
    /// Deliberately absent: anything that identifies a person or a device. No player id, device id,
    /// advertising id, display name, store order id or free text. Text values are further limited
    /// to an identifier alphabet (see <see cref="Analytics.ValidText"/>), which is what makes "no
    /// e-mail address or sentence can ride along" a property rather than a hope.
    /// </summary>
    public static class AnalyticsSchema
    {
        static readonly Dictionary<string, Dictionary<string, AnalyticsField>> byEvent =
            new Dictionary<string, Dictionary<string, AnalyticsField>>(StringComparer.Ordinal);

        static AnalyticsField I(string key) { return new AnalyticsField(key, AnalyticsType.Int, false); }
        static AnalyticsField IR(string key) { return new AnalyticsField(key, AnalyticsType.Int, true); }
        static AnalyticsField B(string key) { return new AnalyticsField(key, AnalyticsType.Bool, false); }
        static AnalyticsField BR(string key) { return new AnalyticsField(key, AnalyticsType.Bool, true); }
        static AnalyticsField N(string key) { return new AnalyticsField(key, AnalyticsType.Number, false); }
        static AnalyticsField NR(string key) { return new AnalyticsField(key, AnalyticsType.Number, true); }
        static AnalyticsField T(string key) { return new AnalyticsField(key, AnalyticsType.Text, false); }
        static AnalyticsField TR(string key) { return new AnalyticsField(key, AnalyticsType.Text, true); }

        static void Define(string name, params AnalyticsField[] fields)
        {
            var map = new Dictionary<string, AnalyticsField>(StringComparer.Ordinal);
            foreach (var field in fields) map[field.Key] = field;
            byEvent[name] = map;
        }

        static AnalyticsSchema()
        {
            Define(AnalyticsEvents.SessionStart, TR("kind"), IR("days_since_install"), I("sessions"), I("clears"), I("lives"),
                T("difficulty_schedule"), I("gap_s"), I("local_hour"), I("local_min"), T("installer_package"),
                T("traffic_type"), T("test_traffic_source"), T("geo_gate_status"), T("geo_gate_source"));
            Define(AnalyticsEvents.SessionEnd, TR("reason"), IR("seconds"), I("clears"), I("lives"), I("duration_s"), I("active_s"));
            Define(AnalyticsEvents.InstallMetadata, TR("reason"));

            Define(AnalyticsEvents.LevelStart, IR("level"), I("coast"), IR("attempt"), I("stars_best"), I("lives"),
                I("boosters"), I("booster_coins"), I("tailwind_tier"), I("streak"), T("difficulty_tier"),
                B("voluntary_continuation"), I("retry_index"), T("attempt_content_fingerprint"), T("diff_arm"), I("level_id"), B("revisit"), I("mercy_moves"));
            Define(AnalyticsEvents.LevelEnd, IR("level"), TR("result"), I("stars"), I("score"), I("target"),
                I("moves_left"), I("coins"), B("new_best"), I("attempt"), I("boosters"), I("tailwind_tier"), I("coast"),
                I("retry_index"), T("attempt_content_fingerprint"), I("level_id"), N("duration_s"), I("near_miss"), N("remaining_goal_pct"), B("revisit"));
            Define(AnalyticsEvents.LevelQuit, IR("level"), I("moves_left"), I("score"), I("target"), I("attempt"),
                T("end_reason"), I("retry_index"), I("moves_used"), I("level_id"), N("duration_s"));
            Define(AnalyticsEvents.CoastCleared, IR("coast"), I("stars"), I("coins"));

            Define(AnalyticsEvents.EconomyEarn, TR("source"), TR("currency"), IR("amount"), I("balance"), I("level"), I("stars"));
            Define(AnalyticsEvents.EconomySpend, TR("sink"), TR("currency"), IR("amount"), I("balance"), I("level"), I("tickets"),
                I("voluntary"), I("in_tutorial"));

            Define(AnalyticsEvents.BoosterReserved, TR("booster"), B("selected"), I("cost"), I("level"));
            Define(AnalyticsEvents.BoosterSpent, IR("mask"), I("coins"), I("credited"), I("level"));

            Define(AnalyticsEvents.LivesDepleted, IR("level"), I("coast"));
            Define(AnalyticsEvents.LivesRefilled, I("lives"), TR("source"));

            Define(AnalyticsEvents.StoreOpened, I("clears"), I("gems"), I("coins"), T("surface"), T("trigger"),
                T("geo_gate_status"), T("geo_gate_source"));
            Define(AnalyticsEvents.PurchaseStarted, TR("product"), T("attempt"));
            // Price and currency come as a pair or not at all (RevenueReport); a restore carries neither.
            // transaction_ref is a truncated SHA-256 digest, never the store's transaction id.
            Define(AnalyticsEvents.PurchaseCompleted, TR("product"), T("product_id"), I("price_minor_units"), T("price_currency"), B("sandbox"),
                T("transaction_ref"), T("order_verdict"));
            Define(AnalyticsEvents.PurchaseFailed, TR("product"), TR("stage"), T("reason"));

            Define(AnalyticsEvents.AdOffered, TR("placement"), T("state"), B("in_game"));
            Define(AnalyticsEvents.AdStarted, TR("placement"), T("attempt"));
            Define(AnalyticsEvents.AdRewarded, TR("placement"), T("attempt"));
            Define(AnalyticsEvents.AdFailed, TR("placement"), T("reason"));

            Define(AnalyticsEvents.DailyGiftClaimed, I("coins"), T("day"), I("tickets"));
            Define(AnalyticsEvents.DailyChallengeStarted, I("completed_before"), I("level"));
            Define(AnalyticsEvents.DailyChallengeCompleted, I("coins"), I("gems"), I("completed"));

            Define(AnalyticsEvents.TutorialStep, IR("bit"), I("clears"), T("lesson"));
            Define(AnalyticsEvents.HarborProjectBought, TR("kind"), I("index"), I("coast"), I("cost"), I("balance"));

            Define(AnalyticsEvents.FtueStep, TR("step"), I("step_index"), I("level"), I("session_seconds"), I("days_since_install"), T("step_name"));

            Define(AnalyticsEvents.StreakChanged, I("level"), I("from"), I("to"), I("tier"), T("result"));
            Define(AnalyticsEvents.ChestOpened, TR("kind"), I("index"), I("coins"), I("overflow_coins"), I("tickets"),
                T("booster"), T("ability"), I("balance"), I("gems"));

            Define(AnalyticsEvents.FishingCast, I("tickets"), I("cast_index"), I("lifetime_casts"), N("power"),
                N("cast_quality"), B("on_hotspot"), T("paid_source"), T("rod"), T("bait"), T("spot"));
            // species is the reward family (kept for the existing dashboard); species_id names the fish.
            Define(AnalyticsEvents.FishingCatch, T("species"), T("species_id"), T("rarity"), T("size"), B("fumbled"),
                TR("grade"), T("failure"), N("length_cm"), N("fight_s"), B("new_slot"), B("new_record"), B("capped"),
                B("interrupted"), I("coins"), I("gems"), I("boosters"), I("lives"));
            Define(AnalyticsEvents.FishingOpen, TR("source"), I("tickets"), I("seconds_to_next"), I("lifetime_casts"));
            Define(AnalyticsEvents.FishingHook, TR("result"), I("latency_ms"), I("window_ms"), T("species_id"), T("rarity"));
            Define(AnalyticsEvents.FishingEmpty, B("can_watch"), B("can_gems"), B("can_coins"), I("gems"), I("coins"),
                I("seconds_to_next"));
            Define(AnalyticsEvents.FishingTicketsGranted, TR("source"), I("count"), I("landed"), I("banked"));
            Define(AnalyticsEvents.FishingAlbum, TR("action"), T("species_id"), T("rarity"), I("reward"));
            Define(AnalyticsEvents.FishingClose, I("casts"), I("seconds"), I("tickets_left"), T("exit"));
            Define(AnalyticsEvents.FishingOddsViewed, TR("trigger"));

            Define(AnalyticsEvents.PromptShown, TR("prompt"), I("priority"));
            Define(AnalyticsEvents.PromptAccepted, TR("prompt"));
            Define(AnalyticsEvents.PromptDismissed, TR("prompt"));
            Define(AnalyticsEvents.TeachingCardShown, TR("feature"));
            Define(AnalyticsEvents.FeatureUnlocked, TR("feature"), I("level"));
            Define(AnalyticsEvents.FeatureSpotlight, TR("feature"), TR("action"), I("level"));

            Define(AnalyticsEvents.WaveBreak, IR("level"), I("wave"), I("moves_left"), I("score"), I("cleared"),
                I("fired"), I("landed"), B("won"));
            Define(AnalyticsEvents.MechanicIntro, IR("level"), TR("mechanic"), TR("action"));

            Define(AnalyticsEvents.CoastArrival, IR("from"), IR("to"), I("world"));
            Define(AnalyticsEvents.LandmarkRelit, I("world"), IR("index"), I("style"), I("stars"), I("balance"), T("action"));

            Define(AnalyticsEvents.ConsentResult, TR("region_class"), BR("analytics_on"), TR("source"));
            Define(AnalyticsEvents.AdRevenue, NR("revenue_usd"), TR("network"), TR("format"), T("placement"),
                T("ad_unit"), T("precision"), B("test_ad"), T("dsp_name"), T("creative_id"),
                B("unrequested"), N("seconds_since_own_ad"), I("unrequested_count"));

            Define(AnalyticsEvents.OfferShown, TR("offer"), TR("placement"), B("rendered"));
            Define(AnalyticsEvents.OfferAccepted, TR("offer"), TR("placement"));
            Define(AnalyticsEvents.OfferDismissed, TR("offer"), TR("placement"), I("open_ms"));
            // product_id is the store catalogue id every buyer shares (the purchase_completed exemption).
            Define(AnalyticsEvents.CheckoutStart, TR("product_id"), TR("placement"));
            Define(AnalyticsEvents.ContinueOffered, IR("attempt_continue_index"), IR("price_gems"), TR("source"), I("level"));
            Define(AnalyticsEvents.ContinueUsed, TR("source"), I("attempt_continue_index"), I("price_gems"), I("level"));
            Define(AnalyticsEvents.DeficitBridgeShown, IR("shortfall_gems"), TR("product_id"), I("price_gems"));
            Define(AnalyticsEvents.ShellBankDeposit, IR("amount"), IR("balance"), I("level"));
            Define(AnalyticsEvents.ShellBankBreak, IR("gems"));

            Define(AnalyticsEvents.NotificationPermissionPrompted, TR("stage"), TR("trigger"));
            Define(AnalyticsEvents.NotificationPermissionGranted, TR("stage"), TR("trigger"));
            Define(AnalyticsEvents.NotificationPermissionDenied, TR("stage"), TR("trigger"));
            Define(AnalyticsEvents.NotificationScheduled, TR("kind"), IR("hours_ahead"));
            Define(AnalyticsEvents.NotificationOpened, TR("kind"));
            Define(AnalyticsEvents.PushFired, IR("notif_id"), TR("slug"), IR("delay_s"), TR("app_state"));
            Define(AnalyticsEvents.ReviewPrompt, TR("stage"), TR("context"), T("reason"), I("ask_index"), I("level"),
                I("day_since_install"), I("session_index"));
            Define(AnalyticsEvents.AdValueMilestone, TR("milestone"), IR("threshold_micros"), I("cumulative_micros"));
            Define(AnalyticsEvents.MetaCombo, IR("clears"), I("rewarded_count"), I("sessions"), B("is_payer"));
            Define(AnalyticsEvents.DifficultyAssigned, TR("diff_arm"), TR("diff_version"), T("diff_cell"), B("holdout"));
            Define(AnalyticsEvents.DifficultyFallback, TR("reason"), T("diff_arm"));
            Define(AnalyticsEvents.RestorePurchasesResult, TR("outcome"), I("restored_count"));

            // Canonical v4 lifecycle, FTUE, retention and VTE records.
            Define(AnalyticsEvents.FirstOpen, T("installer_package"), T("traffic_type"), T("test_traffic_source"),
                T("internal_reason"), I("notif_pregranted"));
            Define(AnalyticsEvents.TutorialStart, I("level"));
            Define(AnalyticsEvents.TutorialComplete, I("level"), N("hours_since_install"));
            Define(AnalyticsEvents.FirstPurchase, T("product_id"), I("price_minor_units"), T("price_currency"),
                T("transaction_ref"), T("order_verdict"));
            Define(AnalyticsEvents.D1Return, N("hours_since_install"), I("sessions"));
            Define(AnalyticsEvents.AttributionMilestone, TR("name"), I("rung_micros"), N("fact_h"), I("def_version"), I("cumulative_micros"));

            // Day-0 signal stack atomic events (PostHog-only in ref 36; admitted on spine so coverage & consent match).
            Define(AnalyticsEvents.InterstitialSurvived, TR("placement"), IR("gap_ms"), TR("next"));
            Define(AnalyticsEvents.StoreSurfaceClose, TR("surface"), IR("dwell_ms"), IR("voluntary"));
            Define(AnalyticsEvents.AudioToggle, TR("channel"), IR("on"));
            Define(AnalyticsEvents.MetaAction, TR("kind"), TR("item"));
            Define(AnalyticsEvents.ResourceEmpty, TR("resource"), TR("via"), I("level"));

            // Day-0 profile, golden event, composites and shadow value (full maps ride on PostHog via RichProperties).
            Define(AnalyticsEvents.D0Profile, IR("snap_v"), IR("snap_seq"), NR("snap_h"), IR("final"),
                I("sessions_n"), I("returns_n"), I("clears_n"), I("max_level"), I("rv_granted_n"),
                I("val_ad_micros"), I("val_iap_net_micros"), I("val_ad_unrequested_micros"), I("iap_orders_n"),
                I("ad_exempt"), I("legacy_fired"), T("acq_objective"), T("ad_arm"));
            Define(AnalyticsEvents.D0Golden, IR("rung"), IR("def_v"), NR("h"), IR("value_micros"), IR("on_slot"),
                T("variant"), T("branch"), T("acq_objective"), T("meta_event"),
                I("iap_orders_n"), I("val_ad_micros"), I("val_iap_net_micros"), I("val_ad_unrequested_micros"),
                I("returns_n"), I("max_gap_min"), I("max_level"), I("rv_granted_n"));
            Define(AnalyticsEvents.D0ReturnProgress, IR("def_v"), T("variant"), TR("branch"), NR("h"), T("acq_objective"),
                I("returns_n"), I("clears_n"), I("max_level"));
            Define(AnalyticsEvents.D0FrictionSurvived, IR("def_v"), T("variant"), TR("branch"), NR("h"), T("acq_objective"),
                I("near_miss_recoveries_n"), I("clears_n"), I("max_level"), I("fails_n"), I("post_fail_clears_n"));
            Define(AnalyticsEvents.D0RvReturn, IR("def_v"), T("variant"), TR("branch"), NR("h"), T("acq_objective"),
                I("rv_granted_n"), I("returns_n"), I("max_level"));
            Define(AnalyticsEvents.D0ShopperIntent, IR("def_v"), T("variant"), TR("branch"), NR("h"), T("acq_objective"),
                I("returns_n"), I("store_voluntary_n"), I("offer_dwell_n"), I("empty_plus_taps_n"),
                I("sink_spend_n"), N("starter_depletion_pct"), I("iap_orders_n"));
            Define(AnalyticsEvents.D0Value, IR("def_v"), NR("value_usd"), I("val_ad_micros"), I("val_iap_micros"), T("acq_objective"));

            // Revenue Pulse analytics rows (contracts/rev-pulse 95523951).
            Define(AnalyticsEvents.PulseOpen, IR("opener"), T("pulse_country"));
            Define(AnalyticsEvents.RevPulseBuy, IR("seq"), IR("cents"), IR("gate"));
            Define(AnalyticsEvents.QualRung, IR("rung"), IR("wins"), IR("net_cents"), T("pulse_country"), NR("age_h"));
            Define(AnalyticsEvents.RevUnit, IR("unit_n"), IR("net_cents"), T("pulse_country"), NR("age_h"));
        }

        /// <summary>True when the event has a declared parameter set (every allowlisted one does).</summary>
        public static bool Has(string name) { return name != null && byEvent.ContainsKey(name); }

        /// <summary>The declared parameters of one event, or an empty list.</summary>
        public static IEnumerable<AnalyticsField> Fields(string name)
        {
            Dictionary<string, AnalyticsField> map;
            if (name == null || !byEvent.TryGetValue(name, out map)) return new AnalyticsField[0];
            return map.Values;
        }

        public static bool TryGetField(string name, string key, out AnalyticsField field)
        {
            field = null;
            Dictionary<string, AnalyticsField> map;
            return name != null && key != null && byEvent.TryGetValue(name, out map) && map.TryGetValue(key, out field);
        }

        /// <summary>Whether a string is a well-formed value of a type, the way the validator reads it.</summary>
        public static bool Conforms(AnalyticsType type, string value)
        {
            if (value == null) return false;
            switch (type)
            {
                case AnalyticsType.Int:
                    long whole;
                    return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out whole);
                case AnalyticsType.Number:
                    double number;
                    return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                        !double.IsNaN(number) && !double.IsInfinity(number);
                case AnalyticsType.Bool:
                    return value == "true" || value == "false";
                case AnalyticsType.Text:
                    return Analytics.ValidText(value);
                default:
                    return false;
            }
        }
    }

    /// <summary>One reported event: a name from the allowlist, a UTC stamp and flat parameters.</summary>
    [Serializable]
    public sealed class AnalyticsOrigin
    {
        public string build = "";
        public string versionCode = "";
        public string buildSha = "";
        public string appVersion = "";
        public string contentVersion = "";
        public string contentDigest = "";
        public string rulesVersion = "";
        public string configVersion = "";
        public bool buildDirty;
        public int levelReached;
        public int sessionNumber;
        public long installUtcTicks;
        public bool installKnown;
    }

    public sealed class AnalyticsEvent
    {
        public string Name;
        public DateTime UtcTime;
        // Optional checkout-time snapshot. No store receipt or personal data is retained.
        public AnalyticsOrigin Origin;
        public readonly Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Optional PostHog-only rich properties (e.g. full d0_profile / d0_golden maps, semicolon-formatted params).
        /// Ignored by typed UGS/Firebase spine mappers.</summary>
        public Dictionary<string, object> RichProperties;
        /// <summary>Optional PostHog person properties ($set) attached to this event (WO-20261029-64).</summary>
        public Dictionary<string, object> PersonSet;

        public override string ToString()
        {
            var builder = new StringBuilder(Name);
            foreach (var pair in Parameters) builder.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Durable pre-consent hold store (WO-20261114-1415): persists events held before UMP resolves so
    /// boot one-shots (first_open, d1_return, tutorial_start) survive a kill before consent and never
    /// re-fire on relaunch, while being wiped immediately on explicit consent refusal or after flush.
    /// </summary>
    public interface IPreConsentHoldStore
    {
        List<AnalyticsEvent> Load();
        bool Save(IReadOnlyList<AnalyticsEvent> events);
        void Clear();
    }

    /// <summary>
    /// A destination for events. Implemented per provider outside Core, so choosing a provider is a
    /// one-class decision rather than a rewrite of every call site. Nothing in the game references a
    /// provider type.
    /// </summary>
    public interface IAnalyticsSink
    {
        void Send(AnalyticsEvent reported);
    }

    /// <summary>Confirms local durable enqueue only, never provider receipt or delivery.</summary>
    public interface IDurableAnalyticsSink
    {
        bool SendDurably(AnalyticsEvent reported);
    }

    /// <summary>
    /// Optional on a sink: told when the player's answer changes, and once on configuration if the
    /// answer is already known. A provider that collects anything of its own (a session heartbeat,
    /// an installation id) has to be switched on and off by the same answer that gates events, or
    /// refusing would stop the game's events and leave the provider's running.
    /// </summary>
    public interface IAnalyticsConsentListener
    {
        void ConsentChanged(bool granted);
    }

    /// <summary>
    /// The game's event spine: provider-neutral, consent-gated and incapable of interrupting play.
    ///
    /// Pure C# with no UnityEngine reference, so the offline suite exercises the exact validator the
    /// player runs. Three rules shape everything here, and each exists because the opposite is a
    /// well-known way to lose a launch:
    ///
    ///   * **Nothing here may throw.** A sink is third-party code on a background thread on a phone
    ///     model nobody tested. An exception escaping analytics must never reach the game loop, so
    ///     every dispatch is guarded and failures are counted, not raised.
    ///   * **Nothing is reported without consent.** Events are buffered before consent is answered
    ///     (optionally persisted via <see cref="IPreConsentHoldStore"/> so boot one-shots survive a
    ///     pre-consent kill without re-firing) and released only once granted; a refusal erases the
    ///     held buffer immediately so nothing leaks.
    ///   * **A name, parameter or value that is not declared is dropped and counted.** A dashboard
    ///     that is quietly missing a metric is worse than one that is obviously broken.
    /// </summary>
    public static class Analytics
    {
        /// <summary>Events held while consent is unanswered. Beyond this the oldest non-boot event
        /// is dropped so boot one-shots (first_open, d1_return, tutorial_start) cannot be evicted.</summary>
        public const int PendingLimit = 256;

        /// <summary>Longest a parameter value may be. Providers truncate silently; this does not.</summary>
        public const int ValueLimit = 96;

        /// <summary>
        /// Most parameters one event may carry. 20 since fishing WP4 (2026-10-09), when fishing_catch
        /// grew to 17: PostHog takes any number of properties per event and GA4/Firebase takes 25, so
        /// 20 keeps headroom under the strictest provider the studio uses.
        /// </summary>
        public const int ParameterLimit = 20;

        /// <summary>Hard cap on accepted-subscriber notifications drained per entry (WO-20261121-143).</summary>
        public const int MaxAcceptedDrainPerCall = 128;

        static readonly HashSet<string> allowed = new HashSet<string>(AnalyticsEvents.All, StringComparer.Ordinal);
        static readonly HashSet<string> bootOneShots = new HashSet<string>(StringComparer.Ordinal)
        {
            AnalyticsEvents.FirstOpen,
            AnalyticsEvents.D1Return,
            AnalyticsEvents.TutorialStart,
            AnalyticsEvents.TutorialComplete,
            AnalyticsEvents.FirstPurchase,
        };
        static readonly List<AnalyticsEvent> pending = new List<AnalyticsEvent>();
        static readonly List<Action<AnalyticsEvent>> acceptedSubscribers = new List<Action<AnalyticsEvent>>();
        static readonly Queue<AnalyticsEvent> acceptedQueue = new Queue<AnalyticsEvent>();
        static IAnalyticsSink sink;
        static IPreConsentHoldStore preConsentStore;
        static bool? consent;
        static bool notifyingAccepted;

        public static int DroppedUnknownName { get; private set; }
        public static int DroppedInvalidParameter { get; private set; }
        /// <summary>Well-formed parameters the event does not declare, values of the wrong type,
        /// and declared-required parameters that are missing.</summary>
        public static int DroppedSchema { get; private set; }
        public static int DroppedOverflow { get; private set; }
        public static int SinkFailures { get; private set; }
        public static int Reported { get; private set; }
        public static int PendingCount { get { return pending.Count; } }
        public static int PendingAcceptedNotifications { get { return acceptedQueue.Count; } }

        /// <summary>True once consent has been answered, either way.</summary>
        public static bool ConsentAnswered { get { return consent.HasValue; } }
        public static bool ConsentGranted { get { return consent.HasValue && consent.Value; } }

        /// <summary>Boot one-shots exempt from pre-consent hold-cap eviction (WO-20261114-1415).</summary>
        public static bool IsBootOneShot(string name)
        {
            return name != null && bootOneShots.Contains(name);
        }

        public static void Reset()
        {
            pending.Clear();
            acceptedSubscribers.Clear();
            acceptedQueue.Clear();
            notifyingAccepted = false;
            sink = null;
            preConsentStore = null;
            consent = null;
            DroppedUnknownName = DroppedInvalidParameter = DroppedSchema = DroppedOverflow = SinkFailures = Reported = 0;
        }

        /// <summary>Installs the destination. Safe before or after consent; buffered events flush
        /// as soon as both a sink and a granted consent exist.</summary>
        public static void Configure(IAnalyticsSink destination)
        {
            sink = destination;
            if (consent.HasValue) NotifyConsent();
            Flush();
        }

        /// <summary>Installs a durable pre-consent hold store (WO-20261114-1415). Restores any held
        /// rows saved before a prior kill; flushes them if consent is already granted or wipes them
        /// if consent was refused.</summary>
        public static void ConfigurePreConsentStore(IPreConsentHoldStore store)
        {
            preConsentStore = store;
            if (store == null) return;
            if (consent.HasValue && !consent.Value)
            {
                ClearPreConsentStore();
                return;
            }
            try
            {
                var loaded = store.Load();
                if (loaded != null && loaded.Count > 0)
                {
                    var existingBoot = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < pending.Count; i++)
                        if (IsBootOneShot(pending[i].Name)) existingBoot.Add(pending[i].Name);
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        var item = loaded[i];
                        if (item == null || string.IsNullOrEmpty(item.Name) || !allowed.Contains(item.Name)) continue;
                        if (IsBootOneShot(item.Name) && !existingBoot.Add(item.Name)) continue;
                        AddPending(item);
                    }
                }
            }
            catch (Exception) { SinkFailures++; }
            if (consent.HasValue && consent.Value) Flush();
        }

        /// <summary>Subscribes to accepted spine events. Callbacks are dispatched iteratively with a
        /// re-entrancy guard (WO-20261121-143) so publishing inside a subscriber cannot recurse.</summary>
        public static void SubscribeAccepted(Action<AnalyticsEvent> listener)
        {
            if (listener == null || acceptedSubscribers.Contains(listener)) return;
            acceptedSubscribers.Add(listener);
        }

        public static void UnsubscribeAccepted(Action<AnalyticsEvent> listener)
        {
            if (listener == null) return;
            acceptedSubscribers.Remove(listener);
        }

        /// <summary>Drains any accepted-event notifications deferred by the re-entrancy cap.</summary>
        public static void DrainAcceptedSubscribers()
        {
            if (notifyingAccepted || acceptedQueue.Count == 0 || acceptedSubscribers.Count == 0) return;
            notifyingAccepted = true;
            try
            {
                int drained = 0;
                while (acceptedQueue.Count > 0 && drained < MaxAcceptedDrainPerCall)
                {
                    var ev = acceptedQueue.Dequeue();
                    drained++;
                    var snapshot = acceptedSubscribers.ToArray();
                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        try { snapshot[i](ev); }
                        catch (Exception) { SinkFailures++; }
                    }
                }
            }
            finally { notifyingAccepted = false; }
        }

        /// <summary>
        /// Records the player's answer. Granting releases whatever was buffered; refusing discards
        /// it (and wipes any durable pre-consent hold), because an event held from before a refusal
        /// is exactly the data a refusal covers.
        /// </summary>
        public static void SetConsent(bool granted)
        {
            consent = granted;
            if (!granted)
            {
                pending.Clear();
                ClearPreConsentStore();
            }
            NotifyConsent();
            if (granted) Flush();
        }

        /// <summary>Starts a parameter set. Never null, so a call site never has to branch.</summary>
        public static AnalyticsParameters Parameters()
        {
            return new AnalyticsParameters();
        }

        /// <summary>
        /// Reports an event, or drops it and says so in a counter. Returns whether it was accepted
        /// (including durably held before consent resolves per WO-20261114-1415).
        /// </summary>
        public static bool Track(string name, DateTime utcNow, AnalyticsParameters parameters = null)
        {
            return TrackWithRich(name, utcNow, parameters, null, null);
        }

        /// <summary>
        /// Reports an event with optional PostHog-only rich properties and/or $set person properties.
        /// Validates the spine schema identically to <see cref="Track"/>.
        /// </summary>
        public static bool TrackWithRich(string name, DateTime utcNow, AnalyticsParameters parameters,
            IDictionary<string, object> richProperties = null, IDictionary<string, object> personSet = null)
        {
            AnalyticsEvent reported;
            if (!TryBuild(name, utcNow, parameters, out reported)) return false;
            if (richProperties != null && richProperties.Count > 0)
                reported.RichProperties = new Dictionary<string, object>(richProperties, StringComparer.Ordinal);
            if (personSet != null && personSet.Count > 0)
                reported.PersonSet = new Dictionary<string, object>(personSet, StringComparer.Ordinal);

            if (consent.HasValue && consent.Value && sink != null)
            {
                bool sent = Dispatch(reported);
                if (sent) NotifyAccepted(reported);
                return sent;
            }
            if (consent.HasValue && !consent.Value) return false;
            AddPending(reported);
            PersistPreConsentHold();
            NotifyAccepted(reported);
            return true;
        }

        /// <summary>The caller retains its outbox unless the durable sink accepts locally. Unknown
        /// consent, an unavailable provider, storage failure or capacity backpressure returns false;
        /// this path never enters the spine's memory-only pending queue.</summary>
        public static bool TrackDurably(string name, DateTime utcNow, AnalyticsParameters parameters = null, AnalyticsOrigin origin = null)
        {
            return TrackDurablyWithRich(name, utcNow, parameters, origin, null, null);
        }

        public static bool TrackDurablyWithRich(string name, DateTime utcNow, AnalyticsParameters parameters,
            AnalyticsOrigin origin, IDictionary<string, object> richProperties, IDictionary<string, object> personSet)
        {
            AnalyticsEvent reported;
            if (!TryBuild(name, utcNow, parameters, out reported) || !ConsentGranted) return false;
            reported.Origin = origin;
            if (richProperties != null && richProperties.Count > 0)
                reported.RichProperties = new Dictionary<string, object>(richProperties, StringComparer.Ordinal);
            if (personSet != null && personSet.Count > 0)
                reported.PersonSet = new Dictionary<string, object>(personSet, StringComparer.Ordinal);
            var durable = sink as IDurableAnalyticsSink;
            if (durable == null) return false;
            try
            {
                if (!durable.SendDurably(reported)) return false;
                Reported++;
                NotifyAccepted(reported);
                return true;
            }
            catch (Exception) { SinkFailures++; return false; }
        }

        static void AddPending(AnalyticsEvent reported)
        {
            if (pending.Count >= PendingLimit)
            {
                int evictIndex = -1;
                for (int i = 0; i < pending.Count; i++)
                {
                    if (!IsBootOneShot(pending[i].Name)) { evictIndex = i; break; }
                }
                if (evictIndex < 0) evictIndex = 0;
                pending.RemoveAt(evictIndex);
                DroppedOverflow++;
            }
            pending.Add(reported);
        }

        static void PersistPreConsentHold()
        {
            if (preConsentStore == null) return;
            try { preConsentStore.Save(pending); }
            catch (Exception) { SinkFailures++; }
        }

        static void ClearPreConsentStore()
        {
            if (preConsentStore == null) return;
            try { preConsentStore.Clear(); }
            catch (Exception) { SinkFailures++; }
        }

        static void NotifyAccepted(AnalyticsEvent reported)
        {
            if (acceptedSubscribers.Count == 0 || reported == null) return;
            acceptedQueue.Enqueue(reported);
            if (!notifyingAccepted) DrainAcceptedSubscribers();
        }

        static bool TryBuild(string name, DateTime utcNow, AnalyticsParameters parameters, out AnalyticsEvent reported)
        {
            reported = null;
            if (string.IsNullOrEmpty(name) || !allowed.Contains(name)) { DroppedUnknownName++; return false; }
            reported = new AnalyticsEvent { Name = name, UtcTime = utcNow };
            if (parameters != null)
            {
                if (parameters.Count > ParameterLimit) { DroppedInvalidParameter++; return false; }
                foreach (var pair in parameters.Values)
                {
                    if (!ValidKey(pair.Key) || pair.Value == null || pair.Value.Length > ValueLimit)
                    { DroppedInvalidParameter++; return false; }
                    AnalyticsField field;
                    if (!AnalyticsSchema.TryGetField(name, pair.Key, out field) || !AnalyticsSchema.Conforms(field.Type, pair.Value))
                    { DroppedSchema++; return false; }
                    reported.Parameters[pair.Key] = pair.Value;
                }
            }
            foreach (var field in AnalyticsSchema.Fields(name))
                if (field.Required && !reported.Parameters.ContainsKey(field.Key)) { DroppedSchema++; return false; }
            return true;
        }

        /// <summary>
        /// A parameter key: lower case, letters, digits and underscores, starting with a letter.
        /// Narrow on purpose — it is the shape every provider accepts, and it refuses the free-text
        /// keys that turn into accidental identifiers.
        /// </summary>
        static bool ValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 40) return false;
            if (key[0] < 'a' || key[0] > 'z') return false;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// A text value: ASCII letters, digits and <c>_ - . :</c>, up to <see cref="ValueLimit"/>.
        /// Enum names, product ids, slugs and dates all fit; a sentence, an e-mail address or a
        /// typed name does not, so none can be reported by accident.
        /// </summary>
        public static bool ValidText(string value)
        {
            if (value == null || value.Length > ValueLimit) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                    c == '_' || c == '-' || c == '.' || c == ':';
                if (!ok) return false;
            }
            return true;
        }

        static void Flush()
        {
            if (sink == null || !consent.HasValue || !consent.Value) return;
            for (int i = 0; i < pending.Count; i++) Dispatch(pending[i]);
            pending.Clear();
            ClearPreConsentStore();
        }

        static void NotifyConsent()
        {
            var listener = sink as IAnalyticsConsentListener;
            if (listener == null || !consent.HasValue) return;
            try { listener.ConsentChanged(consent.Value); }
            catch (Exception) { SinkFailures++; }
        }

        static bool Dispatch(AnalyticsEvent reported)
        {
            try { sink.Send(reported); Reported++; return true; }
            catch (Exception) { SinkFailures++; return false; }
        }
    }

    /// <summary>
    /// A flat parameter set. Values are stringified here rather than at the call site so a number
    /// is formatted the same way every time, with the invariant culture: a device set to a comma
    /// decimal separator must not report a different number than one set to a point.
    /// </summary>
    public sealed class AnalyticsParameters
    {
        readonly List<KeyValuePair<string, string>> values = new List<KeyValuePair<string, string>>();

        public IEnumerable<KeyValuePair<string, string>> Values { get { return values; } }
        public int Count { get { return values.Count; } }

        public AnalyticsParameters Add(string key, string value)
        {
            values.Add(new KeyValuePair<string, string>(key, value));
            return this;
        }

        public AnalyticsParameters Add(string key, int value)
        {
            return Add(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public AnalyticsParameters Add(string key, long value)
        {
            return Add(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public AnalyticsParameters Add(string key, bool value)
        {
            return Add(key, value ? "true" : "false");
        }

        public AnalyticsParameters Add(string key, double value)
        {
            return Add(key, value.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }
}
