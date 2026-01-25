using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public enum BlacklistReason {
		HasFleet,
		HasDefense,
		LowResources,
		ManuallyAdded
	}

	public class BlacklistedTarget {
		public Coordinate Coordinate { get; set; }
		public BlacklistReason Reason { get; set; }
		public DateTime BlacklistedAt { get; set; }
		public DateTime ExpiresAt { get; set; }

		// Parameterless constructor for JSON deserialization
		public BlacklistedTarget() {
		}

		public BlacklistedTarget(Coordinate coordinate, BlacklistReason reason, DateTime expiresAt) {
			Coordinate = coordinate;
			Reason = reason;
			BlacklistedAt = DateTime.UtcNow;
			ExpiresAt = expiresAt;
		}

		public bool IsExpired() {
			return DateTime.UtcNow >= ExpiresAt;
		}
	}

	public class AutoFarmBlacklist {
		private List<BlacklistedTarget> _blacklistedTargets;
		private readonly object _lock = new object();
		private string _filePath;

		public AutoFarmBlacklist() {
			_blacklistedTargets = new List<BlacklistedTarget>();
		}

		public AutoFarmBlacklist(string filePath) {
			// Ensure data folder exists
			string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			if (!Directory.Exists(dataFolder)) {
				Directory.CreateDirectory(dataFolder);
			}
			_filePath = Path.Combine(dataFolder, filePath);
			_blacklistedTargets = new List<BlacklistedTarget>();
			LoadFromFile();
		}

		public void AddTarget(Coordinate coordinate, BlacklistReason reason, int hoursUntilReset) {
			lock (_lock) {
				// Remove existing entry if present
				_blacklistedTargets.RemoveAll(t => t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);

				DateTime expiresAt = DateTime.UtcNow.AddHours(hoursUntilReset);
				_blacklistedTargets.Add(new BlacklistedTarget(coordinate, reason, expiresAt));
				SaveToFile();
			}
		}

		public bool IsBlacklisted(Coordinate coordinate) {
			lock (_lock) {
				// Remove expired entries first
				CleanupExpiredTargets();

				return _blacklistedTargets.Any(t =>
					t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);
			}
		}

		public BlacklistedTarget GetBlacklistedTarget(Coordinate coordinate) {
			lock (_lock) {
				CleanupExpiredTargets();

				return _blacklistedTargets.FirstOrDefault(t =>
					t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);
			}
		}

		public void RemoveTarget(Coordinate coordinate) {
			lock (_lock) {
				_blacklistedTargets.RemoveAll(t =>
					t.Coordinate.Galaxy == coordinate.Galaxy
					&& t.Coordinate.System == coordinate.System
					&& t.Coordinate.Position == coordinate.Position);
			}
			SaveToFile();
		}

		public void ClearAll() {
			lock (_lock) {
				_blacklistedTargets.Clear();
			SaveToFile();
			}
		}

		public int GetBlacklistedCount() {
			lock (_lock) {
				CleanupExpiredTargets();
				return _blacklistedTargets.Count;
			}
		}

		public List<BlacklistedTarget> GetAllBlacklisted() {
			lock (_lock) {
				CleanupExpiredTargets();
				return new List<BlacklistedTarget>(_blacklistedTargets);
			}
		}

		private void CleanupExpiredTargets() {
			_blacklistedTargets.RemoveAll(t => t.IsExpired());
		}

		public void ManualCleanup() {
			lock (_lock) {
				CleanupExpiredTargets();
			}
		}
	private void LoadFromFile() {
		if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) {
			return;
		}

		try {
			string json = File.ReadAllText(_filePath);
			var loaded = JsonConvert.DeserializeObject<List<BlacklistedTarget>>(json);
			if (loaded != null) {
				_blacklistedTargets = loaded;
				// Clean up expired entries on load
				CleanupExpiredTargets();
			}
		} catch (Exception) {
			// If loading fails, start with empty list
			_blacklistedTargets = new List<BlacklistedTarget>();
		}
	}

	private void SaveToFile() {
		if (string.IsNullOrEmpty(_filePath)) {
			return;
		}

		try {
			string json = JsonConvert.SerializeObject(_blacklistedTargets, Formatting.Indented);
			File.WriteAllText(_filePath, json);
		} catch (Exception) {
			// Silently ignore save errors to not break AutoFarm
		}
	}
	}
}
