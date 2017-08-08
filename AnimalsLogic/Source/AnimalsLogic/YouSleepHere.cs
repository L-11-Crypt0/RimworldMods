﻿using System;
using System.Collections.Generic;
using System.Linq;
using Harmony;
using RimWorld;
using Verse;
using Verse.AI;
using UnityEngine;
using System.Reflection.Emit;
using System.Reflection;

namespace AnimalsLogic
{
    /*
     * Animals needing medical attention would stay in bed even if it is outside their allowed area.
     * 
     * Also, allows to assign beds to specific animals. Idea (and even one line of code) shamelessly stolen from The Salad Spinner of Woe!!
     */

    class YouSleepHere
    {
        // Patches area seeking job so animal in a medical bed would stay.
        [HarmonyPatch(typeof(JobGiver_SeekAllowedArea), "TryGiveJob", new Type[] { typeof(Pawn) })]
        static class JobGiver_SeekAllowedArea_TryGiveJob_Patch
        {
            static bool Prefix(ref Job __result, Pawn pawn)
            {
                if (pawn.RaceProps.Animal && pawn.InBed() && (pawn.CurrentBed().Medical || HealthAIUtility.ShouldSeekMedicalRestUrgent(pawn) || pawn.health.hediffSet.HasTendedAndHealingInjury()))
                {
                    __result = null;
                    return false;
                }
                return true;
            }
        }

        // Patches GetGizmos to display medical and set owner buttons
        [HarmonyPatch(typeof(Building_Bed), "GetGizmos", new Type[0])]
        static class Building_Bed_GetGizmos_Patch
        {
            static void Postfix(ref IEnumerable<Gizmo> __result, ref Building_Bed __instance)
            {
                if (__instance.Faction != Faction.OfPlayer || __instance.def.building.bed_humanlike)
                    return;

                Building_Bed bed = __instance;
                var gizmos = new List<Gizmo>(__result)
                {
                    new Command_Toggle
                    {
                        defaultLabel = "CommandBedSetAsMedicalLabel".Translate(),
                        defaultDesc = "CommandBedSetAsMedicalDesc".Translate(),
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/AsMedical", true),
                        isActive = (() => bed.Medical),
                        toggleAction = delegate
                        {
                            bed.Medical = !bed.Medical;
                        },
                        hotKey = KeyBindingDefOf.Misc2
                    }
                };

                if (!bed.Medical)
                {
                    gizmos.Add(
                        new Command_Action
                        {
                            defaultLabel = "CommandBedSetOwnerLabel".Translate(),
                            icon = ContentFinder<Texture2D>.Get("UI/Commands/AssignOwner", true),
                            defaultDesc = "CommandBedSetOwnerDesc".Translate(),
                            action = delegate
                            {
                                Find.WindowStack.Add(new Dialog_AssignBuildingOwner(bed));
                            },
                            hotKey = KeyBindingDefOf.Misc3
                        }
                    );
                }

                __result = gizmos.AsEnumerable();
            }
        }

        // patches get_AssigningCandidates to display list of animals
        [HarmonyPatch(typeof(Building_Bed), "get_AssigningCandidates", new Type[0])]
        static class Building_Bed_get_AssigningCandidates_Patch
        {
            static void Postfix(ref IEnumerable<Pawn> __result, ref Building_Bed __instance)
            {
                if (__instance.def.building.bed_humanlike)
                    return;

                if (!__instance.Spawned)
                {
                    __result = Enumerable.Empty<Pawn>();
                }

                __result = from p in Find.VisibleMap.mapPawns.AllPawns
                           where p.RaceProps.Animal && p.Faction == Faction.OfPlayer
                           select p;
            }
        }

        // patches TryAssignPawn to accept animals
        [HarmonyPatch(typeof(Building_Bed), "TryAssignPawn", new Type[] { typeof(Pawn) })]
        static class Building_Bed_TryAssignPawn_Patch
        {
            static bool Prefix(Pawn owner)
            {
                if (owner.ownership == null)
                    owner.ownership = new Pawn_Ownership(owner);
                return true;
            }
        }

        // patches set_Medical to allow medical assignment for non-humanlikes
        [HarmonyPatch(typeof(Building_Bed), "set_Medical", new Type[] { typeof(bool) })]
        static class Building_Bed_set_Medical_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo bed_humanlike = typeof(BuildingProperties).GetField("bed_humanlike");

                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    //IL_000d: ldfld class Verse.ThingDef Verse.Thing::def
                    //IL_0012: ldfld class RimWorld.BuildingProperties Verse.ThingDef::building
                    //IL_0017: ldfld bool RimWorld.BuildingProperties::bed_humanlike
                    if (codes[i].opcode == OpCodes.Ldfld)
                    {
                        FieldInfo operand = (FieldInfo)codes[i].operand;
                        if (operand == bed_humanlike)
                        {
                            codes.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                            codes.Insert(i + 2, new CodeInstruction(OpCodes.Ldc_I4_1));
                            break;
                        }
                    }
                }

                return codes.AsEnumerable();
            }
        }

        // patches GetInspectString to display all stats for non-humanlike beds
        [HarmonyPatch(typeof(Building_Bed), "GetInspectString", new Type[0])]
        static class Building_Bed_GetInspectString_Patch
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo bed_humanlike = typeof(BuildingProperties).GetField("bed_humanlike");

                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    //IL_000d: ldfld class Verse.ThingDef Verse.Thing::def
                    //IL_0012: ldfld class RimWorld.BuildingProperties Verse.ThingDef::building
                    //IL_0017: ldfld bool RimWorld.BuildingProperties::bed_humanlike
                    if (codes[i].opcode == OpCodes.Ldfld)
                    {
                        FieldInfo operand = (FieldInfo)codes[i].operand;
                        if (operand == bed_humanlike)
                        {
                            codes.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                            codes.Insert(i + 2, new CodeInstruction(OpCodes.Ldc_I4_1));
                            break;
                        }
                    }
                }

                return codes.AsEnumerable();
            }
        }

        /*
         * Patches RestUtility to find correct animal bed for medical purposes - medical if possible, assigned otherwise. Otherwise they would just lay down on nearest one and claim it.
         */

        [HarmonyPatch]
        static class RestUtility_FindPatientBedFor_Patch
        {
            static MethodInfo TargetMethod()
            {
                var toils_inner = typeof(RestUtility).GetNestedTypes(AccessTools.all);
                foreach (var inner_class in toils_inner)
                {
                    if (!inner_class.Name.Contains("<FindPatientBedFor>"))
                        continue;

                    var methods = inner_class.GetMethods(AccessTools.all);
                    foreach (var method in methods)
                    {
                        if (method.Name.Contains("<>m__"))
                            return method;
                    }
                }
                Log.Error("Animal Logic is unable to detect FindPatientBedFor inner method.");
                return null;
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                // 		IL_0025: ldfld bool RimWorld.BuildingProperties::bed_humanlike
                FieldInfo bed_humanlike = typeof(BuildingProperties).GetField("bed_humanlike");

                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Ldfld)
                    {
                        FieldInfo operand = (FieldInfo)codes[i].operand;
                        if (operand == bed_humanlike)
                        {
                            //IL_001a: ldloc.0
                            //IL_001b: ldfld class Verse.ThingDef Verse.Thing::def
                            //IL_0020: ldfld class RimWorld.BuildingProperties Verse.ThingDef::building
                            //IL_0025: ldfld bool RimWorld.BuildingProperties::bed_humanlike
                            //IL_002a: brfalse IL_0031

                            codes.RemoveAt(i - 3);
                            codes.RemoveAt(i - 3);
                            codes.RemoveAt(i - 3);
                            codes.RemoveAt(i - 3);
                            codes.RemoveAt(i - 3);
                            break;
                        }
                    }
                }

                return codes.AsEnumerable();
            }
        }
        
        /*
         * Patches WorkGiver_TakeToBedToOperate, so it won't stuck when animal is already moving to bed and therefore reserved it.
         */

        // public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        [HarmonyPatch(typeof(WorkGiver_TakeToBedToOperate), "HasJobOnThing", new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) })]
        static class WorkGiver_TakeToBedToOperate_HasJobOnThing_Patch
        {
            static bool Prefix(ref bool __result, Pawn pawn, Thing t)
            {
                Pawn pawn2 = t as Pawn;
                if (pawn2 != null && pawn2.CurJob != null && pawn2.CurJob.def == JobDefOf.LayDown)
                {
                    __result = false;
                    return false;
                }

                return true;
            }
        }
    }
}
