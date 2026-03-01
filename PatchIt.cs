using HarmonyLib;
using System;
using UnityEngine;

namespace PriceSlinger
{
    [HarmonyPatch]
    public static class PatchIt
    {
        private static bool _isRunning;

        // ── Hotkeys ───────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(CGameManager), "Update")]
        [HarmonyPostfix]
        public static void OnGameManagerUpdate()
        {
            if (_isRunning) return;

            try
            {
                if (Plugin.PriceAllKey.Value.IsDown())
                {
                    _isRunning = true;
                    Pricer.PriceAllShelfCards();
                    Pricer.PriceAllGradedShelfCards();
                    Pricer.PriceAllItems();
                    Pricer.PlaySound();
                    _isRunning = false;
                    return;
                }

                if (Plugin.PriceCardsKey.Value.IsDown())
                {
                    _isRunning = true;
                    Pricer.PriceAllShelfCards();
                    Pricer.PlaySound();
                    _isRunning = false;
                    return;
                }

                if (Plugin.PriceGradedKey.Value.IsDown())
                {
                    _isRunning = true;
                    Pricer.PriceAllGradedShelfCards();
                    Pricer.PlaySound();
                    _isRunning = false;
                    return;
                }

                if (Plugin.PriceItemsKey.Value.IsDown())
                {
                    _isRunning = true;
                    Pricer.PriceAllItems();
                    Pricer.PlaySound();
                    _isRunning = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PriceSlinger] Hotkey check failed: " + ex);
                _isRunning = false;
            }
        }

        // ── On card placed to shelf ───────────────────────────────────────────

        /// <summary>
        /// Fires when the player releases a card onto a shelf compartment.
        /// We inspect what's in the compartment and route to the right pricer.
        /// </summary>
        [HarmonyPatch(typeof(InteractableCardCompartment), "OnMouseButtonUp")]
        [HarmonyPostfix]
        public static void OnCardCompartmentMouseUp(ref InteractableCardCompartment __instance)
        {
            try
            {
                if (__instance == null) return;

                var stored = __instance.m_StoredCardList;
                if (stored == null || stored.Count == 0) return;

                // Determine if the compartment contains a graded card
                bool hasGraded = false;

                try
                {
                    CardData cd = stored[0].m_Card3dUI.m_CardUI.GetCardData();
                    if (cd != null && cd.cardGrade > 0)
                        hasGraded = true;
                }
                catch { }

                if (hasGraded)
                {
                    if (Plugin.PriceGradedOnCardPlaced.Value)
                    {
                        // Price each graded card in this compartment individually
                        for (int i = 0; i < stored.Count; i++)
                        {
                            try
                            {
                                CardData cd = stored[i].m_Card3dUI.m_CardUI.GetCardData();
                                if (cd != null && cd.cardGrade > 0)
                                    Pricer.PriceGradedCompartmentCard(cd, __instance);
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    if (Plugin.PriceOnCardPlaced.Value)
                        Pricer.PriceCompartment(__instance);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PriceSlinger] OnCardCompartmentMouseUp failed: " + ex);
            }
        }
    }
}
