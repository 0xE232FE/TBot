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
using Tbot.Common.Settings;

namespace Tbot.Workers {
	public class AutoFarmWorker : WorkerBase {
		private readonly IOgameService _ogameService;
		private readonly IFleetScheduler _fleetScheduler;
		private readonly ICalculationService _calculationService;
		private readonly ITBotOgamedBridge _tbotOgameBridge;
		private readonly AutoFarmBlacklist _blacklist;
		private readonly AutoFarmSuccessfulTargets _successfulTargets;
		public AutoFarmWorker(ITBotMain parentInstance,
			IOgameService ogameService,
			IFleetScheduler fleetScheduler,
			ICalculationService calculationService,
			ITBotOgamedBridge tbotOgameBridge) :
			base(parentInstance) {
			_ogameService = ogameService;
			_fleetScheduler = fleetScheduler;
			_calculationService = calculationService;
			_tbotOgameBridge = tbotOgameBridge;
		string blacklistPath = $"autofarm_blacklist_{_tbotInstance.InstanceAlias}.json";
		_blacklist = new AutoFarmBlacklist(blacklistPath);
		string successfulPath = $"autofarm_successful_{_tbotInstance.InstanceAlias}.json";
		_successfulTargets = new AutoFarmSuccessfulTargets(successfulPath);
		}
		public override bool IsWorkerEnabledBySettings() {
			try {
				return (bool) _tbotInstance.InstanceSettings.AutoFarm.Active;
			} catch (Exception) {
				return false;
			}
		}
		public override string GetWorkerName() {
			return "AutoFarm";
		}
		public override Feature GetFeature() {
			return Feature.AutoFarm;
		}

		public override LogSender GetLogSender() {
			return LogSender.AutoFarm;
		}

		private async Task PruneOldReports() {
			var newTime = await _tbotOgameBridge.GetDateTime();
			var removeReports = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent || (t.Report != null && DateTime.Compare(t.Report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), newTime) < 0)).ToList();
			foreach (var remove in removeReports) {
				var updateReport = remove;
				updateReport.State = FarmState.ProbesPending;
				updateReport.Report = null;
				_tbotInstance.UserData.farmTargets.Remove(remove);
				_tbotInstance.UserData.farmTargets.Add(updateReport);
			}
		}

		private async Task<Dictionary<int, long>> GetCelestialProbes() {
			var localCelestials = await _tbotOgameBridge.UpdateCelestials();
			Dictionary<int, long> celestialProbes = new Dictionary<int, long>();
			foreach (var celestial in localCelestials) {
				Celestial tempCelestial = await _tbotOgameBridge.UpdatePlanet(celestial, UpdateTypes.Fast);
				tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
				celestialProbes.Add(tempCelestial.ID, tempCelestial.Ships.EspionageProbe);
			}
			return celestialProbes;
		}

		private bool ShouldExcludeSystem(int galaxy, int system) {
			bool excludeSystem = false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoFarm.Exclude) {
				bool hasPosition = false;
				foreach (var value in exclude.Keys)
					if (value == "Position")
						hasPosition = true;
				if ((int) exclude.Galaxy == galaxy && (int) exclude.System == system && !hasPosition) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping system {system.ToString()}: system in exclude list.");
					excludeSystem = true;
					break;
				}
			}
			return excludeSystem;
		}

		private async Task<List<Celestial>> GetScannedTargetsFromGalaxy(int galaxy, int system) {
			GalaxyInfo galaxyInfo = null;
			int retryCount = 0;
			int maxRetries = 5;

			while (retryCount < maxRetries) {
				try {
					galaxyInfo = await _ogameService.GetGalaxyInfo(galaxy, system);
					break;
				} catch (Exception e) when (e.Message.Contains("system must be within") || e.Message.Contains("503") || e.Message.Contains("Service Unavailable")) {
					retryCount++;
					int waitSeconds = retryCount * 3;

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception details: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Galaxy scan failed. Retry {retryCount}/{maxRetries} in {waitSeconds}s...");

					if (retryCount < maxRetries) {
						await Task.Delay(waitSeconds * 1000);

						try {
							_tbotInstance.UserData.serverData = await _ogameService.GetServerData();
							_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"ServerData after refresh: Systems={_tbotInstance.UserData.serverData.Systems}");
						} catch (Exception serverDataEx) {
							_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to refresh ServerData: {serverDataEx.Message}");
						}

						if (retryCount == 4 && _tbotInstance.UserData.serverData.Systems == 0) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"ServerData broken. Root cause: {e.Message}. Restarting ogamed...");

							try {
								_ogameService.KillOgamedExecutable();
								await Task.Delay(5000);
								_ogameService.RerunOgamed();
								await Task.Delay(10000);

								try {
									_tbotInstance.UserData.serverData = await _ogameService.GetServerData();
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"ServerData after restart: Systems={_tbotInstance.UserData.serverData.Systems}");
								} catch (Exception restartEx) {
									_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to get ServerData after ogamed restart: {restartEx.Message}");
								}
							} catch (Exception ogamedEx) {
								_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Failed to restart ogamed: {ogamedEx.Message}");
								_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {ogamedEx.StackTrace}");
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"Galaxy scan failed after {maxRetries} retries.");
						throw;
					}
				}
			}

			var planets = galaxyInfo.Planets.Where(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation);
			List<Celestial> scannedTargets = planets.Cast<Celestial>().ToList();
			await _fleetScheduler.UpdateFleets();
			scannedTargets.RemoveAll(t => _tbotInstance.UserData.fleets.Any(f => f.Destination.IsSame(t.Coordinate) && f.Mission == Missions.Attack));
			return scannedTargets;
		}

		private bool IsTargetInMinimumRank(Celestial planet, List<Celestial> scannedTargets) {
			if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinimumPlayerRank") && _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank != 0) {
				int rank = 1;
				if (planet.Coordinate.Type == Celestials.Planet) {
					rank = (planet as Planet).Player.Rank;
				} else {
					if (scannedTargets.Any(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet)))) {
						rank = (scannedTargets.Single(t => t.HasCoords(new(planet.Coordinate.Galaxy, planet.Coordinate.System, planet.Coordinate.Position, Celestials.Planet))) as Planet).Player.Rank;
					}
				}
				if ((int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank < rank) {

					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Skipping {planet.ToString()}: player has rank {rank} that is less than minimum configured {(int) _tbotInstance.InstanceSettings.AutoFarm.MinimumPlayerRank}.");
					return false;
				}
			}
			return true;
		}

		private bool ShouldExcludePlanet(Celestial planet) {
			bool excludePlanet = false;
			foreach (var exclude in _tbotInstance.InstanceSettings.AutoFarm.Exclude) {
				bool hasPosition = false;
				foreach (var value in exclude.Keys)
					if (value == "Position")
						hasPosition = true;
				if ((int) exclude.Galaxy == planet.Coordinate.Galaxy && (int) exclude.System == planet.Coordinate.System && hasPosition && (int) exclude.Position == planet.Coordinate.Position) {
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {planet.ToString()}: celestial in exclude list.");
					excludePlanet = true;
					break;
				}
			}
			return excludePlanet;
		}

		private void AddMoons(List<Celestial> scannedTargets) {
			foreach (var t in scannedTargets.ToList()) {
				var planet = t as Planet;
				if (planet == null)
					continue;
				if (planet.Moon != null) {
					Celestial tempCelestial = planet.Moon;
					tempCelestial.Coordinate = t.Coordinate;
					tempCelestial.Coordinate.Type = Celestials.Moon;
					scannedTargets.Add(tempCelestial);
				}
			}
		}

		private FarmTarget CheckDuplicatesAndGetExisting(Celestial planet) {
			var exists = _tbotInstance.UserData.farmTargets.Where(t => t != null && t.Celestial.HasCoords(planet.Coordinate)).ToList();
			if (exists.Count() > 1) {
				var firstExisting = exists.First();
				_tbotInstance.UserData.farmTargets.RemoveAll(c => c.Celestial.HasCoords(planet.Coordinate));
				_tbotInstance.UserData.farmTargets.Add(firstExisting);
				return firstExisting;
			}
			else if (exists.Count() == 1) {
				return exists.First();
			}
			return null;
		}

		private FarmTarget GetFarmTarget(Celestial planet) {
		bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
			SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
			(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;

		if (blacklistActive && _blacklist.IsBlacklisted(planet.Coordinate)) {
			var blacklistedTarget = _blacklist.GetBlacklistedTarget(planet.Coordinate);
			_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} is blacklisted (Reason: {blacklistedTarget.Reason}). Skipping...");
			return null;
		}

			var target = CheckDuplicatesAndGetExisting(planet);

			if (target == null) {
				target = new(planet, FarmState.ProbesPending);
				_tbotInstance.UserData.farmTargets.Add(target);
			} else {
				target.Celestial = planet;

				if (target.State == FarmState.Idle)
					target.State = FarmState.ProbesPending;

				if (target.State == FarmState.NotSuitable && target.Report != null) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as Not Suitable. Skipping...");
					return null;
				}

				if (target.State == FarmState.ProbesSent || target.State == FarmState.AttackPending) {
					_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Target {planet.ToString()} marked as {target.State.ToString()}. Skipping...");
					return null;
				}
			}
			return target;
		}

		private int GetNeededProbes(FarmTarget target) {
			int neededProbes = (int) _tbotInstance.InstanceSettings.AutoFarm.NumProbes;
			if (target.State == FarmState.ProbesRequired)
				neededProbes *= 3;
			if (target.State == FarmState.FailedProbesRequired)
				neededProbes *= 9;
			return neededProbes;
		}

		private class SpyOriginResult {
			public SpyOriginResult(Celestial origin, int freeSlots) {
				Origin = origin;
				BackIn = 0;
				FreeSlots = freeSlots;
			}

			public SpyOriginResult(Celestial origin, int backin, int freeSlots) {
				Origin = origin;
				BackIn = backin;
				FreeSlots = freeSlots;
			}
			public Celestial Origin { get; set; }
			public int BackIn { get; set; }
			public int FreeSlots { get; set; }
		}

		private async Task<SpyOriginResult> GetBestOrigin(List<Celestial> closestCelestials,
			Dictionary<int, long> celestialProbes,
			FarmTarget target,
			int neededProbes,
			int slotsToLeaveFree,
			int freeSlots) {
			SpyOriginResult bestOrigin = new SpyOriginResult(closestCelestials.First(), int.MaxValue, freeSlots);
			foreach (var closest in closestCelestials) {
				var tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Ships);
				celestialProbes.Remove(closest.ID);
				celestialProbes.Add(closest.ID, tempCelestial.Ships.EspionageProbe);

				if (celestialProbes[closest.ID] >= neededProbes) {
					bestOrigin = new SpyOriginResult(closest, freeSlots);
					break;
				}

				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();

				if (freeSlots <= slotsToLeaveFree) {
					var espionageMissions = _calculationService.GetMissionsInProgress(closest.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets);
					if (espionageMissions.Any()) {
						var returningProbes = espionageMissions.Sum(f => f.Ships.EspionageProbe);
						if (celestialProbes[closest.ID] + returningProbes >= neededProbes) {
							var returningFleets = espionageMissions.OrderBy(f => f.BackIn).ToArray();
							long probesCount = 0;
							for (int i = 0; i < returningFleets.Length; i++) {
								probesCount += returningFleets[i].Ships.EspionageProbe;
								if (probesCount >= neededProbes) {
									if (bestOrigin.BackIn > returningFleets[i].BackIn)
										bestOrigin = new SpyOriginResult(closest, returningFleets[i].BackIn ?? int.MaxValue, freeSlots);
									break;
								}
							}
						}
					}
				} else {
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Cannot spy {target.Celestial.Coordinate.ToString()} from {closest.Coordinate.ToString()}, insufficient probes ({celestialProbes[closest.ID]}/{neededProbes}).");
					if (bestOrigin.BackIn < int.MaxValue)
						continue;

					tempCelestial = await _tbotOgameBridge.UpdatePlanet(closest, UpdateTypes.Constructions);
					if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
						Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
						continue;
					}
					await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Productions);
					if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
						continue;
					}

					await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);
					var buildProbes = neededProbes - celestialProbes[closest.ID];
					var cost = _calculationService.CalcPrice(Buildables.EspionageProbe, (int) buildProbes);
					tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
					if (tempCelestial.Resources.IsEnoughFor(cost)) {
						bestOrigin = new SpyOriginResult(closest, int.MaxValue, freeSlots);
					}
				}
			}

			if (bestOrigin.BackIn != 0) {
				if (bestOrigin.BackIn != int.MaxValue) {
					int interval = (int) ((1000 * bestOrigin.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds));
					if (interval < 0)
						interval = 1000;
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Not enough free slots {freeSlots}/{slotsToLeaveFree}. Waiting {TimeSpan.FromMilliseconds(interval)} for probes to return...");
					await Task.Delay(interval, _ct);
					bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
					bestOrigin.FreeSlots++;
				}
			}

			return bestOrigin;
		}

		private async Task<int> WaitForFreeSlots(int freeSlots, int slotsToLeaveFree) {
			if (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
				freeSlots = _tbotInstance.UserData.slots.Free;
			}

			_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
			while (freeSlots <= slotsToLeaveFree) {
				_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
				if (_tbotInstance.UserData.fleets.Any()) {
					int interval = (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond));
					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Waiting {TimeSpan.FromMilliseconds(interval)} for fleet to return...");
					await Task.Delay(interval, _ct);
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					freeSlots = _tbotInstance.UserData.slots.Free;
				} else {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, "Error: No fleet slots available and no fleets returning!");
					throw new Exception("No fleet slots available and no fleets returning!");
				}
			}
			return freeSlots;
		}

		protected override async Task Execute() {
			bool stop = false;
			bool stopAfterFullScan = false;
			bool finishedFullScan = false;
			try {
				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Running autofarm...");
				stopAfterFullScan = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan")
					&& (bool)_tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan;
				if ((bool) _tbotInstance.InstanceSettings.AutoFarm.Active) {				
					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					
					int freeSlots = _tbotInstance.UserData.slots.Free;
					int slotsToLeaveFree = (int) _tbotInstance.InstanceSettings.AutoFarm.SlotsToLeaveFree;
					if (freeSlots <= slotsToLeaveFree) {
						_tbotInstance.log(LogLevel.Warning, LogSender.FleetScheduler, "Unable to start auto farm, no slots available");
						return;
					}

					try {
						await PruneOldReports();

						var celestialProbes = await GetCelestialProbes();

						int numProbed = 0;

						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Detecting farm targets...");
						bool stopAutoFarm = false;

						var scanRanges = ((IEnumerable<dynamic>)_tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();

						var allScanRanges = ((IEnumerable<dynamic>)_tbotInstance.InstanceSettings.AutoFarm.ScanRange).ToList();
						int totalSystemsAcrossAllGalaxies = allScanRanges.Sum(r => (int)r.EndSystem - (int)r.StartSystem + 1);
						int instanceHash = Math.Abs(_tbotInstance.InstanceAlias.GetHashCode());
						int minSpacing = 499;
						int numSlotsGlobal = Math.Max(1, totalSystemsAcrossAllGalaxies / minSpacing);
						int globalSlotIndex = instanceHash % numSlotsGlobal;
						int globalOffset = globalSlotIndex * minSpacing;

						int targetGalaxy = 1;
						int targetSystem = 1;
						int remainingOffset = globalOffset;
						foreach (var r in allScanRanges) {
							int systemsInRange = (int)r.EndSystem - (int)r.StartSystem + 1;
							if (remainingOffset < systemsInRange) {
								targetGalaxy = (int)r.Galaxy;
								targetSystem = (int)r.StartSystem + remainingOffset;
								break;
							}
							remainingOffset -= systemsInRange;
						}

						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Instance: {_tbotInstance.InstanceAlias}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Total systems across all galaxies: {totalSystemsAcrossAllGalaxies}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Num slots globally: {numSlotsGlobal}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Global slot index: {globalSlotIndex}");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Global offset: {globalOffset} systems");
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"[GLOBAL SPACING] Target Galaxy: {targetGalaxy}, Target System: {targetSystem}");

						var orderedRanges = scanRanges
							.OrderBy(r => (int)r.Galaxy)
							.ThenBy(r => (int)r.StartSystem)
							.ToList();

						if (!orderedRanges.Any()) {
							_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "No scan ranges match galaxies with planets. Skipping AutoFarm.");
							stopAutoFarm = true;
						}

						int startRangeIndex = _tbotInstance.UserData.autoFarmLastRangeIndex;
						if (startRangeIndex < 0 || startRangeIndex >= orderedRanges.Count) {
							startRangeIndex = 0;
						}
						if (_tbotInstance.UserData.autoFarmLastGalaxy == 0 && _tbotInstance.UserData.autoFarmLastSystem == 0) {
							int idx = orderedRanges.FindIndex(r => (int)r.Galaxy == targetGalaxy);
							if (idx >= 0) startRangeIndex = idx;
							_tbotInstance.UserData.autoFarmLastRangeIndex = startRangeIndex;
						}

						bool globalOffsetUsed = false;

						for (int rangeIndex = startRangeIndex; rangeIndex < orderedRanges.Count; rangeIndex++) {
							if (stopAutoFarm)
								break;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
								break;
							}

							var range = orderedRanges[rangeIndex];

							int galaxy = (int) range.Galaxy;
							int originalStartSystem = (int) range.StartSystem;
							int endSystem = (int) range.EndSystem;
							int startSystem = originalStartSystem;
							bool isRandomStart = false;

                                 if (_tbotInstance.UserData.autoFarmLastGalaxy == galaxy &&
                                       _tbotInstance.UserData.autoFarmLastSystem >= originalStartSystem &&
                                    _tbotInstance.UserData.autoFarmLastSystem <= endSystem) {

                                startSystem = _tbotInstance.UserData.autoFarmLastSystem;
                                   isRandomStart = false;
                                       _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
                                       $"Resuming scan from Galaxy {galaxy} System {startSystem}");
                           }
                            else {
                                         startSystem = originalStartSystem;
                                        isRandomStart = false;

                                  _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
                               $"[START] Galaxy {galaxy} - Ordered start at system {startSystem}");
                              }

							int systemsToScan = endSystem - originalStartSystem + 1;
							int scannedCount = 0;

							for (var offset = 0; offset < systemsToScan && scannedCount < systemsToScan; offset++) {
								int system = startSystem + offset;
								if (system > endSystem) {
									break;
								}
								scannedCount++;

								if (stopAutoFarm)
									break;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") && ((int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0) && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
									int nextSystem = system + 1;
									if (nextSystem <= endSystem) {
										_tbotInstance.UserData.autoFarmLastRangeIndex = rangeIndex;
										_tbotInstance.UserData.autoFarmLastGalaxy = galaxy;
										_tbotInstance.UserData.autoFarmLastSystem = nextSystem;
									} else {
										int nextRangeIndex = rangeIndex + 1;
										if (nextRangeIndex < orderedRanges.Count) {
											var nextRange = orderedRanges[nextRangeIndex];
											_tbotInstance.UserData.autoFarmLastRangeIndex = nextRangeIndex;
											_tbotInstance.UserData.autoFarmLastGalaxy = (int)nextRange.Galaxy;
											_tbotInstance.UserData.autoFarmLastSystem = (int)nextRange.StartSystem;
										} else {
											_tbotInstance.UserData.autoFarmLastRangeIndex = 0;
											_tbotInstance.UserData.autoFarmLastGalaxy = 0;
											_tbotInstance.UserData.autoFarmLastSystem = 0;
											finishedFullScan = true;
										}
									}
									stopAutoFarm = true;
									break;
								}

								bool excludeSystem = ShouldExcludeSystem(galaxy, system);
								if (excludeSystem)
									continue;

								var scannedTargets = await GetScannedTargetsFromGalaxy(galaxy, system);
								_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Found {scannedTargets.Count} targets on System {galaxy}:{system}");

								if (!scannedTargets.Any())
									continue;

								if ((bool) _tbotInstance.InstanceSettings.AutoFarm.ExcludeMoons == false) {
									AddMoons(scannedTargets);
								}

								foreach (Celestial planet in scannedTargets) {
									if (stopAutoFarm)
										break;
									if (!IsTargetInMinimumRank(planet, scannedTargets)) {
										continue;
									}

									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") &&
										_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack != 0 && numProbed >= (int) _tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Maximum number of targets to probe reached, proceeding to attack.");
										break;
									}

									if (ShouldExcludePlanet(planet))
										continue;

									var target = GetFarmTarget(planet);
									if (target == null)
										continue;

									List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0) ? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials) : _tbotInstance.UserData.celestials;

									List<Celestial> closestCelestials = tempCelestials
										.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
										.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData)).ToList();

									if (!closestCelestials.Any()) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"No origin celestials available. Skipping target {target.Celestial.ToString()}");
										continue;
									}


									int neededProbes = GetNeededProbes(target);

									await Task.Delay(RandomizeHelper.CalcRandomInterval(IntervalType.LessThanFiveSeconds), _ct);

									_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
									var probesInMission = _tbotInstance.UserData.fleets.Select(c => c.Ships).Sum(c => c.EspionageProbe);

									var bestOrigin = await GetBestOrigin(closestCelestials,
										celestialProbes,
										target,
										neededProbes,
										slotsToLeaveFree,
										freeSlots);

									freeSlots = bestOrigin.FreeSlots;

									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Best origin found: {bestOrigin.Origin.Name} ({bestOrigin.Origin.Coordinate.ToString()})");

									if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate))) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Probes already on route towards {target.ToString()}.");
										continue;
									}
									if (_calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Attack, _tbotInstance.UserData.fleets).Any(f => f.Destination.IsSame(target.Celestial.Coordinate) && f.ReturnFlight == false)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Attack already on route towards {target.ToString()}.");
										continue;
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
										celestialProbes.Remove(bestOrigin.Origin.ID);
										celestialProbes.Add(bestOrigin.Origin.ID, tempCelestial.Ships.EspionageProbe);
									}

									if (celestialProbes[bestOrigin.Origin.ID] < neededProbes) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Insufficient probes ({celestialProbes[bestOrigin.Origin.ID]}/{neededProbes}).");
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildProbes") && _tbotInstance.InstanceSettings.AutoFarm.BuildProbes == true) {

											var tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Constructions);
											if (tempCelestial.Constructions.BuildingID == (int) Buildables.Shipyard || tempCelestial.Constructions.BuildingID == (int) Buildables.NaniteFactory) {
												Buildables buildingInProgress = (Buildables) tempCelestial.Constructions.BuildingID;
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: {buildingInProgress.ToString()} is upgrading.");
												break;
											}

											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Productions);
											if (tempCelestial.Productions.Any(p => p.ID == (int) Buildables.EspionageProbe)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Skipping {tempCelestial.ToString()}: Probes already building.");
												break;
											}

											var buildProbes = neededProbes - celestialProbes[bestOrigin.Origin.ID];
											var cost = _calculationService.CalcPrice(Buildables.EspionageProbe, (int) buildProbes);
											tempCelestial = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Resources);
											if (tempCelestial.Resources.IsEnoughFor(cost)) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {buildProbes}x{Buildables.EspionageProbe.ToString()}");

												try {
													await _ogameService.BuildShips(tempCelestial, Buildables.EspionageProbe, buildProbes);
													tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);
													int interval = (int) (_calculationService.CalcProductionTime(Buildables.EspionageProbe, (int) buildProbes, _tbotInstance.UserData.serverData, tempCelestial.Facilities) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Production succesfully started. Waiting {TimeSpan.FromMilliseconds(interval)} for build order to finish...");
													await Task.Delay(interval, _ct);
												} catch {
													_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
												}
											} else {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Not enough resources to build probes.");
												_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
												var spyMissions = _calculationService.GetMissionsInProgress(bestOrigin.Origin.Coordinate, Missions.Spy, _tbotInstance.UserData.fleets);
												if (spyMissions.Any()) {
													var spyMissionToWait = spyMissions.OrderBy(c => c.BackIn).First();
													int interval = (int) ((1000 * spyMissionToWait.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.LessThanASecond));
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for spy mission to return...");
													await Task.Delay(interval);
												} else {
													_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"There are not enough probes or resources to build them. Skipping this AutoFarm Execution.");
													stopAutoFarm = true;
													break;
												}
											}
										}
									}

									if (celestialProbes[bestOrigin.Origin.ID] >= neededProbes) {

										Ships ships = new();
										ships.Add(Buildables.EspionageProbe, neededProbes);

										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Spying {target.ToString()} from {bestOrigin.Origin.ToString()} with {neededProbes} probes.");

										var fleetId = (int) SendFleetCode.GenericError;
										int retryCount = 0;
										int maxRetryCount = 5;
										do {
											_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
											freeSlots = _tbotInstance.UserData.slots.Free;
											freeSlots = await WaitForFreeSlots(freeSlots, slotsToLeaveFree);

											bestOrigin.Origin = await _tbotOgameBridge.UpdatePlanet(bestOrigin.Origin, UpdateTypes.Ships);
											var availableProbes = bestOrigin.Origin.Ships.EspionageProbe;

											if (availableProbes < neededProbes) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
													$"Insufficient probes on {bestOrigin.Origin.ToString()} ({availableProbes}/{neededProbes}). Skipping {target.ToString()}.");
												fleetId = (int) SendFleetCode.GenericError;
												break;
											}

											fleetId = await _fleetScheduler.SendFleet(bestOrigin.Origin, ships, target.Celestial.Coordinate, Missions.Spy, Speeds.HundredPercent);
											if (fleetId == (int)SendFleetCode.NotEnoughSlots) {
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Another worker took the slot, waiting again for a free slot... Retry count: {retryCount}/{maxRetryCount}");
											}
											retryCount++;
										} while (fleetId == (int) SendFleetCode.NotEnoughSlots && retryCount <= maxRetryCount);

										if (fleetId > (int) SendFleetCode.GenericError) {
											freeSlots--;
											numProbed++;
											celestialProbes[bestOrigin.Origin.ID] -= neededProbes;

											if (target.State == FarmState.ProbesRequired || target.State == FarmState.FailedProbesRequired)
												continue;

											_tbotInstance.UserData.farmTargets.Remove(target);
											target.State = FarmState.ProbesSent;
											_tbotInstance.UserData.farmTargets.Add(target);

											continue;
										} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
											stop = true;
											return;
										} else if (fleetId == (int) SendFleetCode.NotEnoughSlots) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Unable to achieve a free slot after {retryCount} retries.");
											continue;
										} else {
											continue;
										}
									}
								}
							}
						}
                              if (!stopAutoFarm && (
                                    !SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "TargetsProbedBeforeAttack") ||
                                (int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack == 0 ||
                             numProbed <= (int)_tbotInstance.InstanceSettings.AutoFarm.TargetsProbedBeforeAttack)
                                  ) {
                                 _tbotInstance.UserData.autoFarmLastGalaxy = 0;
                        _tbotInstance.UserData.autoFarmLastSystem = 0;

                              _tbotInstance.UserData.autoFarmLastRangeIndex = 0;

                          _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
                                   "Full scan cycle completed, resetting scan position for next cycle");

                              if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "StopAfterFullScan") &&
                               (bool)_tbotInstance.InstanceSettings.AutoFarm.StopAfterFullScan) {

                                   _tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
                                "StopAfterFullScan=true -> Full scan completed. Stopping AutoFarm and waiting for /startautofarm.");

                                 stop = true;
                       return;
                                }
                        }

					} catch (Exception e) {
						_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Exception: {e.Message}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to parse scan range");
					}

					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
					Fleet firstReturning = _calculationService.GetLastReturningEspionage(_tbotInstance.UserData.fleets);
					if (firstReturning != null) {
						int interval = (int) ((1000 * firstReturning.BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
						if (interval < 0)
							interval = 1000;
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxWaitTime")
							&& (int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime != 0
							&& interval > (int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime * 1000) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
								$"Probe return wait {TimeSpan.FromMilliseconds(interval)} exceeds MaxWaitTime {(int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime}s. Proceeding without waiting for all probes.");
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Waiting {TimeSpan.FromMilliseconds(interval)} for all probes to return...");
							await Task.Delay(interval, _ct);
						}
					}

					_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "Processing espionage reports of found inactives...");

					await AutoFarmProcessReports();

					_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
					_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
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
					int MaxSlots = _calculationService.CalcSlotsPriority(Feature.AutoFarm, rankSlotsPriority, _tbotInstance.UserData.slots, _tbotInstance.UserData.fleets, (int) _tbotInstance.InstanceSettings.General.SlotsToLeaveFree);

					List<FarmTarget> attackTargets;
					if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal")
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Metal).ToList();
					else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal")
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal).ToList();
					else if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium")
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium).ToList();
					else
						attackTargets = _tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackPending).OrderByDescending(t => t.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources).ToList();

					if (attackTargets.Count() > 0) {
						var resourceAmount = new Resources();
						attackTargets.ForEach(target => resourceAmount = resourceAmount.Sum(target.Report.Loot(_tbotInstance.UserData.userInfo.Class)));
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking suitable farm targets... (Estimated total profit: {resourceAmount.TransportableResources})");
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, "No suitable targets found.");
						return;
					}

					Buildables cargoShip = Buildables.LargeCargo;
					if (!Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip)) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to parse cargoShip. Falling back to default LargeCargo");
						cargoShip = Buildables.LargeCargo;
					}
					if (cargoShip == Buildables.Null) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip is Null");
						return;
					}
					if (cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 0) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to send attack: cargoShip set to EspionageProbe, but this universe does not have probe cargo.");
						return;
					}

					_tbotInstance.UserData.researches = await _tbotOgameBridge.UpdateResearches();
					_tbotInstance.UserData.celestials = await _tbotOgameBridge.UpdateCelestials();
					int attackTargetsCount = 0;
					decimal lootFuelRatio = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") ? (decimal) _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio : (decimal) 0.0001;
					decimal speed = 0;
					foreach (FarmTarget target in attackTargets) {
						attackTargetsCount++;
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking target {attackTargetsCount}/{attackTargets.Count()} at {target.Celestial.Coordinate.ToString()} for {target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TransportableResources}.");
						var loot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class);
						Celestial tempCelestial = _tbotInstance.UserData.celestials.Where(c => c.Coordinate.Type == Celestials.Planet).First();
						tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
						float cargoBonus = tempCelestial.LFBonuses.GetShipCargoBonus(cargoShip);
						var numCargo = _calculationService.CalcShipNumberForPayload(loot, cargoShip, _tbotInstance.UserData.researches.HyperspaceTechnology, _tbotInstance.UserData.serverData, cargoBonus, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.serverData.ProbeCargo);
						if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "CargoSurplusPercentage") && (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage > 0) {
							numCargo = (long) Math.Round(numCargo + (numCargo / 100 * (double) _tbotInstance.InstanceSettings.AutoFarm.CargoSurplusPercentage), 0);
						}
						var attackingShips = new Ships().Add(cargoShip, numCargo);

						List<Celestial> tempCelestials = (_tbotInstance.InstanceSettings.AutoFarm.Origin.Length > 0) ? _calculationService.ParseCelestialsList(_tbotInstance.InstanceSettings.AutoFarm.Origin, _tbotInstance.UserData.celestials) : _tbotInstance.UserData.celestials;
						List<Celestial> closestCelestials = tempCelestials
							.OrderByDescending(planet => planet.Coordinate.Type == Celestials.Moon)
							.OrderBy(c => _calculationService.CalcDistance(c.Coordinate, target.Celestial.Coordinate, _tbotInstance.UserData.serverData))
							.ToList();

						Celestial fromCelestial = null;
						foreach (var c in closestCelestials) {
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(c, UpdateTypes.Ships);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
							tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
							if (tempCelestial.Ships != null && tempCelestial.Ships.GetAmount(cargoShip) >= (numCargo + _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep)) {
								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
									long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
									var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
									if (optimalSpeed == 0) {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

									} else {
										_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
										speed = optimalSpeed;
									}
								}
								if (speed == 0) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
										speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
										if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
											speed = Speeds.HundredPercent;
										}
									} else {
										speed = Speeds.HundredPercent;
									}
								}
								FleetPrediction prediction = _calculationService.CalcFleetPrediction(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, Missions.Attack, speed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								if (
									(
										!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ||
										(long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime == 0 ||
										prediction.Time <= (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime
									) &&
									prediction.Fuel <= tempCelestial.Resources.Deuterium
								) {
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No origin celestial available near destination {target.Celestial.ToString()} with enough cargo ships.");
							foreach (var closest in closestCelestials) {
								tempCelestial = closest;
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Ships);
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Resources);
								tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.LFBonuses);
								speed = 0;
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
									speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
									if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
										speed = Speeds.HundredPercent;
									}
								} else {
									speed = 0;
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
										long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
										var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
										if (optimalSpeed == 0) {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

										} else {
											_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
											speed = optimalSpeed;
										}
									}
									if (speed == 0) {
										if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
											speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
											if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
												_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
												speed = Speeds.HundredPercent;
											}
										} else {
											speed = Speeds.HundredPercent;
										}
									}
								}
								FleetPrediction prediction = _calculationService.CalcFleetPrediction(tempCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, Missions.Attack, speed, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, tempCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);

								if (
									tempCelestial.Ships.GetAmount(cargoShip) < numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep &&
									tempCelestial.Resources.Deuterium >= prediction.Fuel &&
									(
										!SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ||
										(long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime == 0 ||
										prediction.Time <= (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime
									)
								) {
									if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "BuildCargos") && _tbotInstance.InstanceSettings.AutoFarm.BuildCargos == true) {
										var neededCargos = numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep - tempCelestial.Ships.GetAmount(cargoShip);
										var cost = _calculationService.CalcPrice(cargoShip, (int) neededCargos);
										if (tempCelestial.Resources.IsEnoughFor(cost)) {
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Building {neededCargos}x{cargoShip.ToString()}");
										} else {
											var buildableCargos = _calculationService.CalcMaxBuildableNumber(cargoShip, tempCelestial.Resources);
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"{tempCelestial.ToString()}: Not enough resources to build {neededCargos}x{cargoShip.ToString()}. {buildableCargos.ToString()} will be built instead.");
											neededCargos = buildableCargos;
										}

										try {
											await _ogameService.BuildShips(tempCelestial, cargoShip, neededCargos);
											tempCelestial = await _tbotOgameBridge.UpdatePlanet(tempCelestial, UpdateTypes.Facilities);
											int interval = (int) (_calculationService.CalcProductionTime(cargoShip, (int) neededCargos, _tbotInstance.UserData.serverData, tempCelestial.Facilities) * 1000 + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Production succesfully started. Waiting {TimeSpan.FromMilliseconds(interval)} for build order to finish...");
											await Task.Delay(interval, _ct);
										} catch {
											_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, "Unable to start ship production.");
										}
									}

									if (tempCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep < (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToSend) {
										_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Insufficient {cargoShip.ToString()} on {tempCelestial.Coordinate}, require {numCargo + (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep} {cargoShip.ToString()}.");
										continue;
									}

									numCargo = tempCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
									fromCelestial = tempCelestial;
									break;
								}
							}
						}

						if (fromCelestial == null) {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Unable to attack {target.Celestial.Coordinate}. No suitable origin celestial available near the destination.");
							continue;
						}

						if (freeSlots <= slotsToLeaveFree) {
							_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
							freeSlots = _tbotInstance.UserData.slots.Free;
						}

						while (freeSlots <= slotsToLeaveFree) {
							_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
							if (_tbotInstance.UserData.fleets.Any()) {
								int interval = (int) ((1000 * _tbotInstance.UserData.fleets.OrderBy(fleet => fleet.BackIn).First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.AFewSeconds));
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxWaitTime") && (int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime != 0 && interval > (int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime * 1000) {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Time to wait greater than set {(int) _tbotInstance.InstanceSettings.AutoFarm.MaxWaitTime} seconds. Stopping autofarm.");
									return;
								} else {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Out of fleet slots. Waiting {TimeSpan.FromMilliseconds(interval)} for first fleet to return...");
									await Task.Delay(interval, _ct);
									_tbotInstance.UserData.slots = await _tbotOgameBridge.UpdateSlots();
									freeSlots = _tbotInstance.UserData.slots.Free;
								}
							} else {
								_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, "Error: No fleet slots available and no fleets returning!");
								return;
							}
						}

						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						List<Fleet> slotUsed = _tbotInstance.UserData.fleets
							.Where(fleet => fleet.Mission == Missions.Attack)
							.ToList();

						if (_tbotInstance.UserData.slots.Free > slotsToLeaveFree && slotUsed.Count() < MaxSlots) {
							fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.Ships);
							var availableShips = fromCelestial.Ships.GetAmount(cargoShip) - (long) _tbotInstance.InstanceSettings.AutoFarm.MinCargosToKeep;
							if (availableShips <= 0) {
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"No {cargoShip.ToString()} available on {fromCelestial.ToString()} (all ships already in use). Skipping target.");
								continue;
							}
							if (availableShips < numCargo) {
								_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Only {availableShips} {cargoShip.ToString()} available (needed {numCargo}). Adjusting fleet size.");
								numCargo = availableShips;
								attackingShips = new Ships();
								attackingShips.Add(cargoShip, numCargo);

								var cargoCapacity = _calculationService.CalcFleetCapacity(
									attackingShips, _tbotInstance.UserData.serverData, _tbotInstance.UserData.researches.HyperspaceTechnology,
									fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class);
								var totalLoot = target.Report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources;

								if (cargoCapacity < totalLoot) {
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm,
										$"Insufficient cargo space after adjustment. {numCargo} {cargoShip.ToString()} can carry {cargoCapacity:N0} but need {totalLoot:N0}. Skipping target.");
									continue;
								}
							}

							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacking {target.ToString()} from {fromCelestial} with {numCargo} {cargoShip.ToString()}.");
							Ships ships = new();
							fromCelestial = await _tbotOgameBridge.UpdatePlanet(fromCelestial, UpdateTypes.LFBonuses);

							speed = 0;
							if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MinLootFuelRatio") && _tbotInstance.InstanceSettings.AutoFarm.MinLootFuelRatio != 0) {
								long maxFlightTime = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "MaxFlightTime") ? (long) _tbotInstance.InstanceSettings.AutoFarm.MaxFlightTime : 86400;
								var optimalSpeed = _calculationService.CalcOptimalFarmSpeed(fromCelestial.Coordinate, target.Celestial.Coordinate, attackingShips, target.Report.Loot(_tbotInstance.UserData.userInfo.Class), lootFuelRatio, maxFlightTime, _tbotInstance.UserData.researches, _tbotInstance.UserData.serverData, fromCelestial.LFBonuses, _tbotInstance.UserData.userInfo.Class, _tbotInstance.UserData.allianceClass);
								if (optimalSpeed == 0) {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Unable to calculate a valid optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");

								} else {
									_tbotInstance.log(LogLevel.Debug, LogSender.AutoFarm, $"Calculated optimal speed: {(int) Math.Round(optimalSpeed * 10, 0)}%");
									speed = optimalSpeed;
								}
							}
							if (speed == 0) {
								if (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "FleetSpeed") && _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed > 0) {
									speed = (int) _tbotInstance.InstanceSettings.AutoFarm.FleetSpeed / 10;
									if (!_calculationService.GetValidSpeedsForClass(_tbotInstance.UserData.userInfo.Class).Any(s => s == speed)) {
										_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Invalid FleetSpeed, falling back to default 100%.");
										speed = Speeds.HundredPercent;
									}
								} else {
									speed = Speeds.HundredPercent;
								}
							}

							var fleetId = await _fleetScheduler.SendFleet(fromCelestial, attackingShips, target.Celestial.Coordinate, Missions.Attack, speed);

							if (fleetId > (int) SendFleetCode.GenericError) {
								freeSlots--;

								_successfulTargets.RecordAttack(target.Celestial.Coordinate, loot);
							} else if (fleetId == (int) SendFleetCode.AfterSleepTime) {
								stop = true;
								return;
							}

							_tbotInstance.UserData.farmTargets.Remove(target);
							target.State = FarmState.AttackSent;
							_tbotInstance.UserData.farmTargets.Add(target);
						} else {
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Unable to attack {target.Celestial.Coordinate}: {slotUsed.Count()} slots used by AutoFarm, {MaxSlots} slots usable by AutoFarm, {_tbotInstance.UserData.slots.Free} slots free, {_tbotInstance.InstanceSettings.General.SlotsToLeaveFree} must remain free.");
							return;
						}
					}
				}
			} catch (Exception e) {
				_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarm Exception: {e.Message}");
				_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
			} finally {
				if (stopAfterFullScan && finishedFullScan) {
					stop = true;
				}

				_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attacked targets: {_tbotInstance.UserData.farmTargets.Where(t => t.State == FarmState.AttackSent).Count()}");
				_tbotInstance.UserData.farmTargets.RemoveAll(t => t.State == FarmState.ProbesSent);
				if (!_tbotInstance.UserData.isSleeping) {
					if (stop) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Stopping feature.");
						await EndExecution();
					} else {
						var time = await _tbotOgameBridge.GetDateTime();
						_tbotInstance.UserData.fleets = await _fleetScheduler.UpdateFleets();
						List<Fleet> orderedFleets = _tbotInstance.UserData.fleets
							.Where(fleet => fleet.Mission == Missions.Attack)
							.ToList();
						orderedFleets = orderedFleets
							.OrderByDescending(fleet => fleet.BackIn)
							.ToList();
						long interval;
						try {
							interval = (int) ((1000 * orderedFleets.First().BackIn) + RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds));
						} catch {
							interval = RandomizeHelper.CalcRandomInterval((int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMin, (int) _tbotInstance.InstanceSettings.AutoFarm.CheckIntervalMax);
							if (interval <= 0)
								interval = RandomizeHelper.CalcRandomInterval(IntervalType.SomeSeconds);
						}
						var newTime = time.AddMilliseconds(interval);
						ChangeWorkerPeriod(interval);
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Next autofarm check at {newTime.ToString()}");
						await _tbotOgameBridge.CheckCelestials();
					}
				}
			}
		}

		private async Task AutoFarmProcessReports() {
			List<EspionageReportSummary> summaryReports = await _ogameService.GetEspionageReports();
			foreach (var summary in summaryReports) {
				if (summary.Type == EspionageReportType.Action)
					continue;

				try {
					var report = await _ogameService.GetEspionageReport(summary.ID);
					if (DateTime.Compare(report.Date.AddMinutes((double) _tbotInstance.InstanceSettings.AutoFarm.KeepReportFor), await _tbotOgameBridge.GetDateTime()) < 0) {
						await _ogameService.DeleteReport(report.ID);
						continue;
					}

					if (_tbotInstance.UserData.farmTargets.Any(t => t.HasCoords(report.Coordinate))) {
						FarmTarget target;
						var matchingTarget = _tbotInstance.UserData.farmTargets.Where(t => t.HasCoords(report.Coordinate));
						if (matchingTarget.Count() == 0) {
							if (!report.IsInactive)
								continue;
							var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
							var planet = galaxyInfo.Planets.FirstOrDefault(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation && p.HasCoords(report.Coordinate));
							if (planet != null) {
								target = GetFarmTarget(planet);
								if (target == null)
									continue;
							} else {
								continue;
							}
						} else {
							target = matchingTarget.First();
						}
						var newFarmTarget = target;

						if (target.Report != null && DateTime.Compare(report.Date, target.Report.Date) < 0) {
							await _ogameService.DeleteReport(report.ID);
							continue;
						}

						Buildables cargoShip;
						Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
						bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
						newFarmTarget.Report = report;
						if (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Metal" && report.Loot(_tbotInstance.UserData.userInfo.Class).Metal > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Crystal" && report.Loot(_tbotInstance.UserData.userInfo.Class).Crystal > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| _tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "Deuterium" && report.Loot(_tbotInstance.UserData.userInfo.Class).Deuterium > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources
							|| (_tbotInstance.InstanceSettings.AutoFarm.PreferedResource == "" && report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources)) {
							if (!report.HasFleetInformation || !report.HasDefensesInformation) {
								if (target.State == FarmState.ProbesRequired)
									newFarmTarget.State = FarmState.FailedProbesRequired;
								else if (target.State == FarmState.FailedProbesRequired)
									newFarmTarget.State = FarmState.NotSuitable;
								else
									newFarmTarget.State = FarmState.ProbesRequired;

								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Need more probes on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else if (report.IsDefenceless(isUsingProbes)) {
								newFarmTarget.State = FarmState.AttackPending;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
							} else {
								newFarmTarget.State = FarmState.NotSuitable;
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - defences present.");
							bool blacklistActive = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActive) {
								int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
								_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
								_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
							}
							}
						} else {
							newFarmTarget.State = FarmState.NotSuitable;
							_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not suitable - insufficient loot ({report.Loot(_tbotInstance.UserData.userInfo.Class)})");
							bool blacklistActiveLowRes = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
								SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "Active") &&
								(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
							if (blacklistActiveLowRes) {
								long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long)500000);
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
									int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
									_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
									_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
								}
							}
						}

						_tbotInstance.UserData.farmTargets.Remove(target);
						_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
					} else {
					bool processAllReports = (SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "ProcessAllReports") &&
						(bool) _tbotInstance.InstanceSettings.AutoFarm.ProcessAllReports) ||
						(SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
						SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ProcessAllReports") &&
						(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.ProcessAllReports);
					if (processAllReports && report.IsInactive) {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Processing report for {report.Coordinate} not scanned by TBot (ProcessAllReports enabled)...");
						var galaxyInfo = await _ogameService.GetGalaxyInfo(report.Coordinate.Galaxy, report.Coordinate.System);
						var planet = galaxyInfo.Planets.FirstOrDefault(p => p != null && p.Inactive && !p.Administrator && !p.Banned && !p.Vacation && p.HasCoords(report.Coordinate));
						if (planet != null) {
							var target = GetFarmTarget(planet);
							if (target != null) {
								var newFarmTarget = target;
								Buildables cargoShip;
								Enum.TryParse<Buildables>((string) _tbotInstance.InstanceSettings.AutoFarm.CargoType, true, out cargoShip);
								bool isUsingProbes = cargoShip == Buildables.EspionageProbe && _tbotInstance.UserData.serverData.ProbeCargo == 1 ? true : false;
								newFarmTarget.Report = report;
								if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources > _tbotInstance.InstanceSettings.AutoFarm.MinimumResources) {
									if (report.HasFleetInformation && report.HasDefensesInformation) {
										if (report.IsDefenceless(isUsingProbes)) {
											newFarmTarget.State = FarmState.AttackPending;
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Attack pending on {report.Coordinate}. Loot: {report.Loot(_tbotInstance.UserData.userInfo.Class)}");
										} else {
											newFarmTarget.State = FarmState.NotSuitable;
											bool blacklistActiveDefense2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
												(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
											if (blacklistActiveDefense2) {
												int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
												_blacklist.AddTarget(report.Coordinate, BlacklistReason.HasDefense, hoursUntilReset);
												_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (defenses present).");
											}
										}
									}
								} else {
									newFarmTarget.State = FarmState.NotSuitable;
									bool blacklistActiveLowRes2 = SettingsService.IsSettingSet(_tbotInstance.InstanceSettings.AutoFarm, "Blacklist") &&
										(bool) _tbotInstance.InstanceSettings.AutoFarm.Blacklist.Active;
									if (blacklistActiveLowRes2) {
										long minResources = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "MinimumResourcesToNotBlacklist", (long)500000);
										if (report.Loot(_tbotInstance.UserData.userInfo.Class).TotalResources < minResources) {
											int hoursUntilReset = SettingsService.GetSetting(_tbotInstance.InstanceSettings.AutoFarm.Blacklist, "ResetAfterHours", 48);
											_blacklist.AddTarget(report.Coordinate, BlacklistReason.LowResources, hoursUntilReset);
											_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} blacklisted for {hoursUntilReset}h (low resources: {report.Loot(_tbotInstance.UserData.userInfo.Class)}).");
										}
									}
								}
								_tbotInstance.UserData.farmTargets.Remove(target);
								_tbotInstance.UserData.farmTargets.Add(newFarmTarget);
							}
						}
					} else {
						_tbotInstance.log(LogLevel.Information, LogSender.AutoFarm, $"Target {report.Coordinate} not scanned by TBot, ignoring...");
					}
					}
				} catch (Exception e) {
					_tbotInstance.log(LogLevel.Error, LogSender.AutoFarm, $"AutoFarmProcessReports Exception: {e.Message}");
					_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Stacktrace: {e.StackTrace}");
					continue;
				}
			}

			int deleteRetries = 3;
			for (int i = 0; i < deleteRetries; i++) {
				try {
					await _ogameService.DeleteAllEspionageReports();
					break;
				} catch (Exception e) when (e.Message.Contains("503") || e.Message.Contains("Service Unavailable") || e.Message.Contains("Unable to delete")) {
					if (i < deleteRetries - 1) {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Failed to delete espionage reports (503 error), retry {i + 1}/{deleteRetries}...");
						await Task.Delay(3000);
					} else {
						_tbotInstance.log(LogLevel.Warning, LogSender.AutoFarm, $"Could not delete espionage reports after {deleteRetries} attempts. Will try next cycle.");
					}
				}
			}

		}
	}
}