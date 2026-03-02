using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
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

        // ── Graded price writing ──────────────────────────────────────────────
        //
        // EnhancedPrefabLoader patches CPlayerData.SetCardPrice and replaces it with:
        //   m_CardPriceList[cardData.cardIndex] = price
        // This is completely wrong for graded cards — it ignores GetCardSaveIndex,
        // ignores cardGrade, and ignores the per-expansion m_GradedCardPriceSetList
        // structure, causing ArgumentOutOfRangeException and the price is never saved.
        //
        // Vanilla SetCardPrice does:
        //   int saveIndex = CPlayerData.GetCardSaveIndex(cardData);
        //   m_GradedCardPriceSetList[saveIndex].floatDataList[cardData.cardGrade - 1] = price
        //   (with per-expansion variants for Destiny, Ghost, Megabot, FantasyRPG, CatJob)
        //
        // GradingOverhaul encodes cardGrade as large integers (e.g. PSA grade 10 =
        // 200000090). We must decode to get the actual 1-10 grade for the floatDataList
        // index, or the index would be 200000089 and crash.
        //
        // Strategy: replicate vanilla exactly using reflection, using GetCardSaveIndex
        // for the outer index and the actual decoded grade (1-10) for the inner index.
        // Fall back to calling the original SetCardPrice via a transpiler-bypassing
        // direct invoke if reflection fails.

        private static bool _gradedReflectionCached;
        private static MethodInfo _getCardSaveIndexMethod;

        // Per-expansion graded price list fields (List<FloatDataList> or similar)
        private static FieldInfo _gradedListTetramon;
        private static FieldInfo _gradedListDestiny;
        private static FieldInfo _gradedListGhost;
        private static FieldInfo _gradedListGhostBlack;
        private static FieldInfo _gradedListMegabot;
        private static FieldInfo _gradedListFantasyRPG;
        private static FieldInfo _gradedListCatJob;

        // floatDataList field on the wrapper element type
        private static FieldInfo _floatDataListField;

        // GradingOverhaul decode method (soft dep — null if not installed)
        private static bool _gradingOverhaulCached;
        private static MethodInfo _decodeGradeMethod;

        private static void EnsureGradedReflectionCached()
        {
            if (_gradedReflectionCached) return;
            _gradedReflectionCached = true;

            // GetCardSaveIndex — vanilla index for the outer list
            _getCardSaveIndexMethod = AccessTools.Method(typeof(CPlayerData), "GetCardSaveIndex",
                new[] { typeof(CardData) });
            if (_getCardSaveIndexMethod == null)
                Plugin.Log.LogWarning("[PriceSlinger] CPlayerData.GetCardSaveIndex not found — graded pricing will fall back.");

            // Per-expansion graded price set lists
            _gradedListTetramon = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetList");
            _gradedListDestiny = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetListDestiny");
            _gradedListGhost = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetListGhost");
            _gradedListGhostBlack = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetListGhostBlack");
            _gradedListMegabot = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetListMegabot");
            _gradedListFantasyRPG = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetListFantasyRPG");
            _gradedListCatJob = AccessTools.Field(typeof(CPlayerData), "m_GradedCardPriceSetListCatJob");

            if (_gradedListTetramon != null)
                LogDebug("Found m_GradedCardPriceSetList — will use direct vanilla write path.");
            else
                Plugin.Log.LogWarning("[PriceSlinger] m_GradedCardPriceSetList not found.");

            // floatDataList field — find it by inspecting the element type of the list
            if (_gradedListTetramon != null)
            {
                try
                {
                    // Get the static list, peek at element type via reflection on the generic arg
                    var listType = _gradedListTetramon.FieldType;                    // e.g. List<FloatDataList>
                    var elemType = listType.IsGenericType
                        ? listType.GetGenericArguments()[0]
                        : listType.GetElementType();
                    if (elemType != null)
                    {
                        _floatDataListField =
                            AccessTools.Field(elemType, "floatDataList") ??
                            AccessTools.Field(elemType, "m_FloatDataList") ??
                            AccessTools.Field(elemType, "FloatDataList");
                    }
                    if (_floatDataListField != null)
                        LogDebug("Found floatDataList field on " + (elemType?.Name ?? "?"));
                    else
                        Plugin.Log.LogWarning("[PriceSlinger] floatDataList not found on element type " + (elemType?.Name ?? "null"));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[PriceSlinger] Could not inspect floatDataList: " + ex.Message);
                }
            }
        }

        private static void EnsureGradingOverhaulCached()
        {
            if (_gradingOverhaulCached) return;
            _gradingOverhaulCached = true;

            // Try to find GradingOverhaul.Helper.DecodeGrade(int, out GradingCompany, out int, out int)
            var helperType = AccessTools.TypeByName("TCGCardShopSimulator.GradingOverhaul.Helper");
            if (helperType != null)
            {
                _decodeGradeMethod = AccessTools.Method(helperType, "DecodeGrade");
                if (_decodeGradeMethod != null)
                    LogDebug("Found GradingOverhaul.Helper.DecodeGrade — will decode encoded grades.");
            }
            else
            {
                LogDebug("GradingOverhaul not detected — using raw cardGrade as grade index.");
            }
        }

        /// <summary>
        /// Returns the actual 1-10 grade from a potentially encoded cardGrade.
        /// If GradingOverhaul is installed, decodes it. Otherwise returns raw value.
        /// </summary>
        private static int GetActualGrade(int encodedGrade)
        {
            EnsureGradingOverhaulCached();

            if (_decodeGradeMethod != null)
            {
                try
                {
                    // DecodeGrade(int cardGrade, out GradingCompany company, out int grade, out int cert)
                    var args = new object[] { encodedGrade, null, null, null };
                    _decodeGradeMethod.Invoke(null, args);
                    return (int)args[2]; // grade is out param index 2
                }
                catch { }
            }

            // No GradingOverhaul — vanilla Cardinals grades are 1-10 raw
            return encodedGrade;
        }

        /// <summary>
        /// Selects the correct per-expansion graded price list field based on CardData.
        /// Handles Ghost/GhostBlack split.
        /// </summary>
        private static FieldInfo GetExpansionGradedListField(CardData cd)
        {
            switch (cd.expansionType)
            {
                case ECardExpansionType.Tetramon: return _gradedListTetramon;
                case ECardExpansionType.Destiny: return _gradedListDestiny;
                case ECardExpansionType.Megabot: return _gradedListMegabot;
                case ECardExpansionType.FantasyRPG: return _gradedListFantasyRPG;
                case ECardExpansionType.CatJob: return _gradedListCatJob;
                case ECardExpansionType.Ghost:
                    return cd.isDestiny ? _gradedListGhostBlack : _gradedListGhost;
                default:
                    return _gradedListTetramon; // best guess for future expansions
            }
        }

        /// <summary>
        /// Sets the price of a graded card, bypassing EnhancedPrefabLoader's broken
        /// SetCardPrice patch. Replicates vanilla SetCardPrice logic exactly using
        /// GetCardSaveIndex + floatDataList[actualGrade - 1], supporting both vanilla
        /// cardGrade (1-10) and GradingOverhaul encoded grades.
        /// Falls back to calling SetCardPrice directly (skipping EPL's Harmony prefix)
        /// if reflection is unavailable.
        /// </summary>
        private static void SetGradedCardPriceSafe(CardData cd, float price)
        {
            EnsureGradedReflectionCached();

            // Decode actual 1-10 grade (handles GradingOverhaul encoded values)
            int actualGrade = GetActualGrade(cd.cardGrade);
            if (actualGrade < 1 || actualGrade > 10)
            {
                LogWarnThrottled("BadGrade",
                    "Unexpected actualGrade " + actualGrade + " for encoded " + cd.cardGrade +
                    " on " + cd.monsterType + " — skipping graded price write.");
                return;
            }

            int gradeIndex = actualGrade - 1; // 0-based index into floatDataList

            // Main path: replicate vanilla SetCardPrice directly
            if (_getCardSaveIndexMethod != null && _floatDataListField != null)
            {
                try
                {
                    int saveIndex = (int)_getCardSaveIndexMethod.Invoke(null, new object[] { cd });
                    FieldInfo listField = GetExpansionGradedListField(cd);

                    if (listField != null)
                    {
                        var outerList = listField.GetValue(null); // static field

                        // outerList is List<T> where T has floatDataList
                        // Access via IList interface to stay type-agnostic
                        var ilist = outerList as System.Collections.IList;
                        if (ilist != null && saveIndex >= 0 && saveIndex < ilist.Count)
                        {
                            var element = ilist[saveIndex];
                            var innerList = _floatDataListField.GetValue(element) as System.Collections.IList;
                            if (innerList != null && gradeIndex >= 0 && gradeIndex < innerList.Count)
                            {
                                innerList[gradeIndex] = price;

                                // Fire the price-changed event the same way vanilla does
                                try
                                {
                                    var evtType = AccessTools.TypeByName("CEventPlayer_CardPriceChanged");
                                    if (evtType != null)
                                    {
                                        var evt = Activator.CreateInstance(evtType, new object[] { cd, price });
                                        AccessTools.Method(typeof(CEventManager), "QueueEvent")?.Invoke(null, new[] { evt });
                                    }
                                }
                                catch { /* event fire is best-effort */ }

                                LogDebug("SetGradedCardPriceSafe: " + cd.expansionType + " " + cd.monsterType +
                                         " grade " + actualGrade + " [saveIndex=" + saveIndex +
                                         "] @ " + price);
                                return;
                            }
                            else
                            {
                                LogWarnThrottled("GradedInnerOOB",
                                    "floatDataList out of range: gradeIndex=" + gradeIndex +
                                    " count=" + (innerList?.Count.ToString() ?? "null") +
                                    " for " + cd.monsterType);
                            }
                        }
                        else
                        {
                            LogWarnThrottled("GradedOuterOOB",
                                "outer graded list out of range: saveIndex=" + saveIndex +
                                " count=" + (ilist?.Count.ToString() ?? "null") +
                                " expansion=" + cd.expansionType);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogWarnThrottled("GradedDirectWrite",
                        "Direct vanilla write failed: " + ex.Message + ". Falling back to SetCardPrice bypass.");
                }
            }

            // Fallback: invoke the original SetCardPrice method body directly,
            // skipping Harmony patches entirely via MethodInfo.Invoke on the
            // unpatched original (not guaranteed to work if EPL's patch is a prefix
            // that returns false, but worth trying).
            try
            {
                var originalSetCardPrice = AccessTools.Method(typeof(CPlayerData), "SetCardPrice",
                    new[] { typeof(CardData), typeof(float) });
                if (originalSetCardPrice != null)
                {
                    originalSetCardPrice.Invoke(null, new object[] { cd, price });
                    LogDebug("SetGradedCardPriceSafe: fell back to SetCardPrice invoke for " + cd.monsterType);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogWarnThrottled("GradedFallback", "SetCardPrice fallback also failed: " + ex.Message);
            }

            Plugin.Log.LogWarning("[PriceSlinger] SetGradedCardPriceSafe: all approaches exhausted, price not saved for " +
                                  cd.monsterType + " grade=" + cd.cardGrade);
        }

        // ── Bulk box detection ────────────────────────────────────────────────

        private static bool IsBulkBox(EItemType type)
        {
            int t = (int)type;
            return (t >= 37 && t <= 38) || (t >= 89 && t <= 94);
        }

        // ── Rounding helper ───────────────────────────────────────────────────

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

                float convRate = GameInstance.GetCurrencyConversionRate();
                float roundDiv = GameInstance.GetCurrencyRoundDivideAmount();

                for (int i = 0; i < stored.Count; i++)
                {
                    try
                    {
                        CardData cd = stored[i].m_Card3dUI.m_CardUI.GetCardData();
                        if (cd == null) continue;

                        // Route graded cards to their own safe pricer
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
        /// Uses SetGradedCardPriceSafe instead of CPlayerData.SetCardPrice to avoid
        /// the EnhancedPrefabLoader patch crash (ArgumentOutOfRangeException on
        /// cardIndex vs gradedCardIndex mismatch).
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

                if (marketPrice <= 0f)
                {
                    LogDebug("Skipping graded card with 0 or invalid market price: " +
                             cd.monsterType + " grade " + cd.cardGrade);
                    return false;
                }

                float price = Mathf.RoundToInt(marketPrice * mult * roundDiv) / roundDiv;
                price = ApplyRounding(price, marketPrice, roundEnabled, roundStep, noUnderMarket);
                price = Mathf.Max(Plugin.AbsoluteMinPrice.Value, price);

                price = Mathf.RoundToInt(price / convRate * 100f) / 100f;

                // Safe writer bypasses the broken EnhancedPrefabLoader SetCardPrice patch
                SetGradedCardPriceSafe(cd, price);

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