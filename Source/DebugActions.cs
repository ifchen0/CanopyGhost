using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace CanopyGhost
{
    /// <summary>
    /// Dev-mode helper: lays out every kind of thing the ghosts handle differently, each under its own tree
    /// with an uncovered copy further south, so they can be checked side by side. Changes the map; do not
    /// keep the save afterwards.
    /// </summary>
    public static class DebugActions
    {
        private const string Category = "Canopy Ghost";

        [DebugAction(Category, "Spawn check scene", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SpawnCheckScene()
        {
            Map map = Find.CurrentMap;
            IntVec3 origin = UI.MouseCell();
            var labels = new List<string>();
            int column = 0;

            void Slot(string label, System.Action<IntVec3> spawn)
            {
                // Columns three cells apart. The tree stands one cell south of the slot so its canopy covers it;
                // a reference copy stands three cells south, clear of the canopy, to compare the ghost against.
                IntVec3 cell = origin + new IntVec3(column * 3, 0, 0);
                IntVec3 tree = cell + IntVec3.South;
                IntVec3 reference = cell + IntVec3.South * 3;
                column++;
                if (!cell.InBounds(map) || !reference.InBounds(map))
                    return;
                ClearCell(map, cell);
                ClearCell(map, tree);
                ClearCell(map, reference);
                PlantTree(map, tree);
                spawn(cell);
                spawn(reference);
                labels.Add($"{cell.x},{cell.z} {label}");
            }

            Slot("human corpse, fresh", c => SpawnCorpse(map, c, PawnKindDefOf.Villager, RotDrawMode.Fresh));
            Slot("human corpse, rotting", c => SpawnCorpse(map, c, PawnKindDefOf.Villager, RotDrawMode.Rotting));
            Slot("human corpse, dessicated", c => SpawnCorpse(map, c, PawnKindDefOf.Villager, RotDrawMode.Dessicated));
            PawnKindDef animal = DefDatabase<PawnKindDef>.GetNamedSilentFail("Muffalo") ?? PawnKindDefOf.Thrumbo;
            Slot("animal corpse, fresh", c => SpawnCorpse(map, c, animal, RotDrawMode.Fresh));
            Slot("animal corpse, rotting", c => SpawnCorpse(map, c, animal, RotDrawMode.Rotting));
            Slot("animal corpse, dessicated", c => SpawnCorpse(map, c, animal, RotDrawMode.Dessicated));
            Slot("weapon (random angle)", c => SpawnItem(map, c, "Gun_AssaultRifle"));
            Slot("weapon (random angle)", c => SpawnItem(map, c, "MeleeWeapon_LongSword"));
            Slot("packed furniture", c => SpawnMinified(map, c, "Armchair"));
            Slot("packed tree", c => SpawnMinified(map, c, "Plant_TreeOak"));
            Slot("item on a shelf (no ghost)", c =>
            {
                SpawnBuilding(map, c, "Shelf");
                SpawnItem(map, c, "MedicineIndustrial");
            });
            Slot("steel stack", c => SpawnItem(map, c, "Steel"));

            Find.CameraDriver.JumpToCurrentMapLoc(origin + new IntVec3(column, 0, 0));
            Log.Message("[Canopy Ghost] check scene (west to east):\n" + string.Join("\n", labels));
            Messages.Message($"Canopy Ghost check scene: {labels.Count} slots, see the log for the list.", MessageTypeDefOf.NeutralEvent, false);
        }

        private static void ClearCell(Map map, IntVec3 c)
        {
            foreach (Thing t in c.GetThingList(map).ToArray())
            {
                if (t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth)
                    t.Destroy();
            }
        }

        private static void PlantTree(Map map, IntVec3 c)
        {
            ThingDef def = DefDatabase<ThingDef>.AllDefsListForReading.Find(CanopyCache.IsCanopyTree)
                           ?? DefDatabase<ThingDef>.GetNamed("Plant_TreeOak");
            var tree = (Plant)ThingMaker.MakeThing(def);
            tree.Growth = 1f;
            GenSpawn.Spawn(tree, c, map);
        }

        private static void SpawnCorpse(Map map, IntVec3 c, PawnKindDef kind, RotDrawMode rot)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null));
            GenSpawn.Spawn(pawn, c, map);
            pawn.Kill(null);
            CompRottable rottable = pawn.Corpse?.GetComp<CompRottable>();
            if (rottable == null || rot == RotDrawMode.Fresh)
                return;
            rottable.RotProgress = rot == RotDrawMode.Rotting
                ? rottable.PropsRot.TicksToRotStart + 1
                : rottable.PropsRot.TicksToDessicated + 1;
        }

        private static void SpawnItem(Map map, IntVec3 c, string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                return;
            Thing t = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            t.stackCount = Mathf.Min(def.stackLimit, 25);
            GenSpawn.Spawn(t, c, map);
        }

        private static void SpawnMinified(Map map, IntVec3 c, string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                return;
            Thing building = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            if (building is Plant plant)
                plant.Growth = 1f;
            GenSpawn.Spawn(building.MakeMinified(), c, map);
        }

        private static void SpawnBuilding(Map map, IntVec3 c, string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                return;
            Thing building = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            building.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(building, c, map, Rot4.North);
        }
    }
}
