using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tbot.Common.Settings;
using Tbot.Helpers;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace Tbot.Workers {
	public class ExpeditionsWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public ExpeditionsWorker(ITBotMain parentInstance,
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
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool)_tbotInstance.InstanceSettings.Expeditions.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "Expeditions";
		}
		public override Feature GetFeature() {
			return Feature.Expeditions;
		}

		public override LogSender GetLogSender() {
			return LogSender.Expeditions;
		}
  private int CountActiveExpeditionsFromOrigin(Celestial origin) {
    return _tbotInstance.UserData.fleets.Count(f =>
        f.Mission == Missions.Expedition &&
        f.Origin != null &&
        f.Origin.Galaxy == origin.Coordinate.Galaxy &&
        f.Origin.System == origin.Coordinate.System &&
        f.Origin.Position == origin.Coordinate.Position &&
        (int)f.Origin.Type == (int)origin.Coordinate.Type
    );
}
		protected override async Task Execute() {
			bool stop = false;
			bool delay = false;
			try {
				long interval;
				DateTime time;
				DateTime newTime;

				if ((bool)_tbotInstance.InstanceSettings.Expeditions.Active) {
					_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
					if (_tbotInstance.UserData.researches.Astrophysics == 0) {
						DoLog(LogLevel.Information, "Skipping: Astrophysics not yet researched!");
						time = await _tbotOgameBridge.GetDateTime();
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.AboutHalfAnHour);
						newTime = time.AddMilliseconds(interval);
						ChangeWorkerPeriod(interval);
						DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
						return;
					}

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					_tbotInstance.UserData.serverData = await _ogameService.GetServerData();

					List<RankSlotsPriority> rankSlotsPriority = new() {
						new RankSlotsPriority(Feature.BrainAutoMine,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
							((bool)_tbotInstance.InstanceSettings.Brain.Active && (bool)_tbotInstance.InstanceSettings.Brain.Transports.Active &&
							 ((bool)_tbotInstance.InstanceSettings.Brain.AutoMine.Active ||
							  (bool)_tbotInstance.InstanceSettings.Brain.AutoResearch.Active ||
							  (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active ||
							  (bool)_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
							(int)_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
							(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
						new RankSlotsPriority(Feature.Expeditions,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
							(bool)_tbotInstance.InstanceSettings.Expeditions.Active,
							(int)_tbotInstance.UserData.slots.ExpTotal,
							(int)_tbotInstance.UserData.slots.ExpInUse),
						new RankSlotsPriority(Feature.AutoFarm,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
							(bool)_tbotInstance.InstanceSettings.AutoFarm.Active,
							(int)_tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
							(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)),
						new RankSlotsPriority(Feature.Colonize,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
							(bool)_tbotInstance.InstanceSettings.AutoColonize.Active,
							(bool)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
								(int)_tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots : 1,
							(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
						new RankSlotsPriority(Feature.AutoDiscovery,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
							(bool)_tbotInstance.InstanceSettings.AutoDiscovery.Active,
							(int)_tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
							(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
						new RankSlotsPriority(Feature.Harvest,
							(int)_tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
							(bool)_tbotInstance.InstanceSettings.AutoHarvest.Active,
							(int)_tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
							(int)_tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
					};

	               	int MaxSlots = _tbotInstance.UserData.slots.Total 
                       - (int)_tbotInstance.InstanceSettings.General.SlotsToLeaveFree;

                     if (MaxSlots < 0)
                 MaxSlots = 0;

					int expsToSend;
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForAllExpeditions")
						&& (bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions) {
						if (_tbotInstance.UserData.slots.ExpInUse == 0)
							expsToSend = _tbotInstance.UserData.slots.ExpTotal;
						else
							expsToSend = 0;
					} else {
						expsToSend = Math.Min(_tbotInstance.UserData.slots.ExpFree, _tbotInstance.UserData.slots.Free);
					}

					DoLog(LogLevel.Debug, $"Expedition slot free: {expsToSend}");

					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForMajorityOfExpeditions")
						&& (bool)_tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions) {
						if ((double)expsToSend < Math.Round((double)_tbotInstance.UserData.slots.ExpTotal / 2D, 0, MidpointRounding.ToZero) + 1D) {
							DoLog(LogLevel.Debug, $"Majority of expedition already in flight, Skipping...");
							expsToSend = 0;
						}
					}

					expsToSend = expsToSend < MaxSlots ? expsToSend : MaxSlots;

					// === ADD: SMART IDLE WHEN NO SLOTS ===
					if (expsToSend <= 0) {
						var idleInterval = RandomizeHelper.CalcRandomInterval(IntervalType.AboutFiveMinutes);
						var nowIdle = await _tbotOgameBridge.GetDateTime();
						DoLog(LogLevel.Information, "Expeditions idle – no free expedition slots");
						DoLog(LogLevel.Information, $"Next expedition check at {nowIdle.AddMilliseconds(idleInterval)}");
						ChangeWorkerPeriod(idleInterval);
						return;
					}
					// === END ADD ===

					if (expsToSend > 0) {
						if (_tbotInstance.UserData.slots.ExpFree > 0) {
							if (_tbotInstance.UserData.slots.Free > 0) {

								List<Celestial> origins = new();
								if (_tbotInstance.InstanceSettings.Expeditions.Origin.Length > 0) {
									try {
										foreach (var origin in _tbotInstance.InstanceSettings.Expeditions.Origin) {
											Coordinate customOriginCoords = new(
												(int)origin.Galaxy,
												(int)origin.System,
												(int)origin.Position,
												Enum.Parse<Celestials>(origin.Type.ToString())
											);
											Celestial customOrigin = _tbotInstance.UserData.celestials
												.Unique()
												.Single(planet => planet.HasCoords(customOriginCoords));
											customOrigin = await _tbotOgameBridge.UpdatePlanet(customOrigin, UpdateTypes.Ships);
											customOrigin = await _tbotOgameBridge.UpdatePlanet(customOrigin, UpdateTypes.LFBonuses);
											origins.Add(customOrigin);
										}
									} catch (Exception e) {
										DoLog(LogLevel.Debug, $"Exception: {e.Message}");
										DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
										DoLog(LogLevel.Warning, "Unable to parse custom origin");

										_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.Ships);
										_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.LFBonuses);
										origins.Add(_tbotInstance.UserData.celestials
											.OrderBy(planet => planet.Coordinate.Type == Celestials.Moon)
											.ThenByDescending(planet =>
												_calculationService.CalcFleetCapacity(
													planet.Ships,
													_tbotInstance.UserData.serverData,
													_tbotInstance.UserData.researches.HyperspaceTechnology,
													null,
													_tbotInstance.UserData.userInfo.Class,
													_tbotInstance.UserData.serverData.ProbeCargo))
											.First()
										);
									}
								} else {
									_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.Ships);
									_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.LFBonuses);
									origins.Add(_tbotInstance.UserData.celestials
										.OrderBy(planet => planet.Coordinate.Type == Celestials.Moon)
										.ThenByDescending(planet =>
											_calculationService.CalcFleetCapacity(
												planet.Ships,
												_tbotInstance.UserData.serverData,
												_tbotInstance.UserData.researches.HyperspaceTechnology,
												null,
												_tbotInstance.UserData.userInfo.Class,
												_tbotInstance.UserData.serverData.ProbeCargo))
										.First()
									);
								}

								if ((bool)_tbotInstance.InstanceSettings.Expeditions.RandomizeOrder) {
									origins = origins.Shuffle().ToList();
								}
LFBonuses lfBonuses = origins.First().LFBonuses;

// osvježi fleets da CountActive... bude tačan
_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

// === EXPEDITION ORIGIN DEBUG ===
var expFleets = _tbotInstance.UserData.fleets
    .Where(f => f.Mission == Missions.Expedition && f.Origin != null)
    .ToList();

DoLog(LogLevel.Warning, $"[EXP DEBUG] Active expeditions total = {expFleets.Count}");

foreach (var f in expFleets) {
    DoLog(
        LogLevel.Warning,
        $"[EXP DEBUG] Fleet origin: G{f.Origin.Galaxy}:{f.Origin.System}:{f.Origin.Position} Type={f.Origin.Type}"
    );
}
// === END DEBUG ===

// MaxExpeditionsPerOrigin (default: unlimited)
int maxPerOrigin = int.MaxValue;
if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "MaxExpeditionsPerOrigin")) {
    maxPerOrigin = (int)_tbotInstance.InstanceSettings.Expeditions.MaxExpeditionsPerOrigin;
}

// SUMMARY LOG (prije capacity)
DoLog(LogLevel.Warning,
    $"[EXP DEBUG] Origins count={origins.Count}, expsToSend={expsToSend}, maxPerOrigin={maxPerOrigin}");

// 1) izračunaj kapacitet po originu
var capacity = new Dictionary<Celestial, int>();
foreach (var o in origins) {
    int active = CountActiveExpeditionsFromOrigin(o);
    int cap = Math.Max(0, maxPerOrigin - active);
    capacity[o] = cap;

    DoLog(LogLevel.Warning,
        $"[EXP DEBUG] Origin {o.Coordinate.Galaxy}:{o.Coordinate.System}:{o.Coordinate.Position} Type={o.Coordinate.Type} " +
        $"active={active} maxPerOrigin={maxPerOrigin} cap={cap}");
}

// 2) pripremi raspodjelu
var originExps = origins.ToDictionary(o => o, o => 0);

// koliko realno možemo poslati (ako nema dovoljno slobodnih origin-a)
int remaining = Math.Min(expsToSend, capacity.Values.Sum());

// 3) round-robin: puni samo gdje cap > 0
while (remaining > 0) {
    bool progressed = false;

    foreach (var o in origins) {
        if (remaining <= 0) break;

        if (originExps[o] < capacity[o]) {
            originExps[o]++;
            remaining--;
            progressed = true;
        }
    }

    if (!progressed) break; // nema više slobodnih origin-a
}

// PLAN LOG (šta je bot odlučio da pošalje)
foreach (var o in origins) {
    DoLog(LogLevel.Warning,
        $"[EXP DEBUG] PLAN origin {o.Coordinate.Galaxy}:{o.Coordinate.System}:{o.Coordinate.Position} Type={o.Coordinate.Type} " +
        $"willSend={originExps[o]} (cap={capacity[o]})");
}
								// === END ADD ===

								int delayExpedition = 0;
								foreach (var origin in origins) {
									int expsToSendFromThisOrigin = originExps[origin];
									if (expsToSendFromThisOrigin == 0) {
										if (delayExpedition > 0)
											delayExpedition--;
										else
											continue;
									}
									else if (origin.Ships.IsEmpty()) {
										DoLog(LogLevel.Warning, "Unable to send expeditions: no ships available");
										delayExpedition++;
										continue;
									} else {
										Ships fleet;
										if ((bool)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Active) {
											fleet = new(
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.LightFighter,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.HeavyFighter,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Cruiser,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Battleship,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Battlecruiser,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Bomber,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Destroyer,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Deathstar,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.SmallCargo,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.LargeCargo,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.ColonyShip,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Recycler,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.EspionageProbe,
												0,
												0,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Reaper,
												(long)_tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Pathfinder
											);
											if (!origin.Ships.HasAtLeast(fleet, expsToSendFromThisOrigin)) {
												DoLog(LogLevel.Warning, $"Unable to send expeditions: not enough ships in origin {origin}");
												delayExpedition++;
												continue;
											}
										} else {
											// ORIGINAL AUTO SHIP LOGIC (UNCHANGED)
											Buildables primaryShip = Buildables.LargeCargo;
											if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.PrimaryShip.ToString(), true, out primaryShip)) {
												DoLog(LogLevel.Warning, "Unable to parse PrimaryShip. Falling back to default LargeCargo");
												primaryShip = Buildables.LargeCargo;
											}
											if (primaryShip == Buildables.Null) {
												DoLog(LogLevel.Warning, "Unable to send expeditions: primary ship is Null");
												delayExpedition++;
												continue;
											}

											var availableShips = origin.Ships.GetMovableShips();
											if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "PrimaryToKeep")
												&& (int)_tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep > 0) {
												availableShips.SetAmount(
													primaryShip,
													Math.Max(0, availableShips.GetAmount(primaryShip)
														- (long)_tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep));
											}

											fleet = _calculationService.CalcFullExpeditionShips(
												availableShips,
												primaryShip,
												expsToSendFromThisOrigin,
												_tbotInstance.UserData.serverData,
												_tbotInstance.UserData.researches,
												lfBonuses,
												_tbotInstance.UserData.userInfo.Class,
												_tbotInstance.UserData.serverData.ProbeCargo
											);
										}

										DoLog(LogLevel.Information, $"{expsToSendFromThisOrigin} expeditions with {fleet} will be sent from {origin}");

										List<int> syslist = new();
										for (int i = 0; i < expsToSendFromThisOrigin; i++) {
											Coordinate destination;
											if ((bool)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Active) {
												var rand = new Random();
												int range = (int)_tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Range;
												while (expsToSendFromThisOrigin > range * 2)
													range += 1;

												destination = new Coordinate {
													Galaxy = origin.Coordinate.Galaxy,
													System = rand.Next(origin.Coordinate.System - range, origin.Coordinate.System + range + 1),
													Position = 16,
													Type = Celestials.DeepSpace
												};
												destination.System = GeneralHelper.WrapSystem(destination.System);
												while (syslist.Contains(destination.System))
													destination.System = rand.Next(origin.Coordinate.System - range, origin.Coordinate.System + range + 1);
												syslist.Add(destination.System);
											} else {
												destination = new Coordinate {
													Galaxy = origin.Coordinate.Galaxy,
													System = origin.Coordinate.System,
													Position = 16,
													Type = Celestials.DeepSpace
												};
											}

											_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
                                              Resources payload = new();
                                            if ((long)_tbotInstance.InstanceSettings.Expeditions.FuelToCarry > 0) {
	                                          payload.Deuterium = (long)_tbotInstance.InstanceSettings.Expeditions.FuelToCarry;
        }
											if (_tbotInstance.UserData.slots.ExpFree > 0) {

 // osvježi stanje brodova na originu (BITNO)
var originUpdated = await _tbotOgameBridge.UpdatePlanet(origin, UpdateTypes.Ships);

// provjeri slot tek sad (najtačnije)
_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
if (_tbotInstance.UserData.slots.ExpFree <= 0) {
    DoLog(LogLevel.Information, "Unable to send expeditions: no expedition slots available.");
    delay = true;
    return;
}

// safety check prije slanja flote
if (fleet == null || fleet.IsEmpty() || !originUpdated.Ships.HasAtLeast(fleet, 1)) {
    DoLog(LogLevel.Warning, $"Skipping expedition: no ships available on origin {originUpdated}");
    delayExpedition++;
    break;
}

var fleetId = await _fleetScheduler.SendFleet(
    originUpdated,
    fleet,
    destination,
    Missions.Expedition,
    Speeds.HundredPercent,
    payload
);
												if (fleetId == (int)SendFleetCode.AfterSleepTime) {
													stop = true;
													return;
												}
												if (fleetId == (int)SendFleetCode.NotEnoughSlots) {
													delay = true;
													return;
												}

												int minWait = (int)_tbotInstance.InstanceSettings.Expeditions.MinWaitNextFleet;
                                             int maxWait = (int)_tbotInstance.InstanceSettings.Expeditions.MaxWaitNextFleet;

                                          // failsafe ako je user pogrešno podesio
                                                        if (maxWait < minWait) {
	                                     DoLog(LogLevel.Warning,
	                                        	$"Expeditions wait misconfigured (MinWaitNextFleet={minWait} > MaxWaitNextFleet={maxWait}). Swapping values.");
                                     	(minWait, maxWait) = (maxWait, minWait);
                                     }
                                      var rndWaitTimeMs = (int)RandomizeHelper.CalcRandomIntervalSecToMs(minWait, maxWait);

												DoLog(LogLevel.Information, $"Wait {(rndWaitTimeMs / 1000f):0.00}s for next Expedition");
												await Task.Delay(rndWaitTimeMs, _ct);
											} else {
												DoLog(LogLevel.Information, "Unable to send expeditions: no expedition slots available.");
												delay = true;
												return;
											}
										}
									}
								}
							}
						}
					}

					// ORIGINAL END-OF-CYCLE LOGIC (UNCHANGED)
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					List<Fleet> orderedFleets = _tbotInstance.UserData.fleets
						.Where(fleet => fleet.Mission == Missions.Expedition)
						.ToList();

					if ((bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions) {
						orderedFleets = orderedFleets.OrderByDescending(fleet => fleet.BackIn).ToList();
					} else {
						orderedFleets = orderedFleets.OrderBy(fleet => fleet.BackIn).ToList();
					}

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					if ((orderedFleets.Count == 0) ||
						(_tbotInstance.UserData.slots.ExpFree > 0 &&
						 !((bool)_tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions) &&
						 !((bool)_tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions))) {
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.AboutFiveMinutes);
					} else {
						interval = (int)((1000 * orderedFleets.First().BackIn) +
							RandomizeHelper.CalcRandomIntervalSecToMs(
								(int)_tbotInstance.InstanceSettings.Expeditions.MinWaitNextRound,
								(int)_tbotInstance.InstanceSettings.Expeditions.MaxWaitNextRound));
					}

					time = await _tbotOgameBridge.GetDateTime();
					newTime = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					DoLog(LogLevel.Information, $"Next check at {newTime}");
					await _tbotOgameBridge.CheckCelestials();
				}
			}
			catch (Exception e) {
				DoLog(LogLevel.Warning, $"HandleExpeditions exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				long interval = RandomizeHelper.CalcRandomInterval(IntervalType.AMinuteOrTwo);
				var time = await _tbotOgameBridge.GetDateTime();
				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next check at {time.AddMilliseconds(interval)}");
			}
			finally {
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						DoLog(LogLevel.Information, $"Stopping feature.");
						await EndExecution();
					}
					if (delay) {
						DoLog(LogLevel.Information, $"Delaying...");
						var time = await _tbotOgameBridge.GetDateTime();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						long interval;
						try {
							interval = (_tbotInstance.UserData.fleets.OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000
								+ RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} catch {
							interval = RandomizeHelper.CalcRandomInterval(
								(int)_tbotInstance.InstanceSettings.Expeditions.CheckIntervalMin,
								(int)_tbotInstance.InstanceSettings.Expeditions.CheckIntervalMax);
						}
						ChangeWorkerPeriod(interval);
						DoLog(LogLevel.Information, $"Next check at {time.AddMilliseconds(interval)}");
					}
					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}
	}
}
