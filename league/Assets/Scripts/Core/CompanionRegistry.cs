using System;

namespace BloomHarbor
{
    /// <summary>One reviewed companion animal: the coast it is the mascot of, its installed
    /// resources, and the exact source hashes its packages must declare.
    ///
    /// Owner direction 2026-09-09, "each world should look unique with custom character for each
    /// world": an animal is not chosen, it belongs to a coast. The coast's mascot is the animal on
    /// that coast's map and on Home while that coast is current. Reaching a coast is therefore how
    /// its animal is met, which is the same number as its coast id.
    ///
    /// The fox is the parent. Its six reviewed revisions (R7, R10, R11, R12, the winding repair and
    /// R13) stay enumerated inside <see cref="CompanionGaitProfile"/> and
    /// <see cref="CompanionExpressionProfile"/> exactly as before, so nothing about the fox's
    /// validation changed. A derived species instead carries one exact tuple here, because it has a
    /// single revision.
    ///
    /// Every derived species shares the fox's skeleton, paw geometry and standing height by
    /// construction (design/implementation/companions-r1/README.md), which is why
    /// <see cref="SoleHullCounts"/> is the fox R13 table for all of them rather than a per-species
    /// measurement: the extraction measured the same counts, and this field makes that a checked
    /// expectation instead of a coincidence.</summary>
    public sealed class CompanionEntry
    {
        public readonly string Id;
        /// <summary>Shown on the Home collection strip. Not a level or coast name.</summary>
        public readonly string DisplayName;
        /// <summary>The coast this animal is the mascot of, and so also the coast that introduces it.</summary>
        public readonly int Coast;
        public readonly string ModelResource, GaitResource, ExpressionResource, SourceResource;
        /// <summary>Null for the fox, whose revisions are enumerated in the profile selectors.</summary>
        public readonly CompanionSourceBinding Binding;
        public static readonly int[] SoleHullCounts = { 25, 20, 28, 27 };

        internal CompanionEntry(string id, string displayName, int coast, CompanionSourceBinding binding)
        {
            Id = id; DisplayName = displayName; Coast = coast; Binding = binding;
            ModelResource = "Companions/" + id;
            SourceResource = ModelResource + "-source";
            GaitResource = ModelResource + "-gait";
            ExpressionResource = ModelResource + "-expressions";
        }

        /// <summary>True once the player has reached this animal's coast, which is when they meet it.</summary>
        public bool IsMet(int reachedCoast) => reachedCoast >= Coast;
        public string CoastName => LevelCatalog.Coast(Coast).Name;
    }

    /// <summary>The one exact source tuple a derived species' packages must declare.</summary>
    public sealed class CompanionSourceBinding
    {
        public readonly string Model, Blend, Posture;
        public readonly string GaitContract, GaitPackage;
        public readonly string ExpressionContract, ExpressionPackage;
        public readonly string ExpressionReceipt, CelebrationReceipt, AuthoringScript, PoseScript;

        internal CompanionSourceBinding(string model, string blend, string posture, string gaitContract, string gaitPackage,
            string expressionContract, string expressionPackage, string expressionReceipt, string celebrationReceipt,
            string authoringScript, string poseScript)
        {
            Model = model; Blend = blend; Posture = posture;
            GaitContract = gaitContract; GaitPackage = gaitPackage;
            ExpressionContract = expressionContract; ExpressionPackage = expressionPackage;
            ExpressionReceipt = expressionReceipt; CelebrationReceipt = celebrationReceipt;
            AuthoringScript = authoringScript; PoseScript = poseScript;
        }

        /// <summary>True when all three of the model/blend/posture hashes match, which is one complete revision.</summary>
        internal bool Matches(string model, string blend, string posture) =>
            string.Equals(model, Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(blend, Blend, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(posture, Posture, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the tuple shares one or two hashes with this species but not all three.
        /// A partial match is a mixed-revision package and must be refused, never resolved.</summary>
        internal bool Touches(string model, string blend, string posture) =>
            string.Equals(model, Model, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(blend, Blend, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(posture, Posture, StringComparison.OrdinalIgnoreCase);
    }

    public static class CompanionRegistry
    {
        /// <summary>Arrival Cove's mascot, and the animal every coast without one of its own still
        /// shows if a future coast does not yet have an installed animal. All five current
        /// coasts have source-bound models below.</summary>
        public const string DefaultId = "fox";

        /// <summary>In coast order, which is also the order the player meets them.</summary>
        public static readonly CompanionEntry[] All =
        {
            new CompanionEntry("fox", "Fox", 1, null),
            new CompanionEntry("kitten", "Kitten", 2, new CompanionSourceBinding(
                model: "c873be50ebc81fcc79614433215b149dfb3492962ca0630b936eceb9e064e183",
                blend: "4cc0c382179f24ea5e6c1a2becbda32afe8a20271b991dde55993b48624131cf",
                posture: "4d54e68b7f65bd4b1205de743346b286dff39bdabd1a43355f2b2672be102ca2",
                gaitContract: "00b02d6fcae707bd55edd59ccb7f8e06ce2f981a1e4dc79a5b50fec742954099",
                gaitPackage: "9908ed0285e7d58d12e03626abe263b57a95bab2a125bbd7f44cc1bc0d2a3b3c",
                expressionContract: "ee045d41602530b073fb51def318250876409e38982222d53a7775d6fd37c6a1",
                expressionPackage: "eab748362ab33ce2fe5cab342c9b948fb3c1808bd54d9804e04f50954d5415ed",
                expressionReceipt: "19c365d33603fea29c5b81c1c0d5bf9e87be65ad448c594585def4d3cf71198a",
                celebrationReceipt: "4d54e68b7f65bd4b1205de743346b286dff39bdabd1a43355f2b2672be102ca2",
                authoringScript: "1496a6d1861af878a7d5516c07584b86f57cf1db10f510e003c18d0080bc85d2",
                poseScript: "0d33e395c401afb2ea1d2b2b3edd0d416539367c32ab061167dfdf5fa58eb8e0")),
            new CompanionEntry("bunny", "Bunny", 3, new CompanionSourceBinding(
                model: "a6c26663f83de2bc95a3e70b12e3e760e7795e6109f39eb9120cde36e6766f69",
                blend: "313743293df48898bd38bafd904d62fd667390478ce2841cc5547b5cf66e9368",
                posture: "966c56386e26220ada2c7eb5308c37c857b3f66733ef6ec7a82c97916075af33",
                gaitContract: "469402a5db82f8ce495da193f436dc3c8d6cc00466c9f61f18c3c1d35682f43c",
                gaitPackage: "a837898ffb63a362120387c7dab161efe0a551f527a94b8db251746bf5a14ba6",
                expressionContract: "f3edc8d54f6fc55453d736aaf72a064ecb6037225e18b2805dc26f1160a852b8",
                expressionPackage: "6d4a19332293a3dcd5193cd427b2bffc18dbc6cf70e36ee55c5ea559fb9aee81",
                expressionReceipt: "fe2ae0f8e894c3bc139b44aaacdc6ff301a5597193073a116fcca478b63b520b",
                celebrationReceipt: "966c56386e26220ada2c7eb5308c37c857b3f66733ef6ec7a82c97916075af33",
                authoringScript: "1496a6d1861af878a7d5516c07584b86f57cf1db10f510e003c18d0080bc85d2",
                poseScript: "0d33e395c401afb2ea1d2b2b3edd0d416539367c32ab061167dfdf5fa58eb8e0")),
            new CompanionEntry("panda", "Panda", 4, new CompanionSourceBinding(
                model: "f511b4a7a06b37762f0d80204b3801b8ab53b1fb0a88da6557f91f09e56b3c78",
                blend: "e74e63125398b121a13a9df72ac441d26805e487ec10857545d29421c5ad6a92",
                posture: "b6faf4f4ef9716b6d42cbf630c931a893dcb46dc40307ec668007333d831e171",
                gaitContract: "7bed86e641d91eeb0fbc99c7f5f4a226fd9e7ae204537b804d7277a0257b5c37",
                gaitPackage: "1e4818e9f6e984e77aeaf076caf9476980784064d7c6a10619fa25382cd21117",
                expressionContract: "c02825db5ca58bc75af017dbcfc1ff50aa1d9bc937f6fcce8dd5dbc21d7504de",
                expressionPackage: "5270aa24f94ccd40bf517b9b0ae1cdd37121166e06363b40176cf90ee29edd8b",
                expressionReceipt: "e4a1aeee99cb12eeda879c670a3a9b6521b4abcd6637debf9823d73327c5d5d4",
                celebrationReceipt: "b6faf4f4ef9716b6d42cbf630c931a893dcb46dc40307ec668007333d831e171",
                authoringScript: "e1393241bd6554f681e5067ed6137edbfc1fcc35ac547116c05a3fe167de0dab",
                poseScript: "9691a4c10e4c230e15f119dc55c9c597a328ae6bb5a5f7ee86d952e859fe8df4")),
            new CompanionEntry("hamster", "Hamster", 5, new CompanionSourceBinding(
                model: "cac2c45f1175c514bd4949261d86c8dea0aa69f82b4ceb0524c098f6f9c4e88f",
                blend: "dfce1c59986c8252e4f23112c8393e019d451165b1f6d973bc957e11b7cfd7f4",
                posture: "cb31a292dbc06dfff0882eb918befcbf5eab4e64b96f0c108a16d759deb8675f",
                gaitContract: "cf99f903859300a4feb9a1c3b93cc4f02ada9c2cf51fa8282ff13f67c0083270",
                gaitPackage: "1792651e4f361c16f633903fadccf5f399c3543c0cc70787045ea1ee4684d097",
                expressionContract: "9287e442275f55080628b7a0d84410d030ba72e3c1943f9baa97c1c4750505e3",
                expressionPackage: "4603189b4040fd827171eb3b7e63adeee1f5bd908194eaf353c9f8bf79a35a12",
                expressionReceipt: "f0ee3ba933064892d29647dc22b56e81e4f097e23fbf8b0a41fc9e50d8afd1d3",
                celebrationReceipt: "cb31a292dbc06dfff0882eb918befcbf5eab4e64b96f0c108a16d759deb8675f",
                authoringScript: "9ac8a27e4529efa64e4ccb90307752f42971101578a2f8d22f028cddf7194242",
                poseScript: "247f3a9bf3bee1b988ae20358832684caa94c74c0625f389f4e2715af5e229e6")),
        };

        public static bool IsKnown(string id) => Find(id) != null;

        public static CompanionEntry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < All.Length; i++) if (All[i].Id == id) return All[i];
            return null;
        }

        public static CompanionEntry Require(string id)
        {
            var entry = Find(id);
            if (entry == null) throw new InvalidOperationException("Unknown companion id: " + id);
            return entry;
        }

        /// <summary>The animal that rides a coast: its own mascot, or the starter fox for a coast
        /// whose animal has not been built yet. This is the only place the map and Home decide which
        /// model to load, so neither of them branches per animal.</summary>
        public static CompanionEntry ForCoast(int coastId)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].Coast == coastId) return All[i];
            return Require(DefaultId);
        }

        /// <summary>How many coasts have an animal of their own, so a receipt can state plainly how
        /// much of the cast exists rather than implying all five do.</summary>
        public static int BuiltCoasts => All.Length;

        /// <summary>The derived species whose complete source tuple this is, or null. Throws when the
        /// tuple mixes revisions, which is the same refusal the fox's own selector makes.</summary>
        internal static CompanionEntry FindByBinding(string model, string blend, string posture)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Binding != null && All[i].Binding.Matches(model, blend, posture)) return All[i];
            for (int i = 0; i < All.Length; i++)
                if (All[i].Binding != null && All[i].Binding.Touches(model, blend, posture))
                    throw new InvalidOperationException("Companion source tuple mixes different asset revisions.");
            return null;
        }
    }
}
