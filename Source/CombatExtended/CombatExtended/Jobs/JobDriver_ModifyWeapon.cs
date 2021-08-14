﻿using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CombatExtended
{
    public class JobDriver_ModifyWeapon : JobDriver
    {
        public WeaponPlatform EquipedWeapon
        {
            get
            {
                return (WeaponPlatform)pawn.equipment.Primary;
            }
        }

        public WeaponPlatform Weapon
        {
            get
            {
                return (WeaponPlatform)job.targetQueueA[0].Thing;
            }
        }
        
        public Building GunsmithingTable
        {
            get
            {
                return TargetA.Thing as Building;
            }
        }
       
        public AttachmentDef AttachmentDef
        {
            get
            {
                return (job as JobCE).targetThingDefs[0] as AttachmentDef;
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            /*
             * Taken from vanilla's JobDriver_DoBill 
             */
            Thing thing = job.GetTarget(TargetIndex.A).Thing;
            if (!pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed))
                return false;
            if (thing != null && thing.def.hasInteractionCell && !pawn.ReserveSittableOrSpot(thing.InteractionCell, job, errorOnFailed))
                return false;
            pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.B), job);
            return true;
        }

        public override IEnumerable<Toil> MakeNewToils()
        {
            /*
             * Inspired by vanilla's JobDriver_DoBill 
             */
            this.FailOn(() => EquipedWeapon != Weapon);
            this.AddEndCondition(delegate
            {
                Thing thing = GetActor().jobs.curJob.GetTarget(TargetIndex.A).Thing;
                return (!(thing is Building) || thing.Spawned) ? JobCondition.Ongoing : JobCondition.Incompletable;
            });
            this.FailOnBurningImmobile(TargetIndex.A);
            this.AddFinishAction(TryContinueModifyingJob);
            // go to the crafting spot
            Toil gotoBillGiver = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

            // skip hauling if there is nothing to haul
            yield return Toils_Jump.JumpIf(gotoBillGiver, () => job.GetTargetQueue(TargetIndex.B).NullOrEmpty());
            foreach (Toil t in CollectIngredientsToils())
                yield return t;

            yield return gotoBillGiver;
            // crafting wait time
            Toil waitToil = new Toil();
            waitToil.defaultDuration = 30;
            waitToil.defaultCompleteMode = ToilCompleteMode.Delay;
            yield return waitToil.WithProgressBarToilDelay(TargetIndex.A);
            // modify the actual weapon
            Toil modifyToil = new Toil();
            modifyToil.defaultCompleteMode = ToilCompleteMode.Instant;
            modifyToil.initAction = () =>
            {
                if (TryModifyWeapon())
                {
                    this.EndJobWith(JobCondition.Succeeded);
                    return;
                }
                this.EndJobWith(JobCondition.Incompletable);
            };
            yield return modifyToil;
        }

        /// <summary>
        /// Used to generate the hauling jobs for the ingredients
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Toil> CollectIngredientsToils()
        {
            Toil extract = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B);
            yield return extract;
            Toil getToHaulTarget = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return getToHaulTarget;
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, putRemainderInQueue: true, false, true);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell).FailOnDestroyedOrNull(TargetIndex.A);
            Toil findPlaceTarget = Toils_JobTransforms.SetTargetToIngredientPlaceCell(TargetIndex.A, TargetIndex.B, TargetIndex.C);
            yield return findPlaceTarget;
            yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.C, findPlaceTarget, storageMode: false);
            yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.B, extract);
        }

        /// <summary>
        /// Attempts to modify weapon
        /// </summary>
        /// <returns>Wether modification is finished ok</returns>
        private bool TryModifyWeapon()
        {
            Thing attachment = Weapon.attachments.FirstOrDefault(t => t.def == AttachmentDef);            
            if (attachment != null)
            {
                if (!TryRefundIngredient(attachment.def as AttachmentDef))
                    Log.Warning($"CE: Refunding attachment cost failed {attachment.def.label}");
                attachment.Destroy();
                Weapon.attachments.Remove(attachment);
                Weapon.UpdateConfiguration();
                return true;
            }
            else if (TryConsumeIngredient())
            {
                attachment = ThingMaker.MakeThing(AttachmentDef);
                Weapon.attachments.TryAdd(attachment);
                Weapon.UpdateConfiguration();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Attempts to refund cost 
        /// </summary>
        /// <param name="attachmentDef"></param>
        /// <returns></returns>
        private bool TryRefundIngredient(AttachmentDef attachmentDef)
        {
            foreach(ThingDefCountClass countClass in attachmentDef.costList)
            {
                int refund = (int)Mathf.Floor(countClass.count * 0.8f);
                if (refund == 1)
                    continue;
                Thing ingredient = ThingMaker.MakeThing(countClass.thingDef, countClass.thingDef.defaultStuff);                
                ingredient.stackCount = refund;
                // find the best cell for placing this ingredient
                if (!GenPlace.TryFindPlaceSpotNear(pawn.Position, pawn.Rotation, pawn.Map, ingredient, true, out IntVec3 spot))
                    return false;
                // check if we can stack this ingredient                
                // spawn the refunded ingredient
                GenSpawn.Spawn(ingredient, spot, pawn.Map);                
            }
            return true;
        }

        /// <summary>
        /// Attempts to consume the cost list inorder to craft the attachment
        /// </summary>
        /// <returns>Wether all ingredients were consumed</returns>
        private bool TryConsumeIngredient()
        {
            List<Thing> consumeList = new List<Thing>();
            List<Thing>[] cells = GenAdj.CellsAdjacent8WayAndInside(pawn).Select(c => c.GetThingList(pawn.Map).ToList()).ToArray();
            Dictionary<int, int> reaminingCost = new Dictionary<int, int>();

            // create counters to emulate cost that will be paid
            foreach (ThingDefCountClass countClass in AttachmentDef.costList)
                reaminingCost[countClass.thingDef.index] = countClass.count;

            // start reducing the counter
            for (int i = 0; i < cells.Length; i++)
            {
                foreach (Thing thing in cells[i])
                {
                    if (reaminingCost.TryGetValue(thing.def.index, out int counter) && counter > 0)
                    {
                        int n = Math.Min(thing.stackCount, counter);
                        consumeList.Add(thing.stackCount == n && thing.stackCount != 1 ? thing : thing.SplitOff(n));
                        reaminingCost[thing.def.index] = counter - n;
                    }
                }
            }

            // check if something wasn't satisfied
            foreach (ThingDefCountClass countClass in AttachmentDef.costList)
            {
                if (reaminingCost[countClass.thingDef.index] > 0)
                    return false;
            }

            // destroy consumed things
            foreach (Thing thing in consumeList)
                thing.Destroy(DestroyMode.Vanish);
            
            return true;
        }

        /// <summary>
        /// Attempt to start a new Modify weapon job if any other modifications are available
        /// </summary>
        private void TryContinueModifyingJob()
        {
            if (pawn.IsColonist && !pawn.Dead && !pawn.Downed && Weapon == EquipedWeapon && !Weapon.ConfigApplied)
            {
                Job job = pawn.thinker.GetMainTreeThinkNode<JobGiver_ModifyWeapon>().TryGiveJob(pawn);
                if (job != null)
                    pawn.jobs.jobQueue.EnqueueFirst(job);
            }
        }
    }
}
