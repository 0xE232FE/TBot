using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Common.Logging;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Services;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tbot.Helpers;
using TBot.Model;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure;
using System.Numerics;

namespace Tbot.Workers.Brain {
	public class LifeformsAutoMineCelestialWorker : CelestialWorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;

		public LifeformsAutoMineCelestialWorker(ITBotMain parentInstance,
			ITBotWorker parentWorker,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOGameBridge,
			Celestial celestial) :
			base(parentInstance, parentWorker, celestial) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOGameBridge;
		}

		public override bool IsWorkerEnabledBySettings() {
			try {
				return (
					(bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active
				);
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "LifeformsAutoMine-" + celestial.ToString();
		}
		public override Feature GetFeature() {
			return Feature.BrainLifeformAutoMine;
		}

		public override LogSender GetLogSender() {
			return LogSender.LifeformsAutoMine;
		}

		protected override async Task Execute() {
			try {
				if (_tbotInstance.UserData.isSleeping) {
					DoLog(LogLevel.Information, "Skipping: Sleep Mode Active!");
					return;
				}

				if (((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Active)) {
					await LifeformAutoMineCelestial(celestial);
				} else {
					DoLog(LogLevel.Information, "Skipping: feature disabled");
				}
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"Lifeform AutoMine Exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				if (!_tbotInstance.UserData.isSleeping) {
					await _tbotOgameBridge.CheckCelestials();
				}
			}
		}

		private async Task LifeformAutoMineCelestial(Celestial celestial) {
			int fleetId = (int) SendFleetCode.GenericError;
			LFBuildables buildable = LFBuildables.None;
			int level = 0;
			bool started = false;
			bool stop = false;
			bool delay = false;
			bool delayProduction = false;
			bool delayLFResearch = false;
			long delayTime = 0;
			long interval = 0;
			try {
				int maxTechFactory = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseTechBuilding;
				int maxPopuFactory = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBasePopulationBuilding;
				int maxFoodFactory = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseFoodBuilding;
				bool preventIfMoreExpensiveThanNextMine = (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.PreventIfMoreExpensiveThanNextMine;

				DoLog(LogLevel.Information, $"Running Lifeform AutoMine on {celestial.ToString()}");
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Resources);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.ResourcesProduction);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.LFBuildings);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Buildings);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Constructions);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.LFBonuses);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Ships);

				bool preventTechBuilding = false;
				if (celestial.Constructions.LFResearchCountdown > 0) {
					preventTechBuilding = true;
				}

				LFBuildings maxLFBuildings = new();
				maxLFBuildings.ResidentialSector = maxLFBuildings.AssemblyLine = maxLFBuildings.MeditationEnclave = maxLFBuildings.Sanctuary = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBasePopulationBuilding;
				maxLFBuildings.BiosphereFarm = maxLFBuildings.FusionCellFactory = maxLFBuildings.CrystalFarm = maxLFBuildings.AntimatterCondenser = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseFoodBuilding;
				maxLFBuildings.ResearchCentre = maxLFBuildings.RoboticsResearchCentre = maxLFBuildings.RuneTechnologium = maxLFBuildings.VortexChamber = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBaseTechBuilding;
				maxLFBuildings.AcademyOfSciences = maxLFBuildings.UpdateNetwork = maxLFBuildings.RuneForge = maxLFBuildings.HallsOfRealisation = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT2Building;
				maxLFBuildings.NeuroCalibrationCentre = maxLFBuildings.QuantumComputerCentre = maxLFBuildings.Oriktorium = maxLFBuildings.ForumOfTranscendence = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxT3Building;
				maxLFBuildings.HighEnergySmelting = maxLFBuildings.AutomatisedAssemblyCentre = maxLFBuildings.MagmaForge = maxLFBuildings.AntimatterConvector = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding6;
				maxLFBuildings.FoodSilo = maxLFBuildings.HighPerformanceTransformer = maxLFBuildings.DisruptionChamber = maxLFBuildings.CloningLaboratory = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding7;
				maxLFBuildings.FusionPoweredProduction = maxLFBuildings.MicrochipAssemblyLine = maxLFBuildings.Megalith = maxLFBuildings.ChrysalisAccelerator = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding8;
				maxLFBuildings.Skyscraper = maxLFBuildings.ProductionAssemblyHall = maxLFBuildings.CrystalRefinery = maxLFBuildings.BioModifier = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding9;
				maxLFBuildings.BiotechLab = maxLFBuildings.HighPerformanceSynthesiser = maxLFBuildings.DeuteriumSynthesiser = maxLFBuildings.PsionicModulator = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding10;
				maxLFBuildings.Metropolis = maxLFBuildings.ChipMassProduction = maxLFBuildings.MineralResearchCentre = maxLFBuildings.ShipManufacturingHall = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding11;
				maxLFBuildings.PlanetaryShield = maxLFBuildings.NanoRepairBots = maxLFBuildings.AdvancedRecyclingPlant = maxLFBuildings.SupraRefractor = (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.MaxBuilding12;

				if (celestial.Constructions.LFBuildingID != 0 || celestial.Constructions.BuildingID == (int) Buildables.RoboticsFactory || celestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
					DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: there is already a building (LF, robotic or nanite) in production.");
					delayProduction = true;
					delayTime = celestial.Constructions.LFBuildingID != 0
						? ((long) celestial.Constructions.LFBuildingCountdown * (long) 1000) + (long) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds)
						: ((long) celestial.Constructions.BuildingCountdown * (long) 1000) + (long) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
				}
				if (delayTime == 0) {
					if (celestial is Planet) {
						buildable = _calculationService.GetNextLFBuildingToBuild(celestial, maxLFBuildings, preventIfMoreExpensiveThanNextMine, preventTechBuilding);

						if (buildable != LFBuildables.None) {
							level = _calculationService.GetNextLevel(celestial, buildable);
							DoLog(LogLevel.Information, $"Best building for {celestial.ToString()}: {buildable.ToString()}");

							if (
								celestial.Constructions.LFResearchID != 0 &&
								(
									buildable == LFBuildables.ResearchCentre ||
									buildable == LFBuildables.RuneTechnologium ||
									buildable == LFBuildables.RoboticsResearchCentre ||
									buildable == LFBuildables.VortexChamber
								)
							) {
								DoLog(LogLevel.Warning, "Unable to start building construction: a LifeForm Research is already in progress.");
								delayLFResearch = true;
								return;
							}
							float costReduction = _calculationService.CalcLFBuildingsResourcesCostBonus(celestial);
							float popReduction = _calculationService.CalcLFBuildingsPopulationCostBonus(celestial);
							Resources xCostBuildable = _calculationService.CalcPrice(buildable, level, costReduction, 0, popReduction);

							if (celestial.Resources.IsBuildable(xCostBuildable)) {
								DoLog(LogLevel.Information, $"Building {buildable.ToString()} level {level.ToString()} on {celestial.ToString()}");
								try {
									await _ogameService.BuildCancelable(celestial, buildable);
									celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Constructions);
									if (celestial.Constructions.LFBuildingID == (int) buildable) {
										started = true;
										DoLog(LogLevel.Information, "Building succesfully started.");
									} else {
										celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.LFBuildings);
										if (celestial.GetLevel(buildable) != level) {
											DoLog(LogLevel.Warning, "Unable to start building construction: an unknown error has occurred");
										} else {
											started = true;
											DoLog(LogLevel.Information, "Building succesfully started.");
										}
									}

								} catch {
									DoLog(LogLevel.Warning, "Unable to start building construction: a network error has occurred");
								}
							} else if (xCostBuildable.Population > celestial.Resources.Population) {
								DoLog(LogLevel.Information, $"Not enough population to build: {buildable.ToString()} level {level.ToString()} on {celestial.ToString()}. Needed: {xCostBuildable.Population.ToString()} - Available: {celestial.Resources.Population.ToString()}");
							} else {
								DoLog(LogLevel.Information, $"Not enough resources to build: {buildable.ToString()} level {level.ToString()} on {celestial.ToString()}. Needed: {xCostBuildable.LFBuildingCostResources.ToString()} - Available: {celestial.Resources.LFBuildingCostResources.ToString()}");

								if ((bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.Transports.Active && (bool) _tbotInstance.InstanceSettings.Brain.Transports.Active) {
									_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
									_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
									List<RankSlotsPriority> rankSlotsPriority = new() {
										new RankSlotsPriority(Feature.BrainLifeformAutoMine,
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
											(int) _tbotInstance.InstanceSettings.General.SlotPriorityLevel.AutoColonize,
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
									int MaxSlots = _calculationService.CalcSlotsPriority(Feature.BrainLifeformAutoMine, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);
									
									if (MaxSlots > 0) {
										if (!_calculationService.IsThereTransportTowardsCelestial(celestial, _tbotInstance.UserData.fleets)) {
											Celestial origin = new() { ID = 0 };
											List<Celestial> allCelestials = _tbotInstance.UserData.celestials;
											for (int i = 0; i < allCelestials.Count(); i++) {
												allCelestials[i] = await _tbotOgameBridge.UpdatePlanet(allCelestials[i], UpdateTypes.Resources);
												allCelestials[i] = await _tbotOgameBridge.UpdatePlanet(allCelestials[i], UpdateTypes.Ships);
												allCelestials[i] = await _tbotOgameBridge.UpdatePlanet(allCelestials[i], UpdateTypes.LFBonuses);
											}
											Resources missingResources = xCostBuildable.Difference(celestial.Resources);
											if ((bool) _tbotInstance.InstanceSettings.Brain.Transports.CheckMoonOrPlanetFirst) {
												if ((bool) _tbotInstance.InstanceSettings.Brain.Transports.CheckMoonOrPlanetFirst && _calculationService.IsThereMoonHere(allCelestials, celestial)) {
													origin = allCelestials.Unique()
														.Where(c => c.Coordinate.Galaxy == celestial.Coordinate.Galaxy)
														.Where(c => c.Coordinate.System == celestial.Coordinate.System)
														.Where(c => c.Coordinate.Position == celestial.Coordinate.Position)
														.Where(c => c.Coordinate.Type == Celestials.Moon)
														.SingleOrDefault() ?? new() { ID = 0 };
												}
												if (origin.ID != 0) {
													if (origin.Resources.IsEnoughFor(missingResources)) {
														missingResources = _tbotInstance.InstanceSettings.Brain.Transports.RoundResources ? missingResources.Round() : missingResources;
														Buildables preferredShip = Buildables.SmallCargo;
														if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.Brain.Transports.CargoType, true, out preferredShip)) {
															_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to parse CargoType. Falling back to default SmallCargo");
															preferredShip = Buildables.SmallCargo;
														}
														long shipsNeeded = _calculationService.CalcShipNumberForPayload(missingResources, preferredShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, origin.LFBonuses.GetShipCargoBonus(preferredShip), _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
														if (origin.Ships.GetAmount(preferredShip) >= shipsNeeded) {
															Ships ships = new();
															ships.Add((Buildables) preferredShip, shipsNeeded);
															fleetId = await _fleetScheduler.SendFleet(origin, ships, celestial.Coordinate, Missions.Transport, Speeds.HundredPercent, missingResources);
															if (fleetId == (int) SendFleetCode.AfterSleepTime) {
																stop = true;
															}
															if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
																delay = true;
															}
														} else if (_tbotInstance.InstanceSettings.Brain.Transports.DoMultipleTransportIsNotEnoughShipButSamePosition) {
															DoLog(LogLevel.Warning, $"Not enough Cargo available. Multiple transports will take place.");
															Ships ships = new();
															ships.Add((Buildables) preferredShip, origin.Ships.GetAmount(preferredShip));
															Resources transportableResources = _calculationService.CalcMaxTransportableResources(ships, missingResources, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, origin.LFBonuses, _tbotInstance.UserData.userInfo.Class, 0, _tbotInstance.UserData.serverData.ProbeCargo);
															fleetId = await _fleetScheduler.SendFleet(origin, ships, celestial.Coordinate, Missions.Transport, Speeds.HundredPercent, transportableResources);
															if (fleetId == (int) SendFleetCode.AfterSleepTime) {
																stop = true;
															}
															if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
																delay = true;
															}
														} else {
															DoLog(LogLevel.Warning, $"Not enough Cargo available. Skipping CheckMoonOrPlanetFirst.");
														}
													} else {
														DoLog(LogLevel.Warning, $"Not enough resources available on {origin.ToString()} only to send resources to {celestial.ToString()}.");
													}
												} else {
													DoLog(LogLevel.Warning, $"No Moon available on {celestial.ToString()}.");
												}
											}

											if (fleetId <= 0) {
												if ((bool) _tbotInstance.InstanceSettings.Brain.Transports.MultipleOrigins.Active) {
													origin = _tbotInstance.UserData.celestials
														.Unique()
														.Where(c => c.Coordinate.Galaxy == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Galaxy)
														.Where(c => c.Coordinate.System == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.System)
														.Where(c => c.Coordinate.Position == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Position)
														.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Type))
														.SingleOrDefault() ?? new() { ID = 0 };
													List<Celestial> celestialsToExclude = _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.Brain.Transports.MultipleOrigins.Exclude, allCelestials);
													Buildables preferredShip = Buildables.SmallCargo;
													if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.Brain.Transports.CargoType, true, out preferredShip)) {
														_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to parse CargoType. Falling back to default SmallCargo");
														preferredShip = Buildables.SmallCargo;
													}
													TransportSettings transportsSettings = new((bool) _tbotInstance.InstanceSettings.Brain.Transports.Active,
														preferredShip,
														(long) _tbotInstance.InstanceSettings.Brain.Transports.DeutToLeaveOnMoons,
														(bool) _tbotInstance.InstanceSettings.Brain.Transports.RoundResources,
														(bool) _tbotInstance.InstanceSettings.Brain.Transports.SendToTheMoonIfPossible,
														origin,
														(long) _tbotInstance.InstanceSettings.Brain.Transports.MaxSlots,
														(bool) _tbotInstance.InstanceSettings.Brain.Transports.CheckMoonOrPlanetFirst,
														(bool) _tbotInstance.InstanceSettings.Brain.Transports.DoMultipleTransportIsNotEnoughShipButSamePosition,
														new MultipleOrigins((bool) _tbotInstance.InstanceSettings.Brain.Transports.MultipleOrigins.Active,
															(bool) _tbotInstance.InstanceSettings.Brain.Transports.MultipleOrigins.OnlyFromMoons,
															(long) _tbotInstance.InstanceSettings.Brain.Transports.MultipleOrigins.MinimumResourcesToSend,
															(bool) _tbotInstance.InstanceSettings.Brain.Transports.MultipleOrigins.PriorityToProximityOverQuantity,
															celestialsToExclude)
														);
													
													Celestial destination;
													if ((bool) transportsSettings.SendToTheMoonIfPossible && _calculationService.IsThereMoonHere(allCelestials, celestial)) {
														destination = allCelestials
															.Unique()
															.Where(planet => planet.Coordinate.Galaxy == celestial.Coordinate.Galaxy)
															.Where(planet => planet.Coordinate.System == celestial.Coordinate.System)
															.Where(planet => planet.Coordinate.Position == celestial.Coordinate.Position)
															.Where(planet => planet.Coordinate.Type == Celestials.Moon)
															.SingleOrDefault() ?? new() { ID = 0 };
														if (destination.Ships.IsEmpty() || celestial.Resources.TotalResources == 0)
															destination = celestial;
													} else {
														destination = celestial;
													}
													
													var resultOrigins = _calculationService.CalcMultipleOrigin(celestial, allCelestials, missingResources, transportsSettings, _tbotInstance.UserData.fleets, _tbotInstance.UserData);

													if (resultOrigins.Count() == 0) {
														DoLog(LogLevel.Information, $"No origin is available. This may be due to a lack of resources or cargo.");
														return;
													}
													if (resultOrigins.Count() > MaxSlots) {
														DoLog(LogLevel.Information, $"Not enough slots available to send all resources to build: {buildable.ToString()} level {level.ToString()} on {celestial.ToString()}. Slots needed: {resultOrigins.Count().ToString()}/{MaxSlots}.");
														delay = true;
														return;
													}

													Ships ships = new();
													
													foreach (var item in resultOrigins) {
														ships = new();
														ships.Add((Buildables) transportsSettings.CargoType, _calculationService.CalcShipNumberForPayload(item.FirstOrDefault().Value, (Buildables) transportsSettings.CargoType, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, celestial.LFBonuses.GetShipCargoBonus(transportsSettings.CargoType), _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo));
														if (item.FirstOrDefault().Key.Coordinate.IsSame(destination.Coordinate) && transportsSettings.SendToTheMoonIfPossible && destination.Coordinate.Type == Celestials.Moon)
															fleetId= await _fleetScheduler.SendFleet(item.FirstOrDefault().Key, ships, celestial.Coordinate, Missions.Transport, Speeds.HundredPercent, item.FirstOrDefault().Value);
														else
															fleetId= await _fleetScheduler.SendFleet(item.FirstOrDefault().Key, ships, destination.Coordinate, Missions.Transport, Speeds.HundredPercent, item.FirstOrDefault().Value);

														if (fleetId == (int) SendFleetCode.AfterSleepTime) {
															stop = true;
															return;
														}
														if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
															delay = true;
															return;
														}
													}
												} else {
													Celestial destination;
													if ((bool) _tbotInstance.InstanceSettings.Brain.Transports.SendToTheMoonIfPossible && _calculationService.IsThereMoonHere(_tbotInstance.UserData.celestials, celestial)) {
														destination = allCelestials
															.Unique()
															.Where(planet => planet.Coordinate.Galaxy == celestial.Coordinate.Galaxy)
															.Where(planet => planet.Coordinate.System == celestial.Coordinate.System)
															.Where(planet => planet.Coordinate.Position == celestial.Coordinate.Position)
															.Where(planet => planet.Coordinate.Type == Celestials.Moon)
															.SingleOrDefault() ?? new() { ID = 0 };
														if (destination.Ships.IsEmpty() || celestial.Resources.TotalResources == 0)
															destination = celestial;
														xCostBuildable = xCostBuildable.Difference(destination.Resources);
													} else {
														destination = allCelestials
															.Unique()
															.Where(planet => planet.Coordinate.Galaxy == celestial.Coordinate.Galaxy)
															.Where(planet => planet.Coordinate.System == celestial.Coordinate.System)
															.Where(planet => planet.Coordinate.Position == celestial.Coordinate.Position)
															.Where(planet => planet.Coordinate.Type == celestial.Coordinate.Type)
															.SingleOrDefault() ?? new() { ID = 0 };
													}
													origin = allCelestials
														.Unique()
														.Where(c => c.Coordinate.Galaxy == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Galaxy)
														.Where(c => c.Coordinate.System == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.System)
														.Where(c => c.Coordinate.Position == (int) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Position)
														.Where(c => c.Coordinate.Type == Enum.Parse<Celestials>((string) _tbotInstance.InstanceSettings.Brain.Transports.Origin.Type))
														.SingleOrDefault() ?? new() { ID = 0 };
													fleetId = await _fleetScheduler.HandleMinerTransport(origin, celestial, destination, xCostBuildable, buildable, maxLFBuildings, preventIfMoreExpensiveThanNextMine);
													if (fleetId == (int) SendFleetCode.AfterSleepTime) {
														stop = true;
													}
													if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
														delay = true;
													}
												}
											}
										} else {
											DoLog(LogLevel.Information, $"Skipping transport: there is already a transport incoming in {celestial.ToString()}");
											fleetId = _tbotInstance.UserData.fleets.Where(f => f.Mission == Missions.Transport)
												.Where(f => f.Resources.TotalResources > 0)
												.Where(f => f.ReturnFlight == false)
												.Where(f => f.Destination.Galaxy == celestial.Coordinate.Galaxy)
												.Where(f => f.Destination.System == celestial.Coordinate.System)
												.Where(f => f.Destination.Position == celestial.Coordinate.Position)
												.Where(f => f.Destination.Type == celestial.Coordinate.Type)
												.First().ID;
										}
									} else {
										if (_tbotInstance.InstanceSettings.General.SlotPriorityLevel.Brain > 0) {
											if (_tbotInstance.InstanceSettings.Brain.Transports.MaxSlots == 0)
												DoLog(LogLevel.Information, $"Transports.MaxSlots is set to 0, you should increase it.");
											else
												DoLog(LogLevel.Information, $"0 slots available.");
										}
										DoLog(LogLevel.Information, $"Not enough slots available for Transports.MaxSlots, delaying.");
										delay = true;
									}
								}
							}
						} else {
							DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: nothing to build. Check max Lifeform base building max level in _tbotInstance.InstanceSettings file?");
							stop = true;
						}
					}
				}
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"LifeformAutoMine Celestial Exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				var time = await _tbotOgameBridge.GetDateTime();
				DateTime newTime;
				if (stop) {
					DoLog(LogLevel.Information, $"Stopping Lifeform AutoMine check for {celestial.ToString()}.");
					await EndExecution();
				} else {
					if (delayProduction) {
						DoLog(LogLevel.Information, $"Delaying...");
						interval = delayTime;
					} else if (delayLFResearch) {
						DoLog(LogLevel.Information, $"Delaying...");
						try {
							interval = (celestial.Constructions.LFResearchCountdown * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} catch {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMax);
						}
					} else if (delay) {
						DoLog(LogLevel.Information, $"Delaying...");
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						try {
							interval = (_tbotInstance.UserData.fleets.Where(f => f.Mission == Missions.Transport).OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} catch {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMax);
						}
					} else if (started) {
						interval = ((long) celestial.Constructions.LFBuildingCountdown * (long) 1000) + (long) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
					} else if (delayTime > 0) {
						interval = delayTime;
					} else {
						if (fleetId == (int) SendFleetCode.QuickerToWaitForProduction) {
							float costReduction = _calculationService.CalcLFBuildingsResourcesCostBonus(celestial);
							float popReduction = _calculationService.CalcLFBuildingsPopulationCostBonus(celestial);
							var price = _calculationService.CalcPrice(buildable, level, costReduction, 0, popReduction);
							long productionTime = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMax);
							celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.ResourcesProduction);
							DateTime now = await _tbotOgameBridge.GetDateTime();
							if (
								celestial.Coordinate.Type == Celestials.Planet &&
								(price.Metal <= celestial.ResourcesProduction.Metal.StorageCapacity || price.Metal <= celestial.Resources.Metal) &&
								(price.Crystal <= celestial.ResourcesProduction.Crystal.StorageCapacity || price.Crystal <= celestial.Resources.Crystal) &&
								(price.Deuterium <= celestial.ResourcesProduction.Deuterium.StorageCapacity || price.Deuterium <= celestial.Resources.Deuterium)
							) {
								var missingResources = price.Difference(celestial.Resources);
								float metProdInASecond = celestial.ResourcesProduction.Metal.CurrentProduction / (float) 3600;
								float cryProdInASecond = celestial.ResourcesProduction.Crystal.CurrentProduction / (float) 3600;
								float deutProdInASecond = celestial.ResourcesProduction.Deuterium.CurrentProduction / (float) 3600;
								if (
									!(
										(missingResources.Metal > 0 && (metProdInASecond == 0 && celestial.Resources.Metal < price.Metal)) ||
										(missingResources.Crystal > 0 && (cryProdInASecond == 0 && celestial.Resources.Crystal < price.Crystal)) ||
										(missingResources.Deuterium > 0 && (deutProdInASecond == 0 && celestial.Resources.Deuterium < price.Deuterium))
									)
								) {
									float metProductionTime = float.IsNaN(missingResources.Metal / metProdInASecond) ? 0.0F : missingResources.Metal / metProdInASecond;
									float cryProductionTime = float.IsNaN(missingResources.Crystal / cryProdInASecond) ? 0.0F : missingResources.Crystal / cryProdInASecond;
									float deutProductionTime = float.IsNaN(missingResources.Deuterium / deutProdInASecond) ? 0.0F : missingResources.Deuterium / deutProdInASecond;
									productionTime = (long) (Math.Round(Math.Max(Math.Max(metProductionTime, cryProductionTime), deutProductionTime), 0) * 1000);
									//DoLog(LogLevel.Debug, $"The required resources will be produced by {now.AddMilliseconds(productionTime).ToString()}");
								}
							}
							interval = productionTime + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} else {
							if (fleetId > 0) {
								_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();							
								var transportfleet = _tbotInstance.UserData.fleets.Single(f => f.ID == fleetId && f.Mission == Missions.Transport);
								interval = (transportfleet.ArriveIn * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
							} else {
								interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoMine.CheckIntervalMax);
							}
							if ((bool) _tbotInstance.InstanceSettings.Brain.Transports.DoMultipleTransportIsNotEnoughShipButSamePosition) {
								var transportfleet2 = _tbotInstance.UserData.fleets.Where(f => f.Mission == Missions.Transport)
									.Where(f => f.Destination.IsSame(celestial.Coordinate))
									.Where(f => f.Origin.Galaxy == celestial.Coordinate.Galaxy)
									.Where(f => f.Origin.System == celestial.Coordinate.System)
									.Where(f => f.Origin.Position == celestial.Coordinate.Position)
									.Where(f => f.Origin.Type == (celestial.Coordinate.Type == Celestials.Planet ? Celestials.Moon : Celestials.Planet))
									.ToList();
								if (transportfleet2.Count() > 0) {
									interval = (long) (transportfleet2.First().BackIn * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
								}
							}
						}
					}
					time = await _tbotOgameBridge.GetDateTime();
					newTime = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					DoLog(LogLevel.Information, $"Next Lifeform AutoMine check for {celestial.ToString()} at {newTime.ToString()}");
				}
			}
		}
	}
}
