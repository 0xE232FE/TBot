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

		protected override async Task Execute() {
			bool delay = false;
			bool stop = false;
			int skips = 0;
			var rand = new Random();
			try {
				DoLog(LogLevel.Information, $"Starting AutoDiscovery...");
				if (_tbotInstance.UserData.discoveryBlackList == null) {
					_tbotInstance.UserData.discoveryBlackList = new Dictionary<Coordinate, DateTime>();
				}
				if (!_tbotInstance.UserData.isSleeping) {
					if ((bool) _tbotInstance.InstanceSettings.SleepMode.Active) {
						DateTime.TryParse((string) _tbotInstance.InstanceSettings.SleepMode.GoToSleep, out DateTime goToSleep);
						DateTime.TryParse((string) _tbotInstance.InstanceSettings.SleepMode.WakeUp, out DateTime wakeUp);
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
						new RankSlotsPriority(Feature.BrainAutoMine, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain, ((bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active && ((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active || (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)), (int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots, (int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
						new RankSlotsPriority(Feature.Expeditions, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions, (bool)_tbotInstance.InstanceSettings.Expeditions.Active, (int)_tbotInstance.UserData.slots.ExpTotal, (int)_tbotInstance.UserData.slots.ExpInUse),
						new RankSlotsPriority(Feature.AutoFarm, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm, (bool)_tbotInstance.InstanceSettings.AutoFarm.Active, (int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots, (int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)),
						new RankSlotsPriority(Feature.Colonize, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize, (bool)_tbotInstance.InstanceSettings.AutoColonize.Active, (bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ? (int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1, (int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
						new RankSlotsPriority(Feature.AutoDiscovery, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery, (bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active, (int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots, (int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
						new RankSlotsPriority(Feature.Harvest, (int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest, (bool)_tbotInstance.InstanceSettings.AutoHarvest.Active, (int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots, (int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
					};
					int fleetsToSend = _calculationService.CalcSlotsPriority(Feature.AutoDiscovery, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

					if (fleetsToSend <= 0) {
						delay = true;
						return;
					}

					var celestials = await _ogameService.GetCelestials();
					var configureddestinationCoord = _tbotInstance.InstanceSettings.AutoDiscovery.Origin;
					Celestial origin = celestials.FirstOrDefault(c => c.Coordinate.Galaxy == configureddestinationCoord.Galaxy &&
																				c.Coordinate.System == configureddestinationCoord.System &&
																				c.Coordinate.Position == configureddestinationCoord.Position &&
																				c.Coordinate.Type.ToString().Equals(configureddestinationCoord.Type, StringComparison.OrdinalIgnoreCase));

					if (origin == null) {
						stop = true;
						DoLog(LogLevel.Warning, "Unable to parse AutoDiscovery origin");
						return;
					}

					int discoveries = await _ogameService.GetAvailableDiscoveries(origin);
					if (discoveries <= 0) {
						DoLog(LogLevel.Information, "No discoveries available at the moment.");
						stop = true;
						return;
					}
					DoLog(LogLevel.Information, $"There are {discoveries} discoveries available.");

					List<Coordinate> destinationCoord = new();
					if (!((bool) _tbotInstance.InstanceSettings.AutoDiscovery.RandomizeDestination)) {
						int maxGalaxy = _tbotInstance.UserData.serverData.Galaxies;
						int maxSystem = _tbotInstance.UserData.serverData.Systems;
						for (int galaxy = 1; galaxy <= maxGalaxy; galaxy++) {
							for (int system = 1; system <= maxSystem; system++) {
								for (int position = 1; position <= 15; position++) {
									destinationCoord.Add(new Coordinate { Galaxy = galaxy, System = system, Position = position });
								}
							}
						}
						destinationCoord = destinationCoord.Where(c => !_tbotInstance.UserData.discoveryBlackList.ContainsKey(c))
										.OrderBy(c => _calculationService.CalcDistance(new(origin.Coordinate.Galaxy, origin.Coordinate.System), c, _tbotInstance.UserData.serverData))
										.ToList();
					}

					while (discoveries > 0 && !stop && fleetsToSend > 0 && _tbotInstance.UserData.slots.Free > (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
							Coordinate dest;
							if ((bool) _tbotInstance.InstanceSettings.AutoDiscovery.RandomizeDestination) {
								dest = new() { Galaxy = origin.Coordinate.Galaxy, System = Random.Shared.Next(1, _tbotInstance.UserData.serverData.Systems + 1), Position = Random.Shared.Next(1, 16)
								};
							} else {
								dest = destinationCoord.First();
								destinationCoord.RemoveAt(0);
							}
							Coordinate blacklistedCoord = _tbotInstance.UserData.discoveryBlackList.Keys.SingleOrDefault(c => c.Galaxy == dest.Galaxy && c.System == dest.System && c.Position == dest.Position);
							if (blacklistedCoord != null) {
								if (_tbotInstance.UserData.discoveryBlackList[blacklistedCoord] > DateTime.Now) {
									skips++;
									continue;
								} else {
									_tbotInstance.UserData.discoveryBlackList.Remove(blacklistedCoord);
								}
							}

							var result = await _ogameService.SendDiscovery(origin, dest);
							if (!result) {
								DoLog(LogLevel.Warning, $"Failed to send discovery fleet to {dest.ToString()} from {origin.ToString()}.");
								_tbotInstance.UserData.discoveryBlackList.Add(dest, DateTime.Now.AddDays(7));
							} else {
								DoLog(LogLevel.Information, $"Discovery fleet sent to {dest.ToString()} from {origin.ToString()}.");
								_tbotInstance.UserData.discoveryBlackList.Add(dest, DateTime.Now.AddDays(7));
								discoveries--;
								fleetsToSend--;
							}

							if (_tbotInstance.UserData.slots.Free <= (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
								DoLog(LogLevel.Information, "No more fleet slots available for AutoDiscovery in this cycle.");
								stop = true;
								break;
							}
							_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
						}

					if (skips > 0)
						DoLog(LogLevel.Information, $"{skips} positions skipped (blacklisted)");

					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					long interval = (_tbotInstance.UserData.fleets.Where(f => f.Mission == Missions.Discovery).OrderByDescending(f => f.BackIn).ToList().First().BackIn *1000 ?? 0) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);;
					DateTime time = await _tbotOgameBridge.GetDateTime();
					if (interval < 0)
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
					time = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					DoLog(LogLevel.Information, $"Next check at {time.ToString()}");
					await _tbotOgameBridge.CheckCelestials();
				} else {
					stop = true;
				}
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
					long intervalDelay = 0;
					try {
						intervalDelay = (_tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Discovery).OrderByDescending(f => f.BackIn).First().BackIn ?? 0) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
					} catch {
						intervalDelay = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoDiscovery.CheckIntervalMax);
					}
					var newTimeDelay = timeDelay.AddMilliseconds(intervalDelay);
					ChangeWorkerPeriod(intervalDelay);
					DoLog(LogLevel.Information, $"Next AutoDiscovery check at {newTimeDelay.ToString()}");
				}
				await _tbotOgameBridge.CheckCelestials();
			}
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return 
					(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active
				;
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
