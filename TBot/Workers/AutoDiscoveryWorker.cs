using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	public class AutoDiscoveryWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;

		private sealed class OriginCursor {
			public int System;
			public int NextPosition;
		}
		private readonly Dictionary<string, OriginCursor> _originCursors = new();

		private sealed class DiscoveryRunContext {
			public int FleetsToSend;
			public bool Stop;
			public int Skips;
			public int GlobalAttempts;
			public int MaxGlobalAttempts;
		}

		public AutoDiscoveryWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
		}

		private sealed class CoordinateComparer : IEqualityComparer<Coordinate> {
			public bool Equals(Coordinate x, Coordinate y) {
				if (ReferenceEquals(x, y)) return true;
				if (x is null || y is null) return false;
				return x.Galaxy == y.Galaxy && x.System == y.System && x.Position == y.Position && x.Type == y.Type;
			}

			public int GetHashCode(Coordinate obj) {
				if (obj is null) return 0;
				unchecked {
					int hash = 17;
					hash = hash * 31 + obj.Galaxy.GetHashCode();
					hash = hash * 31 + obj.System.GetHashCode();
					hash = hash * 31 + obj.Position.GetHashCode();
					hash = hash * 31 + obj.Type.GetHashCode();
					return hash;
				}
			}
		}

		private static readonly IEqualityComparer<Coordinate> _coordinateComparer = new CoordinateComparer();

		private void EnsureBlacklistInitialized() {
			if (_tbotInstance?.UserData == null) return;

			if (_tbotInstance.UserData.discoveryBlackList == null) {
				_tbotInstance.UserData.discoveryBlackList = new Dictionary<Coordinate, DateTime>(_coordinateComparer);
				return;
			}

			if (!ReferenceEquals(_tbotInstance.UserData.discoveryBlackList.Comparer, _coordinateComparer)) {
				var old = _tbotInstance.UserData.discoveryBlackList;
				var fresh = new Dictionary<Coordinate, DateTime>(_coordinateComparer);
				foreach (var kv in old) {

					if (fresh.TryGetValue(kv.Key, out var existing)) {
						if (kv.Value > existing) fresh[kv.Key] = kv.Value;
					} else {
						fresh[kv.Key] = kv.Value;
					}
				}
				_tbotInstance.UserData.discoveryBlackList = fresh;
			}
		}

		private void CleanupExpiredBlacklistAll(DateTime now) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return;
			var toRemove = _tbotInstance.UserData.discoveryBlackList
				.Where(kv => kv.Value <= now)
				.Select(kv => kv.Key)
				.ToList();
			foreach (var k in toRemove)
				_tbotInstance.UserData.discoveryBlackList.Remove(k);
		}

		private bool IsBlacklistedAndActive(Coordinate c, DateTime now) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return false;
			return _tbotInstance.UserData.discoveryBlackList.TryGetValue(c, out var until) && until > now;
		}

		private void UpsertBlacklist(Coordinate c, DateTime until) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return;
			_tbotInstance.UserData.discoveryBlackList[c] = until;
		}

		private List<Celestial> GetConfiguredOrigins(List<Celestial> celestials) {
			var result = new List<Celestial>();
			if (celestials == null || celestials.Count == 0) return result;

			var originsCfg = _tbotInstance?.InstanceSettings?.AutoDiscovery?.Origin;
			if (originsCfg == null) return result;

			foreach (var o in originsCfg) {
				int galaxy = Convert.ToInt32(o.Galaxy);
				int system = Convert.ToInt32(o.System);
				int position = Convert.ToInt32(o.Position);
				string typeStr = Convert.ToString(o.Type) ?? "Planet";

				Celestials wantedType = Celestials.Planet;
				if (Enum.TryParse(typeStr, true, out Celestials parsed))
					wantedType = parsed;

				var found = celestials.FirstOrDefault(c =>
					c?.Coordinate != null &&
					c.Coordinate.Galaxy == galaxy &&
					c.Coordinate.System == system &&
					c.Coordinate.Position == position &&
					c.Coordinate.Type == wantedType
				);

				if (found != null)
					result.Add(found);
				else
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoDiscovery,
						$"AutoDiscovery origin not found in user celestials: {galaxy}:{system}:{position} ({typeStr})");
			}

			return result;
		}

		private string OriginKey(Celestial origin) {

			return $"{origin.Coordinate.Galaxy}:{origin.Coordinate.System}:{origin.Coordinate.Position}:{origin.Coordinate.Type}";
		}

		private OriginCursor GetOrInitCursor(Celestial origin) {
			var key = OriginKey(origin);
			if (!_originCursors.TryGetValue(key, out var cursor) || cursor == null) {
				cursor = new OriginCursor {
					System = origin.Coordinate.System,
					NextPosition = 1
				};
				_originCursors[key] = cursor;
			}

			int maxSystem = _tbotInstance.UserData.serverData.Systems;
			if (cursor.System < 1 || cursor.System > maxSystem) cursor.System = origin.Coordinate.System;
			if (cursor.NextPosition < 1 || cursor.NextPosition > 15) cursor.NextPosition = 1;

			return cursor;
		}

		private int AdvanceSystem(int system) {
			int maxSystem = _tbotInstance.UserData.serverData.Systems;
			system++;
			if (system > maxSystem) system = 1;
			return system;
		}

		private async Task<int> ProcessOneSystemForOrigin(
			Celestial origin,
			OriginCursor cursor,
			int discoveries,
			DiscoveryRunContext ctx) {

			if (origin?.Coordinate == null) return discoveries;
			if (discoveries <= 0 || ctx.FleetsToSend <= 0 || ctx.Stop) return discoveries;

			int systemToDo = cursor.System;

			DateTime now = await _tbotOgameBridge.GetDateTime();
			int resumeNextPos = cursor.NextPosition;
			for (int pos = cursor.NextPosition; pos <= 15; pos++) {
				resumeNextPos = pos;

				if (discoveries <= 0 || ctx.FleetsToSend <= 0 || ctx.Stop) break;
				if (_tbotInstance.UserData.slots.Free <= (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree) { ctx.Stop = true; break; }

				if (++ctx.GlobalAttempts > ctx.MaxGlobalAttempts) { ctx.Stop = true; break; }

				var dest = new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = systemToDo,
					Position = pos,
					Type = Celestials.Planet
				};

				if (IsBlacklistedAndActive(dest, now)) { ctx.Skips++; continue; }

				var ok = await _ogameService.SendDiscovery(origin, dest);
				if (!ok) {
					DoLog(LogLevel.Warning, $"Failed to send discovery fleet to {dest} from {origin}.");
					UpsertBlacklist(dest, now.AddDays(7));
				} else {
					DoLog(LogLevel.Information, $"Discovery fleet sent to {dest} from {origin}.");
					UpsertBlacklist(dest, now.AddDays(7));
					discoveries--;
					ctx.FleetsToSend--;
				}

				resumeNextPos = pos + 1;

				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
			}

			if (!ctx.Stop && resumeNextPos > 15 && discoveries > 0 && ctx.FleetsToSend > 0 &&
				_tbotInstance.UserData.slots.Free > (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {

				cursor.System = AdvanceSystem(systemToDo);
				cursor.NextPosition = 1;
			} else {

				if (resumeNextPos < 1) resumeNextPos = 1;
				if (resumeNextPos > 15) resumeNextPos = 15;
				cursor.NextPosition = resumeNextPos;
			}

			return discoveries;
		}


		private long CalcFallbackIntervalMs() {
			return RandomizeHelper.CalcRandomInterval(
				(int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin,
				(int)_tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax
			);
		}

		private long CalcNextIntervalFromDiscoveryFleetsOrFallback() {
			var discFleet = _tbotInstance.UserData.fleets
				.Where(f => f.Mission == Missions.Discovery && f.BackIn.HasValue)
				.OrderBy(f => f.BackIn.Value)
				.FirstOrDefault();

			if (discFleet == null) return CalcFallbackIntervalMs();

			long backInSec = discFleet.BackIn ?? 0;
			long interval = backInSec * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
			if (interval <= 0) interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
			return interval;
		}

		protected override async Task Execute() {
			bool delay = false;
			bool stop = false;
			int skips = 0;

			try {
				DoLog(LogLevel.Information, $"Starting AutoDiscovery...");

				EnsureBlacklistInitialized();

				if (_tbotInstance.UserData.isSleeping) {
					stop = true;
					return;
				}

				if ((bool)_tbotInstance.InstanceSettings.SleepMode.Active) {
					DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.GoToSleep, out DateTime goToSleep);
					DateTime.TryParse((string)_tbotInstance.InstanceSettings.SleepMode.WakeUp, out DateTime wakeUp);
					DateTime timeSleep = await _tbotOgameBridge.GetDateTime();
					if (GeneralHelper.ShouldSleep(timeSleep, goToSleep, wakeUp)) {
						DoLog(LogLevel.Warning, "Unable to send discovery fleet: bed time has passed");
						stop = true;
						return;
					}
				}

				DateTime now = await _tbotOgameBridge.GetDateTime();
				CleanupExpiredBlacklistAll(now);

				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				List<RankSlotsPriority> rankSlotsPriority = new() {
					new RankSlotsPriority(Feature.BrainAutoMine, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
						((bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active &&
						((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
						 (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
						(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)
					),
					new RankSlotsPriority(Feature.Expeditions, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
						(bool)_tbotInstance.InstanceSettings.Expeditions.Active,
						(int)_tbotInstance.UserData.slots.ExpTotal,
						(int)_tbotInstance.UserData.slots.ExpInUse
					),
					new RankSlotsPriority(Feature.AutoFarm, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
						(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
						(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)
					),
					new RankSlotsPriority(Feature.Colonize, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
						(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ? (int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)
					),
					new RankSlotsPriority(Feature.AutoDiscovery, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
						(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
						(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)
					),
					new RankSlotsPriority(Feature.Harvest, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
						(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
						(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
						(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest)
					)
				};

				int fleetsToSend = _calculationService.CalcSlotsPriority(
					Feature.AutoDiscovery, rankSlotsPriority,
					_tbotInstance.UserData.slots, _tbotInstance.UserData.fleets,
					(int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree
				);

				if (fleetsToSend <= 0) {
					delay = true;
					return;
				}

				var celestials = await _ogameService.GetCelestials();
				var origins = GetConfiguredOrigins(celestials);

				if (origins == null || origins.Count == 0) {
					stop = true;
					DoLog(LogLevel.Warning, "Unable to parse AutoDiscovery origin.");
					return;
				}

				bool hasPathfinder = false;
				foreach (var origin in origins) {
					if (origin == null) continue;
					var ships = await _ogameService.GetShips(origin);
					if (ships != null && ships.Pathfinder > 0) {
						hasPathfinder = true;
						break;
					}
				}
				if (!hasPathfinder) {
					DoLog(LogLevel.Information, "No Pathfinder on discovery origins; skipping send attempts until one is available.");
					delay = true;
					return;
				}

				var ctx = new DiscoveryRunContext {
					FleetsToSend = fleetsToSend,
					Stop = false,
					Skips = 0,
					GlobalAttempts = 0,
					MaxGlobalAttempts = 600
				};
				
				foreach (var origin in origins) {
					if (ctx.Stop) break;
					if (ctx.FleetsToSend <= 0) break;
					if (origin?.Coordinate == null) continue;

					int discoveries = await _ogameService.GetAvailableDiscoveries(origin);
					if (discoveries <= 0) {
						DoLog(LogLevel.Information, $"No discoveries available from {origin} right now.");
						continue;
					}

					var cursor = GetOrInitCursor(origin);
					DoLog(LogLevel.Information, $"Origin {origin} cursor system={cursor.System}, nextPos={cursor.NextPosition} (discoveries={discoveries})");

					discoveries = await ProcessOneSystemForOrigin(origin, cursor, discoveries, ctx);

					fleetsToSend = ctx.FleetsToSend;
					stop = ctx.Stop;
					skips = ctx.Skips;
				}

				if (skips > 0)
					DoLog(LogLevel.Information, $"{skips} positions skipped (blacklisted)");

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				long interval = CalcNextIntervalFromDiscoveryFleetsOrFallback();
				DateTime time = await _tbotOgameBridge.GetDateTime();
				var nextTime = time.AddMilliseconds(interval);

				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next AutoDiscovery check at {nextTime.ToString()}");
				await _tbotOgameBridge.CheckCelestials();
		    	} catch (Exception e) {
				DoLog(LogLevel.Error, $"An error occured: {e.Message}");
				DoLog(LogLevel.Debug, e.StackTrace);
		    	} finally {
				if (stop) {
					DoLog(LogLevel.Information, $"Stopping feature.");
					await EndExecution();
				}

				if (delay) {
					DoLog(LogLevel.Information, $"Delaying...");
					var timeDelay = await _tbotOgameBridge.GetDateTime();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

					long intervalDelay = CalcNextIntervalFromDiscoveryFleetsOrFallback();
					var newTimeDelay = timeDelay.AddMilliseconds(intervalDelay);

					ChangeWorkerPeriod(intervalDelay);
					DoLog(LogLevel.Information, $"Next AutoDiscovery check at {newTimeDelay.ToString()}");
				}

				await _tbotOgameBridge.CheckCelestials();
			}
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active;
			} catch (Exception) {
				return false;
			}
		}

		public override string GetWorkerName() {
			return "AutoDiscovery";
		}
		public override Feature GetFeature() {
			return Feature.AutoDiscovery;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoDiscovery;
		}
	}
}
