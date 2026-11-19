using HarmonyLib;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    public class GhostSettings : ModSettings
    {
        // Opacity of the ghost copy drawn above tree canopies. 0 turns the ghost off.
        public float alpha = 0.25f;
        public bool includeAnimals = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref alpha, "alpha", 0.25f);
            Scribe_Values.Look(ref includeAnimals, "includeAnimals", true);
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
            listing.CheckboxLabeled("CG_IncludeAnimals".Translate(), ref Settings.includeAnimals, "CG_IncludeAnimals_Desc".Translate());
            listing.End();
        }
    }
}
