﻿using Harmony;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ZombieLand
{
	public class BombVest : Apparel { }
	public class TankySuit : Apparel { }
	public class StickyGoo : Filth { }

	[StaticConstructorOnStartup]
	static class Patches
	{
		static Patches()
		{
			// HarmonyInstance.DEBUG = true;
			var harmony = HarmonyInstance.Create("net.pardeike.zombieland");
			harmony.PatchAll(Assembly.GetExecutingAssembly());

			// prepare Twinkie
			LongEventHandler.QueueLongEvent(() => { Tools.EnableTwinkie(false); }, "", true, null);

			// patches for Combat Extended (need to run late or else statics in those classes are not set yet)
			LongEventHandler.ExecuteWhenFinished(() =>
			{
				Patches.Projectile_Launch_Patch.PatchCombatExtended(harmony);
				Patches.ArmorUtility_GetPostArmorDamage_Patch.PatchCombatExtended(harmony);
			});

			// for debugging
			/*
			DebugRimworldMethodCalls((Type type) =>
			{
				if (type.Name.Contains("AttackTarget")) return true;
				if (type.Name.Contains("_AI")) return true;
				if (type.Name.Contains("Reachability")) return true;
				return false;
			}); */
		}

		// used to prevent zombies from being counted as hostiles
		// both in map exist and for danger music
		//
		static Dictionary<Map, HashSet<IAttackTarget>> playerHostilesWithoutZombies = new Dictionary<Map, HashSet<IAttackTarget>>();

		// patch for debugging: show pheromone grid as overlay
		// 
		[HarmonyPatch(typeof(SelectionDrawer))]
		[HarmonyPatch("DrawSelectionOverlays")]
		static class SelectionDrawer_DrawSelectionOverlays_Patch
		{
			static readonly float pawnAltitude = Altitudes.AltitudeFor(AltitudeLayer.Pawn - 1);

			static void Postfix()
			{
				if (Constants.DEBUGGRID == false && DebugViewSettings.drawDoorsDebug == false) return;

				// debug zombie counts
				Find.CurrentMap.GetGrid().IterateCells((x, z, cell) =>
				{
					var pos = new Vector3(x, pawnAltitude, z);
					if (cell.zombieCount > 1)
					{
						var a = Math.Min(0.9f, 0.2f * (cell.zombieCount - 1));
						Tools.DebugPosition(pos, new Color(0f, 0f, 1f, a));
					}
				});

				// debug timestamps
				var fadeOff = Tools.PheromoneFadeoff();
				var now = Tools.Ticks();
				Find.CurrentMap.GetGrid().IterateCells((x, z, cell) =>
				{
					var pos = new Vector3(x, pawnAltitude, z);
					var diff = now - cell.timestamp;
					if (diff >= -fadeOff && diff < 0)
					{
						var a = GenMath.LerpDouble(-fadeOff, 0, 0.8f, 0.5f, diff);
						Tools.DebugPosition(pos, new Color(1f, 1f, 0f, a));
					}
					else if (diff < fadeOff)
					{
						var a = GenMath.LerpDouble(0, fadeOff, 0.5f, 0.0f, diff);
						Tools.DebugPosition(pos, new Color(1f, 0f, 0f, a));
					}
				});
			}
		}

		// patch for debugging: show zombie avoidance grid
		/*
		[HarmonyPatch(typeof(MapInterface))]
		[HarmonyPatch("MapInterfaceUpdate")]
		class MapInterface_MapInterfaceUpdate_Patch
		{
			static void Postfix()
			{
				if (DebugViewSettings.writePathCosts == false) return;
				if (ZombieSettings.Values.betterZombieAvoidance == false) return;

				var map = Find.CurrentMap;
				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;
				var avoidGrid = tickManager.avoidGrid;

				var currentViewRect = Find.CameraDriver.CurrentViewRect;
				currentViewRect.ClipInsideMap(map);
				foreach (var c in currentViewRect)
				{
					var cost = avoidGrid.GetCosts()[c.x + c.z * map.Size.x];
					if (cost > 0)
						Tools.DebugPosition(c.ToVector3(), new Color(1f, 0f, 0f, GenMath.LerpDouble(0, 10000, 0.4f, 1f, cost)));
				}
			}
		}*/

		// patch for debugging: show zombie pathing grid around the mouse
		//
		[HarmonyPatch(typeof(MapInterface))]
		[HarmonyPatch("MapInterfaceOnGUI_AfterMainTabs")]
		class MapInterface_MapInterfaceOnGUI_AfterMainTabs_Patch
		{
			static void Postfix()
			{
				if (DebugViewSettings.writePathCosts == false) return;
				if (Event.current.type != EventType.Repaint) return;

				var map = Find.CurrentMap;
				if (map == null) return;
				var grid = map.GetGrid();
				var basePos = UI.MouseCell();
				var info = Tools.wanderer.GetMapInfo(map);
				var ignoreBuildings = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
				Tools.GetCircle(4).Select(vec => vec + basePos).Do(cell =>
				{
					var labelVec = GenMapUI.LabelDrawPosFor(cell);
					var newPos = info.GetParent(cell, ignoreBuildings);
					if (newPos.IsValid == false)
					{
						GenMapUI.DrawThingLabel(labelVec, "⁜", Color.red);
						return;
					}

					var d = newPos - cell;
					var n = (d.x + 1) + (d.z + 1) * 3;
					var arrow = "↙↓↘←◌→↖↑↗".Substring(n, 1);
					GenMapUI.DrawThingLabel(labelVec, arrow, ignoreBuildings ? Color.yellow : Color.white);
				});
			}
		}

		// patch to show zombieland version and total number of zombies
		//
		[HarmonyPatch(typeof(GlobalControlsUtility))]
		[HarmonyPatch("DoDate")]
		class GlobalControlsUtility_DoDate_Patch
		{
			static void Postfix(float leftX, float width, ref float curBaseY)
			{
				var map = Find.CurrentMap;
				if (map == null) return;

				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;
				var count = tickManager.ZombieCount();
				if (count == 0) return;
				var zombieCountString = count + " Zombies";
				var rightMargin = 7f;

				var zlRect = new Rect(leftX, curBaseY - 24f, width, 24f);
				Text.Font = GameFont.Small;
				var len = Text.CalcSize(zombieCountString);
				zlRect.xMin = zlRect.xMax - Math.Min(leftX, len.x + rightMargin);

				if (Mouse.IsOver(zlRect))
				{
					Widgets.DrawHighlight(zlRect);
				}

				GUI.BeginGroup(zlRect);
				Text.Anchor = TextAnchor.UpperRight;
				var rect = zlRect.AtZero();
				rect.xMax -= rightMargin;
				Widgets.Label(rect, zombieCountString);
				Text.Anchor = TextAnchor.UpperLeft;
				GUI.EndGroup();

				TooltipHandler.TipRegion(zlRect, new TipSignal(delegate
				{
					var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
					var versionString = currentVersion.Major + "." + currentVersion.Minor + "." + currentVersion.Build;
					return "Zombieland v" + versionString;
				}, 99899));

				curBaseY -= zlRect.height;
			}
		}

		// smart scaled zombie ticking (must be executed as late as possible 
		// in game loop since we only process so many zombies that we can without
		// exceeding the realtime tick -> no lag because of zombies
		//
		[HarmonyPatch(typeof(Verse.TickManager))]
		[HarmonyPatch("TickManagerUpdate")]
		static class Verse_TickManager_TickManagerUpdate_Patch
		{
			static void ZombieTick()
			{
				var tickManager = Find.CurrentMap?.GetComponent<TickManager>();
				tickManager?.ZombieTicking();
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var jump = generator.DefineLabel();
				var m_ZombieTick = SymbolExtensions.GetMethodInfo(() => ZombieTick());

				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime && instruction.opcode == OpCodes.Ldloc_0)
					{
						firstTime = false;
						yield return new CodeInstruction(OpCodes.Ldloc_1);
						yield return new CodeInstruction(OpCodes.Ldc_I4_2);
						yield return new CodeInstruction(OpCodes.Bge, jump);
						yield return new CodeInstruction(OpCodes.Call, m_ZombieTick);
						instruction.labels.Add(jump);
					}
					yield return instruction;
				}

				if (firstTime) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to control if raiders and animals see zombies as hostile
		//
		[HarmonyPatch(typeof(GenHostility))]
		[HarmonyPatch("HostileTo")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Thing) })]
		static class GenHostility_HostileTo_Thing_Thing_Patch
		{
			static void Postfix(Thing a, Thing b, ref bool __result)
			{
				var pawn = a as Pawn;
				var zombie = b as Zombie;
				if (pawn == null || pawn.IsColonist || (pawn is Zombie) || zombie == null)
					return;
				__result = Tools.IsHostileToZombies(pawn);
			}
		}
		[HarmonyPatch(typeof(GenHostility))]
		[HarmonyPatch("HostileTo")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Faction) })]
		static class GenHostility_HostileTo_Thing_Faction_Patch
		{
			static void Postfix(Thing t, Faction fac, ref bool __result)
			{
				var pawn = t as Pawn;
				if (pawn == null || pawn.IsColonist || (pawn is Zombie) || fac == null || fac.def != ZombieDefOf.Zombies)
					return;
				__result = Tools.IsHostileToZombies(pawn);
			}
		}

		// patch to make raiders choose zombies less likely as a target
		//
		[HarmonyPatch(typeof(AttackTargetFinder))]
		[HarmonyPatch("BestAttackTarget")]
		static class AttackTargetFinder_BestAttackTarget_Patch
		{
			static Predicate<IAttackTarget> WrappedValidator(Predicate<IAttackTarget> validator, IAttackTargetSearcher searcher)
			{
				var attacker = searcher as Pawn;

				// attacker is colonist or zombie? use default
				if (validator == null || attacker == null || attacker.IsColonist || attacker is Zombie)
					return validator;

				// attacker is animal
				if (attacker.RaceProps.Animal)
					return (IAttackTarget t) =>
					{
						if (t.Thing is Zombie)
							return ZombieSettings.Values.animalsAttackZombies;
						return validator(t);
					};

				// attacker is friendly
				// (disabled because the postfix deals with that)
				//
				// if (attacker.Faction.HostileTo(Faction.OfPlayer) == false)
				//	return (IAttackTarget t) => (t.Thing is Zombie) ? false : validator(t);

				// attacker is enemy
				return (IAttackTarget t) =>
				{
					var zombie = t.Thing as Zombie;
					if (zombie != null)
					{
						if (ZombieSettings.Values.enemiesAttackZombies == false)
							return false;

						if (zombie.state != ZombieState.Tracking)
							return false;

						var distanceToTarget = (float)(attacker.Position - zombie.Position).LengthHorizontalSquared;
						var verb = searcher.CurrentEffectiveVerb;
						var attackDistance = verb == null ? 1f : verb.verbProps.range * verb.verbProps.range;
						var zombieAvoidRadius = Tools.ZombieAvoidRadius(zombie, true);

						if (attackDistance < zombieAvoidRadius && distanceToTarget >= zombieAvoidRadius)
							return false;

						if (distanceToTarget > attackDistance)
							return false;
					}
					return validator(t);
				};
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var innerValidatorType = (instructions.ToArray()[0].operand as MethodBase).DeclaringType;
				if (innerValidatorType == null) Log.Error("Cannot find inner validator type");
				var f_innerValidator = innerValidatorType.Field("innerValidator");

				var found = false;
				foreach (var instruction in instructions)
				{
					if (found == false && instruction.opcode == OpCodes.Stfld && instruction.operand == f_innerValidator)
					{
						yield return new CodeInstruction(OpCodes.Ldarg_0);
						yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => WrappedValidator(null, null)));
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}

			static void Postfix(ref IAttackTarget __result, Predicate<Thing> validator, IAttackTargetSearcher searcher)
			{
				var thing = __result as Thing;

				if (thing == null)
				{
					// fix only friendlies

					Thing attacker = searcher as Pawn;
					if (attacker == null)
						attacker = searcher.Thing;

					if (attacker != null && attacker.Faction.HostileTo(Faction.OfPlayer) == false)
					{
						var verb = searcher.CurrentEffectiveVerb;
						if (verb != null)
						{
							var props = verb.verbProps;
							if (props.IsMeleeAttack == false && props.range > 0)
							{
								// the following can be improved by choosing targets that
								// are not too close. unsolved problem: we do not know how
								// to relocate shooters yet
								//
								var maxRangeSquared = (int)(props.range * props.range);
								var tickManager = attacker.Map.GetComponent<TickManager>();
								var pos = attacker.Position;
								int zombiePrioritySorter(Zombie zombie)
								{
									var score = maxRangeSquared - pos.DistanceToSquared(zombie.Position);
									if (zombie.IsSuicideBomber)
										score += 30;
									if (zombie.IsTanky)
										score += 20;
									if (zombie.isToxicSplasher)
										score += 10;
									return -score;
								}
								__result = tickManager.allZombiesCached
									.Where(zombie => zombie.Downed == false && zombie.state != ZombieState.Emerging && pos.DistanceToSquared(zombie.Position) <= maxRangeSquared)
									.Where(zombie => verb.CanHitTargetFrom(pos, zombie))
									.OrderBy(zombiePrioritySorter).FirstOrDefault();
								return;
							}
						}
					}
				}

				if (validator != null && thing != null && validator(thing) == false)
					__result = null;
			}
		}

		// patch so other zombies do not affect goodwill of other factions
		//
		[HarmonyPatch(typeof(Faction))]
		[HarmonyPatch("TryAffectGoodwillWith")]
		static class Faction_TryAffectGoodwillWith_Patch
		{
			static bool Prefix(ref bool __result, Faction __instance, Faction other)
			{
				if (__instance.def == ZombieDefOf.Zombies || other.def == ZombieDefOf.Zombies)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch to prefer non-downed zombies from downed one as targets
		//
		[HarmonyPatch(typeof(AttackTargetFinder))]
		[HarmonyPatch("GetAvailableShootingTargetsByScore")]
		static class AttackTargetFinder_GetAvailableShootingTargetsByScore_Patch
		{
			static void Postfix(List<Pair<IAttackTarget, float>> __result)
			{
				for (var i = __result.Count - 1; i >= 0; i--)
				{
					var zombie = __result[i].First as Zombie;
					if (zombie != null && zombie.Downed)
						__result.RemoveAt(i);
				}
			}
		}

		// patch to make downed zombies as easy to kill as standing
		//
		[HarmonyPatch(typeof(Projectile))]
		[HarmonyPatch("ImpactSomething")]
		static class Projectile_ImpactSomething_Patch
		{
			static PawnPosture GetPostureFix(Pawn p)
			{
				if (p is Zombie) return PawnPosture.Standing; // fake standing
				return p.GetPosture();
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_GetPosture = SymbolExtensions.GetMethodInfo(() => PawnUtility.GetPosture(null));

				foreach (var instruction in instructions)
				{
					if (instruction.operand == m_GetPosture)
					{
						instruction.opcode = OpCodes.Call;
						instruction.operand = SymbolExtensions.GetMethodInfo(() => GetPostureFix(null));
					}
					yield return instruction;
				}
			}
		}

		// patch to remove the constant danger music because of the constant thread of zombies
		//
		[HarmonyPatch(typeof(AttackTargetsCache))]
		[HarmonyPatch("RegisterTarget")]
		static class AttackTargetsCache_RegisterTarget_Patch
		{
			static void Add(IAttackTarget target)
			{
				var thing = target.Thing;
				if (thing == null || thing is Zombie) return;
				if (thing.HostileTo(Faction.OfPlayer) == false) return;
				var map = thing.Map;
				if (map == null) return;
				if (playerHostilesWithoutZombies.ContainsKey(map) == false)
					playerHostilesWithoutZombies.Add(map, new HashSet<IAttackTarget>());
				playerHostilesWithoutZombies[map].Add(target);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var all = instructions.ToList();
				for (var i = 0; i < all.Count - 1; i++)
					yield return all[i];
				var ret = all.Last();

				yield return new CodeInstruction(OpCodes.Ldarg_1) { labels = ret.labels };
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => Add(null)));
				yield return new CodeInstruction(OpCodes.Ret);
			}
		}
		//
		[HarmonyPatch(typeof(AttackTargetsCache))]
		[HarmonyPatch("DeregisterTarget")]
		static class AttackTargetsCache_DeregisterTarget_Patch
		{
			static void Remove(IAttackTarget target)
			{
				var thing = target.Thing;
				if (thing == null || thing is Zombie) return;
				var map = thing.Map;
				if (map == null) return;
				if (playerHostilesWithoutZombies.ContainsKey(map))
					playerHostilesWithoutZombies[map].Remove(target);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var all = instructions.ToList();
				for (var i = 0; i < all.Count - 1; i++)
					yield return all[i];
				var ret = all.Last();

				yield return new CodeInstruction(OpCodes.Ldarg_1) { labels = ret.labels };
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => Remove(null)));
				yield return new CodeInstruction(OpCodes.Ret);
			}
		}
		//
		[HarmonyPatch(typeof(MusicManagerPlay))]
		[HarmonyPatch("DangerMusicMode", MethodType.Getter)]
		static class MusicManagerPlay_DangerMusicMode_Patch
		{
			delegate int LastColonistHarmedTickDelegate(DangerWatcher dw);

			static int lastUpdateTick;
			static StoryDanger dangerRatingInt = StoryDanger.None;

			// TODO: the following is ugly since we copy original code (ugh!)
			// The solution is to add method IL copying to Harmony ...
			// Until then: this stays
			//
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ref bool __result)
			{
				var maps = Find.Maps;
				for (var i = 0; i < maps.Count; i++)
				{
					var map = maps[i];
					if (map.IsPlayerHome)
					{
						if (Find.TickManager.TicksGame > lastUpdateTick + 101)
						{
							var hostiles = playerHostilesWithoutZombies.ContainsKey(map)
								? playerHostilesWithoutZombies[map]
								: new HashSet<IAttackTarget>();

							var num = hostiles.Count(GenHostility.IsActiveThreatToPlayer);
							if (num == 0)
								dangerRatingInt = StoryDanger.None;
							else if (num <= Mathf.CeilToInt(map.mapPawns.FreeColonistsSpawnedCount * 0.5f))
								dangerRatingInt = StoryDanger.Low;
							else
							{
								dangerRatingInt = StoryDanger.Low;
								var lastColonistHarmedTick = GetterSetters.clastColonistHarmedTickByRef(map.dangerWatcher);
								if (lastColonistHarmedTick > Find.TickManager.TicksGame - 900)
									dangerRatingInt = StoryDanger.High;
								else
								{
									foreach (var current in map.lordManager.lords)
									{
										if (current.CurLordToil is LordToil_AssaultColony)
										{
											dangerRatingInt = StoryDanger.High;
											break;
										}
									}
								}
							}

							lastUpdateTick = Find.TickManager.TicksGame;
						}
					}
				}
				__result = dangerRatingInt == StoryDanger.High;
				return false;
			}
		}

		// patch to increase hit chance for shooting at zombies
		//
		[HarmonyPatch(typeof(Verb_LaunchProjectile))]
		[HarmonyPatch("TryCastShot")]
		static class Verb_LaunchProjectile_TryCastShot_Patch
		{
			static bool SkipMissingShotsAtZombies(Verb verb, LocalTargetInfo currentTarget)
			{
				// difficulty Intense or worse will trigger default behavior
				if (Find.Storyteller.difficulty.difficulty >= DifficultyDefOf.Rough.difficulty) return false;

				// only for colonists
				var colonist = verb.caster as Pawn;
				if (colonist == null || colonist.Faction != Faction.OfPlayer) return false;

				// shooting zombies
				var zombie = currentTarget.HasThing ? currentTarget.Thing as Zombie : null;
				if (zombie == null) return false;

				// max 15 cells awaw
				if ((zombie.Position - colonist.Position).LengthHorizontalSquared > 225) return false;

				// with line of sight
				var shot = verb as Verb_LaunchProjectile;
				if (shot == null || shot.verbProps.requireLineOfSight == false) return false;

				// skip miss calculations
				return Rand.Chance(Constants.COLONISTS_HIT_ZOMBIES_CHANCE);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var m_SkipMissingShotsAtZombies = SymbolExtensions.GetMethodInfo(() => SkipMissingShotsAtZombies(null, null));
				var f_forcedMissRadius = typeof(VerbProperties).Field(nameof(VerbProperties.forcedMissRadius));
				var m_HitReportFor = SymbolExtensions.GetMethodInfo(() => ShotReport.HitReportFor(null, null, null));
				var f_currentTarget = typeof(Verb).Field("currentTarget");

				var skipLabel = generator.DefineLabel();
				var inList = instructions.ToList();

				var idx1 = inList.FirstIndexOf(instr => instr.opcode == OpCodes.Ldfld && instr.operand == f_forcedMissRadius);
				if (idx1 > 0)
				{
					var idx2 = inList.FindLastIndex(instr => instr.opcode == OpCodes.Call
						&& (instr.operand as MethodInfo)?.DeclaringType == typeof(ShotReport)
						&& (instr.operand as MethodInfo)?.ReturnType == typeof(float)
					);
					var jump = inList[idx2 + 2]; // skip CALL bool Verse.Rand::Chance(float32) 
					if (jump.opcode == OpCodes.Brtrue)
					{
						idx1 -= 2;
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Ldarg_0));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Ldarg_0));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Ldfld, f_currentTarget));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Call, m_SkipMissingShotsAtZombies));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Brtrue, jump.operand));
					}
					else
						Log.Error("No call on ShotReport method returning float in Verb_LaunchProjectile.TryCastShot");
				}
				else
					Log.Error("No ldfld forcedMissRadius in Verb_LaunchProjectile.TryCastShot");

				foreach (var instruction in inList)
					yield return instruction;
			}
		}

		// patch to remove zombies from hostile count so it does not
		// alter game logic (for example when a caravan leaves an enemy base)
		//
		[HarmonyPatch(typeof(GenHostility))]
		[HarmonyPatch("IsActiveThreatTo")]
		[HarmonyPatch(new Type[] { typeof(IAttackTarget), typeof(Faction) })]
		static class GenHostility_IsActiveThreat_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ref bool __result, IAttackTarget target)
			{
				if (target is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch to reduce revenge by animals
		//
		[HarmonyPatch(typeof(Pawn_MindState))]
		[HarmonyPatch("Notify_DamageTaken")]
		static class Pawn_MindState_Notify_DamageTaken_Patch
		{
			static bool ReducedChance(float chance, Pawn instigator)
			{
				if (instigator is Zombie)
					chance = chance / 20;
				return Rand.Chance(chance);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_Chance = SymbolExtensions.GetMethodInfo(() => Rand.Chance(0f));
				var m_ReducedChance = SymbolExtensions.GetMethodInfo(() => ReducedChance(0f, null));

				foreach (var instruction in instructions)
				{
					if (instruction.operand == m_Chance)
					{
						yield return new CodeInstruction(OpCodes.Ldloc_0);
						yield return new CodeInstruction(OpCodes.Call, m_ReducedChance);
					}
					else
						yield return instruction;
				}
			}
		}

		// patch to let predators prefer humans for zombies
		//
		[HarmonyPatch(typeof(FoodUtility))]
		[HarmonyPatch("GetPreyScoreFor")]
		static class FoodUtility_GetPreyScoreFor_Patch
		{
			static void Postfix(Pawn predator, Pawn prey, ref float __result)
			{
				if (prey is Zombie)
					__result -= 35f;
			}
		}

		// patch for pather to avoid zombies
		/* 
			# if (allowedArea != null && !allowedArea[num14])
			# {
			# 	num16 += 600;
			# }
			# // start added code
			# num16 += GetZombieCosts(this.map, num14);
			# // end added code

			IL_098a: ldloc.s 14
			IL_098c: brfalse IL_09a9

			IL_0991: ldloc.s 14
		-4	IL_0993: ldloc.s 36														<-- local var that holds grid index
			IL_0995: callvirt instance bool Verse.Area::get_Item(int32)
			IL_099a: brtrue IL_09a9

		-1	IL_099f: ldloc.s 40														<-- local var that holds sum
		0	IL_09a1: ldc.i4 600														<-- search for "600" to get this as main reference point
			IL_09a6: add
			IL_09a7: stloc.s 40 

		3	IL_09a9:	ldloc.s 40														<-- added code (keep labels from before)
						ldarg.0
						ldfld PathFinder::map
						ldloc.s 36
						call PathFinder_FindPath_Patch::GetZombieCosts(Map map, int idx)
						add
						stloc.s 40
			*/
		//
		[HarmonyPatch(typeof(PathFinder))]
		[HarmonyPatch("FindPath")]
		[HarmonyPatch(new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) })]
		public static class PathFinder_FindPath_Patch
		{
			public static Dictionary<Map, TickManager> tickManagerCache = new Dictionary<Map, TickManager>();

			// infected colonists will still path so exclude them from this check
			// by returning 0 - currently disabled because it does cost too much
			static int GetZombieCosts(Map map, int idx)
			{
				if (ZombieSettings.Values.betterZombieAvoidance == false) return 0;

				if (map == null) return 0;
				TickManager tickManager;
				if (tickManagerCache.TryGetValue(map, out tickManager) == false)
				{
					tickManager = map.GetComponent<TickManager>();
					if (tickManager == null) return 0;
					tickManagerCache[map] = tickManager;
				}
				if (tickManager.avoidGrid == null) return 0;
				return tickManager.avoidGrid.GetCosts()[idx];
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var refIdx = list.FirstIndexOf(ins => ins.operand is int && (int)ins.operand == 600);
				if (refIdx > 0)
				{
					var gridIdx = list[refIdx - 4].operand;
					var sumIdx = list[refIdx - 1].operand;
					var insertIdx = refIdx + 3;
					var movedLabels = list[insertIdx].labels;
					list[insertIdx].labels = new List<Label>();

					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldloc_S, sumIdx) { labels = movedLabels });
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldarg_0));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldfld, typeof(PathFinder).Field("map")));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Ldloc_S, gridIdx));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => GetZombieCosts(null, 0))));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Add));
					list.Insert(insertIdx++, new CodeInstruction(OpCodes.Stloc_S, sumIdx));
				}
				else
					Log.Error("Cannot find path cost 600 in PathFinder.FindPath");

				foreach (var instr in list)
					yield return instr;
			}
		}
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("NeedNewPath")]
		static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static readonly MethodInfo m_ShouldCollideWithPawns = SymbolExtensions.GetMethodInfo(() => PawnUtility.ShouldCollideWithPawns(null));

			static bool ZombieInPath(Pawn_PathFollower __instance, Pawn pawn)
			{
				if (ZombieSettings.Values.betterZombieAvoidance == false) return false;
				if (pawn.RaceProps.Humanlike == false) return false;

				var path = __instance.curPath;
				if (path.NodesLeftCount < 5) return false;
				var lookAhead = path.Peek(4);
				var destination = path.LastNode;
				if ((lookAhead - destination).LengthHorizontalSquared < 25) return false;

				var map = pawn.Map;
				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return false;
				var costs = tickManager.avoidGrid.GetCosts();
				var zombieDanger = costs[lookAhead.x + lookAhead.z * map.Size.x];
				return (zombieDanger > 0);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FirstIndexOf(code => code.opcode == OpCodes.Call && code.operand == m_ShouldCollideWithPawns) - 1;
				if (idx > 0)
				{
					if (list[idx].opcode == OpCodes.Ldfld)
					{
						var jump = generator.DefineLabel();

						// here we should have a Ldarg_0 but original code has one with a label on it so we reuse it
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_PathFollower).Field("pawn")));
						list.Insert(idx++, new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ZombieInPath(null, null))));
						list.Insert(idx++, new CodeInstruction(OpCodes.Brfalse, jump));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldc_I4_1));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ret));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>() { jump } }); // add the missing Ldarg_0 from original code here
					}
					else
						Log.Error("Cannot find Ldfld one instruction before " + m_ShouldCollideWithPawns + " in Pawn_PathFollower.NeedNewPath");
				}
				else
					Log.Error("Cannot find " + m_ShouldCollideWithPawns + " in Pawn_PathFollower.NeedNewPath");

				foreach (var instr in list)
					yield return instr;
			}
		}

		// patch to remove log error "xxx pathing to destroyed thing (zombie)"
		//
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("StartPath")]
		static class Pawn_PathFollower_StartPath_Patch
		{
			static bool ThingDestroyedAndNotZombie(LocalTargetInfo info)
			{
				return info.ThingDestroyed && (info.Thing is Zombie) == false;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = typeof(LocalTargetInfo).PropertyGetter(nameof(LocalTargetInfo.ThingDestroyed));
				var to = SymbolExtensions.GetMethodInfo(() => ThingDestroyedAndNotZombie(null));

				var insArray = instructions.ToArray();
				var i = insArray.FirstIndexOf((ins) => ins.operand == from);
				insArray[i - 1].opcode = OpCodes.Ldarg_1;
				insArray[i].operand = to;

				foreach (var ins in insArray)
					yield return ins;
			}
		}

		// patch to add a pheromone info section to the rimworld cell inspector
		//
		[HarmonyPatch(typeof(EditWindow_DebugInspector))]
		[HarmonyPatch("CurrentDebugString")]
		static class EditWindow_DebugInspector_CurrentDebugString_Patch
		{
			static void DebugGrid(StringBuilder builder)
			{
				if (Current.Game == null) return;
				var map = Current.Game.CurrentMap;
				if (map == null) return;
				var pos = UI.MouseCell();

				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;

				builder.AppendLine("---");
				builder.AppendLine("Center of Interest: " + tickManager.centerOfInterest.x + "/" + tickManager.centerOfInterest.z);
				builder.AppendLine("Total zombie count: " + tickManager.ZombieCount() + " out of " + tickManager.GetMaxZombieCount());

				builder.AppendLine("");
				AccessTools.GetFieldNames(typeof(IncidentParameters)).Do(name =>
				{
					var value = Traverse.Create(tickManager.incidentInfo.parameters).Field(name).GetValue();
					builder.AppendLine(name + ": " + value);
				});
				builder.AppendLine("");

				if (pos.InBounds(map) == false) return;

				if (ZombieSettings.Values.betterZombieAvoidance)
				{
					var avoidGrid = map.GetComponent<TickManager>().avoidGrid;
					builder.AppendLine("Avoid cost: " + avoidGrid.GetCosts()[pos.x + pos.z * map.Size.x]);
				}

				var info = Tools.wanderer.GetMapInfo(map);
				builder.AppendLine("Parent normal: " + info.GetParent(pos, false));
				builder.AppendLine("Parent via doors: " + info.GetParent(pos, true));
				builder.AppendLine("Parent raw: " + info.GetDirectDebug(pos));

				var cell = map.GetGrid().GetPheromone(pos, false);
				if (cell != null)
				{
					var realZombieCount = pos.GetThingList(map).OfType<Zombie>().Count();
					var sb = new StringBuilder();
					sb.Append("Zombie grid: " + cell.zombieCount + " zombies");
					if (cell.zombieCount != realZombieCount) sb.Append(" (real " + realZombieCount + ")");
					builder.AppendLine(sb.ToString());

					var now = Tools.Ticks();
					var tdiff = (cell.timestamp - now).ToString();
					if (tdiff.StartsWith("-"))
						tdiff = tdiff.ReplaceFirst("-", "- ");
					else
						tdiff = "+ " + tdiff;
					builder.AppendLine("Pheromone timestamp " + cell.timestamp + " = " + now + " " + tdiff);
				}
				else
					builder.AppendLine(pos.x + " " + pos.z + ": empty");

				var gridSum = GenAdj.AdjacentCellsAndInside.Select(vec => pos + vec)
					.Where(c => c.InBounds(map))
					.Select(c => map.GetGrid().GetZombieCount(c))
					.Sum();
				var realSum = GenAdj.AdjacentCellsAndInside.Select(vec => pos + vec)
					.Where(c => c.InBounds(map))
					.Select(c => map.thingGrid.ThingsListAtFast(c).OfType<Zombie>().Count())
					.Sum();
				builder.AppendLine("Rage factor: grid=" + gridSum + ", real=" + realSum);

				map.thingGrid.ThingsListAtFast(pos).OfType<Zombie>().Do(zombie =>
				{
					var currPos = zombie.Position;
					var gotoPos = zombie.pather.Moving ? zombie.pather.Destination.Cell : IntVec3.Invalid;
					var wanderTo = zombie.wanderDestination;
					var sb = new StringBuilder();
					sb.Append("Zombie " + zombie.Name.ToStringShort + " at " + currPos.x + "," + currPos.z);
					sb.Append(", " + zombie.state.ToString().ToLower());
					if (zombie.raging > 0) sb.Append(", raging ");
					sb.Append(", going to " + gotoPos.x + "," + gotoPos.z);
					sb.Append(" (wander dest " + wanderTo.x + "," + wanderTo.z + ")");
					builder.AppendLine(sb.ToString());
				});
			}

			static bool Prefix(string __result)
			{
				if (Current.Game == null)
				{
					__result = "";
					return false;
				}
				return true;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var m_ToString = AccessTools.Method(typeof(object), "ToString");
				var idx = list.FindLastIndex(instr => instr.operand == m_ToString);
				if (idx > 0)
				{
					list.Insert(idx++, new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => DebugGrid(null))));
					list.Insert(idx++, list[idx - 3].Clone());
				}
				else
					Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);

				return list.AsEnumerable();
			}
		}

		// patch to add our actions to the debug action menu
		//
		[HarmonyPatch(typeof(DebugWindowsOpener))]
		[HarmonyPatch("ToggleDebugActionsMenu")]
		static class DebugWindowsOpener_ToggleDebugActionsMenu_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = AccessTools.Constructor(typeof(Dialog_DebugActionsMenu));
				var to = AccessTools.Constructor(typeof(Dialog_ZombieDebugActionMenu));
				return instructions.MethodReplacer(from, to);
			}
		}

		// patch for adding zombie faction to existing games
		//
		[HarmonyPatch(typeof(FactionManager))]
		[HarmonyPatch("ExposeData")]
		static class FactionManager_ExposeData_Patch
		{
			static void Postfix(FactionManager __instance, List<Faction> ___allFactions)
			{
				if (Scribe.mode == LoadSaveMode.Saving) return;
				if (___allFactions == null) return;

				var factionDefs = ___allFactions.Select(f => f.def).ToList();
				if (factionDefs.Contains(ZombieDefOf.Zombies) == false)
				{
					var zombies = FactionGenerator.NewGeneratedFaction(ZombieDefOf.Zombies);
					foreach (var faction in ___allFactions)
					{
						var rel1 = new FactionRelation()
						{
							other = faction,
							goodwill = 0,
							kind = FactionRelationKind.Hostile
						};
						GetterSetters.relationsByRef(zombies).Add(rel1);

						var rel2 = new FactionRelation()
						{
							other = zombies,
							goodwill = 0,
							kind = FactionRelationKind.Hostile
						};
						GetterSetters.relationsByRef(faction).Add(rel2);

					}
					___allFactions.Add(zombies);
				}
			}
		}

		// patch for detecting if a pawn enters a new cell
		//
		[HarmonyPatch(typeof(Thing))]
		[HarmonyPatch("Position", MethodType.Setter)]
		static class Thing_Position_Patch
		{
			static readonly MentalStateDef def1 = MentalStateDefOf.Manhunter;
			static readonly MentalStateDef def2 = MentalStateDefOf.ManhunterPermanent;
			static readonly ThingDef stickyGooDef = ThingDef.Named("StickyGoo");

			static void Prefix(Thing __instance, IntVec3 value)
			{
				var pawn = __instance as Pawn;
				if (pawn == null) return;
				var map = pawn.Map;
				if (map == null) return;
				if (pawn.Position == value) return;

				var zombie = pawn as Zombie;
				if (zombie != null)
				{
					var grid = map.GetGrid();

					// tanky zombies leave pherome trace too so other zombies follow
					//
					if (zombie.IsTanky)
					{
						var fadeOff = Tools.PheromoneFadeoff();
						var now = Tools.Ticks();
						var radius = Constants.TANKY_PHEROMONE_RADIUS;
						var dx = pawn.Position.x - value.x;
						var dz = pawn.Position.z - value.z;
						var r2 = radius * radius;
						Tools.GetCircle(radius).Do(vec =>
						{
							var vx = Math.Sign(vec.x);
							var vz = Math.Sign(vec.z);
							var vlen = vec.LengthHorizontalSquared;
							if ((vx == 0 || vx == dx) && (vz == 0 || vz == dz) && vlen > 1f)
							{
								var offset = GenMath.LerpDouble(0f, r2, fadeOff / 8f, fadeOff / 4f, vlen);
								grid.BumpTimestamp(value + vec, now - (long)offset);
							}
						});
					}
					else
					{
						var newCell = grid.GetPheromone(value, false);
						if (newCell != null && newCell.zombieCount > 0)
						{
							newCell.timestamp -= newCell.zombieCount * Constants.ZOMBIE_CLOGGING_FACTOR;
							var notOlderThan = Tools.Ticks() - Tools.PheromoneFadeoff();
							newCell.timestamp = Math.Max(newCell.timestamp, notOlderThan);
						}
					}

					return;
				}

				// manhunting will always trigger senses
				//
				if (pawn.MentalState == null || (pawn.MentalState.def != def1 && pawn.MentalState.def != def2))
				{
					if (ZombieSettings.Values.attackMode == AttackMode.OnlyHumans)
						if (pawn.RaceProps.Humanlike == false) return;

					if (ZombieSettings.Values.attackMode == AttackMode.OnlyColonists)
						if (pawn.IsColonist == false) return;
				}

				// apply toxic splatter damage
				var toxity = 0.05f * pawn.GetStatValue(StatDefOf.ToxicSensitivity, true);
				if (toxity > 0f)
				{
					pawn.Position.GetThingList(pawn.Map).Where(thing => thing.def == stickyGooDef).Do(thing =>
					{
						HealthUtility.AdjustSeverity(pawn, HediffDefOf.ToxicBuildup, toxity);
					});
				}

				// leave pheromone trail
				if (Tools.HasInfectionState(pawn, InfectionState.Infecting) == false)
				{
					var now = Tools.Ticks();
					var radius = Tools.RadiusForPawn(pawn);
					var grid = pawn.Map.GetGrid();
					Tools.GetCircle(radius).Do(vec => grid.BumpTimestamp(value + vec, now - (long)(2f * vec.LengthHorizontal)));
				}
			}
		}

		// patch to make infected colonists have no needs
		//
		[HarmonyPatch(typeof(Need))]
		[HarmonyPatch("CurLevel", MethodType.Setter)]
		public static class Need_CurLevel_Patch
		{
			// this is set periodically from Alerts.Alert_ZombieInfection
			public static HashSet<Pawn> infectedColonists = new HashSet<Pawn>();

			static bool ShouldBeAverageNeed(Pawn pawn)
			{
				return infectedColonists.Contains(pawn);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator il, IEnumerable<CodeInstruction> instructions)
			{
				var average = il.DeclareLocal(typeof(float));
				var originalStart = il.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Stloc, average);
				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(Need).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ShouldBeAverageNeed(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, originalStart);
				yield return new CodeInstruction(OpCodes.Ldc_R4, 0.5f);
				yield return new CodeInstruction(OpCodes.Stloc, average);

				var found = false;
				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime)
						instruction.labels.Add(originalStart);
					if (instruction.opcode == OpCodes.Ldarg_1)
					{
						instruction.opcode = OpCodes.Ldloc;
						instruction.operand = average;
						found = true;
					}
					yield return instruction;
					firstTime = false;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to make infected colonists have no mental breaks
		//
		[HarmonyPatch(typeof(MentalStateHandler))]
		[HarmonyPatch("TryStartMentalState")]
		static class MentalStateHandler_TryStartMentalState_Patch
		{
			static bool NoMentalState(Pawn pawn)
			{
				return Need_CurLevel_Patch.infectedColonists.Contains(pawn);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator il, IEnumerable<CodeInstruction> instructions)
			{
				var originalStart = il.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(MentalStateHandler).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => NoMentalState(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, originalStart);
				yield return new CodeInstruction(OpCodes.Ldc_I4_0);
				yield return new CodeInstruction(OpCodes.Ret);

				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime)
						instruction.labels.Add(originalStart);
					yield return instruction;
					firstTime = false;
				}
			}
		}

		// patch to make infected colonists feel no pain
		//
		[HarmonyPatch(typeof(HediffSet))]
		[HarmonyPatch("PainTotal", MethodType.Getter)]
		static class HediffSet_CalculatePain_Patch
		{
			static bool Prefix(HediffSet __instance, ref float __result)
			{
				if (Need_CurLevel_Patch.infectedColonists.Contains(__instance.pawn))
				{
					__result = 0f;
					return false;
				}
				return true;
			}
		}

		// patch to make infected colonists have full capacity
		//
		[HarmonyPatch(typeof(PawnCapacitiesHandler))]
		[HarmonyPatch("GetLevel")]
		static class PawnCapacitiesHandler_GetLevel_Patch
		{
			static bool FullLevel(Pawn pawn)
			{
				if (pawn.health.Dead) return false;
				return Need_CurLevel_Patch.infectedColonists.Contains(pawn);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator il, IEnumerable<CodeInstruction> instructions)
			{
				var originalStart = il.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(PawnCapacitiesHandler).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => FullLevel(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, originalStart);
				yield return new CodeInstruction(OpCodes.Ldc_R4, 1f);
				yield return new CodeInstruction(OpCodes.Ret);

				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime)
						instruction.labels.Add(originalStart);
					yield return instruction;
					firstTime = false;
				}
			}
		}

		// patch to allow spawning Zombies with debug tools
		//
		[HarmonyPatch(typeof(PawnGenerator))]
		[HarmonyPatch("GenerateNewPawnInternal")]
		static class PawnGenerator_GenerateNewPawnInternal_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ref PawnGenerationRequest request, ref Pawn __result)
			{
				if (request.Faction == null || request.Faction.def != ZombieDefOf.Zombies) return true;
				__result = ZombieGenerator.GeneratePawn(ZombieGenerator.ZombieType.Random);
				return false;
			}
		}

		// patches to disallow interacting with zombies or zombiecorpses
		//
		[HarmonyPatch(typeof(ReservationManager))]
		[HarmonyPatch("CanReserve")]
		static class ReservationManager_CanReserve_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(LocalTargetInfo target, ref bool __result)
			{
				if (target.HasThing)
				{
					var zombie = target.Thing as Zombie;
					if ((zombie != null && zombie.wasMapPawnBefore == false) || target.Thing is ZombieCorpse)
					{
						__result = false;
						return false;
					}
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(ReservationManager))]
		[HarmonyPatch("Reserve")]
		static class ReservationManager_Reserve_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(LocalTargetInfo target, ref bool __result)
			{
				if (target.HasThing)
				{
					if (target.Thing is Zombie || target.Thing is ZombieCorpse)
					{
						__result = false;
						return false;
					}
				}
				return true;
			}
		}

		// patch so you cannot strip zombies
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("AnythingToStrip")]
		static class Pawn_AnythingToStrip_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, ref bool __result)
			{
				if (__instance is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch to not show forbidden red cross icon on zombies
		//
		[HarmonyPatch(typeof(ForbidUtility))]
		[HarmonyPatch("IsForbidden")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Faction) })]
		static class ForbidUtility_IsForbidden_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t, ref bool __result)
			{
				if (t is Zombie || t is ZombieCorpse)
				{
					__result = true;
					return false;
				}
				return true;
			}
		}

		// patch to make zombies appear to be never "down"
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("Downed", MethodType.Getter)]
		static class Pawn_Downed_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, ref bool __result)
			{
				var zombie = __instance as Zombie;
				if (zombie == null) return true;
				__result = false;
				return false;
			}
		}

		// patch to keep shooting even if a zombie is down (only
		// if "double tap" is on)
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("ThreatDisabled")]
		static class Pawn_ThreatDisabled_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, ref bool __result)
			{
				var zombie = __instance as Zombie;
				if (zombie == null) return true;
				__result = !zombie.Spawned;
				return false;
			}
		}
		[HarmonyPatch]
		static class Toils_Combat_FollowAndMeleeAttack_KillIncappedTarget_Patch
		{
			static bool IncappedTargetCheck(Job curJob, Pawn target)
			{
				if (target is Zombie) return true;
				return curJob.killIncappedTarget;
			}

			static MethodBase TargetMethod()
			{
				var inner = typeof(Toils_Combat).InnerTypeStartingWith("<FollowAndMeleeAttack");
				return inner.MethodStartingWith("<>m__");
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_get_Downed = typeof(Pawn).PropertyGetter(nameof(Pawn.Downed));

				var found1 = false;
				var found2 = false;
				CodeInstruction last = null;
				CodeInstruction localPawnInstruction = null;
				foreach (var instruction in instructions)
				{
					if (instruction.opcode == OpCodes.Callvirt && instruction.operand == m_get_Downed)
					{
						localPawnInstruction = new CodeInstruction(last);
						found1 = true;
					}

					if (instruction.opcode == OpCodes.Ldfld
						&& instruction.operand == typeof(Job).Field(nameof(Job.killIncappedTarget))
						&& localPawnInstruction != null)
					{
						yield return localPawnInstruction;

						instruction.opcode = OpCodes.Call;
						instruction.operand = SymbolExtensions.GetMethodInfo(() => IncappedTargetCheck(null, null));
						found2 = true;
					}
					yield return instruction;
					last = instruction;
				}

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}
		[HarmonyPatch(typeof(Stance_Warmup))]
		[HarmonyPatch("StanceTick")]
		static class Stance_Warmup_StanceTick_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch]
		static class Toils_Jump_JumpIfTargetDownedDistant_Patch
		{
			static MethodBase TargetMethod()
			{
				var inner = typeof(Toils_Jump).InnerTypeStartingWith("<JumpIfTargetDowned>c__");
				return inner.MethodStartingWith("<>m__");
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_MindState))]
		[HarmonyPatch("MeleeThreatStillThreat", MethodType.Getter)]
		static class Pawn_MindState_MeleeThreatStillThreat_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch(typeof(JobDriver_Wait))]
		[HarmonyPatch("CheckForAutoAttack")]
		static class JobDriver_Wait_CheckForAutoAttack_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions, 1);
			}
		}
		[HarmonyPatch]
		static class JobDriver_AttackStatic_TickAction_Patch
		{
			static MethodBase TargetMethod()
			{
				var inner = typeof(JobDriver_AttackStatic).InnerTypeStartingWith("<MakeNewToils>c__Iterator");
				return inner?.MethodMatching(methods =>
				{
					return methods.Where(m => m.Name.StartsWith("<>m__"))
						.OrderBy(m => m.Name)
						.LastOrDefault(); // the second one is the tickAction
				});
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch(typeof(TargetingParameters))]
		[HarmonyPatch("CanTarget")]
		static class TargetingParameters_CanTarget_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}

		// patch for custom zombie graphic parts
		//
		[HarmonyPatch(typeof(PawnGraphicSet))]
		[HarmonyPatch("ResolveAllGraphics")]
		static class PawnGraphicSet_ResolveAllGraphics_Patch
		{
			static void Postfix(PawnGraphicSet __instance)
			{
				if (ZombieSettings.Values.useCustomTextures == false) return;

				var zombie = __instance.pawn as Zombie;
				if (zombie == null) return;

				if (zombie.customBodyGraphic != null)
					__instance.nakedGraphic = zombie.customBodyGraphic;
				zombie.customBodyGraphic = null;

				if (zombie.customHeadGraphic != null)
					__instance.headGraphic = zombie.customHeadGraphic;
				zombie.customHeadGraphic = null;
			}
		}

		// patch for rendering zombies
		//
		[HarmonyPatch(typeof(PawnRenderer))]
		[HarmonyPatch("RenderPawnInternal")]
		[HarmonyPatch(new Type[] { typeof(Vector3), typeof(float), typeof(bool), typeof(Rot4), typeof(Rot4), typeof(RotDrawMode), typeof(bool), typeof(bool) })]
		static class PawnRenderer_RenderPawnInternal_Patch
		{
			static Vector3 toxicAuraOffset = new Vector3(0f, 0f, 0.1f);
			static readonly float leanAngle = 15f;

			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var m_AngleAxis = SymbolExtensions.GetMethodInfo(() => Quaternion.AngleAxis(0f, Vector3.zero));
				var f_pawn = AccessTools.Field(typeof(PawnRenderer), "pawn");

				var list = instructions.ToList();
				var idx = list.FirstIndexOf(instr => instr.operand == m_AngleAxis);
				if (idx > 0)
				{
					list[idx].operand = SymbolExtensions.GetMethodInfo(() => Zombie.ZombieAngleAxis(0f, Vector3.zero, null));
					list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0));
					list.Insert(idx++, new CodeInstruction(OpCodes.Ldfld, f_pawn));
				}
				else
					Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);

				return list.AsEnumerable();
			}

			[HarmonyPriority(Priority.First)]
			static void Postfix(PawnRenderer __instance, Vector3 rootLoc, float angle, bool renderBody)
			{
				var zombie = __instance.graphics.pawn as Zombie;
				if (zombie != null && zombie.isToxicSplasher && renderBody && zombie.state != ZombieState.Emerging)
				{
					var idx = ((Find.TickManager.TicksGame + zombie.thingIDNumber) / 10) % 8;
					if (idx >= 5) idx = 8 - idx;
					if (zombie.Rotation == Rot4.West) angle -= leanAngle;
					if (zombie.Rotation == Rot4.East) angle += leanAngle;
					var quat = Quaternion.AngleAxis(angle, Vector3.up);
					GraphicToolbox.DrawScaledMesh(MeshPool.plane20, Constants.TOXIC_AURAS[idx], rootLoc + toxicAuraOffset, quat, 1f, 1f);
				}
			}
		}

		[HarmonyPatch(typeof(PawnRenderer))]
		[HarmonyPatch("RenderPawnAt")]
		[HarmonyPatch(new Type[] { typeof(Vector3), typeof(RotDrawMode), typeof(bool) })]
		static class PawnRenderer_RenderPawnAt_Patch
		{
			static readonly float moteAltitute = Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);
			static Vector3 leftEyeOffset = new Vector3(-0.092f, 0f, -0.08f);
			static Vector3 rightEyeOffset = new Vector3(0.092f, 0f, -0.08f);

			static readonly Mesh bodyMesh = MeshPool.GridPlane(new Vector2(1.5f, 1.5f));
			static readonly Mesh bodyMesh_flipped = MeshPool.GridPlaneFlip(new Vector2(1.5f, 1.5f));

			static readonly Mesh headMesh = MeshPool.GridPlane(new Vector2(1.5f, 1.5f));
			static readonly Mesh headMesh_flipped = MeshPool.GridPlaneFlip(new Vector2(1.5f, 1.5f));

			static readonly Mesh shieldMesh = MeshPool.GridPlane(new Vector2(2f, 2f));
			static readonly Mesh shieldMesh_flipped = MeshPool.GridPlaneFlip(new Vector2(2f, 2f));

			[HarmonyPriority(Priority.First)]
			static bool Prefix(PawnRenderer __instance, Vector3 drawLoc, RotDrawMode bodyDrawType)
			{
				var zombie = __instance.graphics.pawn as Zombie;
				if (zombie == null) return true;

				if (zombie.state == ZombieState.Emerging)
				{
					zombie.Render(__instance, drawLoc, bodyDrawType);
					return false;
				}

				return true;
			}

			static void Postfix(PawnRenderer __instance, Vector3 drawLoc)
			{
				var zombie = __instance.graphics.pawn as Zombie;
				if (zombie == null) return;
				if (zombie.state == ZombieState.Emerging || zombie.GetPosture() != PawnPosture.Standing) return;

				// general zombie drawing

				Verse.TickManager tm = null;
				var orientation = zombie.Rotation;

				if (zombie.IsSuicideBomber)
				{
					tm = Find.TickManager;
					var currentTick = tm.TicksAbs;
					var interval = (int)zombie.bombTickingInterval;
					if (currentTick >= zombie.lastBombTick + interval)
						zombie.lastBombTick = currentTick;
					else if (currentTick <= zombie.lastBombTick + interval / 2)
					{
						if (zombie.state != ZombieState.Emerging)
						{
							var bombLightLoc = drawLoc + new Vector3(0, 0.1f, -0.2f);
							var scale = 1f;
							if (orientation == Rot4.South || orientation == Rot4.North) bombLightLoc.z += 0.05f;
							if (orientation == Rot4.North) { bombLightLoc.y -= 0.1f; scale = 1.5f; }
							if (orientation == Rot4.West) { bombLightLoc.x -= 0.25f; bombLightLoc.z -= 0.05f; }
							if (orientation == Rot4.East) { bombLightLoc.x += 0.25f; bombLightLoc.z -= 0.05f; }
							GraphicToolbox.DrawScaledMesh(MeshPool.plane10, Constants.BOMB_LIGHT, bombLightLoc, Quaternion.identity, scale, scale);
						}
					}
				}

				var location = drawLoc;
				location.y += Altitudes.AltInc / 2f;
				if (orientation == Rot4.North)
					location.y += Altitudes.AltInc / 12f;

				if (zombie.hasTankySuit > 0f && zombie.hasTankySuit <= 1f)
				{
					var n = (int)(zombie.hasTankySuit * 4f + 0.5f);

					var pos = location;
					var f = 25f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);
					pos.z += (Mathf.Max(0.5f, Mathf.Cos(f)) - 0.7f) / 20f;

					if (orientation == Rot4.South || orientation == Rot4.North)
					{
						var rot = Quaternion.identity;
						var frontBack = (int)(orientation == Rot4.South ? FacingIndex.South : FacingIndex.North);
						GraphicToolbox.DrawScaledMesh(bodyMesh, Constants.TANKYSUITS[frontBack][n], pos, rot, 1f, 1f);
					}
					else
					{
						var rot = Quaternion.identity;
						var mesh = orientation == Rot4.West ? bodyMesh_flipped : bodyMesh;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYSUITS[(int)FacingIndex.East][n], pos, rot, 1f, 1f);
					}
				}

				if (zombie.hasTankyHelmet > 0f && zombie.hasTankyHelmet <= 1f)
				{
					var n = (int)(zombie.hasTankyHelmet * 4f + 0.5f);
					var headOffset = zombie.Drawer.renderer.BaseHeadOffsetAt(orientation);
					headOffset.y += Altitudes.AltInc / 2f;

					var pos = location;
					var f = 25f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);
					pos.z += (Mathf.Max(0.5f, Mathf.Cos(f + 0.8f)) - 0.7f) / 20f;

					if (orientation == Rot4.South || orientation == Rot4.North)
					{
						var rot = Quaternion.identity;
						var frontBack = (int)(orientation == Rot4.South ? FacingIndex.South : FacingIndex.North);
						GraphicToolbox.DrawScaledMesh(headMesh, Constants.TANKYHELMETS[frontBack][n], pos + headOffset, rot, 1f, 1f);
					}
					else
					{
						var rot = Quaternion.identity;
						var mesh = orientation == Rot4.West ? headMesh_flipped : headMesh;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYHELMETS[(int)FacingIndex.East][n], pos + headOffset, rot, 1f, 1f);
					}
				}

				if (zombie.hasTankyShield > 0f && zombie.hasTankyShield <= 1f)
				{
					var n = (int)(zombie.hasTankyShield * 4f + 0.5f);
					var f = Mathf.PI * 4f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);

					if (orientation == Rot4.South || orientation == Rot4.North)
					{
						var x = Mathf.Sin(f) * 0.03f;
						var dx = x + (orientation == Rot4.South ? 0.2f : -0.2f);
						var dy = orientation == Rot4.South ? 0.2f : -0.2f;
						var dz = Mathf.Abs(Mathf.Cos(f) * 0.05f) + (orientation == Rot4.South ? -0.2f : 0.2f);
						var rot = Quaternion.Euler(0f, x * 100f, 0f);
						var mesh = orientation == Rot4.South ? shieldMesh : shieldMesh_flipped;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYSHIELDS[(int)FacingIndex.South][n], drawLoc + new Vector3(dx, dy, dz), rot, 0.52f, 0.52f);
					}
					else
					{
						var dx = orientation == Rot4.West ? -0.45f : 0.45f;
						var dy = 0.3f;
						var dz = Mathf.Abs(Mathf.Cos(f) * 0.05f);
						var rot = Quaternion.Euler(0f, dx * 22f, 0f);
						var mesh = orientation == Rot4.West ? shieldMesh_flipped : shieldMesh;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYSHIELDS[(int)FacingIndex.East][n], drawLoc + new Vector3(dx, dy, dz), rot, 0.62f, 0.62f);
					}
				}

				if (zombie.raging == 0) return;

				// raging zombies drawing

				drawLoc.y = moteAltitute;
				var quickHeadCenter = drawLoc + new Vector3(0, 0, 0.35f);

				if (Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle)
				{
					tm = tm ?? Find.TickManager;
					var blinkPeriod = 60 + zombie.thingIDNumber % 180; // between 2-5s
					var eyesOpen = (tm.TicksAbs % blinkPeriod) > 3;
					if (eyesOpen || tm.CurTimeSpeed == TimeSpeed.Paused)
					{
						// the following constant comes from PawnRenderer.RenderPawnInternal
						var loc = drawLoc + __instance.BaseHeadOffsetAt(orientation) + new Vector3(0, 0.0281250011f, 0);

						// not clear why 75 but it seems to fit
						var eyeX = zombie.sideEyeOffset.x / 75f;
						var eyeZ = zombie.sideEyeOffset.z / 75f;

						if (orientation == Rot4.West)
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, loc + new Vector3(-eyeX, 0, eyeZ), Quaternion.identity, 0.5f, 0.5f);

						else if (orientation == Rot4.East)
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, loc + new Vector3(eyeX, 0, eyeZ), Quaternion.identity, 0.5f, 0.5f);

						if (orientation == Rot4.South)
						{
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, quickHeadCenter + leftEyeOffset, Quaternion.identity, 0.5f, 0.5f);
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, quickHeadCenter + rightEyeOffset, Quaternion.identity, 0.5f, 0.5f);
						}
					}
				}

				if (orientation == Rot4.West) quickHeadCenter.x -= 0.09f;
				if (orientation == Rot4.East) quickHeadCenter.x += 0.09f;
				GraphicToolbox.DrawScaledMesh(MeshPool.plane20, Constants.RAGE_AURAS[Find.CameraDriver.CurrentZoom], quickHeadCenter, Quaternion.identity, 1f, 1f);
			}
		}

		// patch to exclude any zombieland apparel from being used at all
		// (we fake our own apparel via the patch below)
		//
		[HarmonyPatch]
		static class PawnApparelGenerator_PossibleApparelSet_PairOverlapsAnything_Patch
		{
			static MethodBase TargetMethod()
			{
				var inner = AccessTools.Inner(typeof(PawnApparelGenerator), "PossibleApparelSet");
				return AccessTools.Method(inner, "PairOverlapsAnything");
			}

			[HarmonyPriority(Priority.First)]
			static bool Prefix(ThingStuffPair pair, ref bool __result)
			{
				if (pair.thing?.IsZombieDef() ?? false)
				{
					__result = true;
					return false;
				}
				if (pair.stuff?.IsZombieDef() ?? false)
				{
					__result = true;
					return false;
				}
				return true;
			}
		}

		// patch for giving zombies accessories like bomb vests or tanky suits
		//
		[HarmonyPatch(typeof(PawnGraphicSet))]
		[HarmonyPatch("ResolveApparelGraphics")]
		static class PawnGraphicSet_ResolveApparelGraphics_Patch
		{
			[HarmonyPriority(Priority.Last)]
			static void Postfix(PawnGraphicSet __instance)
			{
				var zombie = __instance.pawn as Zombie;
				if (zombie == null) return;

				if (zombie.IsSuicideBomber)
				{
					var apparel = new Apparel() { def = ThingDef.Named("Apparel_BombVest") };
					ApparelGraphicRecord record;
					if (ApparelGraphicRecordGetter.TryGetGraphicApparel(apparel, BodyTypeDefOf.Hulk, out record))
						__instance.apparelGraphics.Add(record);
				}
			}
		}

		// patch for reducing the warmup smash time for raging zombies
		//
		[HarmonyPatch(typeof(Verb))]
		[HarmonyPatch("TryStartCastOn")]
		static class Verb_TryStartCastOn_Patch
		{
			static int ModifyTicks(float seconds, Verb verb)
			{
				var ticks = seconds.SecondsToTicks();
				var zombie = verb?.caster as Zombie;
				if (zombie != null && (zombie.raging > 0 || zombie.wasMapPawnBefore))
				{
					var grid = zombie.Map.GetGrid();
					var count = grid.GetZombieCount(zombie.Position);
					if (count > 0) ticks = ticks / count;
				}
				return ticks;
			}

			[HarmonyPriority(Priority.Last)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_SecondsToTicks = SymbolExtensions.GetMethodInfo(() => GenTicks.SecondsToTicks(0f));

				var found = false;
				foreach (var instruction in instructions)
				{
					if (instruction.operand == m_SecondsToTicks)
					{
						yield return new CodeInstruction(OpCodes.Ldarg_0);
						instruction.opcode = OpCodes.Call;
						instruction.operand = SymbolExtensions.GetMethodInfo(() => ModifyTicks(0, null));
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch for variable zombie movement speed
		//
		[HarmonyPatch(typeof(StatExtension))]
		[HarmonyPatch("GetStatValue")]
		static class StatExtension_GetStatValue_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing thing, StatDef stat, ref float __result)
			{
				var zombie = thing as Zombie;
				if (zombie == null) return true;

				if (stat == StatDefOf.PainShockThreshold)
				{
					if (zombie.raging > 0 || zombie.wasMapPawnBefore)
					{
						__result = 1000f;
						return false;
					}
					if (zombie.hasTankyShield != -1f || zombie.hasTankyHelmet != -1f || zombie.hasTankySuit != -1f)
					{
						__result = 5000f;
						return false;
					}

					var bodyType = zombie.story.bodyType;
					if (bodyType == BodyTypeDefOf.Thin)
					{
						__result = 0.1f;
						return false;
					}
					if (bodyType == BodyTypeDefOf.Hulk)
					{
						__result = 0.8f;
						return false;
					}
					else if (bodyType == BodyTypeDefOf.Fat)
					{
						__result = 10f;
						return false;
					}
					__result = 0.8f;
					return false;
				}

				if (stat == StatDefOf.MeleeHitChance)
				{
					if (zombie.hasTankyShield != -1f)
					{
						__result = 1.0f;
						return false;
					}

					if (zombie.hasTankyHelmet != -1f || zombie.hasTankySuit != -1f)
					{
						__result = 0.9f;
						return false;
					}

					if (zombie.story.bodyType == BodyTypeDefOf.Fat)
					{
						__result = 0.8f;
						return false;
					}

					if (zombie.state == ZombieState.Tracking || zombie.raging > 0 || zombie.wasMapPawnBefore)
						__result = Constants.ZOMBIE_HIT_CHANCE_TRACKING;
					else
						__result = Constants.ZOMBIE_HIT_CHANCE_IDLE;
					return false;
				}

				if (stat == StatDefOf.MoveSpeed)
				{
					if (zombie.IsTanky)
					{
						__result = 0.002f * Find.TickManager.TickRateMultiplier;
						return false;
					}

					float speed;
					if (zombie.state == ZombieState.Tracking || zombie.raging > 0 || zombie.wasMapPawnBefore)
						speed = ZombieSettings.Values.moveSpeedTracking;
					else
						speed = ZombieSettings.Values.moveSpeedIdle;

					var factor = 1f;
					var bodyType = zombie.story.bodyType;
					if (bodyType == BodyTypeDefOf.Thin)
						factor = 0.8f;
					else if (bodyType == BodyTypeDefOf.Hulk)
						factor = 0.1f;
					else if (bodyType == BodyTypeDefOf.Fat)
						factor = 0.05f;

					// instead of ticking zombies as often as everything else, we tick
					// them at 1x speed and make them faster instead. Not perfect but
					// a very good workaround for good game speed
					//
					__result = speed * factor * Find.TickManager.TickRateMultiplier;
					if (zombie.wasMapPawnBefore)
						__result *= 2f;
					return false;
				}

				return true;
			}
		}

		// patch for variable zombie damage factor
		//
		[HarmonyPatch(typeof(VerbProperties))]
		[HarmonyPatch("GetDamageFactorFor", typeof(Tool), typeof(Pawn), typeof(HediffComp_VerbGiver))]
		static class Verb_GetDamageFactorFor_Patch
		{
			static void Postfix(Pawn attacker, ref float __result)
			{
				var zombie = attacker as Zombie;
				if (zombie == null) return;

				if (zombie.hasTankyShield > 0f || zombie.hasTankyHelmet > 0f || zombie.hasTankySuit > 0f)
				{
					var val = 0f;
					if (zombie.hasTankyShield > 0f) val += 30f;
					if (zombie.hasTankyHelmet > 0f) val += 10f;
					if (zombie.hasTankySuit > 0f) val += 20f;
					__result *= val;
					return;
				}

				var settings = ZombieSettings.Values.damageFactor;
				var bodyType = zombie.story.bodyType;
				if (bodyType == BodyTypeDefOf.Thin)
					__result *= 0.5f * settings;
				else if (bodyType == BodyTypeDefOf.Hulk)
					__result *= 3f * settings;
				else if (bodyType == BodyTypeDefOf.Fat)
					__result *= 4f * settings;

				if (zombie.wasMapPawnBefore)
					__result *= 10f;
			}
		}

		// patch for zombies rotting regardless of temperature
		//
		[HarmonyPatch(typeof(Thing))]
		[HarmonyPatch("AmbientTemperature", MethodType.Getter)]
		static class Thing_AmbientTemperature_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing __instance, ref float __result)
			{
				if (__instance is Zombie || __instance is ZombieCorpse)
				{
					__result = 21f; // fake normal conditions
					return false;
				}
				return true;
			}
		}

		// patch to set zombie bite injuries as non natural healing to avoid
		// the healing cross mote
		//
		[HarmonyPatch(typeof(HediffUtility))]
		[HarmonyPatch("CanHealNaturally")]
		static class HediffUtility_CanHealNaturally_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Hediff_Injury hd, ref bool __result)
			{
				var zombieBite = hd as Hediff_Injury_ZombieBite;
				if (zombieBite != null && zombieBite.pawn.RaceProps.Humanlike)
				{
					var tendDuration = zombieBite.TendDuration;
					if (tendDuration != null)
					{
						var state = tendDuration.GetInfectionState();
						__result = (state != InfectionState.BittenNotVisible && state < InfectionState.BittenInfectable);
						return false;
					}
				}
				return true;
			}
		}

		// patch to allow amputation of biten body parts
		//
		[HarmonyPatch]
		static class Recipe_RemoveBodyPart_GetPartsToApplyOn_Patch
		{
			static MethodBase TargetMethod()
			{
				var type = "RimWorld.Recipe_RemoveBodyPart".ToType();
				return type.MethodNamed("GetPartsToApplyOn");
			}

			[HarmonyPriority(Priority.Last)]
			static void Postfix(Pawn pawn, RecipeDef recipe, ref IEnumerable<BodyPartRecord> __result)
			{
				if (recipe != RecipeDefOf.RemoveBodyPart)
					return;

				__result = pawn.health.hediffSet
					.GetHediffs<Hediff_Injury_ZombieBite>()
					.Select(bite => bite.Part)
					.Union(__result);
			}
		}

		// patch to keep zombie bite injuries even after tending if they have to stay around
		//
		[HarmonyPatch(typeof(Hediff))]
		[HarmonyPatch("ShouldRemove", MethodType.Getter)]
		static class Hediff_ShouldRemove_Patch
		{
			[HarmonyPriority(Priority.Last)]
			static void Postfix(Hediff __instance, ref bool __result)
			{
				if (__result == false) return;

				// do not remove our zombie hediffs from dead pawns
				if (__instance.pawn != null && __instance.pawn.Dead && __instance.def.IsZombieHediff())
				{
					__result = false;
					return;
				}

				var zombieBite = __instance as Hediff_Injury_ZombieBite;
				if (zombieBite != null && zombieBite.pawn.RaceProps.Humanlike)
				{
					var tendDuration = zombieBite.TendDuration;
					if (tendDuration != null)
					{
						var state = tendDuration.GetInfectionState();
						if (state == InfectionState.BittenNotVisible || state >= InfectionState.BittenInfectable)
							__result = false;
					}
				}
			}
		}

		// patch for making burning zombies keep their fire (even when it rains) 
		//
		[HarmonyPatch(typeof(Fire))]
		[HarmonyPatch("VulnerableToRain")]
		static class Fire_VulnerableToRain_Patch
		{
			static bool Prefix(Fire __instance, ref bool __result)
			{
				var zombie = __instance.parent as Zombie;
				if (zombie != null)
				{
					__result = false;
					return false;
				}

				return true;
			}
		}

		// patch for making zombies burn slower
		//
		[HarmonyPatch(typeof(Fire))]
		[HarmonyPatch("DoFireDamage")]
		static class Fire_DoFireDamage_Patch
		{
			static int Reduce(int n) { return n / 2; }

			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, IEnumerable<CodeInstruction> instructions)
			{
				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime && instruction.opcode == OpCodes.Ldarg_1)
					{
						firstTime = false;
						var label = generator.DefineLabel();

						yield return instruction;
						yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
						yield return new CodeInstruction(OpCodes.Brfalse, label);
						yield return new CodeInstruction(OpCodes.Ldloc_1);
						yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => Reduce(0)));
						yield return new CodeInstruction(OpCodes.Stloc_1);

						yield return new CodeInstruction(OpCodes.Ldarg_1) { labels = new List<Label> { label } };
					}
					else
						yield return instruction;
				}

				if (firstTime) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch for excluding burning zombies from total fire count 
		//
		[HarmonyPatch(typeof(FireWatcher))]
		[HarmonyPatch("UpdateObservations")]
		static class FireWatcher_UpdateObservations_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, IEnumerable<CodeInstruction> instructions)
			{
				var found1 = false;
				var found2 = false;

				var n = 0;
				var label = generator.DefineLabel();

				foreach (var instruction in instructions)
				{
					yield return instruction;

					if (instruction.opcode == OpCodes.Stloc_2)
					{
						yield return new CodeInstruction(OpCodes.Ldloc_2);
						yield return new CodeInstruction(OpCodes.Ldfld, typeof(AttachableThing).Field(nameof(AttachableThing.parent)));
						yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
						yield return new CodeInstruction(OpCodes.Brtrue, label);
						found1 = true;
					}

					if (n >= 0 && instruction.opcode == OpCodes.Add)
						n++;

					if (instruction.opcode == OpCodes.Ldloc_1 && n == 2)
					{
						instruction.labels.Add(label);
						n = -1;
						found2 = true;
					}
				}

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to prevent damage if zombie has armor
		//
		[HarmonyPatch(typeof(ArmorUtility))]
		[HarmonyPatch("GetPostArmorDamage")]
		public static class ArmorUtility_GetPostArmorDamage_Patch
		{
			static void PlayTink(Thing thing)
			{
				if (Constants.USE_SOUND && Prefs.VolumeAmbient > 0f)
				{
					var info = SoundInfo.InMap(thing);
					SoundDef.Named("TankyTink").PlayOneShot(info);
				}
			}

			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref float amount, BodyPartRecord part, out bool deflectedByMetalArmor, out bool diminishedByMetalArmor, ref float __result)
			{
				deflectedByMetalArmor = false;
				diminishedByMetalArmor = false;

				var zombie = pawn as Zombie;
				if (zombie == null)
					return true;

				var difficulty = Find.Storyteller.difficulty.difficulty;
				if (amount > 45)
					amount = 45 + Mathf.Sqrt(amount - 46);

				if (zombie.hasTankyShield > 0f)
				{
					PlayTink(zombie);
					zombie.hasTankyShield -= amount / (1f + difficulty * 150f);
					if (zombie.hasTankyShield < 0f)
						zombie.hasTankyShield = -1f;
					__result = -1f;
					deflectedByMetalArmor = true;
					return false;
				}

				var fakeHeadShot = (zombie.hasTankySuit <= 0f && zombie.hasTankyHelmet > 0f && Rand.Chance(0.4f));
				if (part.groups.Contains(BodyPartGroupDefOf.FullHead) || fakeHeadShot)
				{
					if (zombie.hasTankyHelmet > 0f)
					{
						PlayTink(zombie);
						zombie.hasTankyHelmet -= amount / (1f + difficulty * 10f);
						if (zombie.hasTankyHelmet < 0f)
							zombie.hasTankyHelmet = -1f;
						__result = -1f;
						deflectedByMetalArmor = true;
						return false;
					}
				}
				else
				{
					if (zombie.hasTankySuit > 0f)
					{
						PlayTink(zombie);
						zombie.hasTankySuit -= amount / (1f + difficulty * 100f);
						if (zombie.hasTankySuit < 0f)
							zombie.hasTankySuit = -1f;
						__result = -1f;
						deflectedByMetalArmor = true;
						return false;
					}
				}

				// still a tough zombie even if we hit the body but some armor is left
				if (zombie.hasTankyHelmet > 0f || zombie.hasTankySuit > 0f)
				{
					var toughnessLevel = Find.Storyteller.difficulty.difficulty;
					amount = (amount + toughnessLevel) / (toughnessLevel + 1);
				}

				return true;
			}

			static bool GetAfterArmorDamagePrefix(ref DamageInfo originalDinfo, Pawn pawn, BodyPartRecord hitPart, out bool shieldAbsorbed)
			{
				var dinfo = new DamageInfo(originalDinfo);
				var dmgAmount = dinfo.Amount;

				shieldAbsorbed = false;
				if (pawn == null || hitPart == null) return true;
				var prefixResult = 0f;
				var result = Prefix(pawn, ref dmgAmount, hitPart, out var deflect, out var diminish, ref prefixResult);
				if (result && originalDinfo.Instigator != null)
					return (pawn.Spawned && pawn.Dead == false
						&& pawn.Destroyed == false
						&& originalDinfo.Instigator.Spawned
						&& originalDinfo.Instigator.Destroyed == false);

				dinfo.SetAmount(dmgAmount);
				originalDinfo = dinfo;
				shieldAbsorbed = deflect || diminish;

				return false;
			}

			// called from Main
			public static void PatchCombatExtended(HarmonyInstance harmony)
			{
				var t_ArmorUtilityCE = AccessTools.TypeByName("CombatExtended.ArmorUtilityCE");
				if (t_ArmorUtilityCE == null) return;

				var m_GetAfterArmorDamage = AccessTools.Method(t_ArmorUtilityCE, "GetAfterArmorDamage", new Type[] { typeof(DamageInfo), typeof(Pawn), typeof(BodyPartRecord), typeof(bool).MakeByRefType() });
				if (m_GetAfterArmorDamage != null)
				{
					var damageInfo = new DamageInfo();
					var someBool = false;
					var prefix = new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => GetAfterArmorDamagePrefix(ref damageInfo, null, null, out someBool)));
					harmony.Patch(m_GetAfterArmorDamage, prefix, new HarmonyMethod(null));
				}
			}
		}

		// patch for simpler attack verb handling on zombies (story work tab confict)
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("TryStartAttack")]
		static class Pawn_TryStartAttack_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, LocalTargetInfo targ, ref bool __result)
			{
				if ((__instance is Zombie) == false)
					return true;

				var verb = __instance.TryGetAttackVerb(targ.Thing);
				__result = verb != null && verb.TryStartCastOn(targ, false, true);
				return false;
			}
		}

		// patch to handle various things when someone dies
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("Kill")]
		static class Pawn_Kill_Patch
		{
			[HarmonyPriority(Priority.First)]
			static void Prefix(Pawn __instance)
			{
				// remove current job of zombie immediately when killed
				var zombie = __instance as Zombie;
				if (zombie != null)
				{
					if (zombie.jobs != null && zombie.CurJob != null)
						zombie.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
					return;
				}
				var pawn = __instance;

				// flag zombie bites to be infectious when pawn dies
				pawn.health.hediffSet
					.GetHediffs<Hediff_Injury_ZombieBite>()
					.Where(zombieBite => zombieBite.TendDuration.InfectionStateBetween(InfectionState.BittenInfectable, InfectionState.Infected))
					.Do(zombieBite => zombieBite.mayBecomeZombieWhenDead = true);
			}
		}

		// patch headshot to kill zombies right away
		//
		[HarmonyPatch(typeof(DamageWorker_AddInjury))]
		[HarmonyPatch("IsHeadshot")]
		static class DamageWorker_AddInjury_IsHeadshot_Patch
		{
			static void Postfix(Pawn pawn, bool __result)
			{
				if (__result == false) return;
				var zombie = pawn as Zombie;
				if (zombie != null && zombie.Spawned && zombie.Dead == false && zombie.raging == 0 && zombie.wasMapPawnBefore == false)
					zombie.state = ZombieState.ShouldDie;
			}
		}

		// patch for disallowing social interaction with zombies
		//
		[HarmonyPatch(typeof(RelationsUtility))]
		[HarmonyPatch("HasAnySocialMemoryWith")]
		static class RelationsUtility_HasAnySocialMemoryWith_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn p, Pawn otherPawn, ref bool __result)
			{
				if (p is Zombie || otherPawn is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(Pawn_RelationsTracker))]
		[HarmonyPatch("OpinionOf")]
		static class Pawn_RelationsTracker_OpinionOf_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var returnZeroLabel = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_RelationsTracker).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
				yield return new CodeInstruction(OpCodes.Brtrue_S, returnZeroLabel);
				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
				yield return new CodeInstruction(OpCodes.Brtrue_S, returnZeroLabel);

				foreach (var instruction in instructions)
					yield return instruction;

				yield return new CodeInstruction(OpCodes.Ldc_I4_0)
				{
					labels = new List<Label> { returnZeroLabel }
				};
				yield return new CodeInstruction(OpCodes.Ret);
			}
		}
		[HarmonyPatch(typeof(RelationsUtility))]
		[HarmonyPatch("PawnsKnowEachOther")]
		static class RelationsUtility_PawnsKnowEachOther_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn p1, Pawn p2, ref bool __result)
			{
				if (p1 is Zombie || p2 is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(ThoughtHandler))]
		[HarmonyPatch("GetSocialThoughts")]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(List<ISocialThought>) })]
		static class ThoughtHandler_GetSocialThoughts_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ThoughtHandler __instance, Pawn otherPawn, List<ISocialThought> outThoughts)
			{
				if (otherPawn is Zombie || __instance.pawn is Zombie)
				{
					outThoughts.Clear();
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(SituationalThoughtHandler))]
		[HarmonyPatch("AppendSocialThoughts")]
		static class SituationalThoughtHandler_AppendSocialThoughts_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(SituationalThoughtHandler __instance, Pawn otherPawn)
			{
				return !(otherPawn is Zombie || __instance.pawn is Zombie);
			}
		}
		[HarmonyPatch(typeof(Corpse))]
		[HarmonyPatch("GiveObservedThought")]
		static class Corpse_GiveObservedThought_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Corpse __instance)
			{
				return !(__instance is ZombieCorpse);
			}
		}

		// patch for disallowing thoughts on zombies
		//
		[HarmonyPatch(typeof(ThoughtUtility))]
		[HarmonyPatch("CanGetThought")]
		static class ThoughtUtility_CanGetThought_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref bool __result)
			{
				if (pawn is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch for not forbidding zombie corpses
		//
		[HarmonyPatch(typeof(ForbidUtility))]
		[HarmonyPatch("SetForbiddenIfOutsideHomeArea")]
		static class ForbidUtility_SetForbiddenIfOutsideHomeArea_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t)
			{
				return (t as ZombieCorpse == null);
			}
		}

		// patches to prevent interaction with zombies
		//
		[HarmonyPatch(typeof(Pawn_InteractionsTracker))]
		[HarmonyPatch("TryInteractWith")]
		static class Pawn_InteractionsTracker_TryInteractWith_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = new List<CodeInstruction>();
				conditions.AddRange(Tools.NotZombieInstructions(generator, method));
				conditions.AddRange(Tools.NotZombieInstructions(generator, method, "recipient"));
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions);
				return transpiler(generator, instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_InteractionsTracker))]
		[HarmonyPatch("InteractionsTrackerTick")]
		static class Pawn_InteractionsTracker_InteractionsTrackerTick_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions);
				return transpiler(generator, instructions);
			}
		}

		// patch to colorize the label of zombies that were colonists
		//
		[HarmonyPatch(typeof(PawnNameColorUtility))]
		[HarmonyPatch("PawnNameColorOf")]
		static class PawnNameColorUtility_PawnNameColorOf_Patch
		{
			static Color zombieLabelColor = new Color(0.7f, 1f, 0.7f);

			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref Color __result)
			{
				var zombie = pawn as Zombie;
				if (zombie != null && zombie.wasMapPawnBefore)
				{
					__result = zombieLabelColor;
					return false;
				}
				return true;
			}
		}

		// allow clicks on zombies that were colonists
		//
		[HarmonyPatch(typeof(ThingSelectionUtility))]
		[HarmonyPatch("SelectableByMapClick")]
		static class ThingSelectionUtility_SelectableByMapClick_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t, ref bool __result)
			{
				var zombie = t as Zombie;
				if (zombie != null && zombie.wasMapPawnBefore)
				{
					__result = true;
					return false;
				}
				return true;
			}
		}

		// patch for a custom zombie corpse class
		//
		[HarmonyPatch(typeof(ThingMaker))]
		[HarmonyPatch("MakeThing")]
		static class ThingMaker_MakeThing_Patch
		{
			static void Prefix(ThingDef def)
			{
				if (def == null || def.IsCorpse == false) return;
				if (def.ingestible == null) return;
				if (def.ingestible.sourceDef is ThingDef_Zombie)
				{
					def.selectable = false;
					def.neverMultiSelect = true;
					def.drawGUIOverlay = false;
					def.hasTooltip = false;
					def.hideAtSnowDepth = 99f;
					def.inspectorTabs = new List<Type>();
					def.passability = Traversability.Standable;
					def.stackLimit = 1;
					def.thingClass = typeof(ZombieCorpse);
				}
			}
		}

		// patch to handle targets downed so that we update our grid
		//
		[HarmonyPatch(typeof(Pawn_HealthTracker))]
		[HarmonyPatch("MakeDowned")]
		static class Pawn_HealthTracker_MakeDowned_Patch
		{
			static void Postfix(Pawn_HealthTracker __instance, Pawn ___pawn)
			{
				if (___pawn is Zombie) return;
				if (___pawn == null || ___pawn.Map == null) return;

				var grid = ___pawn.Map.GetGrid();
				if (Constants.KILL_CIRCLE_RADIUS_MULTIPLIER > 0)
				{
					var timestamp = grid.GetTimestamp(___pawn.Position);
					if (timestamp > 0)
					{
						var radius = Tools.RadiusForPawn(___pawn) * Constants.KILL_CIRCLE_RADIUS_MULTIPLIER;
						radius /= ZombieSettings.Values.zombieInstinct.HalfToDoubleValue();
						Tools.GetCircle(radius).Do(vec =>
						{
							var pos = ___pawn.Position + vec;
							var cell = grid.GetPheromone(pos, false);
							if (cell != null && cell.timestamp > 0 && cell.timestamp <= timestamp)
								cell.timestamp = 0;
						});
					}
				}
				grid.SetTimestamp(___pawn.Position, 0);
			}
		}

		// patch to update twinkie graphics
		//
		[HarmonyPatch(typeof(Game))]
		[HarmonyPatch("FinalizeInit")]
		static class Game_FinalizeInit_Patch
		{
			static void Postfix(Game __instance)
			{
				Tools.EnableTwinkie(ZombieSettings.Values.replaceTwinkie);
				ModCounter.Trigger();
			}
		}

		// patches to update our zombie count grid
		//
		[HarmonyPatch(typeof(Map))]
		[HarmonyPatch("FinalizeLoading")]
		static class Map_FinalizeLoading_Patch
		{
			static void Prefix(Map __instance)
			{
				var grid = __instance.GetGrid();
				grid.IterateCellsQuick(cell => cell.zombieCount = 0);
			}
		}

		// convert dying infected pawns when they start rotting
		//
		[HarmonyPatch(typeof(Corpse))]
		[HarmonyPatch("RotStageChanged")]
		static class Corpse_RotStageChanged_Patch
		{
			static void Postfix(Corpse __instance)
			{
				var pawn = __instance.InnerPawn;
				if (pawn == null || (pawn is Zombie))
					return;

				var rotStage = __instance.GetRotStage();
				if (rotStage == RotStage.Fresh)
					return;

				var hasBrain = pawn.health.hediffSet.GetBrain() != null;
				if (hasBrain == false)
					return;

				var shouldBecomeZombie = (pawn.health != null && pawn.health.hediffSet
					.GetHediffs<Hediff_Injury_ZombieBite>()
					.Any(zombieByte => zombieByte.mayBecomeZombieWhenDead));

				if (shouldBecomeZombie)
					Tools.QueueConvertToZombie(__instance);
			}
		}

		// show infection on dead pawns
		//
		[HarmonyPatch(typeof(HealthCardUtility))]
		[HarmonyPatch("DrawOverviewTab")]
		static class HealthCardUtility_DrawOverviewTab_Patch
		{
			static void Postfix(Pawn pawn, Rect leftRect, ref float __result)
			{
				if (pawn == null)
					return;

				var hasBrain = pawn.health.hediffSet.GetBrain() != null;
				if (hasBrain == false)
					return;

				if (pawn.Dead)
				{
					if (pawn.health.hediffSet.GetHediffs<Hediff_Injury_ZombieBite>()
						.All(zombieByte => zombieByte.mayBecomeZombieWhenDead == false))
						return;
				}
				else
				{
					if (Tools.HasInfectionState(pawn, InfectionState.BittenInfectable, InfectionState.Infected) == false)
						return;
				}

				__result += 15f;
				GUI.color = Color.red;
				var text = "BodyIsInfectedLabel".Translate();
				var textHeight = Text.CalcHeight(text, leftRect.width);
				Widgets.Label(new Rect(0f, __result, leftRect.width, textHeight), text);
				TooltipHandler.TipRegion(new Rect(0f, __result, leftRect.width, textHeight), "BodyIsInfectedTooltip".Translate());
				__result += textHeight;
				GUI.color = Color.white;
			}
		}

		// patch to handle targets deaths so that we update our grid
		//
		[HarmonyPatch(typeof(PawnComponentsUtility))]
		[HarmonyPatch("RemoveComponentsOnKilled")]
		static class PawnComponentsUtility_RemoveComponentsOnKilled_Patch
		{
			static void Postfix(Pawn pawn)
			{
				if (pawn is Zombie || pawn.Map == null) return;

				if (Constants.KILL_CIRCLE_RADIUS_MULTIPLIER > 0)
				{
					var grid = pawn.Map.GetGrid();
					var timestamp = grid.GetTimestamp(pawn.Position);
					var radius = Tools.RadiusForPawn(pawn) * Constants.KILL_CIRCLE_RADIUS_MULTIPLIER;
					radius /= ZombieSettings.Values.zombieInstinct.HalfToDoubleValue();
					Tools.GetCircle(radius).Do(vec =>
					{
						var pos = pawn.Position + vec;
						var cell = grid.GetPheromone(pos, false);
						if (cell != null && cell.timestamp > 0 && cell.timestamp <= timestamp)
							grid.SetTimestamp(pos, 0);
					});
				}
			}
		}

		// patch to prevent thoughts on zombies
		//
		[HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility))]
		[HarmonyPatch("TryGiveThoughts")]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(DamageInfo?), typeof(PawnDiedOrDownedThoughtsKind) })]
		static class PawnDiedOrDownedThoughtsUtility_TryGiveThoughts_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn victim)
			{
				return !(victim is Zombie);
			}
		}

		// patch to remove immunity ticks on zombies
		//
		[HarmonyPatch(typeof(ImmunityHandler))]
		[HarmonyPatch("ImmunityHandlerTick")]
		static class ImmunityHandler_ImmunityHandlerTick_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ImmunityHandler __instance)
			{
				return !(__instance.pawn is Zombie);
			}
		}

		// patch to trigger on gun shots
		//
		[HarmonyPatch(typeof(Projectile))]
		[HarmonyPatch("Launch")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(Thing), typeof(ThingDef) })]
		public static class Projectile_Launch_Patch
		{
			static void Postfix(Thing launcher, Vector3 origin, LocalTargetInfo usedTarget)
			{
				var pawn = launcher as Pawn;
				if (pawn == null) return;

				var noiseScale = 1f;
				if (pawn.equipment.PrimaryEq != null)
					noiseScale = pawn.equipment.PrimaryEq.PrimaryVerb.verbProps.muzzleFlashScale / Constants.BASE_MUZZLE_FLASH_VALUE;

				var now = Tools.Ticks();
				var pos = origin.ToIntVec3();
				var magnitude = (usedTarget.CenterVector3 - origin).magnitude * noiseScale * Math.Min(1f, ZombieSettings.Values.zombieInstinct.HalfToDoubleValue());
				var radius = Tools.Boxed(magnitude, Constants.MIN_WEAPON_RANGE, Constants.MAX_WEAPON_RANGE);
				var grid = launcher.Map.GetGrid();
				Tools.GetCircle(radius).Do(vec => grid.BumpTimestamp(pos + vec, now - vec.LengthHorizontalSquared));
			}

			static float GetDistanceTraveled(float velocity, float angle, float shotHeight)
			{
				if (shotHeight < 0.001f)
					return (velocity * velocity / 9.8f) * Mathf.Sin(2f * angle);
				var velsin = velocity * Mathf.Sin(angle);
				return ((velocity * Mathf.Cos(angle)) / 9.8f) * (velsin + Mathf.Sqrt(velsin * velsin + 2f * 9.8f * shotHeight));
			}

			static void PostfixCombatExtended(Thing launcher, Vector2 origin, float shotAngle, float shotRotation, float shotHeight = 0f, float shotSpeed = -1f, Thing equipment = null)
			{
				var pawn = launcher as Pawn;
				if (pawn == null) return;

				var noiseScale = 1f;
				if (pawn.equipment.PrimaryEq != null)
					noiseScale = pawn.equipment.PrimaryEq.PrimaryVerb.verbProps.muzzleFlashScale / Constants.BASE_MUZZLE_FLASH_VALUE;

				var now = Tools.Ticks();
				var pos = new IntVec3(origin);
				var delta = GetDistanceTraveled(shotSpeed, shotAngle, shotHeight);
				var magnitude = noiseScale * delta * Math.Min(1f, ZombieSettings.Values.zombieInstinct.HalfToDoubleValue());
				var radius = Tools.Boxed(magnitude, Constants.MIN_WEAPON_RANGE, Constants.MAX_WEAPON_RANGE);
				var grid = launcher.Map.GetGrid();
				Tools.GetCircle(radius).Do(vec => grid.BumpTimestamp(pos + vec, now - vec.LengthHorizontalSquared));
			}

			// called from Main
			public static void PatchCombatExtended(HarmonyInstance harmony)
			{
				// do not throw or error if this type does not exist
				// it only exists if CombatExtended is loaded (optional)
				//
				var type = AccessTools.TypeByName("CombatExtended.ProjectileCE");
				if (type == null) return;

				// do not throw or error if this method does not exist either
				//
				var originalMethodInfo = AccessTools.Method(type, "Launch", new Type[] { typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing) });
				if (originalMethodInfo == null) return;

				var prefix = new HarmonyMethod(null);
				var postfix = new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => PostfixCombatExtended(null, Vector2.zero, 0, 0, 0, 0, null)));
				harmony.Patch(originalMethodInfo, prefix, postfix);
			}
		}

		// patch to allow zombies to occupy the same spot without collision
		// 
		[HarmonyPatch(typeof(PawnCollisionTweenerUtility))]
		[HarmonyPatch("PawnCollisionPosOffsetFor")]
		static class PawnCollisionTweenerUtility_PawnCollisionPosOffsetFor_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref Vector3 __result)
			{
				if (!(pawn is Zombie)) return true;
				__result = Vector3.zero;
				return false;
			}
		}

		// patches so that zombies do not have needs
		// 
		[HarmonyPatch(typeof(Pawn_NeedsTracker))]
		[HarmonyPatch("AllNeeds", MethodType.Getter)]
		static class Pawn_NeedsTracker_AllNeeds_Patch
		{
			static List<Need> Replacement()
			{
				return new List<Need>();
			}

			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var replacement = SymbolExtensions.GetMethodInfo(() => Replacement());
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method, replacement);
				return transpiler(generator, instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_NeedsTracker))]
		[HarmonyPatch("AddOrRemoveNeedsAsAppropriate")]
		static class Pawn_NeedsTracker_AddOrRemoveNeedsAsAppropriate_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method);
				return transpiler(generator, instructions);
			}
		}

		// patches so that zombies have no records
		//
		[HarmonyPatch(typeof(Pawn_RecordsTracker))]
		[HarmonyPatch("AddTo")]
		static class Pawn_RecordsTracker_AddTo_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method);
				return transpiler(generator, instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_RecordsTracker))]
		[HarmonyPatch("Increment")]
		static class Pawn_RecordsTracker_Increment_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method);
				return transpiler(generator, instructions);
			}
		}

		// patch so zombies do not bleed
		//
		[HarmonyPatch(typeof(Pawn_HealthTracker))]
		[HarmonyPatch("DropBloodFilth")]
		static class Pawn_HealthTracker_DropBloodFilth_Patch
		{
			static bool SkipDropBlood(Pawn pawn)
			{
				var zombie = pawn as Zombie;
				if (zombie == null) return false;
				if (ZombieSettings.Values.zombiesDropBlood == false) return true;
				if (zombie.hasTankyShield > 0 || zombie.hasTankySuit > 0) return true;
				return false;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var jump = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_HealthTracker).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => SkipDropBlood(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, jump);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(jump);

				foreach (var instr in list)
					yield return instr;
			}
		}

		// patch to insert our settings page
		//
		[HarmonyPatch(typeof(Scenario))]
		[HarmonyPatch("GetFirstConfigPage")]
		static class Scenario_GetFirstConfigPage_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var found = false;
				foreach (var instruction in instructions)
				{
					var constructorInfo = instruction.operand as ConstructorInfo;
					var constructorName = constructorInfo?.DeclaringType.Name ?? "";

					if (constructorName == "Page_SelectLandingSite" || constructorName == "Page_SelectStartingSite")
					{
						yield return new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(SettingsDialog)));
						yield return new CodeInstruction(OpCodes.Callvirt, typeof(List<Page>).MethodNamed(nameof(List<Page>.Add)));
						yield return new CodeInstruction(OpCodes.Ldloc_0);
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patches to avoid null reference exception
		//
		/*[HarmonyPatch(typeof(ThoughtWorker_ColonistLeftUnburied))]
		[HarmonyPatch("CurrentStateInternal")]
		static class ThoughtWorker_ColonistLeftUnburied_CurrentStateInternal_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var found1 = false;
				var found2 = false;

				var get_InnerPawn = typeof(Corpse).PropertyGetter(nameof(Corpse.InnerPawn));
				var skipLabel = generator.DefineLabel();

				var list = instructions.ToList();
				var skipLoopIndex = list.FirstIndexOf(instr => instr.opcode == OpCodes.Add) - 2;
				if (skipLoopIndex > 0)
				{
					list[skipLoopIndex].labels.Add(skipLabel);
					found1 = true;
				}

				foreach (var instruction in list)
				{
					yield return instruction;
					if (instruction.opcode == OpCodes.Stloc_3)
					{
						yield return new CodeInstruction(OpCodes.Ldloc_3);
						yield return new CodeInstruction(OpCodes.Callvirt, get_InnerPawn);
						yield return new CodeInstruction(OpCodes.Brfalse_S, skipLabel);

						found2 = true;
					}
				}

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}*/
		[HarmonyPatch(typeof(Pawn_HealthTracker))]
		[HarmonyPatch("PreApplyDamage")]
		static class Pawn_HealthTracker_PreApplyDamage_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var m_TryGainMemory = typeof(MemoryThoughtHandler).MethodNamed("TryGainMemory", new Type[] { typeof(ThoughtDef), typeof(Pawn) });
				var f_pawn = typeof(Pawn_HealthTracker).Field("pawn");

				var found1 = false;
				var found2 = false;

				var list = instructions.ToList();
				var jumpIndex = list.FirstIndexOf(instr => instr.operand == m_TryGainMemory) + 1;
				if (jumpIndex > 0)
				{
					var skipLabel = generator.DefineLabel();
					list[jumpIndex].labels.Add(skipLabel);
					found1 = true;

					for (var i = jumpIndex; i >= 0; i--)
						if (list[i].opcode == OpCodes.Ldarg_0)
						{
							var j = i;
							list.Insert(j++, new CodeInstruction(OpCodes.Ldarg_0));
							list.Insert(j++, new CodeInstruction(OpCodes.Ldfld, f_pawn));
							list.Insert(j++, new CodeInstruction(OpCodes.Isinst, typeof(Zombie)));
							list.Insert(j++, new CodeInstruction(OpCodes.Brtrue_S, skipLabel));

							found2 = true;
							break;
						}
				}

				foreach (var instruction in list)
					yield return instruction;

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to add our settings to the main bottom-right menu
		//
		[HarmonyPatch(typeof(MainTabWindow_Menu))]
		[HarmonyPatch("RequestedTabSize", MethodType.Getter)]
		static class MainTabWindow_Menu_RequestedTabSize_Path
		{
			static void Postfix(ref Vector2 __result)
			{
				__result.y += MainMenuDrawer_DoMainMenuControls_Path.addedHeight;
			}
		}
		[HarmonyPatch(typeof(MainTabWindow_Menu))]
		[HarmonyPatch("DoWindowContents")]
		static class MainTabWindow_Menu_DoWindowContents_Path
		{
			static void Prefix(ref Rect rect)
			{
				rect.height += MainMenuDrawer_DoMainMenuControls_Path.addedHeight;
			}
		}
		[HarmonyPatch(typeof(Widgets))]
		[HarmonyPatch("ButtonTextWorker")]
		static class Widgets_ButtonText_Path
		{
			static void NewDrawAtlas(Rect rect, Texture2D atlas, string label)
			{
				Widgets.DrawAtlas(rect, atlas);
				if (label == "Zombieland")
				{
					var texture = Tools.GetZombieButtonBackground();
					GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true, 0f);
				}
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = typeof(Widgets).MethodNamed("DrawAtlas", new Type[] { typeof(Rect), typeof(Texture2D) });
				var to = SymbolExtensions.GetMethodInfo(() => NewDrawAtlas(Rect.zero, null, null));

				var found = false;
				foreach (var instruction in instructions)
				{
					if (instruction.operand == from)
					{
						instruction.operand = to;
						yield return new CodeInstruction(OpCodes.Ldarg_1);
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}
		[HarmonyPatch(typeof(MainMenuDrawer))]
		[HarmonyPatch("DoMainMenuControls")]
		static class MainMenuDrawer_DoMainMenuControls_Path
		{
			// called from MainTabWindow_Menu_RequestedTabSize_Path
			public static float addedHeight = 45f + 7f; // default height ListableOption + OptionListingUtility.DrawOptionListing spacing

			static readonly MethodInfo[] patchMethods = new MethodInfo[] {
				SymbolExtensions.GetMethodInfo(() => DrawOptionListingPatch1(Rect.zero, null)),
				SymbolExtensions.GetMethodInfo(() => DrawOptionListingPatch2(Rect.zero, null))
			};

			static float DrawOptionListingPatch1(Rect rect, List<ListableOption> optList)
			{
				if (Current.ProgramState == ProgramState.Playing)
				{
					var label = "Options".Translate();
					var idx = optList.FirstIndexOf(opt => opt.label == label);
					if (idx > 0) optList.Insert(idx, new ListableOption("Zombieland", delegate
					{
						var dialog = new Dialog_ModSettings();
						var me = LoadedModManager.GetMod<ZombielandMod>();
						GetterSetters.selModByRef(dialog) = me;
						Find.WindowStack.Add(dialog);
					}, null));
				}
				return OptionListingUtility.DrawOptionListing(rect, optList);
			}

			static float DrawOptionListingPatch2(Rect rect, List<ListableOption> optList)
			{
				if (Current.ProgramState == ProgramState.Playing)
				{
					var item = new ListableOption_WebLink("Brrainz", "http://patreon.com/pardeike", Tools.GetMenuIcon());
					optList.Add(item);
				}
				return OptionListingUtility.DrawOptionListing(rect, optList);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_DrawOptionListing = SymbolExtensions.GetMethodInfo(() => OptionListingUtility.DrawOptionListing(Rect.zero, null));

				var counter = 0;
				foreach (var instruction in instructions)
				{
					if (instruction.operand == m_DrawOptionListing)
						instruction.operand = patchMethods[counter++];
					yield return instruction;
				}

				if (counter != 2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}
	}
}