using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TBot.Common.Logging;
using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using Tbot.Services;
using Microsoft.Extensions.Logging;
using System.Threading;
using Tbot.Helpers;
using TBot.Model;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure;
using Tbot.Common.Settings;

namespace Tbot.Workers.Brain {
	public class LifeformsAutoResearchCelestialWorker : CelestialWorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		public LifeformsAutoResearchCelestialWorker(ITBotMain parentInstance,
			ITBotWorker parentWorker,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge,
			Celestial celestial) :
			base(parentInstance, parentWorker, celestial) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
		}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (
					(bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active
				);
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "LifeformsAutoResearch-" + celestial.ToString();
		}
		public override Feature GetFeature() {
			return Feature.BrainLifeformAutoResearch;
		}

		public override LogSender GetLogSender() {
			return LogSender.LifeformsAutoResearch;
		}

		protected override async Task Execute() {
			try {
				if (_tbotInstance.UserData.isSleeping) {
					DoLog(LogLevel.Information, "Skipping: Sleep Mode Active!");
					return;
				}

				if (((bool) _tbotInstance.InstanceSettings.Brain.Active && (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Active)) {
					await LifeformAutoResearchCelestial(celestial);
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

		private async Task LifeformAutoResearchCelestial(Celestial celestial) {
			int fleetId = (int) SendFleetCode.GenericError;
			LFTechno buildable = LFTechno.None;
			int level = 0;
			bool started = false;
			bool stop = false;
			bool delay = false;
			bool delayProduction = false;
			long delayTime = 0;
			long interval = 0;
			_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
			List<Celestial> planets = new();
			foreach (var p in _tbotInstance.UserData.celestials) {
				if (p.Coordinate.Type == Celestials.Planet) {
					var newPlanet = await _tbotOgameBridge.UpdatePlanet(p, UpdateTypes.Facilities);
					newPlanet = await _tbotOgameBridge.UpdatePlanet(p, UpdateTypes.Buildings);
					newPlanet = await _tbotOgameBridge.UpdatePlanet(p, UpdateTypes.LFBonuses);
					planets.Add(newPlanet);
				}
			}
			try {
				DoLog(LogLevel.Information, $"Running Lifeform AutoResearch on {celestial.ToString()}");
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Resources);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.LFTechs);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Constructions);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.LFBonuses);
				celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Ships);

				int maxResearchLevel = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxResearchLevel") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxResearchLevel : 1;
				int maxTechs11 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs11") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs11 : maxResearchLevel;
				int maxTechs12 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs12") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs12 : maxResearchLevel;
				int maxTechs13 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs13") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs13 : maxResearchLevel;
				int maxTechs14 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs14") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs14 : maxResearchLevel;
				int maxTechs15 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs15") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs15 : maxResearchLevel;
				int maxTechs16 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs16") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs16 : maxResearchLevel;
				int maxTechs21 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs21") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs21 : maxResearchLevel;
				int maxTechs22 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs22") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs22 : maxResearchLevel;
				int maxTechs23 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs23") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs23 : maxResearchLevel;
				int maxTechs24 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs24") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs24 : maxResearchLevel;
				int maxTechs25 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs25") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs25 : maxResearchLevel;
				int maxTechs26 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs26") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs26 : maxResearchLevel;
				int maxTechs31 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs31") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs31 : maxResearchLevel;
				int maxTechs32 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs32") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs32 : maxResearchLevel;
				int maxTechs33 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs33") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs33 : maxResearchLevel;
				int maxTechs34 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs34") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs34 : maxResearchLevel;
				int maxTechs35 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs35") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs35 : maxResearchLevel;
				int maxTechs36 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.Brain.LifeformAutoResearch, "MaxTechs36") ? (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.MaxTechs36 : maxResearchLevel;

				LFTechs maxLFTechs = new();
				maxLFTechs.IntergalacticEnvoys = maxLFTechs.VolcanicBatteries = maxLFTechs.CatalyserTechnology = maxLFTechs.HeatRecovery =  maxTechs11;
				maxLFTechs.HighPerformanceExtractors = maxLFTechs.AcousticScanning = maxLFTechs.PlasmaDrive = maxLFTechs.SulphideProcess = maxTechs12;
				maxLFTechs.FusionDrives = maxLFTechs.HighEnergyPumpSystems = maxLFTechs.EfficiencyModule = maxLFTechs.PsionicNetwork = maxTechs13;
				maxLFTechs.StealthFieldGenerator = maxLFTechs.CargoHoldExpansionCivilianShips = maxLFTechs.DepotAI = maxLFTechs.TelekineticTractorBeam = maxTechs14;
				maxLFTechs.OrbitalDen = maxLFTechs.MagmaPoweredProduction = maxLFTechs.GeneralOverhaulLightFighter = maxLFTechs.EnhancedSensorTechnology = maxTechs15;
				maxLFTechs.ResearchAI = maxLFTechs.GeothermalPowerPlants = maxLFTechs.AutomatedTransportLines = maxLFTechs.NeuromodalCompressor = maxTechs16;
				maxLFTechs.HighPerformanceTerraformer = maxLFTechs.DepthSounding = maxLFTechs.ImprovedDroneAI = maxLFTechs.NeuroInterface = maxTechs21;
				maxLFTechs.EnhancedProductionTechnologies = maxLFTechs.IonCrystalEnhancementHeavyFighter = maxLFTechs.ExperimentalRecyclingTechnology = maxLFTechs.InterplanetaryAnalysisNetwork = maxTechs22;
				maxLFTechs.LightFighterMkII = maxLFTechs.ImprovedStellarator = maxLFTechs.GeneralOverhaulCruiser = maxLFTechs.OverclockingHeavyFighter = maxTechs23;
				maxLFTechs.CruiserMkII = maxLFTechs.HardenedDiamondDrillHeads = maxLFTechs.SlingshotAutopilot = maxLFTechs.TelekineticDrive = maxTechs24;
				maxLFTechs.ImprovedLabTechnology = maxLFTechs.SeismicMiningTechnology = maxLFTechs.HighTemperatureSuperconductors = maxLFTechs.SixthSense = maxTechs25;
				maxLFTechs.PlasmaTerraformer = maxLFTechs.MagmaPoweredPumpSystems = maxLFTechs.GeneralOverhaulBattleship = maxLFTechs.Psychoharmoniser = maxTechs26;
				maxLFTechs.LowTemperatureDrives = maxLFTechs.IonCrystalModules = maxLFTechs.ArtificialSwarmIntelligence = maxLFTechs.EfficientSwarmIntelligence = maxTechs31;
				maxLFTechs.BomberMkII = maxLFTechs.OptimisedSiloConstructionMethod = maxLFTechs.GeneralOverhaulBattlecruiser = maxLFTechs.OverclockingLargeCargo = maxTechs32;
				maxLFTechs.DestroyerMkII = maxLFTechs.DiamondEnergyTransmitter = maxLFTechs.GeneralOverhaulBomber = maxLFTechs.GravitationSensors = maxTechs33;
				maxLFTechs.BattlecruiserMkII = maxLFTechs.ObsidianShieldReinforcement = maxLFTechs.GeneralOverhaulDestroyer = maxLFTechs.OverclockingBattleship = maxTechs34;
				maxLFTechs.RobotAssistants = maxLFTechs.RuneShields = maxLFTechs.ExperimentalWeaponsTechnology = maxLFTechs.PsionicShieldMatrix = maxTechs35;
				maxLFTechs.Supercomputer = maxLFTechs.RocktalCollectorEnhancement = maxLFTechs.MechanGeneralEnhancement = maxLFTechs.KaeleshDiscovererEnhancement = maxTechs36;
				
				
				if (celestial.Constructions.LFResearchID != 0) {
					DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: there is already a Lifeform research in production.");
					delayProduction = true;
					delayTime = (long) celestial.Constructions.LFResearchCountdown * (long) 1000 + (long) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
					return;
				}
				if (celestial is Planet) {
					buildable = _calculationService.GetNextLFTechToBuild(celestial, maxLFTechs, (bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.WaitLvl1BeforeIncrease);//maxResearchLevel);

					if (buildable != LFTechno.None) {
						level = _calculationService.GetNextLevel(celestial, buildable);
						Resources nextLFTechCost = _calculationService.CalcPrice(buildable, level);
						var isLessCostLFTechToBuild = _calculationService.GetLessExpensiveLFTechToBuild(celestial, nextLFTechCost, maxLFTechs);//maxResearchLevel);
						if (isLessCostLFTechToBuild != LFTechno.None) {
							level = _calculationService.GetNextLevel(celestial, isLessCostLFTechToBuild);
							buildable = isLessCostLFTechToBuild;
						}
						DoLog(LogLevel.Information, $"Best Lifeform Research for {celestial.ToString()}: {buildable.ToString()}");

						Resources xCostBuildable = _calculationService.CalcPrice(buildable, level);

						if (celestial.Resources.IsEnoughFor(xCostBuildable)) {
							DoLog(LogLevel.Information, $"Lifeform Research {buildable.ToString()} level {level.ToString()} on {celestial.ToString()}");
							try {
								await _ogameService.BuildCancelable(celestial, (LFTechno) buildable);
								celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Constructions);
								if (celestial.Constructions.LFResearchID == (int) buildable) {
									started = true;
									DoLog(LogLevel.Information, "Lifeform Research succesfully started.");
								} else {
									celestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.LFTechs);
									if (celestial.GetLevel(buildable) != level)
										DoLog(LogLevel.Warning, "Unable to start Lifeform Research construction: an unknown error has occurred");
									else {
										started = true;
										DoLog(LogLevel.Information, "Lifeform Research succesfully started.");
									}
								}

							} catch {
								DoLog(LogLevel.Warning, "Unable to start Lifeform Research: a network error has occurred");
							}
						} else {
							DoLog(LogLevel.Information, $"Not enough resources to build: {buildable.ToString()} level {level.ToString()} on {celestial.ToString()}. Needed: {xCostBuildable.LFBuildingCostResources.ToString()} - Available: {celestial.Resources.LFBuildingCostResources.ToString()}");

							if ((bool) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.Transports.Active && (bool) _tbotInstance.InstanceSettings.Brain.Transports.Active) {
								_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
								_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
								List<RankSlotsPriority> rankSlotsPriority = new() {
									new RankSlotsPriority(Feature.BrainLifeformAutoResearch,
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
								int MaxSlots = _calculationService.CalcSlotsPriority(Feature.BrainLifeformAutoResearch, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);
								
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
												
												Celestial destination = new() { ID = 0 };
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
												Celestial destination = new() { ID = 0 };
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
												fleetId = await _fleetScheduler.HandleMinerTransport(origin, celestial, destination, xCostBuildable, LFBuildables.None);
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
						DoLog(LogLevel.Information, $"Skipping {celestial.ToString()}: nothing to build. All research reached _tbotInstance.InstanceSettings MaxResearchLevel ?");
						stop = true;
					}
				}
			} catch (Exception e) {
				DoLog(LogLevel.Error, $"LifeformAutoResearch Celestial Exception: {e.Message}");
				DoLog(LogLevel.Warning, $"Stacktrace: {e.StackTrace}");
			} finally {
				var time = await _tbotOgameBridge.GetDateTime();
				DateTime newTime;
				if (stop) {
					DoLog(LogLevel.Information, $"Stopping Lifeform AutoResearch check for {celestial.ToString()}.");
					await EndExecution();
				} else {
					if (delayProduction) {
						DoLog(LogLevel.Information, $"Delaying...");
						interval = delayTime;
					} else if (delay) {
						DoLog(LogLevel.Information, $"Delaying...");
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						try {
							interval = (_tbotInstance.UserData.fleets.Where(f => f.Mission == Missions.Transport).OrderBy(f => f.BackIn).First().BackIn ?? 0) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} catch {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.CheckIntervalMax);
						}
					} else if (started) {
						interval = ((long) celestial.Constructions.LFResearchCountdown * (long) 1000) + (long) RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds);
					} else {
						if (fleetId > 0) {
							_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();							
							var transportfleet = _tbotInstance.UserData.fleets.Single(f => f.ID == fleetId && f.Mission == Missions.Transport);
							interval = (transportfleet.ArriveIn * 1000) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						} else {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.Brain.LifeformAutoResearch.CheckIntervalMax);
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
					
					time = await _tbotOgameBridge.GetDateTime();
					newTime = time.AddMilliseconds(interval);
					ChangeWorkerPeriod(interval);
					DoLog(LogLevel.Information, $"Next Lifeform AutoResearch check for {celestial.ToString()} at {newTime.ToString()}");
				}
			}
		}

	}
}
