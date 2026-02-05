using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Diagnostics;
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
				return (bool) _tbotInstance.InstanceSettings.Expeditions.Active;
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

		protected override async Task Execute() {
			bool stop = false;
			bool delay = false;
			try {
				// Wait for the thread semaphore to avoid the concurrency with itself
				long interval;
				DateTime time;
				DateTime newTime;

				if ((bool) _tbotInstance.InstanceSettings.Expeditions.Active) {
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
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain,
							((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.Transports.Active && ((bool) _tbotInstance.InstanceSettings.Brain.AutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.AutoResearch.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active || (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)),
							(int) _tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Transport)),
						new RankSlotsPriority(Feature.Expeditions,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Expeditions,
							(bool) _tbotInstance.InstanceSettings.Expeditions.Active,
							(int) _tbotInstance.UserData.slots.ExpTotal,
							(int)_tbotInstance.UserData.slots.ExpInUse),
						new RankSlotsPriority(Feature.AutoFarm,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoFarm,
							(bool) _tbotInstance.InstanceSettings.AutoFarm.Active,
							(int) _tbotInstance.InstanceSettings.AutoFarm.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Attack)),
						new RankSlotsPriority(Feature.Colonize,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.Colonize,
							(bool) _tbotInstance.InstanceSettings.AutoColonize.Active,
							(bool) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.Active ?
								(int) _tbotInstance.InstanceSettings.AutoColonize.IntensiveResearch.MaxSlots :
								1,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Colonize)),
						new RankSlotsPriority(Feature.AutoDiscovery,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoDiscovery,
							(bool) _tbotInstance.InstanceSettings.AutoDiscovery.Active,
							(int) _tbotInstance.InstanceSettings.AutoDiscovery.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Discovery)),
						new RankSlotsPriority(Feature.Harvest,
							(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoHarvest,
							(bool) _tbotInstance.InstanceSettings.AutoHarvest.Active,
							(int) _tbotInstance.InstanceSettings.AutoHarvest.MaxSlots,
							(int) _tbotInstance.UserData.fleets.Count(f => f.Mission == Missions.Harvest))
					};
					int MaxSlots = _calculationService.CalcSlotsPriority(Feature.Expeditions, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

					int expsToSend;
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForAllExpeditions") && (bool) _tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions) {
						if (_tbotInstance.UserData.slots.ExpInUse == 0)
							expsToSend = _tbotInstance.UserData.slots.ExpTotal;
						else
							expsToSend = 0;
					} else {
						expsToSend = Math.Min(_tbotInstance.UserData.slots.ExpFree, _tbotInstance.UserData.slots.Free);
					}
					DoLog(LogLevel.Debug, $"Expedition slot free: {expsToSend}");
					if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Expeditions, "WaitForMajorityOfExpeditions") && (bool) _tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions) {
						if ((double) expsToSend < Math.Round((double) _tbotInstance.UserData.slots.ExpTotal / 2D, 0, MidpointRounding.ToZero) + 1D) {
							DoLog(LogLevel.Debug, $"Majority of expedition already in flight, Skipping...");
							expsToSend = 0;
						}
					}
					expsToSend = expsToSend < MaxSlots ? expsToSend : MaxSlots;
					if (expsToSend > 0) {
						if (_tbotInstance.UserData.slots.ExpFree > 0) {
							if (_tbotInstance.UserData.slots.Free > 0) {
								List<Celestial> origins = new();
								if (_tbotInstance.InstanceSettings.Expeditions.Origin.Length > 0) {
									try {
										foreach (var origin in _tbotInstance.InstanceSettings.Expeditions.Origin) {
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
									} catch (Exception e) {
										DoLog(LogLevel.Debug, $"Exception: {e.Message}");
										DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
										DoLog(LogLevel.Warning, "Unable to parse custom origin");

										_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.Ships);
										_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.LFBonuses);
										origins.Add(_tbotInstance.UserData.celestials
											.OrderBy(planet => planet.Coordinate.Type == Celestials.Moon)
											.ThenByDescending(planet => _calculationService.CalcFleetCapacity(planet.Ships, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, planet.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo))
											.First()
										);
									}
								} else {
									_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.Ships);
									_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdatePlanets(UpdateTypes.LFBonuses);
									origins.Add(_tbotInstance.UserData.celestials
										.OrderBy(planet => planet.Coordinate.Type == Celestials.Moon)
										.ThenByDescending(planet => _calculationService.CalcFleetCapacity(planet.Ships, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology, planet.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo))
										.First()
									);
								}

								Ships fleetForOneExpedition = GetShipsForOneExpedition(origins.First().LFBonuses);
								DoLog(LogLevel.Information, $"The optimal composition of one expedition: {fleetForOneExpedition.ToString()}");

								if (fleetForOneExpedition.IsEmpty()) {
									DoLog(LogLevel.Warning, "Unable to send expeditions: no ships available to form a proper expedition fleet");
									stop = true;
									return;
								}

								Ships shipsToKeep = new();
								Dictionary<Celestial, Dictionary<Ships, int>> expToSendFromEachOrigin = new();
								Buildables primaryShip = Buildables.LargeCargo;
								if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.PrimaryShip.ToString(), true, out primaryShip)) {
									primaryShip = Buildables.LargeCargo;
								}
								if ((long) _tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep > 0) {
									shipsToKeep.SetAmount(primaryShip, (long) _tbotInstance.InstanceSettings.Expeditions.PrimaryToKeep);
								}
								Buildables secondaryShip = Buildables.Null;
								if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.SecondaryShip, true, out secondaryShip)) {
									secondaryShip = Buildables.Null;
								}
								// vérifier les ToKeep !!
								if (!((bool) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Active))
									origins.RemoveAll(celestial => celestial.Ships.GetAmount(primaryShip) <= shipsToKeep.GetAmount(primaryShip));
								else
									origins.RemoveAll(celestial => !celestial.Ships.HasAtLeast(fleetForOneExpedition, 1));
								origins.RemoveAll(celestial => celestial.Ships.IsEmpty());

								DoLog(LogLevel.Information, $"Only {origins.Count()} origin(s) have enough ships to send expeditions");

								if (origins.Count() == 0) {
									DoLog(LogLevel.Warning, "Unable to send expeditions: no ships available");
									delay = true;
									return;
								}

								foreach (var origin in origins) {
									int tempExpsToSend = 0;
									Celestial celestial = origin;
									if (!((bool) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Active)) {
										celestial.Ships = celestial.Ships.unMerge(shipsToKeep);
									}

									for (int i = 0; i < expsToSend; i++) {
										if (celestial.Ships.HasAtLeast(fleetForOneExpedition, i + 1))
											tempExpsToSend++;
									}

									if (tempExpsToSend == 0) {
										Ships availableShips = fleetForOneExpedition.Clone().unMerge(fleetForOneExpedition.Clone().unMerge(celestial.Ships));
										expToSendFromEachOrigin.Add(celestial, new Dictionary<Ships, int> { { availableShips, tempExpsToSend } });
										DoLog(LogLevel.Warning, $"Not enough ships from {celestial.ToString()} to send a proper expeditions: missing {fleetForOneExpedition.Clone().unMerge(availableShips).ToString()}");
									} else {
										expToSendFromEachOrigin.Add(celestial, new Dictionary<Ships, int> { { fleetForOneExpedition.Clone(), tempExpsToSend } });
									}
								}

								DoLog(LogLevel.Information, $"Number of expeditions to send from each origin: ");
								foreach (var kvp in expToSendFromEachOrigin) {
									DoLog(LogLevel.Information, $"{kvp.Key.ToString()}: {kvp.Value.Values.First().ToString()} expeditions with {kvp.Value.Keys.First().ToString()}");
								}

								Dictionary<Celestial, Dictionary<Ships, int>> cleanExpToSendFromEachOrigin = new();

								cleanExpToSendFromEachOrigin = expToSendFromEachOrigin
																	.Where(d => d.Value.Values.First() > 0)
																	.OrderByDescending(d => d.Value.Values.First())
																	.ToDictionary(d => d.Key, d => d.Value);

								if (cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.First()) < expsToSend && !((bool) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Active)) {
									int properMission = cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.Sum());
									DoLog(LogLevel.Warning, $"Not enough ships available to send {expsToSend.ToString()} proper expeditions (only {properMission.ToString()} proper possible)");

									List<Celestial> expToSends = expToSendFromEachOrigin.Keys.ToList();
									foreach (Celestial expToSend in expToSends) {
										if (expToSendFromEachOrigin[expToSend].Values.First() <= 0)
											continue;
										Ships ships = expToSendFromEachOrigin[expToSend].Keys.First();
										int value = expToSendFromEachOrigin[expToSend].Values.First();
										expToSendFromEachOrigin.Remove(expToSend);
										ships = fleetForOneExpedition.Clone().unMerge(fleetForOneExpedition.Clone().unMerge(expToSend.Ships.Clone().unMerge(fleetForOneExpedition.Clone().Multiply(value))));
										expToSendFromEachOrigin.Add(expToSend, new Dictionary<Ships, int> { { ships, 0 } });
									}

									expToSends = expToSendFromEachOrigin
													.Where(d => !d.Key.Ships.IsEmpty())
													.Where(d => d.Value.Keys.First().GetFleetPoints() < fleetForOneExpedition.GetFleetPoints())
													.OrderByDescending(d => d.Value.Keys.First().GetFleetPoints())
													.ToDictionary(d => d.Key, d => d.Value)
													.Keys.ToList();
									
									if (expToSendFromEachOrigin.Where(d => !d.Key.Ships.IsEmpty()).Where(d => d.Value.Keys.First().GetFleetPoints() < fleetForOneExpedition.GetFleetPoints()).ToDictionary(d => d.Key, d => d.Value).Count() == 1 || expsToSend - properMission == 1) {
										if (cleanExpToSendFromEachOrigin.ContainsKey(expToSends.First()))
											cleanExpToSendFromEachOrigin[expToSends.First()].Add(expToSendFromEachOrigin[expToSends.First()].Keys.First().Divide(expsToSend - properMission), expsToSend - properMission);
										else
											cleanExpToSendFromEachOrigin.Add(expToSends.First(), new Dictionary<Ships, int> { { expToSendFromEachOrigin[expToSends.First()].Keys.First().Divide(expsToSend - properMission), expsToSend - properMission } });
									} else {

										expToSendFromEachOrigin = expToSendFromEachOrigin
																	.Where(d => !d.Key.Ships.IsEmpty())
																	.Where(d => d.Value.Keys.First().GetFleetPoints() < fleetForOneExpedition.GetFleetPoints())
																	.OrderBy(d => d.Value.Keys.First().GetFleetPoints())
																	.ToDictionary(d => d.Key, d => d.Value);
										int tempExpValue = expsToSend - properMission;
										long tempIntValue = 0;
										int rate = 0;

										for (int i = 1; tempIntValue < tempExpValue; i++) {
											rate++;
											long tempShips = expToSendFromEachOrigin.First().Value.Keys.First().GetFleetPoints() /i;
											foreach (var exp in expToSendFromEachOrigin) {
												if (exp.Key.ID == expToSendFromEachOrigin.First().Key.ID)
													tempIntValue = i;
												tempIntValue += (long) Math.Floor((double) (exp.Value.Keys.First().GetFleetPoints() / (expToSendFromEachOrigin.First().Value.Keys.First().GetFleetPoints() /i)));
											}
										}

										if (tempIntValue == tempExpValue) {
											foreach (var exp in expToSendFromEachOrigin) {
												int value = (int) Math.Floor((double) (exp.Value.Keys.First().GetFleetPoints() / (expToSendFromEachOrigin.First().Value.Keys.First().GetFleetPoints() /rate)));
												Ships ships = exp.Value.Keys.First().Divide(value);
												if (cleanExpToSendFromEachOrigin.ContainsKey(exp.Key))
													cleanExpToSendFromEachOrigin[exp.Key].Add(ships, value);
												else
													cleanExpToSendFromEachOrigin.Add(exp.Key, new Dictionary<Ships, int> { { ships, value } });
											}
										} else if (tempIntValue > tempExpValue) {
											tempIntValue = 0;
											rate = rate > 1 ? rate-- : rate;
											foreach (var exp in expToSendFromEachOrigin) {
												int value = 0;
												if (exp.Key.ID == expToSendFromEachOrigin.Last().Key.ID)
													value = tempExpValue - (int) tempIntValue;
												else
													value = (int) Math.Floor((double) (exp.Value.Keys.First().GetFleetPoints() / (expToSendFromEachOrigin.First().Value.Keys.First().GetFleetPoints() /rate)));
												Ships ships = exp.Value.Keys.First().Divide(value);
												tempIntValue += value;													
												if (cleanExpToSendFromEachOrigin.ContainsKey(exp.Key))
													cleanExpToSendFromEachOrigin[exp.Key].Add(ships, value);
												else
													cleanExpToSendFromEachOrigin.Add(exp.Key, new Dictionary<Ships, int> { { ships, value } });
											}
										}
									}
									DoLog(LogLevel.Information, $"{properMission.ToString()} expeditions will be properly sent, {(cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.Sum()) - properMission).ToString()} as close as possible to proper configuration");
								} else {

									if ((bool) _tbotInstance.InstanceSettings.Expeditions.RandomizeOrder)
										cleanExpToSendFromEachOrigin = cleanExpToSendFromEachOrigin.Shuffle().ToDictionary();
									
									if (cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.First()) > expsToSend) {

										expToSendFromEachOrigin = cleanExpToSendFromEachOrigin.OrderBy(d => d.Value.Values.First()).ToDictionary(d => d.Key, d => d.Value);
										int maxExpPossible = cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.First());
										int quot = (int) Math.Floor((float) expsToSend / (float) cleanExpToSendFromEachOrigin.Count());
										int rest = (int) Math.Floor((float) expsToSend % (float) cleanExpToSendFromEachOrigin.Count());
										int toDelay = 0;
										int result = 0;
										cleanExpToSendFromEachOrigin = new();

										for (int i = 0; i < expToSendFromEachOrigin.Count(); i++) {
											var wave = expToSendFromEachOrigin.ElementAt(i);
											int tempQuot = quot + (toDelay > 0 ? 1 : 0);
											toDelay = 0;
											if (wave.Value.Values.First() < tempQuot) {
												result = wave.Value.Values.First();
												if ((int) Math.Floor((float) (tempQuot - wave.Value.Values.First()) / (float) (expToSendFromEachOrigin.Count() - (i + 1))) > 0)
													tempQuot += (int) Math.Floor((float) (tempQuot - wave.Value.Values.First()) / (float) (expToSendFromEachOrigin.Count() - (i + 1)));
												else
													toDelay = tempQuot - wave.Value.Values.First();
											} else {
												result = tempQuot;
											}
											if (cleanExpToSendFromEachOrigin.ContainsKey(wave.Key))
												cleanExpToSendFromEachOrigin.Remove(wave.Key);
											cleanExpToSendFromEachOrigin.Add(wave.Key, new Dictionary<Ships, int> { { wave.Value.Keys.First(), result } });
										}
										
										List<Celestial> tempCelestial = cleanExpToSendFromEachOrigin.Keys.ToList();
										while (cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.First()) < expsToSend) {
											foreach (var tempCel in tempCelestial) {
												if (cleanExpToSendFromEachOrigin.Values.Sum(d => d.Values.First()) < expsToSend) {
													Ships ships = cleanExpToSendFromEachOrigin[tempCel].Keys.First();
													int value = cleanExpToSendFromEachOrigin[tempCel].Values.First();
													if (value < expToSendFromEachOrigin[tempCel].Values.First())
														value++;
													cleanExpToSendFromEachOrigin.Remove(tempCel);
													cleanExpToSendFromEachOrigin.Add(tempCel, new Dictionary<Ships, int> { { ships, value } });
												}
											}
                                        }
                                    }
                                }

								/*
									DoLog(LogLevel.Debug, $"--------------------------------------");
									foreach (var originss in cleanExpToSendFromEachOrigin) {
										DoLog(LogLevel.Debug, $" ---->>>> {originss.Key.ToString()}");
										var origin = originss.Value;
										DoLog(LogLevel.Debug, $"Proper: {origin.Values.First().ToString()} x");
										foreach (var (key, value) in origin) {
											DoLog(LogLevel.Debug, $" -- {value.ToString()} x {key.ToString()}");
										}
									}
									DoLog(LogLevel.Debug, $"--------------------------------------");
								*/

								Dictionary<Celestial, Dictionary<Ships, int>> originExps = new();
								if ((bool) _tbotInstance.InstanceSettings.Expeditions.RandomizeOrder)
									cleanExpToSendFromEachOrigin = cleanExpToSendFromEachOrigin.Shuffle().ToDictionary();
								else
									cleanExpToSendFromEachOrigin = cleanExpToSendFromEachOrigin.OrderBy(planet => planet.Key.Coordinate.Type == Celestials.Moon).ToDictionary();

								for (int o = 0; o < cleanExpToSendFromEachOrigin.Count(); o++) {
									var origin = cleanExpToSendFromEachOrigin.ElementAt(o);
									if (origin.Key.Ships.IsEmpty()) {
										DoLog(LogLevel.Information, $"Unable to send expeditions from {origin.Key.ToString()}: no ships available");
										continue;
									}
									foreach (var wave in origin.Value) {
										int expsToSendFromThisOrigin = wave.Value;
										if (expsToSendFromThisOrigin == 0) {
											continue;
										} else {
											Ships fleet = wave.Key;
											DoLog(LogLevel.Information, $"{expsToSendFromThisOrigin.ToString()} expeditions with {fleet.ToString()} will be sent from {origin.Key.ToString()}");
											List<int> syslist = new();
											for (int i = 0; i < expsToSendFromThisOrigin; i++) {
												Coordinate destination;
												if ((bool) _tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Active) {
													var rand = new Random();

													int range = (int) _tbotInstance.InstanceSettings.Expeditions.SplitExpeditionsBetweenSystems.Range;
													while (expsToSendFromThisOrigin > range * 2)
														range += 1;

													destination = new Coordinate {
														Galaxy = origin.Key.Coordinate.Galaxy,
														System = rand.Next(origin.Key.Coordinate.System - range, origin.Key.Coordinate.System + range + 1),
														Position = 16,
														Type = Celestials.DeepSpace
													};
													destination.System = GeneralHelper.WrapSystem(destination.System);
													while (syslist.Contains(destination.System))
														destination.System = rand.Next(origin.Key.Coordinate.System - range, origin.Key.Coordinate.System + range + 1);
													syslist.Add(destination.System);
												} else {
													destination = new Coordinate {
														Galaxy = origin.Key.Coordinate.Galaxy,
														System = origin.Key.Coordinate.System,
														Position = 16,
														Type = Celestials.DeepSpace
													};
												}
												_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
												Resources payload = new();
												if ((long) _tbotInstance.InstanceSettings.Expeditions.FuelToCarry > 0) {
													payload.Deuterium = (long) _tbotInstance.InstanceSettings.Expeditions.FuelToCarry;
												}
												if (_tbotInstance.UserData.slots.ExpFree > 0) {
													var fleetId = await _fleetScheduler.SendFleet(origin.Key, fleet, destination, Missions.Expedition, Speeds.HundredPercent, payload);

													if (fleetId == (int) SendFleetCode.AfterSleepTime) {
														stop = true;
														return;
													}
													if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
														delay = true;
														return;
													}

													DoLog(LogLevel.Information, $"Sending expedition fleet from {origin.Key.ToString()} to {destination.ToString()} with fleet {fleet.ToString()}");


													var minWaitNextFleet = (int) _tbotInstance.InstanceSettings.Expeditions.MinWaitNextFleet;
													var maxWaitNextFleet = (int) _tbotInstance.InstanceSettings.Expeditions.MaxWaitNextFleet;

													if (minWaitNextFleet < 0)
														minWaitNextFleet = 0;
													if (maxWaitNextFleet < 1)
														maxWaitNextFleet = 1;

													var rndWaitTimeMs = (int) RandomizeHelper.CalcRandomIntervalSecToMs(minWaitNextFleet, maxWaitNextFleet);

													DoLog(LogLevel.Information, $"Wait {((float) rndWaitTimeMs / 1000).ToString("0.00")}s for next Expedition");
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
							} else {
								DoLog(LogLevel.Warning, "Unable to send expeditions: no fleet slots available");
							}
						} else {
							DoLog(LogLevel.Warning, "Unable to send expeditions: no expeditions slots available");
						}
					}

					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					List<Fleet> orderedFleets = _tbotInstance.UserData.fleets
						.Where(fleet => fleet.Mission == Missions.Expedition)
						.ToList();
					if ((bool) _tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions) {
						orderedFleets = orderedFleets
							.OrderByDescending(fleet => fleet.BackIn)
							.ToList();
					} else {
						orderedFleets = orderedFleets
							.OrderBy(fleet => fleet.BackIn)
							.ToList();
					}

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					if ((orderedFleets.Count() == 0) || (_tbotInstance.UserData.slots.ExpFree > 0 && (!((bool) _tbotInstance.InstanceSettings.Expeditions.WaitForAllExpeditions) && !((bool) _tbotInstance.InstanceSettings.Expeditions.WaitForMajorityOfExpeditions)))) {
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.AboutFiveMinutes);
					} else {

						var minWaitNextRound = (int) _tbotInstance.InstanceSettings.Expeditions.MinWaitNextRound;
						var maxWaitNextRound = (int) _tbotInstance.InstanceSettings.Expeditions.MaxWaitNextRound;

						if (minWaitNextRound < 0)
							minWaitNextRound = 0;
						if (maxWaitNextRound < 1)
							maxWaitNextRound = 1;

						interval = (int) ((1000 * orderedFleets.First().BackIn) + RandomizeHelper.CalcRandomIntervalSecToMs(minWaitNextRound, maxWaitNextRound));

					}
					time = await _tbotOgameBridge.GetDateTime();
					if (interval <= 0)
						interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
					newTime = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
					await _tbotOgameBridge.CheckCelestials();
				}
			} catch (Exception e) {
				DoLog(LogLevel.Warning, $"HandleExpeditions exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
				long interval = (long) (RandomizeHelper.CalcRandomInterval(IntervalType.AMinuteOrTwo));
				var time = await _tbotOgameBridge.GetDateTime();
				DateTime newTime = time.AddMilliseconds(interval);
				ChangeWorkerPeriod(interval);
				DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
			} finally {
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
							interval = (_tbotInstance.UserData.fleets.OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} catch {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Expeditions.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Expeditions.CheckIntervalMax);
						}
						var newTime = time.AddMilliseconds(interval);
						ChangeWorkerPeriod(interval);
						DoLog(LogLevel.Information, $"Next check at {newTime.ToString()}");
					}
					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}

		private Ships GetShipsForOneExpedition(LFBonuses lfBonuses) {
			Ships fleet = new(
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				(long) long.MaxValue,
				0,
				0,
				(long) long.MaxValue,
				(long) long.MaxValue
			);
			if ((bool) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Active) {
				return new(
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.LightFighter,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.HeavyFighter,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Cruiser,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Battleship,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Battlecruiser,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Bomber,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Destroyer,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Deathstar,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.SmallCargo,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.LargeCargo,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.ColonyShip,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Recycler,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.EspionageProbe,
					0,
					0,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Reaper,
					(long) _tbotInstance.InstanceSettings.Expeditions.ManualShips.Ships.Pathfinder
				);
			} else {
				Buildables primaryShip = Buildables.LargeCargo;
				if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.PrimaryShip.ToString(), true, out primaryShip)) {
					DoLog(LogLevel.Warning, "Unable to parse PrimaryShip. Falling back to default LargeCargo");
					primaryShip = Buildables.LargeCargo;
				}
				if (primaryShip == Buildables.Null) {
					DoLog(LogLevel.Warning, "Unable to send expeditions: primary ship is Null");
					return new();
				}
				
				fleet = _calculationService.CalcFullExpeditionShips(fleet.GetMovableShips(), primaryShip, 1, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches, lfBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);

				if (fleet.GetAmount(primaryShip) < (long) _tbotInstance.InstanceSettings.Expeditions.MinPrimaryToSend || fleet.GetAmount(primaryShip) < 1) {
					fleet.SetAmount(primaryShip, (long) _tbotInstance.InstanceSettings.Expeditions.MinPrimaryToSend);
				}

				Buildables secondaryShip = Buildables.Null;
				if (!Enum.TryParse<Buildables>(_tbotInstance.InstanceSettings.Expeditions.SecondaryShip, true, out secondaryShip)) {
					DoLog(LogLevel.Warning, "Unable to parse SecondaryShip. Falling back to default Null");
					secondaryShip = Buildables.Null;
				}
				if (secondaryShip != Buildables.Null) {
					long secondaryToSend = Math.Min(
						fleet.GetAmount(secondaryShip),
						(long) Math.Round(
							fleet.GetAmount(primaryShip) * (float) _tbotInstance.InstanceSettings.Expeditions.SecondaryToPrimaryRatio,
							0,
							MidpointRounding.ToZero
						)
					);
					if (secondaryToSend < (long) _tbotInstance.InstanceSettings.Expeditions.MinSecondaryToSend) {
						secondaryToSend = (long) _tbotInstance.InstanceSettings.Expeditions.MinSecondaryToSend;
					}
					fleet.Add(secondaryShip, secondaryToSend);
				}
			}
			return fleet;
		}
	}
}
