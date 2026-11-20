using HarmonyLib;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    public class GhostSettings : ModSettings
    {
        // How much of what stands under a canopy shows through it: the canopy picture is laid at 1 - alpha.
        // 0 makes canopies solid; 1 leaves trees fully below pawns.
        public float alpha = 0.4f;
        // Resolution of the see-through layer relative to the screen (TreeOverlay); lower is lighter on the GPU.
        public float resolution = 0.5f;
        public bool hideBetterTreesButton;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref alpha, "alpha", 0.4f);
            Scribe_Values.Look(ref resolution, "resolution", 0.5f);
            Scribe_Values.Look(ref hideBetterTreesButton, "hideBetterTreesButton", false);
        }
    }

    public class CanopyGhostMod : Mod
    {
        public static GhostSettings Settings;

        public CanopyGhostMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<GhostSettings>();
            new Harmony("ifchen0.canopyghost").PatchAll(typeof(CanopyGhostMod).Assembly);
        }

        public override string SettingsCategory() => "Canopy Ghost";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("CG_Alpha".Translate(Settings.alpha.ToStringPercent()), tooltip: "CG_Alpha_Desc".Translate());
            Settings.alpha = Mathf.Round(listing.Slider(Settings.alpha, 0f, 1f) * 20f) / 20f;
            listing.Label("CG_Resolution".Translate(Settings.resolution.ToStringPercent()), tooltip: "CG_Resolution_Desc".Translate());
            Settings.resolution = Mathf.Max(0.25f, Mathf.Round(listing.Slider(Settings.resolution, 0.25f, 1f) * 4f) / 4f);
            if (ModsConfig.IsActive("ChaoticEnrico.BetterTrees"))
                listing.CheckboxLabeled("CG_HideBetterTreesButton".Translate(), ref Settings.hideBetterTreesButton, "CG_HideBetterTreesButton_Desc".Translate());
            listing.End();
        }
    }
}
