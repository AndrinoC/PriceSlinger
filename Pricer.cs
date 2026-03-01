using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PriceSlinger
{
    /// <summary>
    /// All pricing math lives here. No patches, pure logic.
    /// </summary>
    internal static class Pricer
    {
        // ── Logging helpers ───────────────────────────────────────────────────

        private static readonly Dictionary<string, float> _logCooldowns = new Dictionary<string, float>();

        internal static void LogDebug(string msg)
        {
            if (!Plugin.DebugLogging.Value) return;
            Plugin.Log.LogInfo("[PriceSlinger] " + msg);
        }

        private static void LogWarnThrottled(string key, string msg, float cooldown = 15f)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_logCooldowns.TryGetValue(key, out float next) && now < next) return;
                _logCooldowns[key] = now + cooldown;
                Plugin.Log.LogWarning("[PriceSlinger] " + msg);
            }
            catch { }
        }

        // ── Bulk box detection (mirrors AutoSetPrices logic) ──────────────────

        private static bool IsBulkBox(EItemType type)
        {
            // EItemType range 37–38 and 89–94 are bulk box types in the base game
            int t = (int)type;
            return (t >= 37 && t <= 38) || (t >= 89 && t <= 94);
        }

        // ── Rounding helper ───────────────────────────────────────────────────

        /// <summary>
        /// Apply rounding and floor-to-market logic, then enforce absolute minimum.
        /// </summary>
        private static float ApplyRounding(float price, float marketPrice, bool roundingEnabled,
                                           float roundToNearest, bool preventBelowMarket)
        {
            float absMin = Plugin.AbsoluteMinPrice.Value;

            if (roundingEnabled)
            {
                float step = roundToNearest <= 0f ? 1f : roundToNearest;
                price = Mathf.Round(price / step) * step;

                if (preventBelowMarket)
                {
                    while (price < marketPrice)
                        price += step;
                }
            }
            else if (preventBelowMarket)
            {
                price = Mathf.Max(price, marketPrice);
            }

            return Mathf.Max(absMin, price);
        }

        // ── Item pricing ──────────────────────────────────────────────────────

        internal static void PriceAllItems()
        {
            try
            {
                float absMin = Plugin.AbsoluteMinPrice.Value;
                float itemMult = 1f + Plugin.ItemMarkupPercent.Value / 100f;
                float bulkMult = 1f + Plugin.BulkBoxMarkupPercent.Value / 100f;
                bool roundEnabled = Plugin.ItemRoundingEnabled.Value;
                float roundStep = Plugin.ItemRoundToNearest.Value;
                bool noUnderMarket = Plugin.ItemPreventBelowMarket.Value && Plugin.ItemMarkupPercent.Value > 0;
                bool useAvgCost = Plugin.ItemMarkupOnAvgCost.Value;

                foreach (EItemType itemType in Enum.GetValues(typeof(EItemType)))
                {
                    if (itemType < 0) continue;

                    try
                    {
                        float marketPrice;
                        float baseForMarkup;
                        float mult;

                        if (IsBulkBox(itemType))
                        {
                            marketPrice = CPlayerData.GetItemMarketPrice(itemType);
                            baseForMarkup = marketPrice;
                            mult = bulkMult;
                        }
                        else
                        {
                            marketPrice = CPlayerData.GetItemMarketPrice(itemType);
                            baseForMarkup = useAvgCost
                                ? CPlayerData.GetAverageItemCost(itemType)
                                : marketPrice;
                            mult = itemMult;
                        }

                        float price = Mathf.RoundToInt(baseForMarkup * mult * 100f) / 100f;
                        price = ApplyRounding(price, marketPrice, roundEnabled, roundStep,
                                              noUnderMarket && Plugin.ItemMarkupPercent.Value > 0);
                        price = Mathf.Max(absMin, price);

                        CPlayerData.SetItemPrice(itemType, price);
                    }
                    catch (Exception ex)
                    {
                        LogWarnThrottled("ItemPrice." + itemType,
                            "Failed pricing item " + itemType + ": " + ex.Message);
                    }
                }

                LogDebug("PriceAllItems complete.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PriceSlinger] PriceAllItems failed: " + ex);
            }
        }

        // ── Normal card pricing ───────────────────────────────────────────────

        /// <summary>Price every card in a single compartment.</summary>
        internal static void PriceCompartment(InteractableCardCompartment comp)
        {
            if (comp == null) return;

            try
            {
                List<InteractableCard3d> stored = comp.m_StoredCardList;
                if (stored == null || stored.Count == 0) return;

                float markupPct = Plugin.CardMarkupPercent.Value;
                float mult = 1f + markupPct / 100f;
                bool roundEnabled = Plugin.CardRoundingEnabled.Value;
                float roundStep = Plugin.CardRoundToNearest.Value;
                bool noUnderMarket = Plugin.CardPreventBelowMarket.Value && markupPct > 0f;

                EMoneyCurrencyType currencyType = CSingleton<CGameManager>.Instance.m_CurrencyType;
                float convRate = GameInstance.GetCurrencyConversionRate();
                float roundDiv = GameInstance.GetCurrencyRoundDivideAmount();

                for (int i = 0; i < stored.Count; i++)
                {
                    try
                    {
                        CardData cd = stored[i].m_Card3dUI.m_CardUI.GetCardData();
                        if (cd == null) continue;

                        // Route graded cards to their own pricer
                        if (cd.cardGrade > 0)
                        {
                            PriceGradedCompartmentCard(cd, comp);
                            continue;
                        }

                        float marketPrice = CPlayerData.GetCardMarketPrice(cd) * convRate;
                        if (marketPrice <= 0f)
                        {
                            LogDebug("Skipping card with 0 market price: " + cd.monsterType);
                            continue;
                        }

                        float price = Mathf.RoundToInt(marketPrice * mult * roundDiv) / roundDiv;
                        price = ApplyRounding(price, marketPrice, roundEnabled, roundStep, noUnderMarket);
                        price = Mathf.Max(Plugin.AbsoluteMinPrice.Value, price);

                        // Convert back from display currency to internal
                        price = Mathf.RoundToInt(price / convRate * 100f) / 100f;

                        CPlayerData.SetCardPrice(cd, price);
                        LogDebug("Priced " + cd.monsterType + " @ " + price);
                    }
                    catch (Exception ex)
                    {
                        LogWarnThrottled("CompartCard." + i,
                            "Failed pricing card at index " + i + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PriceSlinger] PriceCompartment failed: " + ex);
            }
        }

        /// <summary>Price all cards on all card shelves.</summary>
        internal static void PriceAllShelfCards()
        {
            try
            {
                List<CardShelf> shelves = CSingleton<ShelfManager>.Instance.m_CardShelfList;
                if (shelves == null) return;

                for (int i = 0; i < shelves.Count; i++)
                {
                    List<InteractableCardCompartment> comps = shelves[i].GetCardCompartmentList();
                    if (comps == null) continue;

                    for (int j = 0; j < comps.Count; j++)
                        PriceCompartment(comps[j]);
                }

                LogDebug("PriceAllShelfCards complete.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PriceSlinger] PriceAllShelfCards failed: " + ex);
            }
        }

        // ── Graded card pricing ───────────────────────────────────────────────

        /// <summary>
        /// Price a single graded CardData that is already on a shelf.
        /// Returns false if the card was skipped (0 price, EPL card, etc.).
        /// </summary>
        internal static bool PriceGradedCompartmentCard(CardData cd, InteractableCardCompartment comp)
        {
            try
            {
                if (cd == null || cd.cardGrade <= 0) return false;

                float markupPct = Plugin.GradedCardMarkupPercent.Value;
                float mult = 1f + markupPct / 100f;
                bool roundEnabled = Plugin.GradedCardRoundingEnabled.Value;
                float roundStep = Plugin.GradedCardRoundToNearest.Value;
                bool noUnderMarket = Plugin.GradedCardPreventBelowMarket.Value && markupPct > 0f;

                float convRate = GameInstance.GetCurrencyConversionRate();
                float roundDiv = GameInstance.GetCurrencyRoundDivideAmount();

                float marketPrice = 0f;

                try
                {
                    marketPrice = CPlayerData.GetCardMarketPrice(cd) * convRate;
                }
                catch (Exception ex)
                {
                    LogWarnThrottled("GradedMP." + cd.monsterType,
                        "GetCardMarketPrice threw for graded card " + cd.monsterType +
                        " grade " + cd.cardGrade + ": " + ex.Message);
                }

                // Safe default: skip cards with no valid price (covers modded EPL cards that
                // don't have a graded market price entry yet)
                if (marketPrice <= 0f)
                {
                    LogDebug("Skipping graded card with 0 or invalid market price: " +
                             cd.monsterType + " grade " + cd.cardGrade);
                    return false;
                }

                float price = Mathf.RoundToInt(marketPrice * mult * roundDiv) / roundDiv;
                price = ApplyRounding(price, marketPrice, roundEnabled, roundStep, noUnderMarket);
                price = Mathf.Max(Plugin.AbsoluteMinPrice.Value, price);

                // Convert back to internal currency
                price = Mathf.RoundToInt(price / convRate * 100f) / 100f;

                CPlayerData.SetCardPrice(cd, price);
                LogDebug("Priced graded " + cd.monsterType + " grade " + cd.cardGrade + " @ " + price);

                return true;
            }
            catch (Exception ex)
            {
                LogWarnThrottled("GradedCard." + (cd != null ? cd.monsterType.ToString() : "null"),
                    "PriceGradedCompartmentCard failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Price all graded cards across all shelves.</summary>
        internal static void PriceAllGradedShelfCards()
        {
            try
            {
                List<CardShelf> shelves = CSingleton<ShelfManager>.Instance.m_CardShelfList;
                if (shelves == null) return;

                int priced = 0, skipped = 0;

                for (int i = 0; i < shelves.Count; i++)
                {
                    List<InteractableCardCompartment> comps = shelves[i].GetCardCompartmentList();
                    if (comps == null) continue;

                    for (int j = 0; j < comps.Count; j++)
                    {
                        InteractableCardCompartment comp = comps[j];
                        List<InteractableCard3d> stored = comp.m_StoredCardList;
                        if (stored == null || stored.Count == 0) continue;

                        for (int k = 0; k < stored.Count; k++)
                        {
                            try
                            {
                                CardData cd = stored[k].m_Card3dUI.m_CardUI.GetCardData();
                                if (cd == null || cd.cardGrade <= 0) continue;

                                if (PriceGradedCompartmentCard(cd, comp))
                                    priced++;
                                else
                                    skipped++;
                            }
                            catch { skipped++; }
                        }
                    }
                }

                LogDebug("PriceAllGradedShelfCards: priced=" + priced + " skipped=" + skipped);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PriceSlinger] PriceAllGradedShelfCards failed: " + ex);
            }
        }

        // ── Sound ─────────────────────────────────────────────────────────────

        internal static void PlaySound()
        {
            try
            {
                if (Plugin.PlaySoundOnPrice.Value)
                    SoundManager.PlayAudio("SFX_Popup2", 1f, 0.3f);
            }
            catch { }
        }
    }
}