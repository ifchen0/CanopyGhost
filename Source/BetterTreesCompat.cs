using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Better Trees' own see-through modes (pawn ghosts, cursor transparency) clash with the canopy layer: its pawn
    /// ghosts look for trees above pawns, which this mod has moved back below them, and its cursor transparency changes
    /// the same tree materials the canopy layer draws with. So while both mods are loaded, Better Trees' toggle (button,
    /// hotkey and right-click settings) is blocked and its mode is turned off for this session, without saving it to
    /// Better Trees' settings. All of it goes through reflection and is skipped if Better Trees changes these members.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class BetterTreesCompat
    {
        static BetterTreesCompat()
        {
            MethodInfo hud = AccessTools.Method("BetterTrees.PlaySettings_HUD_Patch:Postfix");
            if (hud == null)
                return;
            new Harmony("ifchen0.canopyghost.bettertrees")
                .Patch(hud, prefix: new HarmonyMethod(typeof(BetterTreesCompat), nameof(SkipPrefix)));
            // Better Trees applies its mode in its own startup constructor, whose order against ours is not fixed.
            LongEventHandler.ExecuteWhenFinished(TurnOffGhostMode);
        }

        public static bool SkipPrefix() => false;

        private static void TurnOffGhostMode()
        {
            try
            {
                object settings = AccessTools.Field("BetterTrees.BetterTreesMod:settings")?.GetValue(null);
                FieldInfo mode = settings == null ? null : AccessTools.Field(settings.GetType(), "ghostMode");
                if (mode == null || !mode.FieldType.IsEnum)
                {
                    Log.Warning("[Canopy Ghost] Could not find Better Trees' transparency mode; it was left as is.");
                    return;
                }
                if (Convert.ToInt32(mode.GetValue(settings)) == 0)
                    return;
                mode.SetValue(settings, Enum.ToObject(mode.FieldType, 0));
                // Restores the tree shaders and removes its cursor patch, then lets the pawn ghosts unpatch themselves.
                AccessTools.Method("BetterTrees.TransparencyController:RemovePatch")?.Invoke(null, null);
                AccessTools.Method("BetterTrees.PawnGhosts:Refresh")?.Invoke(null, null);
            }
            catch (Exception e)
            {
                Log.Warning("[Canopy Ghost] Could not turn off Better Trees' transparency mode: " + e);
            }
        }
    }
}
