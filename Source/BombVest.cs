﻿using Harmony;
using Verse;

namespace ZombieLand
{
	[StaticConstructorOnStartup]
	class Explosion
	{
		readonly Map map;
		readonly IntVec3 pos;

		public Explosion(Map map, IntVec3 pos)
		{
			this.map = map;
			this.pos = pos;
		}

		public void Explode()
		{
			var damageDef = new SuicideBombDamage();
			var radius = 1f + Find.Storyteller.difficulty.difficulty;
			GenExplosion.DoExplosion(pos, map, radius, damageDef, null);
		}
	}

	class SuicideBombDamage : DamageDef
	{
		static int ScaledValueBetween(int a, int b)
		{
			var n = Find.Storyteller.difficulty.difficulty;
			return (int)GenMath.LerpDouble(0, 5, a, b, n);
		}

		public SuicideBombDamage()
		{
			var baseDef = CustomDefs.SuicideBomb;
			Traverse.IterateFields(baseDef, this, Traverse.CopyFields);

			defaultDamage = ScaledValueBetween(8, 120);
			var damageFactor = ScaledValueBetween(10, 320);
			var trv = Traverse.Create(this);
			trv.Field("buildingDamageFactor").SetValue(damageFactor);
			trv.Field("explosionBuildingDamageFactor").SetValue(damageFactor);
			explosionHeatEnergyPerCell = ScaledValueBetween(8, 128);
		}
	}
}