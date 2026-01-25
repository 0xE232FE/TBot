using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Model {
	/// <summary>
	/// Represents a bot instance in the shared farm state
	/// </summary>
	public class FarmBotInstance {
		public string Name { get; set; }
		public DateTime LastHeartbeat { get; set; }
		public bool IsActive { get; set; }

		public bool IsAlive(TimeSpan timeout) {
			return (DateTime.UtcNow - LastHeartbeat) < timeout;
		}
	}

	/// <summary>
	/// Represents a scanned target in the shared state
	/// </summary>
	public class ScannedTarget {
		public string Coord { get; set; } // Format: "G:S:P"
		public string ScannedBy { get; set; }
		public DateTime ScannedAt { get; set; }
		public DateTime ExpiresAt { get; set; }
		public long TotalResources { get; set; }
		public bool HasFleet { get; set; }
		public bool HasDefense { get; set; }

		public bool IsExpired() {
			return DateTime.UtcNow >= ExpiresAt;
		}

		public Coordinate GetCoordinate() {
			var parts = Coord.Split(':');
			if (parts.Length == 3) {
				return new Coordinate {
					Galaxy = int.Parse(parts[0]),
					System = int.Parse(parts[1]),
					Position = int.Parse(parts[2]),
					Type = Celestials.Planet
				};
			}
			return null;
		}
	}

	/// <summary>
	/// Represents a claimed attack in the shared state
	/// </summary>
	public class ClaimedAttack {
		public string Coord { get; set; }
		public string ClaimedBy { get; set; }
		public DateTime ClaimedAt { get; set; }
		public DateTime? AttackSentAt { get; set; }
		public DateTime ReturnsAt { get; set; }

		public bool IsExpired() {
			// Claim expires if attack returns or if claim is older than 2 hours without being sent
			if (DateTime.UtcNow >= ReturnsAt) {
				return true;
			}
			if (!AttackSentAt.HasValue && (DateTime.UtcNow - ClaimedAt).TotalHours > 2) {
				return true; // Stale claim - probably crashed before sending
			}
			return false;
		}

		public Coordinate GetCoordinate() {
			var parts = Coord.Split(':');
			if (parts.Length == 3) {
				return new Coordinate {
					Galaxy = int.Parse(parts[0]),
					System = int.Parse(parts[1]),
					Position = int.Parse(parts[2]),
					Type = Celestials.Planet
				};
			}
			return null;
		}
	}

	/// <summary>
	/// Shared state for coordinating AutoFarm between multiple bot instances
	/// Thread-safe with file locking
	/// </summary>
	public class SharedFarmState {
		private const string DEFAULT_FILENAME = "shared_farm_state.json";
		private const int MAX_RETRY_ATTEMPTS = 5;
		private const int RETRY_DELAY_MS = 100;

		public string Version { get; set; } = "1.0";
		public DateTime LastUpdated { get; set; }
		public List<FarmBotInstance> Instances { get; set; }
		public List<ScannedTarget> ScannedTargets { get; set; }
		public List<ClaimedAttack> ClaimedAttacks { get; set; }

		private static readonly object _globalLock = new object();
		private string _filePath;

		public SharedFarmState() {
			Instances = new List<FarmBotInstance>();
			ScannedTargets = new List<ScannedTarget>();
			ClaimedAttacks = new List<ClaimedAttack>();
			LastUpdated = DateTime.UtcNow;
		}

		/// <summary>
		/// Initialize shared state with file path
		/// </summary>
		public static SharedFarmState Initialize(string instanceName = null) {
			string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			if (!Directory.Exists(dataFolder)) {
				Directory.CreateDirectory(dataFolder);
			}

			string filePath = Path.Combine(dataFolder, DEFAULT_FILENAME);
			var state = LoadFromFile(filePath);
			state._filePath = filePath;

			// Register or update this instance
			if (!string.IsNullOrEmpty(instanceName)) {
				state.UpdateInstanceHeartbeat(instanceName);
			}

			return state;
		}

		/// <summary>
		/// Load shared state from file with retries and corruption handling
		/// </summary>
		private static SharedFarmState LoadFromFile(string filePath) {
			lock (_globalLock) {
				if (!File.Exists(filePath)) {
					return new SharedFarmState();
				}

				for (int attempt = 0; attempt < MAX_RETRY_ATTEMPTS; attempt++) {
					try {
						string json = File.ReadAllText(filePath);
						var state = JsonConvert.DeserializeObject<SharedFarmState>(json);

						if (state != null) {
							// Cleanup expired entries on load
							state.CleanupExpiredEntries();
							return state;
						}
					} catch (IOException) when (attempt < MAX_RETRY_ATTEMPTS - 1) {
						// File is locked by another process, retry
						Thread.Sleep(RETRY_DELAY_MS * (attempt + 1));
					} catch (JsonException) {
						// Corrupt file - backup and create new
						try {
							string backupPath = filePath + ".corrupt." + DateTime.UtcNow.Ticks;
							File.Move(filePath, backupPath);
						} catch {
							// Ignore backup errors
						}
						return new SharedFarmState();
					} catch {
						// Other errors - start fresh
						return new SharedFarmState();
					}
				}

				// All retries failed
				return new SharedFarmState();
			}
		}

		/// <summary>
		/// Save shared state to file with atomic write
		/// </summary>
		public void Save() {
			if (string.IsNullOrEmpty(_filePath)) {
				return;
			}

			lock (_globalLock) {
				LastUpdated = DateTime.UtcNow;
				CleanupExpiredEntries();

				string tempPath = _filePath + ".tmp";

				try {
					string json = JsonConvert.SerializeObject(this, Formatting.Indented);
					File.WriteAllText(tempPath, json);

					// Atomic replace
					if (File.Exists(_filePath)) {
						File.Delete(_filePath);
					}
					File.Move(tempPath, _filePath);
				} catch {
					// Cleanup temp file on error
					if (File.Exists(tempPath)) {
						try { File.Delete(tempPath); } catch { }
					}
					// Don't throw - fail silently to not break AutoFarm
				}
			}
		}

		/// <summary>
		/// Reload state from file (get latest changes from other bots)
		/// </summary>
		public void Reload() {
			if (string.IsNullOrEmpty(_filePath)) {
				return;
			}

			var fresh = LoadFromFile(_filePath);
			fresh._filePath = _filePath;

			this.Version = fresh.Version;
			this.LastUpdated = fresh.LastUpdated;
			this.Instances = fresh.Instances;
			this.ScannedTargets = fresh.ScannedTargets;
			this.ClaimedAttacks = fresh.ClaimedAttacks;
		}

		/// <summary>
		/// Update heartbeat for this bot instance
		/// </summary>
		public void UpdateInstanceHeartbeat(string instanceName) {
			var instance = Instances.FirstOrDefault(i => i.Name == instanceName);
			if (instance == null) {
				instance = new FarmBotInstance {
					Name = instanceName,
					IsActive = true
				};
				Instances.Add(instance);
			}

			instance.LastHeartbeat = DateTime.UtcNow;
			instance.IsActive = true;
		}

		/// <summary>
		/// Get list of active bot instances (heartbeat within last 5 minutes)
		/// </summary>
		public List<FarmBotInstance> GetActiveBots() {
			return Instances.Where(i => i.IsAlive(TimeSpan.FromMinutes(5))).ToList();
		}

		/// <summary>
		/// Check if a target was recently scanned (within last X hours)
		/// </summary>
		public ScannedTarget GetRecentScan(Coordinate coord, int withinHours = 8) {
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			return ScannedTargets.FirstOrDefault(s =>
				s.Coord == coordStr &&
				(DateTime.UtcNow - s.ScannedAt).TotalHours < withinHours);
		}

		/// <summary>
		/// Add a scanned target to shared state
		/// </summary>
		public void AddScannedTarget(Coordinate coord, string scannedBy, long totalResources,
			bool hasFleet, bool hasDefense, int expiresInHours = 8) {

			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";

			// Remove existing scan of same coordinate
			ScannedTargets.RemoveAll(s => s.Coord == coordStr);

			ScannedTargets.Add(new ScannedTarget {
				Coord = coordStr,
				ScannedBy = scannedBy,
				ScannedAt = DateTime.UtcNow,
				ExpiresAt = DateTime.UtcNow.AddHours(expiresInHours),
				TotalResources = totalResources,
				HasFleet = hasFleet,
				HasDefense = hasDefense
			});
		}

		/// <summary>
		/// Update an existing scanned target with report data (after spy report arrives)
		/// </summary>
		public void UpdateScannedTarget(Coordinate coord, long totalResources, bool hasFleet, bool hasDefense) {
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			var existing = ScannedTargets.FirstOrDefault(s => s.Coord == coordStr);

			if (existing != null) {
				existing.TotalResources = totalResources;
				existing.HasFleet = hasFleet;
				existing.HasDefense = hasDefense;
			}
		}

		/// <summary>
		/// Get all scanned targets with good resources that haven't been claimed for attack
		/// </summary>
		public List<ScannedTarget> GetGoodTargets(long minResources, int maxAgeMinutes = 30) {
			return ScannedTargets
				.Where(s => !s.IsExpired())
				.Where(s => (DateTime.UtcNow - s.ScannedAt).TotalMinutes <= maxAgeMinutes) // Not too old!
				.Where(s => s.TotalResources > 0) // Must have report data (TotalResources > 0)
				.Where(s => s.TotalResources >= minResources)
				.Where(s => !s.HasFleet)
				.Where(s => !s.HasDefense)
				.Where(s => !IsTargetClaimed(s.GetCoordinate()))
				.OrderByDescending(s => s.TotalResources)
				.ToList();
		}

		/// <summary>
		/// Get all targets that don't have report data yet (TotalResources = 0)
		/// These should be re-scanned with priority
		/// </summary>
		public List<ScannedTarget> GetTargetsWithoutReports(string scannedByInstance = null) {
			var query = ScannedTargets
				.Where(s => !s.IsExpired())
				.Where(s => s.TotalResources == 0); // No report data yet

			// Filter by instance if specified (only re-scan own targets!)
			if (!string.IsNullOrEmpty(scannedByInstance)) {
				query = query.Where(s => s.ScannedBy == scannedByInstance);
			}

			return query.OrderBy(s => s.ScannedAt).ToList();
		}

		/// <summary>
		/// Check if a scan is too old and should be refreshed
		/// </summary>
		public bool ShouldRescan(Coordinate coord, int maxAgeMinutes = 30) {
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			var scan = ScannedTargets.FirstOrDefault(s => s.Coord == coordStr && !s.IsExpired());

			if (scan == null) return true; // No scan exists

			// If we have no report data yet (TotalResources = 0), we MUST rescan to get real data
			if (scan.TotalResources == 0) return true;

			return (DateTime.UtcNow - scan.ScannedAt).TotalMinutes > maxAgeMinutes;
		}

		/// <summary>
		/// Try to claim a target for attack (returns true if successfully claimed)
		/// </summary>
		public bool TryClaimAttack(Coordinate coord, string claimedBy, DateTime returnsAt) {
			Reload(); // Get latest state first

			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";

			// Check if already claimed by someone else
			var existingClaim = ClaimedAttacks.FirstOrDefault(c => c.Coord == coordStr && !c.IsExpired());
			if (existingClaim != null && existingClaim.ClaimedBy != claimedBy) {
				return false; // Already claimed by another bot
			}

			// Claim it
			ClaimedAttacks.RemoveAll(c => c.Coord == coordStr); // Remove old claims
			ClaimedAttacks.Add(new ClaimedAttack {
				Coord = coordStr,
				ClaimedBy = claimedBy,
				ClaimedAt = DateTime.UtcNow,
				AttackSentAt = null,
				ReturnsAt = returnsAt
			});

			Save();
			return true;
		}

		/// <summary>
		/// Mark attack as sent (update claim with sent timestamp)
		/// </summary>
		public void MarkAttackSent(Coordinate coord, string claimedBy) {
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			var claim = ClaimedAttacks.FirstOrDefault(c => c.Coord == coordStr && c.ClaimedBy == claimedBy);
			if (claim != null) {
				claim.AttackSentAt = DateTime.UtcNow;
				Save();
			}
		}

		/// <summary>
		/// Check if a target is currently claimed by any bot
		/// </summary>
		public bool IsTargetClaimed(Coordinate coord) {
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			return ClaimedAttacks.Any(c => c.Coord == coordStr && !c.IsExpired());
		}

		/// <summary>
		/// Get who claimed a specific target
		/// </summary>
		public string GetClaimOwner(Coordinate coord) {
			string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
			return ClaimedAttacks.FirstOrDefault(c => c.Coord == coordStr && !c.IsExpired())?.ClaimedBy;
		}

		/// <summary>
		/// Remove expired scans, claims, and inactive bots
		/// </summary>
		private void CleanupExpiredEntries() {
			ScannedTargets.RemoveAll(s => s.IsExpired());
			ClaimedAttacks.RemoveAll(c => c.IsExpired());

			// Mark inactive bots
			foreach (var instance in Instances) {
				if (!instance.IsAlive(TimeSpan.FromMinutes(5))) {
					instance.IsActive = false;
				}
			}
		}

		/// <summary>
		/// Get statistics for debugging
		/// </summary>
		public string GetStats() {
			CleanupExpiredEntries();
			return $"Instances: {Instances.Count(i => i.IsActive)} active, " +
			       $"Scanned: {ScannedTargets.Count}, " +
			       $"Claims: {ClaimedAttacks.Count}";
		}

		/// <summary>
		/// Atomically add a scanned target using a cross-process mutex lock
		/// This prevents race conditions when multiple bot instances write simultaneously
		/// </summary>
		public static void AddScannedTargetAtomic(string filePath, Coordinate coord, string scannedBy,
			long totalResources, bool hasFleet, bool hasDefense) {

			// Use a named mutex that works across processes
			string mutexName = "Global\\TBot_SharedFarmState_Mutex";
			using (var mutex = new Mutex(false, mutexName)) {
				try {
					// Wait up to 5 seconds for the mutex
					if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) {
						// Could not acquire mutex - skip this update to avoid blocking
						return;
					}

					try {
						// Load current state
						var state = LoadFromFile(filePath);
						state._filePath = filePath;

						// Add the scanned target
						state.AddScannedTarget(coord, scannedBy, totalResources, hasFleet, hasDefense);

						// Save atomically
						state.Save();
					} finally {
						mutex.ReleaseMutex();
					}
				} catch (AbandonedMutexException) {
					// Mutex was abandoned by another process - we now own it, continue
					try {
						var state = LoadFromFile(filePath);
						state._filePath = filePath;
						state.AddScannedTarget(coord, scannedBy, totalResources, hasFleet, hasDefense);
						state.Save();
					} finally {
						try { mutex.ReleaseMutex(); } catch { }
					}
				} catch {
					// Fail silently to not break AutoFarm
				}
			}
		}

		/// <summary>
		/// Atomically check if target should be scanned and reserve it if yes
		/// Returns true if successfully reserved (not recently scanned), false otherwise
		/// This prevents race conditions where multiple bots check and scan the same target
		/// </summary>
		public static bool TryReserveScan(string filePath, Coordinate coord, string scannedBy,
			int withinHours = 8, int maxAgeMinutes = 30) {

			// Use a named mutex that works across processes
			string mutexName = "Global\\TBot_SharedFarmState_Mutex";
			using (var mutex = new Mutex(false, mutexName)) {
				try {
					// Wait up to 5 seconds for the mutex
					if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) {
						// Could not acquire mutex - assume can scan to avoid blocking
						return true;
					}

					try {
						// Load current state
						var state = LoadFromFile(filePath);
						state._filePath = filePath;

						// Check if scan is too old and needs refresh
						if (state.ShouldRescan(coord, maxAgeMinutes)) {
							// Old or missing - reserve it
							state.AddScannedTarget(coord, scannedBy, 0, false, false);
							state.Save();
							return true;
						}

						string coordStr = $"{coord.Galaxy}:{coord.System}:{coord.Position}";
						var recentScan = state.ScannedTargets.FirstOrDefault(s =>
							s.Coord == coordStr &&
							(DateTime.UtcNow - s.ScannedAt).TotalHours < withinHours);

						if (recentScan != null) {
							// Already scanned recently
							if (recentScan.ScannedBy == scannedBy) {
								// We scanned it - check if old enough to re-scan
								if (state.ShouldRescan(coord, maxAgeMinutes)) {
									state.AddScannedTarget(coord, scannedBy, 0, false, false);
									state.Save();
									return true;
								}
								return false;
							} else {
								// Another bot scanned it - check if fresh
								if ((DateTime.UtcNow - recentScan.ScannedAt).TotalMinutes <= maxAgeMinutes) {
									return false; // Fresh scan by other bot - don't scan
								}
								// Scan is old - reserve it
								state.AddScannedTarget(coord, scannedBy, 0, false, false);
								state.Save();
								return true;
							}
						}

						// Not scanned recently - reserve it
						state.AddScannedTarget(coord, scannedBy, 0, false, false);
						state.Save();
						return true;
					} finally {
						mutex.ReleaseMutex();
					}
				} catch (AbandonedMutexException) {
					// Mutex was abandoned - we own it now, continue
					try {
						var state = LoadFromFile(filePath);
						state._filePath = filePath;

						// Simple check - if no recent scan, reserve it
						if (state.ShouldRescan(coord, maxAgeMinutes)) {
							state.AddScannedTarget(coord, scannedBy, 0, false, false);
							state.Save();
							return true;
						}
						return false;
					} finally {
						try { mutex.ReleaseMutex(); } catch { }
					}
				} catch {
					// Fail silently - return true (allow scan)
					return true;
				}
			}
		}

		/// <summary>
		/// Atomically try to claim an attack using a cross-process mutex lock
		/// Returns true if successfully claimed, false if already claimed by another bot
		/// </summary>
		public static bool TryClaimAttackAtomic(string filePath, Coordinate coord, string claimedBy, DateTime returnsAt) {
			// Use a named mutex that works across processes
			string mutexName = "Global\\TBot_SharedFarmState_Mutex";
			using (var mutex = new Mutex(false, mutexName)) {
				try {
					// Wait up to 5 seconds for the mutex
					if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) {
						// Could not acquire mutex - assume not claimed to avoid blocking
						return false;
					}

					try {
						// Load current state
						var state = LoadFromFile(filePath);
						state._filePath = filePath;

						// Try to claim
						bool claimed = state.TryClaimAttack(coord, claimedBy, returnsAt);

						// Return result (Save() already called inside TryClaimAttack)
						return claimed;
					} finally {
						mutex.ReleaseMutex();
					}
				} catch (AbandonedMutexException) {
					// Mutex was abandoned by another process - we now own it, continue
					try {
						var state = LoadFromFile(filePath);
						state._filePath = filePath;
						bool claimed = state.TryClaimAttack(coord, claimedBy, returnsAt);
						return claimed;
					} finally {
						try { mutex.ReleaseMutex(); } catch { }
					}
				} catch {
					// Fail silently - return false (not claimed)
					return false;
				}
			}
		}
	}
}
