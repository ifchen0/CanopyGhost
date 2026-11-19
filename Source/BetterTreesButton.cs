using System.Reflection;
using HarmonyLib;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Optionally hides Better Trees' cursor-transparency button in the bottom-right play settings row.
    /// Its hotkey keeps working. Patched only when Better Trees is loaded.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class BetterTreesButton
    {
        static BetterTreesButton()
        {
            MethodInfo target = AccessTools.Method("BetterTrees.PlaySettings_HUD_Patch:Postfix");
            if (target == null)
                return;
            new Harmony("ifchen0.canopyghost.bettertrees")
                .Patch(target, prefix: new HarmonyMethod(typeof(BetterTreesButton), nameof(Prefix)));
        }

        public static bool Prefix() => !CanopyGhostMod.Settings.hideBetterTreesButton;
    }
}
