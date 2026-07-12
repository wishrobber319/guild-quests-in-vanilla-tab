using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GuildFactionAddon;
using HarmonyLib;
using IsekaiLeveling.Quests;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace GuildQuestsInVanillaTab
{
    [StaticConstructorOnStartup]
    public static class GuildQuestsInVanillaTabMod
    {
        static GuildQuestsInVanillaTabMod()
        {
            new Harmony("wishRobber.guildquestsinvanillatab").PatchAll();
        }
    }

    [DefOf]
    public static class GuildQuestsInVanillaTabDefOf
    {
        public static LetterDef GuildQuestsInVanillaTab_QuestsLetter;

        static GuildQuestsInVanillaTabDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(GuildQuestsInVanillaTabDefOf));
        }
    }

    // The consolidated "new guild quests" letter: clicking it opens the vanilla Quests tab on the
    // "Available" sub-tab (where the freshly-posted guild quests live), then dismisses itself.
    public class Letter_OpenQuestsTab : StandardLetter
    {
        // MainTabWindow_Quests remembers its last sub-tab in a private enum field, so it would
        // otherwise reopen wherever the player left it (often Historical). 0 = QuestsTab.Available.
        private static readonly FieldInfo CurTabField =
            AccessTools.Field(typeof(MainTabWindow_Quests), "curTab");

        public override void OpenLetter()
        {
            Find.LetterStack.RemoveLetter(this);

            MainButtonDef questsButton = MainButtonDefOf.Quests;
            if (questsButton == null) return;
            questsButton.Worker?.InterfaceTryActivate();

            if (questsButton.TabWindow is MainTabWindow_Quests window)
            {
                CurTabField?.SetValue(window, Enum.ToObject(CurTabField.FieldType, 0)); // Available

                // Select the first row the Available tab would show: offered, not dismissed/hidden,
                // ordered by soonest to expire (matching the window's own sort).
                Quest first = Find.QuestManager.questsInDisplayOrder
                    .Where(q => q.State == QuestState.NotYetAccepted && !q.dismissed && !q.hiddenInUI && !q.hidden)
                    .OrderBy(q => q.TicksUntilExpiry)
                    .FirstOrDefault();
                window.Select(first);
            }
        }
    }

    // Each in-game day the Guild Faction Add-on rolls 5 quest-board listings (lazily, only when its
    // world-map board is opened). This component drives that roll on a daily tick and posts each
    // listing as an offered vanilla quest (via the add-on's own IncidentWorker_IsekaiHunt.CreateHuntQuest,
    // which calls Find.QuestManager.Add). The result: the daily guild quests show up in the normal
    // Quests tab as "Available" without ever opening the board.
    public class GuildQuestVanillaMirror : WorldComponent
    {
        // While true, the per-quest "new quest offered" letters that CreateHuntQuest sends are
        // swallowed (see Patch_LetterStack_ReceiveLetter) so we can emit one consolidated letter.
        internal static bool SuppressQuestLetters;

        private int postedDay = -1;

        public GuildQuestVanillaMirror(World world) : base(world) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref postedDay, "postedDay", -1);
        }

        public override void WorldComponentTick()
        {
            // Cheap daily check; the board uses 60000-tick days so hourly polling is plenty.
            if (Find.TickManager.TicksGame % 2500 != 0) return;

            GuildQuestBoardWorldComponent board = GuildQuestBoardWorldComponent.Get();
            if (board == null) return;

            int day = board.CurrentDay;
            if (postedDay == day) return;

            // CreateHuntQuest needs a player home map with colonists; wait until one exists.
            Map map = Find.Maps?.FirstOrDefault(m => m != null && m.IsPlayerHome
                && m.mapPawns?.FreeColonistsSpawnedCount > 0);
            if (map == null) return;

            // Force the add-on to roll today's listings if it hasn't (its refresh is otherwise lazy).
            board.TryRefresh();

            List<GuildQuestBoardEntry> entries = board.Entries;
            if (entries == null || entries.Count == 0) return;

            List<Quest> allQuests = Find.QuestManager.QuestsListForReading;
            int questCountBefore = allQuests.Count;

            int posted = 0;
            SuppressQuestLetters = true;
            try
            {
                foreach (GuildQuestBoardEntry entry in entries)
                {
                    if (entry?.PawnKind == null) continue;
                    try
                    {
                        IncidentWorker_IsekaiHunt.CreateHuntQuest(
                            entry.PawnKind, entry.rank, entry.xpReward, entry.silverReward, map, entry.isBounty);
                        posted++;
                    }
                    catch (System.Exception ex)
                    {
                        Log.Warning($"[Guild Quests in Vanilla Tab] Failed to post a guild quest: {ex.Message}");
                    }
                }
            }
            finally
            {
                SuppressQuestLetters = false;
            }

            postedDay = day;
            if (posted > 0)
            {
                int now = Find.TickManager.TicksGame;
                for (int i = questCountBefore; i < allQuests.Count; i++)
                {
                    // The guild board refreshes daily, so shorten each offer to a 1-day window.
                    ApplyOneDayOfferExpiry(allQuests[i], now);
                }

                // One consolidated letter in place of the per-quest letters we just suppressed.
                // Uses a custom letter class so clicking it opens the vanilla Quests tab directly.
                Find.LetterStack.ReceiveLetter(
                    "GuildQuestsInVanillaTab_LetterLabel".Translate(),
                    "GuildQuestsInVanillaTab_LetterText".Translate(posted),
                    GuildQuestsInVanillaTabDefOf.GuildQuestsInVanillaTab_QuestsLetter);
            }
        }

        private const int OneDayTicks = 60000;

        // CreateHuntQuest gives offers a 3-7 day acceptance window via two parallel mechanisms:
        // acceptanceExpireTick (the displayed "Expires in Xd") and a QuestPart_Delay timer (the
        // actual expiry). We set both to 1 day so the board's daily refresh matches. The offer
        // timer is the QuestPart_Delay whose completion signal ends in ".OfferExpired"; the
        // separate post-acceptance completion deadline (".Expired") is left alone. We run this in
        // the same tick the quest was created, so the delay just started counting -> 1 day flat.
        private static void ApplyOneDayOfferExpiry(Quest quest, int now)
        {
            quest.acceptanceExpireTick = now + OneDayTicks;

            foreach (QuestPart part in quest.PartsListForReading)
            {
                if (part is QuestPart_Delay delay
                    && delay.outSignalsCompleted != null
                    && delay.outSignalsCompleted.Any(s => s != null && s.EndsWith(".OfferExpired")))
                {
                    delay.delayTicks = OneDayTicks;
                }
            }
        }
    }

    // CreateHuntQuest sends its own "new quest offered" letter per quest. While we batch-post the
    // day's listings we swallow those so the player gets one consolidated letter instead of five.
    // All ReceiveLetter overloads funnel through ReceiveLetter(Letter, ...), so this one prefix
    // covers them. The suppression window is the synchronous posting loop only.
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
        new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_LetterStack_ReceiveLetter
    {
        public static bool Prefix() => !GuildQuestVanillaMirror.SuppressQuestLetters;
    }

    // The listings are now auto-posted to the vanilla Quests tab, so the board's own "Accept" would
    // create a duplicate offered quest. Skip it and point the player to the Quests tab instead. The
    // board still works as a read-only preview.
    [HarmonyPatch(typeof(Dialog_GuildQuestBoard), "AcceptEntry")]
    public static class Patch_Dialog_GuildQuestBoard_AcceptEntry
    {
        public static bool Prefix()
        {
            Messages.Message("GuildQuestsInVanillaTab_UseQuestsTab".Translate(),
                MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
    }
}
