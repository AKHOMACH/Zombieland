﻿using Harmony;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ZombieLand
{
	public class ZombieRequest
	{
		public Map map;
		public IntVec3 cell;
		public Zombie zombie;
		public bool isEvent;
	}

	[StaticConstructorOnStartup]
	public class ZombieGenerator
	{
		ConcurrentQueue<ZombieRequest> requestQueue;
		Dictionary<Map, ConcurrentQueue<ZombieRequest>> resultQueues;

		Thread workerThread;

		public ConcurrentQueue<ZombieRequest> QueueForMap(Map map)
		{
			if (resultQueues.TryGetValue(map, out var queue) == false)
			{
				queue = new ConcurrentQueue<ZombieRequest>(true);
				resultQueues.Add(map, queue);
			}
			return queue;
		}

		public ZombieGenerator()
		{
			requestQueue = new ConcurrentQueue<ZombieRequest>();
			resultQueues = new Dictionary<Map, ConcurrentQueue<ZombieRequest>>();

			workerThread = new Thread(() =>
			{
				EndlessLoop:

				try
				{
					var request = requestQueue.Dequeue();
					if (request.zombie == null)
						request.zombie = GeneratePawn(ZombieType.Random);
					if (request.zombie != null)
					{
						var queue = QueueForMap(request.map);
						queue.Enqueue(request);
					}
				}
				catch (Exception e)
				{
					Log.Warning("ZombieGenerator thread error: " + e);
					Thread.Sleep(500);
				}

				goto EndlessLoop;
			})
			{
				Priority = System.Threading.ThreadPriority.Lowest
			};
			workerThread.Start();
		}

		public int ZombiesQueued(Map map)
		{
			var queue = QueueForMap(map);
			return requestQueue.Count(q => q.map == map) + queue.Count();
		}

		public void SpawnZombieAt(Map map, IntVec3 cell, bool isEvent)
		{
			requestQueue.Enqueue(new ZombieRequest() { map = map, cell = cell, isEvent = isEvent, zombie = null });
		}

		public ZombieRequest TryGetNextGeneratedZombie(Map map)
		{
			var queue = QueueForMap(map);
			return queue.Dequeue();
		}

		public void RequeueZombie(ZombieRequest request)
		{
			var queue = QueueForMap(request.map);
			queue.Enqueue(request);
		}

		static Color HairColor()
		{
			var num3 = Rand.Value;
			if (num3 < 0.25f)
				return new Color(0.2f, 0.2f, 0.2f);

			if (num3 < 0.5f)
				return new Color(0.31f, 0.28f, 0.26f);

			if (num3 < 0.75f)
				return new Color(0.25f, 0.2f, 0.15f);

			return new Color(0.3f, 0.2f, 0.1f);
		}

		static readonly Dictionary<string, IntVec2> eyeOffsets = new Dictionary<string, IntVec2>() {
			{ "Female_Average_Normal", new IntVec2(11, -5) },
			{ "Female_Average_Pointy", new IntVec2(11, -5) },
			{ "Female_Average_Wide", new IntVec2(11, -6) },
			{ "Female_Narrow_Normal", new IntVec2(10, -7) },
			{ "Female_Narrow_Pointy", new IntVec2(8, -8) },
			{ "Female_Narrow_Wide", new IntVec2(9, -8) },
			{ "Male_Average_Normal", new IntVec2(15, -7) },
			{ "Male_Average_Pointy", new IntVec2(14, -6) },
			{ "Male_Average_Wide", new IntVec2(15, -7) },
			{ "Male_Narrow_Normal", new IntVec2(9, -8) },
			{ "Male_Narrow_Pointy", new IntVec2(8, -8) },
			{ "Male_Narrow_Wide", new IntVec2(10, -8) }
		};
		static IntVec2 SideEyeOffset(string headPath)
		{
			return eyeOffsets[headPath];
		}

		public static Zombie GeneratePawn(ZombieType overwriteType)
		{
			var thing = ThingMaker.MakeThing(ZombieDefOf.Zombie.race, null);
			var zombie = thing as Zombie;
			if (zombie == null)
			{
				Log.Error("ThingMaker.MakeThing(ZombieDefOf.Zombie.race, null) unexpectedly returned " + thing);
				return null;
			}

			var bodyType = PrepareZombieType(zombie, overwriteType);

			zombie.kindDef = ZombieDefOf.Zombie;
			zombie.SetFactionDirect(FactionUtility.DefaultFactionFrom(ZombieDefOf.Zombies));

			PawnComponentsUtility.CreateInitialComponents(zombie);
			zombie.health.hediffSet.Clear();

			var ageInYears = (long)Rand.Range(14, 130);
			zombie.ageTracker.AgeBiologicalTicks = (ageInYears * 3600000);
			zombie.ageTracker.AgeChronologicalTicks = zombie.ageTracker.AgeBiologicalTicks;
			zombie.ageTracker.BirthAbsTicks = GenTicks.TicksAbs - zombie.ageTracker.AgeBiologicalTicks;
			var idx = zombie.ageTracker.CurLifeStageIndex; // trigger calculations

			zombie.needs.SetInitialLevels();
			zombie.needs.mood = new Need_Mood(zombie);

			var name = PawnNameDatabaseSolid.GetListForGender((zombie.gender == Gender.Female) ? GenderPossibility.Female : GenderPossibility.Male).RandomElement();
			var n1 = name.First.Replace('s', 'z').Replace('S', 'Z');
			var n2 = name.Last.Replace('s', 'z').Replace('S', 'Z');
			var n3 = name.Nick.Replace('s', 'z').Replace('S', 'Z');
			zombie.Name = new NameTriple(n1, n3, n2);

			zombie.story.childhood = BackstoryDatabase.allBackstories
				.Where(kvp => kvp.Value.slot == BackstorySlot.Childhood)
				.RandomElement().Value;
			if (zombie.ageTracker.AgeBiologicalYearsFloat >= 20f)
				zombie.story.adulthood = BackstoryDatabase.allBackstories
				.Where(kvp => kvp.Value.slot == BackstorySlot.Adulthood)
				.RandomElement().Value;

			zombie.story.melanin = 0.01f * Rand.Range(10, 91);
			zombie.story.bodyType = bodyType;
			zombie.story.crownType = Rand.Bool ? CrownType.Average : CrownType.Narrow;

			zombie.story.hairColor = HairColor();
			zombie.story.hairDef = PawnHairChooser.RandomHairDefFor(zombie, ZombieDefOf.Zombies);

			if (ZombieSettings.Values.useCustomTextures)
				AssignNewCustomGraphics(zombie);

			zombie.Drawer.leaner = new ZombieLeaner(zombie);

			if (zombie.pather == null)
				zombie.pather = new Pawn_PathFollower(zombie);
			GetterSetters.destinationByRef(zombie.pather) = IntVec3.Invalid;

			return zombie;
		}

		public enum ZombieType
		{
			Random = -1,
			SuicideBomber = 0,
			ToxicSplasher = 1,
			TankyOperator = 2,
			Normal = 3
		}

		private static BodyTypeDef PrepareZombieType(Zombie zombie, ZombieType overwriteType)
		{
			var zombieTypeInitializers = new Pair<float, Func<BodyTypeDef>>[]
			{
				// suicide bomber
				new Pair<float, Func<BodyTypeDef>>(
					ZombieSettings.Values.suicideBomberChance / 2f,
					delegate
					{
						zombie.bombTickingInterval = 60f;
						zombie.lastBombTick = Find.TickManager.TicksAbs + Rand.Range(0, (int)zombie.bombTickingInterval);
						//
						zombie.gender = Gender.Male;
						return BodyTypeDefOf.Hulk;
					}
				),

				// toxic splasher
				new Pair<float, Func<BodyTypeDef>>(
					ZombieSettings.Values.toxicSplasherChance / 2f,
					delegate
					{
						zombie.isToxicSplasher = true;
						//
						switch (Rand.RangeInclusive(1, 3))
						{
							case 1:
								zombie.gender = Gender.Male;
								return BodyTypeDefOf.Male;
							case 2:
								zombie.gender = Gender.Female;
								return BodyTypeDefOf.Female;
							case 3:
								zombie.gender = Gender.Male;
								return BodyTypeDefOf.Thin;
						}
						return null;
					}
				),

				// tanky operator
				new Pair<float, Func<BodyTypeDef>>(
					ZombieSettings.Values.tankyOperatorChance / 3f,
					delegate
					{
						zombie.hasTankyShield = 1f;
						zombie.hasTankyHelmet = 1f;
						zombie.hasTankySuit = 1f;
						//
						zombie.gender = Gender.Male;
						return BodyTypeDefOf.Fat;
					}
				),

				// default ordinary zombie
				new Pair<float, Func<BodyTypeDef>>(
					float.MaxValue,
					delegate
					{
						switch (Rand.RangeInclusive(1, 4))
						{
							case 1:
								zombie.gender = Gender.Male;
								return BodyTypeDefOf.Male;
							case 2:
								zombie.gender = Gender.Female;
								return BodyTypeDefOf.Female;
							case 3:
								zombie.gender = Gender.Male;
								return BodyTypeDefOf.Thin;
							case 4:
								zombie.gender = Gender.Male;
								return BodyTypeDefOf.Fat;
						}
						return null;
					}
				)
			};

			if (overwriteType != ZombieType.Random)
			{
				var initializer = zombieTypeInitializers[(int)overwriteType];
				return initializer.Second();
			}

			var typeChance = Rand.Value;
			BodyTypeDef bodyType = null;
			foreach (var initializer in zombieTypeInitializers)
			{
				typeChance -= initializer.First;
				if (typeChance < 0f)
				{
					bodyType = initializer.Second();
					break;
				}
			}
			return bodyType;
		}

		static readonly string[] headShapes = { "Normal", "Pointy", "Wide" };
		public static void AssignNewCustomGraphics(Zombie zombie)
		{
			var renderPrecedence = 0;

			var bodyPath = "Zombie/Naked_" + zombie.story.bodyType.ToString();
			var color = zombie.isToxicSplasher ? "toxic" : GraphicToolbox.RandomSkinColorString();
			var bodyRequest = new GraphicRequest(typeof(VariableGraphic), bodyPath, ShaderDatabase.CutoutSkin, Vector2.one, Color.white, Color.white, null, renderPrecedence, new List<ShaderParameter>());
			zombie.customBodyGraphic = new VariableGraphic { bodyColor = color };
			zombie.customBodyGraphic.Init(bodyRequest);

			var headShape = zombie.hasTankyHelmet == 1f ? "Wide" : headShapes[Rand.Range(0, 3)];
			var headPath = "Zombie/" + zombie.gender + "_" + zombie.story.crownType + "_" + headShape;
			var headRequest = new GraphicRequest(typeof(VariableGraphic), headPath, ShaderDatabase.CutoutSkin, Vector2.one, Color.white, Color.white, null, renderPrecedence, new List<ShaderParameter>());
			zombie.customHeadGraphic = new VariableGraphic { bodyColor = color };
			zombie.customHeadGraphic.Init(headRequest);

			zombie.sideEyeOffset = SideEyeOffset(headPath.ReplaceFirst("Zombie/", ""));
		}

		public static void FinalizeZombieGeneration(Zombie zombie)
		{
			var graphicPath = GraphicDatabaseHeadRecords.GetHeadRandom(zombie.gender, zombie.story.SkinColor, zombie.story.crownType).GraphicPath;
			GetterSetters.headGraphicPathByRef(zombie.story) = graphicPath;

			var request = new PawnGenerationRequest(zombie.kindDef);
			PawnApparelGenerator.GenerateStartingApparelFor(zombie, request);
			zombie.apparel.WornApparel.Do(apparel =>
			{
				Color[] colors =
				{
					"442a0a".HexColor(),
					"615951".HexColor(),
					"1f4960".HexColor(),
					"182a64".HexColor(),
					"73000d".HexColor(),
					"2c422a".HexColor(),
					"332341".HexColor()
				};
				(colors.Clone() as Color[]).Do(c =>
				{
					c.r *= Rand.Range(0.2f, 1f);
					c.g *= Rand.Range(0.2f, 1f);
					c.b *= Rand.Range(0.2f, 1f);
					colors.Add(c);
				});
				colors.Add("000000".HexColor());

				apparel.SetColor(colors[Rand.Range(0, colors.Length)]);
			});
		}
	}
}