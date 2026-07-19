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

    // Each in-game day the Guild Faction Add-on rolls 5 quest-board listings (lazily, only when its
    // world-map board is opened). This component drives that roll on a daily tick and posts each
    // listing as an offered vanilla quest (via the add-on's own IncidentWorker_IsekaiHunt.CreateHuntQuest,
    // which calls Find.QuestManager.Add). The result: the daily guild quests show up in the normal
    // Quests tab as "Available" without ever opening the board.
    public class GuildQuestVanillaMirror : WorldComponent
    {
        // While true, the per-quest "new quest offered" letters that CreateHuntQuest sends are
        // swallowed (see Patch_LetterStack_ReceiveLetter) so the daily quest roll is silent.
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

                // No letter: the day's guild quests just appear silently in the Quests tab. The
                // add-on's own per-quest letters stay swallowed (see SuppressQuestLetters below).
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
    // day's listings we swallow those so the roll is silent (no letter spam - by design).
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
