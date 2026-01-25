using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
	public class SuccessfulTarget {
		public string Coordinate { get; set; }
		public DateTime FirstAttacked { get; set; }
		public DateTime LastAttacked { get; set; }
		public int TimesAttacked { get; set; }
		public Resources TotalLootCollected { get; set; }
		public Resources AverageLoot { get; set; }

		// Parameterless constructor for JSON deserialization
		public SuccessfulTarget() {
			TotalLootCollected = new Resources();
			AverageLoot = new Resources();
		}

		public SuccessfulTarget(string coordinate, Resources loot) {
			Coordinate = coordinate;
			FirstAttacked = DateTime.UtcNow;
			LastAttacked = DateTime.UtcNow;
			TimesAttacked = 1;
			TotalLootCollected = new Resources {
				Metal = loot.Metal,
				Crystal = loot.Crystal,
				Deuterium = loot.Deuterium
			};
			AverageLoot = new Resources {
				Metal = loot.Metal,
				Crystal = loot.Crystal,
				Deuterium = loot.Deuterium
			};
		}

		public void AddAttack(Resources loot) {
			LastAttacked = DateTime.UtcNow;
			TimesAttacked++;
			TotalLootCollected.Metal += loot.Metal;
			TotalLootCollected.Crystal += loot.Crystal;
			TotalLootCollected.Deuterium += loot.Deuterium;

			// Recalculate average
			AverageLoot.Metal = TotalLootCollected.Metal / TimesAttacked;
			AverageLoot.Crystal = TotalLootCollected.Crystal / TimesAttacked;
			AverageLoot.Deuterium = TotalLootCollected.Deuterium / TimesAttacked;
		}
	}

	public class AutoFarmSuccessfulTargets {
		private List<SuccessfulTarget> _successfulTargets;
		private readonly object _lock = new object();
		private string _filePath;

		public AutoFarmSuccessfulTargets() {
			_successfulTargets = new List<SuccessfulTarget>();
		}

		public AutoFarmSuccessfulTargets(string filePath) {
			// Ensure data folder exists
			string dataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
			if (!Directory.Exists(dataFolder)) {
				Directory.CreateDirectory(dataFolder);
			}
			_filePath = Path.Combine(dataFolder, filePath);
			_successfulTargets = new List<SuccessfulTarget>();
			LoadFromFile();
		}

		public void RecordAttack(Coordinate coordinate, Resources loot) {
			lock (_lock) {
				string coordStr = $"{coordinate.Galaxy}:{coordinate.System}:{coordinate.Position}";

				var existing = _successfulTargets.FirstOrDefault(t => t.Coordinate == coordStr);

				if (existing != null) {
					existing.AddAttack(loot);
				} else {
					_successfulTargets.Add(new SuccessfulTarget(coordStr, loot));
				}

				SaveToFile();
			}
		}

		public SuccessfulTarget GetTarget(Coordinate coordinate) {
			lock (_lock) {
				string coordStr = $"{coordinate.Galaxy}:{coordinate.System}:{coordinate.Position}";
				return _successfulTargets.FirstOrDefault(t => t.Coordinate == coordStr);
			}
		}

		public List<SuccessfulTarget> GetAllTargets() {
			lock (_lock) {
				return new List<SuccessfulTarget>(_successfulTargets);
			}
		}

		public int GetTotalCount() {
			lock (_lock) {
				return _successfulTargets.Count;
			}
		}

		public long GetTotalLootCollected() {
			lock (_lock) {
				return _successfulTargets.Sum(t => t.TotalLootCollected.Metal + t.TotalLootCollected.Crystal + t.TotalLootCollected.Deuterium);
			}
		}

		private void LoadFromFile() {
			if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) {
				return;
			}

			try {
				string json = File.ReadAllText(_filePath);
				var loaded = JsonConvert.DeserializeObject<List<SuccessfulTarget>>(json);
				if (loaded != null) {
					_successfulTargets = loaded;
				}
			} catch (Exception) {
				// If loading fails, start with empty list
				_successfulTargets = new List<SuccessfulTarget>();
			}
		}

		private void SaveToFile() {
			if (string.IsNullOrEmpty(_filePath)) {
				return;
			}

			try {
				string json = JsonConvert.SerializeObject(_successfulTargets, Formatting.Indented);
				File.WriteAllText(_filePath, json);
			} catch (Exception) {
				// Silently ignore save errors to not break AutoFarm
			}
		}
	}
}
