using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace CategorySearchShortcut;

[BepInPlugin("com.musicmaniac.categorysearchshortcut", "Category Search Shortcut", "1.2.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private static readonly FieldInfo InventoryScreenItemContext = AccessTools.Field(typeof(InventoryScreen), "_itemContext");
    private static readonly FieldInfo TraderStashPanel = AccessTools.Field(typeof(TraderDealScreen), "_stashPanel");
    private static readonly FieldInfo TraderStashGridView = AccessTools.Field(typeof(TraderDealScreen), "_stashGridView");
    private static readonly FieldInfo StashFilterPanel = AccessTools.Field(typeof(SimpleStashPanel), "_filterPanel");
    private static readonly FieldInfo StashItemContext = AccessTools.Field(typeof(SimpleStashPanel), "_itemContext");
    private static readonly FieldInfo StashItem = AccessTools.Field(typeof(SimpleStashPanel), "_item");
    private static readonly FieldInfo StashUiContext = AccessTools.Field(typeof(SimpleStashPanel), "_itemUiContext");
    private static readonly FieldInfo ItemUiEquipment = AccessTools.Field(typeof(ItemUiContext), "_equipment");
    private static readonly FieldInfo SearchFilterPanel = AccessTools.Field(typeof(StashSearchWindow), "_filterPanel");
    private static readonly FieldInfo SearchFilterRefresh = AccessTools.Field(typeof(StashSearchWindow), "_filterUpdateRequired");
    private static readonly FieldInfo ItemUiSearchWindow = AccessTools.Field(typeof(ItemUiContext), "_stashSearchWindow");
    private static readonly FieldInfo FilterTabGroup = AccessTools.Field(typeof(FilterPanel), "_tabs");
    private static readonly FieldInfo FilterTabsByType = AccessTools.Field(typeof(FilterPanel), "_filterTabTypes");
    private static ManualLogSource? _log;
    private static ConfigEntry<bool>? _alwaysSelectable;
    private static bool _openingSearch;
    private static FilterPanel? _sourcePanelForSearch;
    private static FilterTab? _sourceTabForSearch;
    private static StashSearchWindow? _openedSearchWindow;
    private static TraderDealScreen? _currentTraderScreen;

    private void Awake()
    {
        _log = Logger;
        _alwaysSelectable = Config.Bind(
            "Stash Categories",
            "AlwaysSelectable",
            true,
            "Keep player stash category buttons clickable in inventory and trader screens, even when matching items are only inside containers. Restart the game after changing this setting.");
        if (InventoryScreenItemContext == null || ItemUiEquipment == null ||
            SearchFilterPanel == null || SearchFilterRefresh == null ||
            ItemUiSearchWindow == null || FilterTabGroup == null || FilterTabsByType == null ||
            TraderStashPanel == null || TraderStashGridView == null ||
            StashFilterPanel == null || StashItemContext == null ||
            StashItem == null || StashUiContext == null)
        {
            Logger.LogError("The installed game UI no longer has the expected search fields; category shortcut was not patched.");
            return;
        }

        var harmony = new Harmony("com.musicmaniac.categorysearchshortcut");
        harmony.Patch(
            AccessTools.Method(typeof(Tab), nameof(Tab.OnPointerClick)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeTabClick)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterTabClick)));
        harmony.Patch(
            AccessTools.Method(typeof(UIInputNode), nameof(UIInputNode.Close)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterWindowClose)));
        harmony.Patch(
            AccessTools.Method(typeof(FilterTab), nameof(FilterTab.SetInteractable)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSetInteractable)));
        harmony.Patch(
            AccessTools.Method(typeof(TraderDealScreen), nameof(TraderDealScreen.Show)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeTraderShow)));
        Logger.LogInfo("Category Search Shortcut loaded.");
    }

    private static void BeforeTraderShow(TraderDealScreen __instance)
    {
        _currentTraderScreen = __instance;
    }

    private static void BeforeSetInteractable(FilterTab __instance, ref bool active)
    {
        if (active || _alwaysSelectable?.Value != true ||
            __instance.GetComponentInParent<StashSearchWindow>(true) != null)
        {
            return;
        }

        var stashPanel = __instance.GetComponentInParent<SimpleStashPanel>(true);
        var filterPanel = __instance.GetComponentInParent<FilterPanel>(true);
        var isInventoryStash = stashPanel != null &&
            ReferenceEquals(StashFilterPanel.GetValue(stashPanel), filterPanel) &&
            stashPanel.GetComponentInParent<InventoryScreen>(true) != null;
        if (isInventoryStash || GetTraderPlayerStash(filterPanel) != null)
        {
            active = true;
        }
    }

    private static SimpleStashPanel? GetTraderPlayerStash(FilterPanel? filterPanel)
    {
        if (filterPanel == null)
        {
            return null;
        }

        var traderScreen = filterPanel.GetComponentInParent<TraderDealScreen>(true)
            ?? _currentTraderScreen;
        var playerGrid = traderScreen == null
            ? null
            : TraderStashGridView.GetValue(traderScreen) as TradingGridView;
        return playerGrid != null && ReferenceEquals(playerGrid.FilterPanel, filterPanel)
            ? TraderStashPanel.GetValue(traderScreen!) as SimpleStashPanel
            : null;
    }

    private static void BeforeTabClick(Tab __instance, out bool __state)
    {
        __state = __instance is FilterTab && __instance.CanHandlePointerClick;
    }

    private static void AfterTabClick(Tab __instance, bool __state)
    {
        if (_openingSearch || !__state ||
            __instance.GetComponentInParent<StashSearchWindow>() != null)
        {
            return;
        }

        // Use the player's stash panel in a trader screen, never the trader's stock filters.
        var sourcePanel = __instance.GetComponentInParent<FilterPanel>();
        var inventoryScreen = __instance.GetComponentInParent<InventoryScreen>();
        var isInventory = inventoryScreen != null && inventoryScreen.isActiveAndEnabled;
        var traderStashPanel = isInventory ? null : GetTraderPlayerStash(sourcePanel);
        var isTraderStash = traderStashPanel != null;
        if (!isInventory && !isTraderStash)
        {
            return;
        }

        var filter = sourcePanel?.CurrentFilter;
        if (filter == null || filter.Type == FilterPanel.EFilterItemType.All)
        {
            return;
        }

        ItemUiContext? ui;
        ItemContext? itemContext;
        InventoryEquipment? equipment;
        if (isTraderStash)
        {
            var panelContext = StashItemContext.GetValue(traderStashPanel!) as ItemContext;
            var stashItem = StashItem.GetValue(traderStashPanel!) as Item;
            if (panelContext == null || stashItem == null)
            {
                return;
            }

            ui = StashUiContext.GetValue(traderStashPanel!) as ItemUiContext;
            itemContext = panelContext.CreateChild(stashItem);
            // TraderDealScreen shows its player stash with StashOnly search availability.
            equipment = null;
        }
        else
        {
            ui = ItemUiContext.Instance;
            itemContext = InventoryScreenItemContext.GetValue(inventoryScreen!) as ItemContext;
            equipment = ui == null ? null : ItemUiEquipment.GetValue(ui) as InventoryEquipment;
        }

        if (ui == null || itemContext == null || (!isTraderStash && equipment == null))
        {
            return;
        }
        var searchWindow = ItemUiSearchWindow.GetValue(ui) as StashSearchWindow;
        if (searchWindow == null)
        {
            return;
        }

        try
        {
            _openingSearch = true;
            ui.ShowStashSearchWindow(itemContext, equipment);

            var searchPanel = SearchFilterPanel.GetValue(searchWindow) as FilterPanel;
            if (searchPanel != null)
            {
                var tabs = FilterTabsByType.GetValue(searchPanel)
                    as Dictionary<FilterPanel.EFilterItemType, FilterTab>;
                var tabGroup = FilterTabGroup.GetValue(searchPanel) as TabGroup;
                if (tabGroup != null && tabs != null && tabs.TryGetValue(filter.Type, out var tab))
                {
                    tabGroup.Show(tab, true);
                    // Filter selection clears the window's pending tab refresh.
                    // Restore it so the game enables every category found in the stash.
                    SearchFilterRefresh.SetValue(searchWindow, true);
                    _sourcePanelForSearch = sourcePanel;
                    _sourceTabForSearch = (FilterTab)__instance;
                    _openedSearchWindow = searchWindow;
                }
            }
        }
        catch (Exception exception)
        {
            _log?.LogError($"Could not open category search: {exception}");
        }
        finally
        {
            _openingSearch = false;
        }
    }

    private static void AfterWindowClose(UIInputNode __instance)
    {
        if (__instance is not StashSearchWindow window ||
            !ReferenceEquals(window, _openedSearchWindow))
        {
            return;
        }

        var sourcePanel = _sourcePanelForSearch;
        var sourceTab = _sourceTabForSearch;
        _sourcePanelForSearch = null;
        _sourceTabForSearch = null;
        _openedSearchWindow = null;

        if (sourcePanel == null)
        {
            return;
        }

        try
        {
            var tabs = FilterTabsByType.GetValue(sourcePanel)
                as Dictionary<FilterPanel.EFilterItemType, FilterTab>;
            var tabGroup = FilterTabGroup.GetValue(sourcePanel) as TabGroup;
            if (tabGroup != null && tabs != null &&
                tabs.TryGetValue(FilterPanel.EFilterItemType.All, out var allTab))
            {
                tabGroup.Show(allTab, true);
                // Show selects All but does not deselect the previous tab's visual.
                if (sourceTab != null && !ReferenceEquals(sourceTab, allTab))
                {
                    sourceTab.UpdateVisual(false, false);
                }
            }
        }
        catch (Exception exception)
        {
            _log?.LogError($"Could not reset stash category after search closed: {exception}");
        }
    }
}
