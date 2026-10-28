using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace TheShack.QssEaqsBridge
{
    // Quick Stack - Store - Sort - Trash - Restock (QSS) asks CompatibilitySupport.InternalIsEquipOrQuickSlot
    // before sorting, quick stacking, storing or trashing a player-inventory slot. Equipment and Quick Slots
    // (EAQS) 3.x moved its slots into the real player inventory, and QSS 1.4.15 doesn't recognise them yet
    // (Goldenrevolver/QuickStackStore#35), so Sort pulls worn armor and quick-slot items into the bag.
    //
    // This postfix asks EAQS's public API (EquipmentAndQuickSlots.API.IsSlotCell) whether a grid position is
    // one of its slots and, if so, reports it to QSS the same way QSS treats other equipment-slot mods:
    // equipment slots are always left alone, quick slots are left alone but may still be restocked.
    // It can only turn a "no" into a "yes", so it stays harmless once QSS ships native support.
    //
    // EAQS's slots live in three hidden rows below the visible grid, and those rows also hold reserved
    // cells that aren't slots. QSS's sort fills its allowed-slot list from the bottom row up, so it put
    // items into those invisible reserved cells. A second postfix moves every hidden-row cell to the end
    // of the allowed list: sort fills the visible grid first, and items already stranded in reserved
    // cells (not slot cells, so QSS still sorts them) come back into view on the next sort.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(QssGuid)]
    [BepInDependency(EaqsGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.qss_eaqs_bridge";
        public const string PluginName = "QSS EAQS Bridge";
        public const string PluginVersion = "1.1.0";

        private const string QssGuid = "goldenrevolver.quick_stack_store";
        private const string EaqsGuid = "randyknapp.mods.equipmentandquickslots";
        private const int EaqsHiddenRows = 3;

        private static ManualLogSource log;
        private static MethodInfo isSlotCell;
        private static MethodInfo getVisibleRows;
        private static bool loggedInvokeError;

        private readonly Harmony harmony = new Harmony(PluginGuid);

        private void Awake()
        {
            log = Logger;

            Type eaqsApi = AccessTools.TypeByName("EquipmentAndQuickSlots.API");
            if (eaqsApi != null)
            {
                isSlotCell = AccessTools.Method(eaqsApi, "IsSlotCell",
                    new[] { typeof(int), typeof(int), typeof(string).MakeByRefType() });
            }

            MethodInfo target = AccessTools.Method("QuickStackStore.CompatibilitySupport:InternalIsEquipOrQuickSlot");

            if (isSlotCell == null || target == null)
            {
                log.LogWarning($"Not patching: EAQS IsSlotCell {(isSlotCell == null ? "missing" : "found")}, " +
                               $"QSS InternalIsEquipOrQuickSlot {(target == null ? "missing" : "found")}.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(Plugin), nameof(InternalIsEquipOrQuickSlotPostfix)));
            log.LogInfo("Quick Stack now leaves Equipment and Quick Slots' armor and quick slots alone.");

            Type eaqsSlots = AccessTools.TypeByName("EquipmentAndQuickSlots.Slots");
            getVisibleRows = eaqsSlots == null ? null : AccessTools.PropertyGetter(eaqsSlots, "VisibleRows");
            MethodInfo allowedSlots = AccessTools.Method("QuickStackStore.SortModule:GetAllowedSlots");
            if (allowedSlots == null)
            {
                log.LogWarning("Not patching sort: QSS SortModule.GetAllowedSlots missing.");
                return;
            }

            harmony.Patch(allowedSlots, postfix: new HarmonyMethod(typeof(Plugin), nameof(GetAllowedSlotsPostfix)));
            log.LogInfo("Quick Stack's sort now fills the visible inventory before Equipment and Quick Slots' hidden rows.");
        }

        // Original: List<Vector2i> GetAllowedSlots(Inventory inventory, bool includeHotbar, UserConfig playerConfig)
        private static void GetAllowedSlotsPostfix(Inventory __0, List<Vector2i> __result)
        {
            if (__result == null || __0 == null || __0 != Player.m_localPlayer?.GetInventory())
            {
                return;
            }

            int visibleRows = __0.GetHeight() - EaqsHiddenRows;
            try
            {
                if (getVisibleRows != null)
                {
                    visibleRows = (int)getVisibleRows.Invoke(null, null);
                }
            }
            catch (Exception)
            {
                // keep the height-based fallback
            }

            // Stable partition: same cells, same order within each group, visible rows first.
            List<Vector2i> hidden = __result.FindAll(pos => pos.y >= visibleRows);
            if (hidden.Count == 0)
            {
                return;
            }

            __result.RemoveAll(pos => pos.y >= visibleRows);
            __result.AddRange(hidden);
        }

        // Original: InternalIsEquipOrQuickSlot(int inventoryHeight, int inventoryWidth, Vector2i itemPos, bool includeRestockableSlots)
        private static void InternalIsEquipOrQuickSlotPostfix(int __0, int __1, Vector2i __2, bool __3, ref bool __result)
        {
            if (__result)
            {
                return;
            }

            // QSS only asks about the local player's inventory, but EAQS cells are player-inventory coordinates,
            // so make sure a container of some other size can never match.
            Inventory inventory = Player.m_localPlayer?.GetInventory();
            if (inventory == null || inventory.GetHeight() != __0 || inventory.GetWidth() != __1)
            {
                return;
            }

            object[] args = { __2.x, __2.y, null };
            try
            {
                if (!(bool)isSlotCell.Invoke(null, args))
                {
                    return;
                }
            }
            catch (Exception e)
            {
                if (!loggedInvokeError)
                {
                    loggedInvokeError = true;
                    log.LogError($"EAQS IsSlotCell failed, bridge inactive for this call: {e.InnerException ?? e}");
                }
                return;
            }

            // Built-in quick slots are "Quick1".."Quick6"; everything else is an equipped-only equipment cell.
            bool isQuickSlot = (args[2] as string ?? "").StartsWith("Quick", StringComparison.OrdinalIgnoreCase);
            if (isQuickSlot && !__3)
            {
                return;
            }

            __result = true;
        }
    }
}
