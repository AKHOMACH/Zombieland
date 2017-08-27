﻿using RimWorld;
using System;
using System.Collections.Generic;
using System.Threading;
using Verse;

namespace ZombieLand
{
	public class ZombieCostSpecs
	{
		public IntVec3 position;
		public float radius;
		public float maxCosts;
	}

	public class AvoidGrid
	{
		private int[][] costGrids;
		private int idx;
		private int mapSize;
		public FloodFiller filler;

		public AvoidGrid(Map map)
		{
			mapSize = map.Size.x * map.Size.z;
			costGrids = new int[][] { new int[mapSize], new int[mapSize] };
			idx = 0;
			filler = new FloodFiller(map);
		}

		public int[] GetCosts()
		{
			return costGrids[idx];
		}

		public int[] GetNewCosts()
		{
			return costGrids[1 - idx];
		}

		public void FinalizeCosts()
		{
			var oldArray = costGrids[idx];
			idx = 1 - idx;

			for (var i = 0; i < mapSize; i++)
				oldArray[i] = 0;
		}
	}

	class AvoidRequest
	{
		public Map map;
		public List<ZombieCostSpecs> specs;
	}

	[StaticConstructorOnStartup]
	public class ZombieAvoider
	{
		ConcurrentQueue<AvoidRequest> requestQueue;
		Dictionary<Map, ConcurrentQueue<AvoidGrid>> resultQueues;
		Dictionary<Map, AvoidGrid> grids;

		Thread workerThread;

		private ConcurrentQueue<AvoidGrid> QueueForMap(Map map)
		{
			ConcurrentQueue<AvoidGrid> queue;
			if (resultQueues.TryGetValue(map, out queue) == false)
			{
				queue = new ConcurrentQueue<AvoidGrid>(true);
				resultQueues.Add(map, queue);
			}
			return queue;
		}

		public ZombieAvoider()
		{
			requestQueue = new ConcurrentQueue<AvoidRequest>();
			resultQueues = new Dictionary<Map, ConcurrentQueue<AvoidGrid>>();
			grids = new Dictionary<Map, AvoidGrid>();

			workerThread = new Thread(() =>
			{
				EndlessLoop:

				try
				{
					var request = requestQueue.Dequeue();
					var result = ProcessRequest(request);

					var queue = QueueForMap(request.map);
					queue.Enqueue(result);
				}
				catch (Exception e)
				{
					Log.Warning("ZombieAvoider thread error: " + e);
					Thread.Sleep(500);
				}

				goto EndlessLoop;
			});

			workerThread.Priority = ThreadPriority.Lowest;
			workerThread.Start();
		}

		public void UpdateZombiePositions(Map map, List<ZombieCostSpecs> specs)
		{
			var request = new AvoidRequest() { map = map, specs = specs };
			requestQueue.Enqueue(request, req => req.map == map);
		}

		public AvoidGrid UpdateZombiePositionsImmediately(Map map, List<ZombieCostSpecs> specs)
		{
			return ProcessRequest(new AvoidRequest() { map = map, specs = specs });
		}

		public AvoidGrid GetCostsGrid(Map map)
		{
			var queue = QueueForMap(map);
			return queue.Dequeue();
		}

		private void GenerateCells(Map map, List<ZombieCostSpecs> specs, int[] costCells, FloodFiller filler)
		{
			var mapSizeX = map.Size.x;
			var pathGrid = map.pathGrid;
			var cardinals = GenAdj.CardinalDirections;

			foreach (var spec in specs)
			{
				var loc = spec.position;
				var costBase = spec.maxCosts;
				var radiusSquared = spec.radius * spec.radius;

				var floodedCells = new Dictionary<IntVec3, int>();
				filler.FloodFill(loc,
					cell =>
						(loc - cell).LengthHorizontalSquared <= radiusSquared
						&& pathGrid.Walkable(cell)
						&& (cell.GetEdifice(map) is Building_Door) == false,
					cell =>
					{
						var f = 1f - (loc - cell).LengthHorizontalSquared / radiusSquared;
						var cost = (int)(costBase * f);
						var idx = cell.x + cell.z * mapSizeX;
						costCells[idx] = Math.Max(costCells[idx], cost);
						floodedCells[cell] = costCells[idx];
					});

				foreach (var cell in floodedCells.Keys)
					for (var i = 0; i < 3; i++)
					{
						var pos = cell + cardinals[i];
						if (floodedCells.ContainsKey(pos) == false
							&& (loc - cell).LengthHorizontalSquared <= radiusSquared
							&& pos.InBounds(map) && pos.GetEdifice(map) is Building_Door)
						{
							costCells[pos.x + pos.z * mapSizeX] = floodedCells[cell];
						}
					}
			}
		}

		private AvoidGrid ProcessRequest(AvoidRequest request)
		{
			var avoidGrid = GetAvoidGrid(request.map);
			GenerateCells(request.map, request.specs, avoidGrid.GetNewCosts(), avoidGrid.filler);
			avoidGrid.FinalizeCosts();
			return avoidGrid;
		}

		private AvoidGrid GetAvoidGrid(Map map)
		{
			AvoidGrid result;
			if (grids.TryGetValue(map, out result) == false)
			{
				result = new AvoidGrid(map);
				grids[map] = result;
			}
			return result;
		}
	}
}