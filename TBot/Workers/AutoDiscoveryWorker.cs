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

		// Cursor state per origin (persists across Execute() cycles as long as this worker instance lives)
		private sealed class OriginCursor {
			public int System;
			public int NextPosition; // 1..15
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

		// -------------------------
		// Blacklist helpers (fix keying issues with Coordinate)
		// -------------------------
		private Coordinate FindBlacklistedKey(Coordinate c) {
			if (_tbotInstance?.UserData?.discoveryBlackList == null) return null;
			return _tbotInstance.UserData.discoveryBlackList.Keys
				.FirstOrDefault(k => k.Galaxy == c.Galaxy && k.System == c.System && k.Position == c.Position);
		}

		private bool IsBlacklistedAndActive(Coordinate c) {
			var key = FindBlacklistedKey(c);
			if (key == null) return false;
			return _tbotInstance.UserData.discoveryBlackList[key] > DateTime.Now;
		}

		private void CleanupExpiredBlacklist(Coordinate c) {
			var key = FindBlacklistedKey(c);
			if (key == null) return;
			if (_tbotInstance.UserData.discoveryBlackList[key] <= DateTime.Now) {
				_tbotInstance.UserData.discoveryBlackList.Remove(key);
			}
		}

		private void UpsertBlacklist(Coordinate c, DateTime until) {
			var key = FindBlacklistedKey(c);
			if (key != null) _tbotInstance.UserData.discoveryBlackList.Remove(key);
			_tbotInstance.UserData.discoveryBlackList[c] = until; // safe upsert
		}

		// -------------------------
		// Origins from settings (multiple)
		// -------------------------
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
			// Key by the origin's own coordinate (moon/planet origin), so each gets its own cursor
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

			// Safety clamps
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

		/// <summary>
		/// Sends (tries to process) one whole target system (positions 1..15) for a given origin.
		/// Behavior you requested:
		/// - Origin A does System X (1..15) then cursor for A moves to X+1
		/// - Then Origin B does its current System Y (1..15) then moves to Y+1
		/// - etc...
		/// </summary>
		private async Task<int> ProcessOneSystemForOrigin(
			Celestial origin,
			OriginCursor cursor,
			int discoveries,
			DiscoveryRunContext ctx) {

			if (origin?.Coordinate == null) return discoveries;
			if (discoveries <= 0 || ctx.FleetsToSend <= 0 || ctx.Stop) return discoveries;

			// If cursor.NextPosition isn't 1 (e.g. mid-system from previous cycle), continue from there
			int systemToDo = cursor.System;

			for (int pos = cursor.NextPosition; pos <= 15; pos++) {
				if (discoveries <= 0 || ctx.FleetsToSend <= 0 || ctx.Stop) break;
				if (_tbotInstance.UserData.slots.Free <= (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree) { ctx.Stop = true; break; }

				if (++ctx.GlobalAttempts > ctx.MaxGlobalAttempts) { ctx.Stop = true; break; }

				var dest = new Coordinate {
					Galaxy = origin.Coordinate.Galaxy,
					System = systemToDo,
					Position = pos,
					Type = Celestials.Planet
				};

				CleanupExpiredBlacklist(dest);
				if (IsBlacklistedAndActive(dest)) { ctx.Skips++; continue; }

				var ok = await _ogameService.SendDiscovery(origin, dest);
				if (!ok) {
					DoLog(LogLevel.Warning, $"Failed to send discovery fleet to {dest} from {origin}.");
					UpsertBlacklist(dest, DateTime.Now.AddDays(7));
				} else {
					DoLog(LogLevel.Information, $"Discovery fleet sent to {dest} from {origin}.");
					UpsertBlacklist(dest, DateTime.Now.AddDays(7));
					discoveries--;
					ctx.FleetsToSend--;
				}

				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
			}

			// Finished this system (or we got stopped). If we completed loop to 15, advance system and reset position.
			if (!ctx.Stop && discoveries > 0 && ctx.FleetsToSend > 0 && _tbotInstance.UserData.slots.Free > (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
				cursor.System = AdvanceSystem(systemToDo);
				cursor.NextPosition = 1;
			} else {
				cursor.NextPosition = 1;
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

				if (_tbotInstance.UserData.discoveryBlackList == null) {
					_tbotInstance.UserData.discoveryBlackList = new Dictionary<Coordinate, DateTime>();
				}

				if (_tbotInstance.UserData.isSleeping) {
					stop = true;
					return;
				}

				// SleepMode check
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

				var ctx = new DiscoveryRunContext {
					FleetsToSend = fleetsToSend,
					Stop = false,
					Skips = 0,
					GlobalAttempts = 0,
					MaxGlobalAttempts = 600 // higher because we loop multiple origins/systems
				};
				// NOTE: We ignore RandomizeDestination here because you requested sequential system walking.
				// If you want random later, we can merge both modes.

				// Main behavior requested:
				// Origin list in order: 37 -> 203 -> 360 -> 438 -> G2:95
				// For each origin: do current cursor system positions 1..15, then cursor system++
				// Then next origin... (repeat each worker cycle)
				foreach (var origin in origins) {
					if (ctx.Stop) break;
					if (ctx.FleetsToSend <= 0) break;
					if (origin?.Coordinate == null) continue;

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
var fleets = _tbotInstance.UserData.fleets ?? new List<Fleet>();

DateTime now = await _tbotOgameBridge.GetDateTime();

int activeDiscovery = fleets.Count(f =>
    f != null &&
    f.Origin != null &&
    f.Mission == Missions.Discovery &&
    f.ArrivalTime > now &&
    f.Origin.Galaxy == origin.Coordinate.Galaxy &&
    f.Origin.System == origin.Coordinate.System &&
    f.Origin.Position == origin.Coordinate.Position &&
    f.Origin.Type == origin.Coordinate.Type
);

int maxDiscoveryFromSettings;
try {
    maxDiscoveryFromSettings = (int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots;
} catch {
    maxDiscoveryFromSettings = 0;
}

int discoveries = Math.Max(0, maxDiscoveryFromSettings - activeDiscovery);

if (discoveries <= 0) {
    DoLog(LogLevel.Information, $"No discoveries available from {origin} right now.");
    continue;
}

					var cursor = GetOrInitCursor(origin);
					DoLog(LogLevel.Information, $"Origin {origin} cursor system={cursor.System}, nextPos={cursor.NextPosition} (discoveries={discoveries})");

					discoveries = await ProcessOneSystemForOrigin(origin, cursor, discoveries, ctx);

					// Sync local variables for the rest of the method
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
