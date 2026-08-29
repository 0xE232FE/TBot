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
		private readonly SemaphoreSlim _asyncLock = new(1, 1);
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
				} else {
					if (_tbotInstance.UserData.discoveryBlackList.First().Value < DateTime.Now || _tbotInstance.UserData.discoveryBlackList.Last().Value < DateTime.Now) {
						foreach(var (blacklistedCoord, blacklistedTime) in _tbotInstance.UserData.discoveryBlackList.ToList()) {
							if (blacklistedTime < DateTime.Now)
								_tbotInstance.UserData.discoveryBlackList.Remove(blacklistedCoord);
						}
					}
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
						new RankSlotsPriority(Feature.BrainAutoMine,
												(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
												((bool)_tbotInstance.InstanceSettings.Brain.Active &&
														(bool)_tbotInstance.InstanceSettings.Brain.Transports.Active &&
															((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active ||
															(bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
															(bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active ||
															(bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
												(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
												(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)
											),
						new RankSlotsPriority(Feature.Expeditions,
												(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
												(bool)_tbotInstance.InstanceSettings.Expeditions.Active,
												(int)_tbotInstance.UserData.slots.ExpTotal,
												(int)_tbotInstance.UserData.slots.ExpInUse
											),
						new RankSlotsPriority(Feature.AutoFarm,
												(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
												(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
												(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
												(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)
											),
						new RankSlotsPriority(Feature.Colonize,
												(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
												(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
												(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
														(int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1,
												(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)
											),
						new RankSlotsPriority(Feature.AutoDiscovery,
												(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
												(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
												(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
												(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)
											),
						new RankSlotsPriority(Feature.Harvest,
												(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
												(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
												(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
												(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest)
											)
					};
					int fleetsToSend = _calculationService.CalcSlotsPriority(Feature.AutoDiscovery,
																				rankSlotsPriority,
																				_tbotInstance.UserData.slots,
																				_tbotInstance.UserData.fleets,
																				(int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree
																			);

					if (fleetsToSend <= 0) {
						delay = true;
						return;
					}

					var celestials = await _ogameService.GetCelestials();
					/*
						var configureddestinationCoord = _tbotInstance.InstanceSettings.AutoDiscovery.Origin;
						Celestial origin = celestials.FirstOrDefault(c => c.Coordinate.Galaxy == configureddestinationCoord.Galaxy &&
																					c.Coordinate.System == configureddestinationCoord.System &&
																					c.Coordinate.Position == configureddestinationCoord.Position &&
																					c.Coordinate.Type.ToString().Equals(configureddestinationCoord.Type, StringComparison.OrdinalIgnoreCase));
					*/

					List<Celestial> origins = new();
					if (_tbotInstance.InstanceSettings.AutoDiscovery.Origin.Length > 0) {
						try {
							foreach (var origin in _tbotInstance.InstanceSettings.AutoDiscovery.Origin) {
								Coordinate customOriginCoords = new(
									(int) origin.Galaxy,
									(int) origin.System,
									(int) origin.Position,
									Enum.Parse<Celestials>(origin.Type.ToString())
								);
								Celestial customOrigin = _tbotInstance.UserData.celestials
									.Unique()
									.Single(planet => planet.HasCoords(customOriginCoords));
								customOrigin = await _tbotOgameBridge.UpdatePlanet(customOrigin, UpdateTypes.Ships);
								customOrigin = await _tbotOgameBridge.UpdatePlanet(customOrigin, UpdateTypes.LFBonuses);
								origins.Add(customOrigin);
							}
							origins = origins.OrderBy(planet => planet.Coordinate.Galaxy)
								.ThenBy(planet => planet.Coordinate.System)
								.ThenBy(planet => planet.Coordinate.Position)
								.ToList();
						} catch (Exception e) {
							DoLog(LogLevel.Debug, $"Exception: {e.Message}");
							DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
							DoLog(LogLevel.Warning, "Unable to parse custom origin");

							origins.Add(_tbotInstance.UserData.celestials
								.OrderBy(planet => planet.Coordinate.Type == Celestials.Planet)
								.ThenByDescending(planet => planet.Resources)
								.First()
							);
						}
					} else {
						origins.Add(_tbotInstance.UserData.celestials
							.OrderBy(planet => planet.Coordinate.Type == Celestials.Planet)
							.ThenByDescending(planet => planet.Resources)
							.First()
						);
					}

					if (origins.Count == 0) {
						stop = true;
						DoLog(LogLevel.Warning, "Unable to parse AutoDiscovery origin");
						return;
					}

					int discoveries = await _ogameService.GetAvailableDiscoveries(origins.First());
					if (discoveries <= 0) {
						DoLog(LogLevel.Information, "No discoveries available at the moment.");
						stop = true;
						return;
					}
					DoLog(LogLevel.Information, $"There are {discoveries} discoveries available.");

					Dictionary<Coordinate, Celestial> destinationCoord = new();
					if (!((bool) _tbotInstance.InstanceSettings.AutoDiscovery.RandomizeDestination)) {
						int maxGalaxy = _tbotInstance.UserData.serverData.Galaxies;
						int maxSystem = _tbotInstance.UserData.serverData.Systems;
						for (int galaxy = 1; galaxy <= maxGalaxy; galaxy++) {
							for (int system = 1; system <= maxSystem; system++) {
								for (int position = 1; position <= 15; position++) {
									destinationCoord.Add(
										new Coordinate { Galaxy = galaxy, System = system, Position = position },
										origins.OrderBy(o => _calculationService.CalcDistance(o.Coordinate, new Coordinate { Galaxy = galaxy, System = system }, _tbotInstance.UserData.serverData)).First()
									);
								}
							}
						}

						destinationCoord = destinationCoord.Where(c => !_tbotInstance.UserData.discoveryBlackList.Keys.Any(k => k.Galaxy == c.Key.Galaxy && k.System == c.Key.System && k.Position == c.Key.Position))
										.OrderBy(c => _calculationService.CalcDistance(new Coordinate { Galaxy = c.Value.Coordinate.Galaxy, System = c.Value.Coordinate.System }, new Coordinate { Galaxy = c.Key.Galaxy, System = c.Key.System }, _tbotInstance.UserData.serverData))
										.ThenBy(c => c.Value.Coordinate.Galaxy)
										.ThenBy(c => c.Value.Coordinate.System)
										.ToDictionary(c => c.Key, c => c.Value);

						/*destinationCoord = destinationCoord.OrderBy(c => c.Value.Coordinate.Galaxy)
										.ThenBy(c => c.Value.Coordinate.System)
										.ToDictionary(c => c.Key, c => c.Value);*/
					}

					while (discoveries > 0 && !stop && fleetsToSend > 0 && _tbotInstance.UserData.slots.Free > (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree) {
						Dictionary<Celestial, Coordinate> dest = new();
						if ((bool) _tbotInstance.InstanceSettings.AutoDiscovery.RandomizeDestination) {
							int randomIndex = Random.Shared.Next(0, origins.Count);
							dest.Add(
								origins[randomIndex],
								new Coordinate { Galaxy = origins[randomIndex].Coordinate.Galaxy, System = Random.Shared.Next(1, _tbotInstance.UserData.serverData.Systems + 1), Position = Random.Shared.Next(1, 16) }
							);
						} else {
							dest.Add(destinationCoord.Values.First(), destinationCoord.Keys.First());
							destinationCoord.Remove(destinationCoord.Keys.First());
						}
						Coordinate blacklistedCoord = _tbotInstance.UserData.discoveryBlackList.Keys.SingleOrDefault(c => c.Galaxy == dest.Values.First().Galaxy && c.System == dest.Values.First().System && c.Position == dest.Values.First().Position);
						if (blacklistedCoord != null) {
							if (_tbotInstance.UserData.discoveryBlackList[blacklistedCoord] > DateTime.Now) {
								skips++;
								continue;
							} else {
								_tbotInstance.UserData.discoveryBlackList.Remove(blacklistedCoord);
							}
						}

						var result = await _ogameService.SendDiscovery(dest.Keys.First(), dest.Values.First());
						_tbotInstance.UserData.discoveryBlackList.Add(dest.Values.First(), DateTime.Now.AddDays(7));
						if (!result) {
							DoLog(LogLevel.Warning, $"Failed to send discovery fleet to {dest.Values.First().ToString()} from {dest.Keys.First().ToString()}.");
						} else {
							DoLog(LogLevel.Information, $"Discovery fleet sent to {dest.Values.First().ToString()} from {dest.Keys.First().ToString()}.");
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
					long interval = (_tbotInstance.UserData.fleets.Where(f => f.Mission == Missions.Discovery).OrderByDescending(f => f.BackIn).ToList().First().BackIn * 1000 ?? 0) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
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
						intervalDelay = (_tbotInstance.UserData.fleets.Where(fleet => fleet.Mission == Missions.Discovery).OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
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
				return (bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active;
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