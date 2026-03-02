using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;

namespace PriceSlinger
{
    [BepInPlugin("com.kritterbizkit.priceslinger", "PriceSlinger", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private readonly Harmony harmony = new Harmony("com.kritterbizkit.priceslinger");

        // ── Cards ────────────────────────────────────────────────────────────
        internal static ConfigEntry<int> CardMarkupPercent;
        internal static ConfigEntry<bool> CardRoundingEnabled;
        internal static ConfigEntry<float> CardRoundToNearest;
        internal static ConfigEntry<bool> CardPreventBelowMarket;

        // ── Graded Cards ─────────────────────────────────────────────────────
        internal static ConfigEntry<int> GradedCardMarkupPercent;
        internal static ConfigEntry<bool> GradedCardRoundingEnabled;
        internal static ConfigEntry<float> GradedCardRoundToNearest;
        internal static ConfigEntry<bool> GradedCardPreventBelowMarket;

        // ── Items (packs, bulk boxes etc.) ───────────────────────────────────
        internal static ConfigEntry<int> ItemMarkupPercent;
        internal static ConfigEntry<int> BulkBoxMarkupPercent;
        internal static ConfigEntry<bool> ItemRoundingEnabled;
        internal static ConfigEntry<float> ItemRoundToNearest;
        internal static ConfigEntry<bool> ItemPreventBelowMarket;
        internal static ConfigEntry<bool> ItemMarkupOnAvgCost;
        internal static ConfigEntry<float> AbsoluteMinPrice;

        // ── Triggers ─────────────────────────────────────────────────────────
        internal static ConfigEntry<KeyboardShortcut> PriceAllKey;
        internal static ConfigEntry<KeyboardShortcut> PriceCardsKey;
        internal static ConfigEntry<KeyboardShortcut> PriceGradedKey;
        internal static ConfigEntry<KeyboardShortcut> PriceItemsKey;
        internal static ConfigEntry<bool> PriceOnCardPlaced;
        internal static ConfigEntry<bool> PriceGradedOnCardPlaced;

        // ── Misc ─────────────────────────────────────────────────────────────
        internal static ConfigEntry<bool> PlaySoundOnPrice;
        internal static ConfigEntry<bool> DebugLogging;

        private void Awake()
        {
            Log = base.Logger;
            InitConfig();
            harmony.PatchAll();
            Log.LogInfo("PriceSlinger loaded!");
        }

        private void OnDestroy()
        {
            harmony.UnpatchSelf();
        }

        private void InitConfig()
        {
            // ── Cards ────────────────────────────────────────────────────────
            CardMarkupPercent = Config.Bind(
                "Cards", "MarkupPercent", 10,
                "Percentage markup over market price for shelf cards. 0 = market price exactly.");

            CardRoundingEnabled = Config.Bind(
                "Cards", "RoundingEnabled", true,
                "Round card prices to the nearest CardRoundToNearest value.");

            CardRoundToNearest = Config.Bind(
                "Cards", "RoundToNearest", 0.25f,
                "Round card prices to this increment (e.g. 0.25 = nearest quarter). Ignored if rounding is off.");

            CardPreventBelowMarket = Config.Bind(
                "Cards", "PreventPricingBelowMarket", true,
                "Never set a card price below market value, even after rounding.");

            // ── Graded Cards ─────────────────────────────────────────────────
            GradedCardMarkupPercent = Config.Bind(
                "GradedCards", "MarkupPercent", 15,
                "Percentage markup over market price for graded cards. Separate from normal card markup.");

            GradedCardRoundingEnabled = Config.Bind(
                "GradedCards", "RoundingEnabled", true,
                "Round graded card prices to the nearest GradedCardRoundToNearest value.");

            GradedCardRoundToNearest = Config.Bind(
                "GradedCards", "RoundToNearest", 0.50f,
                "Round graded card prices to this increment.");

            GradedCardPreventBelowMarket = Config.Bind(
                "GradedCards", "PreventPricingBelowMarket", true,
                "Never set a graded card price below market value, even after rounding.");

            // ── Items ────────────────────────────────────────────────────────
            ItemMarkupPercent = Config.Bind(
                "Items", "MarkupPercent", 10,
                "Percentage markup over market price for standard items (packs, accessories).");

            BulkBoxMarkupPercent = Config.Bind(
                "Items", "BulkBoxMarkupPercent", 5,
                "Percentage markup specifically for bulk box items.");

            ItemMarkupOnAvgCost = Config.Bind(
                "Items", "MarkupOnAverageCost", false,
                "If true, item markup is applied to average purchase cost instead of market price.");

            ItemRoundingEnabled = Config.Bind(
                "Items", "RoundingEnabled", true,
                "Round item prices to the nearest ItemRoundToNearest value.");

            ItemRoundToNearest = Config.Bind(
                "Items", "RoundToNearest", 0.25f,
                "Round item prices to this increment.");

            ItemPreventBelowMarket = Config.Bind(
                "Items", "PreventPricingBelowMarket", true,
                "Never set an item price below market value, even after rounding.");

            AbsoluteMinPrice = Config.Bind(
                "Items", "AbsoluteMinPrice", 0.25f,
                "No item or card will ever be priced below this value.");

            // ── Triggers ─────────────────────────────────────────────────────
            PriceAllKey = Config.Bind(
                "Hotkeys", "PriceEverything",
                new KeyboardShortcut(KeyCode.F6),
                "Price all shelf cards, graded cards, and items at once.");

            PriceCardsKey = Config.Bind(
                "Hotkeys", "PriceCards",
                new KeyboardShortcut(KeyCode.F7),
                "Price only shelf cards (non-graded).");

            PriceGradedKey = Config.Bind(
                "Hotkeys", "PriceGradedCards",
                new KeyboardShortcut(KeyCode.F8),
                "Price only graded cards on shelves.");

            PriceItemsKey = Config.Bind(
                "Hotkeys", "PriceItems",
                new KeyboardShortcut(KeyCode.F5),
                "Price only items (packs, bulk boxes, etc.).");

            PriceOnCardPlaced = Config.Bind(
                "Triggers", "PriceCardOnPlace", true,
                "Automatically price a normal card compartment when a card is placed into it.");

            PriceGradedOnCardPlaced = Config.Bind(
                "Triggers", "PriceGradedCardOnPlace", true,
                "Automatically price a graded card compartment when a graded card is placed into it.");

            // ── Misc ─────────────────────────────────────────────────────────
            PlaySoundOnPrice = Config.Bind(
                "Misc", "PlaySoundOnPrice", true,
                "Play a sound effect when a hotkey pricing run completes.");

            DebugLogging = Config.Bind(
                "Misc", "DebugLogging", false,
                "Enable verbose debug logging. Leave off unless troubleshooting.");
        }
    }
}