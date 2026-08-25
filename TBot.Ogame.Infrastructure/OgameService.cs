using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using TBot.Ogame.Infrastructure.Models;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using RestSharp;
using Newtonsoft.Json;
using System.Text;
using TBot.Ogame.Infrastructure.Exceptions;
using TBot.Ogame.Infrastructure.Enums;
using Newtonsoft.Json.Linq;
using TBot.Common.Logging;
using System.Net.Http;
using Polly;
using Polly.Extensions.Http;
using Polly.Retry;

namespace TBot.Ogame.Infrastructure {

	public class OgameService : IOgameService {

		private readonly ILoggerService<OgameService> _logger;
		private HttpClient _client;
		private Process? _ogamedProcess;
		private string _username;

		private Credentials _credentials;
		private Device _device;
		private string _host;
		private int _port;
		private string _captchaKey;
		private ProxySettings _proxySettings;
		private string _cookiesPath;

		public event EventHandler OnError;

		bool _mustKill = false;	// used whenever we want to actually kill ogamed

		public OgameService(ILoggerService<OgameService> logger) {
			_logger = logger;
		}

		public void Initialize(Credentials credentials,
				Device device,
				ProxySettings proxySettings,
				string host = "127.0.0.1",
				int port = 8080,
				string captchaKey = "") {
			_credentials = credentials;
			_device = device;
			_host = host;
			_port = port;
			_captchaKey = captchaKey;
			_proxySettings = proxySettings;

			_username = credentials.Username;

			_ogamedProcess = ExecuteOgamedExecutable(credentials, device, host, port, captchaKey, proxySettings);

			_client = new HttpClient() {
				BaseAddress = new Uri($"http://{host}:{port}/"),
				Timeout = TimeSpan.FromSeconds(60)
			};
			if (credentials.BasicAuthUsername != "" && credentials.BasicAuthPassword != "") {
				_client.DefaultRequestHeaders.Authorization =
					new AuthenticationHeaderValue("Basic",
					Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes($"{credentials.BasicAuthUsername}:{credentials.BasicAuthPassword}")));
			}
		}

		public bool ValidatePrerequisites() {
			if (!File.Exists(Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), GetExecutableName()))) {
				_logger.WriteLog(LogLevel.Error, LogSender.Main, $"\"{GetExecutableName()}\" not found. Cannot proceed...");
				return false;
			}
			return true;
		}

		public bool IsPortAvailable(string host, int port = 8080) {
			try {
				// Host is not needed. We need to bind locally
				IPAddress localAddr = IPAddress.Parse("127.0.0.1");
				var server = new TcpListener(localAddr, port);

				server.Start(); // Should raise exception if not available

				server.Stop();

				return true;
			} catch (Exception e) {
				_logger.WriteLog(LogLevel.Information, LogSender.OGameD, $"PortAvailable({port} Error: {e.Message}");
				return false;
			}
		}

		public string GetExecutableName() {
			return (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) ? "ogamed.exe" : "ogamed";
		}

		internal Process ExecuteOgamedExecutable(Credentials credentials, Device device, string host = "localhost", int port = 8080, string captchaKey = "", ProxySettings proxySettings = null) {
			Process? ogameProc = null;
			try {
				string args = $"--universe=\"{credentials.Universe}\" --username={credentials.Username} --password={credentials.Password} --device-name={device.Name} --language={credentials.Language} --auto-login=false --port={port} --host=0.0.0.0";
				if (captchaKey != "")
					args += $" --nja-api-key={captchaKey}";
				if (proxySettings.Enabled) {
					if (proxySettings.Type == "socks5" || proxySettings.Type == "http") {
						args += $" --proxy={proxySettings.Address}";
						args += $" --proxy-type={proxySettings.Type}";
						if (proxySettings.Username != "")
							args += $" --proxy-username={proxySettings.Username}";
						if (proxySettings.Password != "")
							args += $" --proxy-password={proxySettings.Password}";
						if (proxySettings.LoginOnly)
							args += " --proxy-login-only=true";
					}
				}
				if (credentials.IsLobbyPioneers)
					args += " --lobby=lobby-pioneers";
				if (credentials.BasicAuthUsername != "" && credentials.BasicAuthPassword != "") {
					args += $" --basic-auth-username={credentials.BasicAuthUsername}";
					args += $" --basic-auth-password={credentials.BasicAuthPassword}";
				}

				if (device.System != "") {
					args += $" --device-system={device.System}";
				}
				if (device.Browser != "") {
					args += $" --device-browser={device.Browser}";
				}
				if (device.UserAgent != "") {
					args += $" --device-user-agent=\"{device.UserAgent}\"";
				}
				if (device.Memory > 0) {
					args += $" --device-memory={device.Memory}";
				}
				if (device.Concurrency > 0) {
					args += $" --device-concurrency={device.Concurrency}";
				}
				if (device.Color > 0) {
					args += $" --device-color={device.Color}";
				}
				if (device.Width > 0) {
					args += $" --device-width={device.Width}";
				}
				if (device.Height > 0) {
					args += $" --device-height={device.Height}";
				}
				if (device.Timezone != "") {
					args += $" --device-timezone={device.Timezone}";
				}
				if (device.Lang != "") {
					args += $" --device-lang={device.Lang}";
				}

				ogameProc = new Process();
				ogameProc.StartInfo.FileName = GetExecutableName();
				ogameProc.StartInfo.Arguments = args;
				ogameProc.EnableRaisingEvents = true;
				ogameProc.StartInfo.RedirectStandardOutput = true;
				ogameProc.StartInfo.RedirectStandardError = true;
				ogameProc.StartInfo.RedirectStandardInput = true;
				ogameProc.Exited += handle_ogamedProcess_Exited;
				ogameProc.OutputDataReceived += handle_ogamedProcess_OutputDataReceived;
				ogameProc.ErrorDataReceived += handle_ogamedProcess_ErrorDataReceived;

				ogameProc.Start();
				ogameProc.BeginErrorReadLine();
				ogameProc.BeginOutputReadLine();

				_logger.WriteLog(LogLevel.Information, LogSender.OGameD, $"OgameD Started with PID {ogameProc.Id}");   // This would raise an exception
				_mustKill = false;
			} catch (Exception ex) {
				_logger.WriteLog(LogLevel.Error, LogSender.OGameD, $"Error executing ogamed instance: {ex.Message}");
				Environment.Exit(0);
			}
			return ogameProc;
		}

		private void handle_ogamedProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e) {
			if (e.Data?.Length != 0)
				dump_ogamedProcess_Log(true, e.Data);
		}

		private void handle_ogamedProcess_OutputDataReceived(object sender, DataReceivedEventArgs e) {
			if (e.Data?.Length != 0)
				dump_ogamedProcess_Log(false, e.Data);
		}

		private void dump_ogamedProcess_Log(bool isErr, string? payload) {
			_logger.WriteLog(isErr ? LogLevel.Error : LogLevel.Information, LogSender.OGameD, $"[{_username}] \"{payload}\"");
		}

		private void handle_ogamedProcess_Exited(object? sender, EventArgs e) {
			var totalRunTime = Math.Round((_ogamedProcess.ExitTime - _ogamedProcess.StartTime).TotalMilliseconds);
			_logger.WriteLog(LogLevel.Information, LogSender.OGameD, $"OgameD Exited {_ogamedProcess.ExitCode}" +
				$" TotalTime(ms) {totalRunTime}");

			// If total run time is very low, then OGameD encountered a serious problem
			if (_mustKill == false) {
				if (totalRunTime > 500) {
					Task.Delay(1000).Wait();
					RerunOgamed();
				} else {
					_ogamedProcess.Dispose();
					_ogamedProcess = null;
					if (OnError != null) {
						OnError(this, EventArgs.Empty);
					}
				}
			} else {
				_ogamedProcess.Dispose();
				_ogamedProcess = null;
			}
		}
		public void RerunOgamed() {
			ExecuteOgamedExecutable(_credentials, _device, _host, _port, _captchaKey, _proxySettings);
		}

		public void KillOgamedExecutable(CancellationToken ct = default) {
			if (_ogamedProcess != null) {
				_mustKill = true;
				_ogamedProcess.Kill();
				_ogamedProcess.Dispose();
				_ogamedProcess = null;
			}
		}

		private async Task<T> ManageResponse<T>(HttpResponseMessage response) {
			OgamedResponse result = null;
			try {
				var jsonResponseContent = await response.Content.ReadAsStringAsync();
				if (jsonResponseContent != null) {
					result = JsonConvert.DeserializeObject<OgamedResponse>(jsonResponseContent, new JsonSerializerSettings {
						DateTimeZoneHandling = DateTimeZoneHandling.Local,
						NullValueHandling = NullValueHandling.Ignore
					});
				}
				else {
					response.EnsureSuccessStatusCode();
				}
			}
			catch {
				response.EnsureSuccessStatusCode();
			}
			if (result != null) {
				if (result.Status == null) {
					throw new OgamedException("An error has occurred");
				}
				else if (result.Status != "ok") {
					throw new OgamedException($"An error has occurred: Status Code: {response.StatusCode} Status: {result?.Status} - Message: {result?.Message}");
				} else {
					if (result.Result is JObject) {
						var jObject = result.Result as JObject;
						return jObject.ToObject<T>();
					} else if (result.Result is JArray) {
						var jArray = result.Result as JArray;
						return jArray.ToObject<T>();
					}
				}
			}
			else {
				response.EnsureSuccessStatusCode();
			}
			return (T) result.Result;
		}

		private AsyncRetryPolicy<HttpResponseMessage> GetRetryPolicy() {
			return HttpPolicyExtensions.HandleTransientHttpError()
				.WaitAndRetryAsync(3, retryCount => TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
		}

		private async Task<T> GetAsync<T>(string resource, bool ensureSuccess = true) {
			var response = await GetRetryPolicy()
				.ExecuteAsync(async () => {
					var request = new HttpRequestMessage() {
						Method = HttpMethod.Get,
						RequestUri = new Uri(resource, UriKind.Relative)
					};
					var response = await _client.SendAsync(request);
					return response;
				});
			return await ManageResponse<T>(response);
		}

		private async Task<T> PostAsync<T>(string resource, params KeyValuePair<string, string>[] parameters) {
			var response = await GetRetryPolicy()
				.ExecuteAsync(async () => {
					var request = new HttpRequestMessage() {
						Method = HttpMethod.Post,
						RequestUri = new Uri(resource, UriKind.Relative)
					};
					request.Content = new FormUrlEncodedContent(parameters);
					var response = await _client.SendAsync(request);
					return response;
				});
			return await ManageResponse<T>(response);
		}

		public async Task SetUserAgent(string userAgent) {
			await PostAsync<object>("/bot/set-user-agent", new KeyValuePair<string, string>("userAgent", userAgent));
		}

		public async Task Login() {
			await GetAsync<object>("/bot/login");
		}

		public async Task Logout() {
			await GetAsync<object>("/bot/logout");
		}

		public async Task<CaptchaChallenge> GetCaptchaChallenge() {
			try {
				return await GetAsync<CaptchaChallenge>("/bot/captcha/challenge");
			} catch {
				return new CaptchaChallenge();
			}
		}

		public async Task SolveCaptcha(string challengeID, int answer) {
			await PostAsync<object>($"/bot/captcha/solve",
				new KeyValuePair<string, string>("challenge_id", challengeID),
				new KeyValuePair<string, string>("answer", answer.ToString()));
		}

		public async Task<string> GetOgamedIP() {
			try {
				return await GetAsync<string>("/bot/ip");
			} catch {
				return "";
			}
		}

		public async Task<string> GetTbotIP() {
			var result = await GetAsync<dynamic>("https://jsonip.com");
			return result.ip;
		}

		public async Task<Server> GetServerInfo() {
			return await GetAsync<Server>("/bot/server");
		}

		public async Task<ServerData> GetServerData() {
			return await GetAsync<ServerData>("/bot/server-data");
		}

		public async Task<string> GetServerUrl() {
			return await GetAsync<string>("/bot/server-url");
		}

		public async Task<string> GetServerLanguage() {
			return await GetAsync<string>("/bot/language");
		}

		public async Task<string> GetServerName() {
			return await GetAsync<string>("/bot/universe-name");
		}

		public async Task<int> GetServerSpeed() {
			return await GetAsync<int>("/bot/server/speed");
		}

		public async Task<int> GetServerFleetSpeed() {
			return await GetAsync<int>("/bot/server/speed-fleet");
		}

		public async Task<string> GetServerVersion() {
			return await GetAsync<string>("/bot/server/version");
		}

		public async Task<DateTime> GetServerTime() {
			return await GetAsync<DateTime>("/bot/server/time");
		}

		public async Task<string> GetUsername() {
			return await GetAsync<string>("/bot/username");
		}

		public async Task<UserInfo> GetUserInfo() {
			return await GetAsync<UserInfo>("/bot/user-infos");
		}

		public async Task<CharacterClass> GetUserClass() {
			return await GetAsync<CharacterClass>("/bot/character-class");
		}

		private Celestial ParseEmpireEntry(JObject obj, Celestials type, LFBonuses lfBonuses = null) {
			Celestial cel = new();
			cel.ID = obj["id"]?.Value<int>() ?? 0;
			cel.Name = obj["name"]?.Value<string>() ?? "";
			cel.Coordinate = new Coordinate {
				Galaxy = obj["galaxy"]?.Value<int>() ?? 0,
				System = obj["system"]?.Value<int>() ?? 0,
				Position = obj["position"]?.Value<int>() ?? 0,
				Type = type
			};
			string fieldUsedStr = obj["fieldUsed"]?.Value<string>();
			int built = 0;
			int.TryParse(fieldUsedStr, out built);
			string fieldMaxStr = obj["fieldMax"]?.Value<string>();
			int total = 0;
			int.TryParse(fieldMaxStr, out total);
			cel.Fields = new Fields {
				Built = built,
				Total = total
			};
			cel.Resources = new Resources {
				Metal = obj["metal"]?.Value<long>() ?? 0,
				Crystal = obj["crystal"]?.Value<long>() ?? 0,
				Deuterium = obj["deuterium"]?.Value<long>() ?? 0,
				Energy = obj["production"]?["hourly"]?["3"]?.Value<long>() ?? 0,
				Food = obj["food"]?.Value<long>() ?? 0,
				Population = obj["population"]?.Value<long>() ?? 0
			};
			cel.Ships = new Ships {
				LightFighter = obj["204"]?.Value<int>() ?? 0,
				HeavyFighter = obj["205"]?.Value<int>() ?? 0,
				Cruiser = obj["206"]?.Value<int>() ?? 0,
				Battleship = obj["207"]?.Value<int>() ?? 0,
				Battlecruiser = obj["215"]?.Value<int>() ?? 0,
				Bomber = obj["211"]?.Value<int>() ?? 0,
				Destroyer = obj["213"]?.Value<int>() ?? 0,
				Deathstar = obj["214"]?.Value<int>() ?? 0,
				SmallCargo = obj["202"]?.Value<int>() ?? 0,
				LargeCargo = obj["203"]?.Value<int>() ?? 0,
				ColonyShip = obj["208"]?.Value<int>() ?? 0,
				Recycler = obj["209"]?.Value<int>() ?? 0,
				EspionageProbe = obj["210"]?.Value<int>() ?? 0,
				SolarSatellite = obj["212"]?.Value<int>() ?? 0,
				Crawler = obj["217"]?.Value<int>() ?? 0,
				Reaper = obj["218"]?.Value<int>() ?? 0,
				Pathfinder = obj["219"]?.Value<int>() ?? 0
			};
			cel.Defences = new Defences {
				RocketLauncher = obj["401"]?.Value<int>() ?? 0,
				LightLaser = obj["402"]?.Value<int>() ?? 0,
				HeavyLaser = obj["403"]?.Value<int>() ?? 0,
				GaussCannon = obj["404"]?.Value<int>() ?? 0,
				IonCannon = obj["405"]?.Value<int>() ?? 0,
				PlasmaTurret = obj["406"]?.Value<int>() ?? 0,
				SmallShieldDome = obj["407"]?.Value<int>() ?? 0,
				LargeShieldDome = obj["408"]?.Value<int>() ?? 0,
				AntiBallisticMissiles = obj["502"]?.Value<int>() ?? 0,
				InterplanetaryMissiles = obj["503"]?.Value<int>() ?? 0
			};
			cel.Buildings = new Buildings {
				MetalMine = obj["1"]?.Value<int>() ?? 0,
				CrystalMine = obj["2"]?.Value<int>() ?? 0,
				DeuteriumSynthesizer = obj["3"]?.Value<int>() ?? 0,
				SolarPlant = obj["4"]?.Value<int>() ?? 0,
				FusionReactor = obj["12"]?.Value<int>() ?? 0,
				SolarSatellite = obj["212"]?.Value<int>() ?? 0,
				MetalStorage = obj["22"]?.Value<int>() ?? 0,
				CrystalStorage = obj["23"]?.Value<int>() ?? 0,
				DeuteriumTank = obj["24"]?.Value<int>() ?? 0
			};
			cel.LFBuildings = new LFBuildings {
				ResidentialSector = obj["11101"]?.Value<int>() ?? 0,
				BiosphereFarm = obj["11102"]?.Value<int>() ?? 0,
				ResearchCentre = obj["11103"]?.Value<int>() ?? 0,
				AcademyOfSciences = obj["11104"]?.Value<int>() ?? 0,
				NeuroCalibrationCentre = obj["11105"]?.Value<int>() ?? 0,
				HighEnergySmelting = obj["11106"]?.Value<int>() ?? 0,
				FoodSilo = obj["11107"]?.Value<int>() ?? 0,
				FusionPoweredProduction = obj["11108"]?.Value<int>() ?? 0,
				Skyscraper = obj["11109"]?.Value<int>() ?? 0,
				BiotechLab = obj["11110"]?.Value<int>() ?? 0,
				Metropolis = obj["11111"]?.Value<int>() ?? 0,
				PlanetaryShield = obj["11112"]?.Value<int>() ?? 0,
				MeditationEnclave = obj["12101"]?.Value<int>() ?? 0,
				CrystalFarm = obj["12102"]?.Value<int>() ?? 0,
				RuneTechnologium = obj["12103"]?.Value<int>() ?? 0,
				RuneForge = obj["12104"]?.Value<int>() ?? 0,
				Oriktorium = obj["12105"]?.Value<int>() ?? 0,
				MagmaForge = obj["12106"]?.Value<int>() ?? 0,
				DisruptionChamber = obj["12107"]?.Value<int>() ?? 0,
				Megalith = obj["12108"]?.Value<int>() ?? 0,
				CrystalRefinery = obj["12109"]?.Value<int>() ?? 0,
				DeuteriumSynthesiser = obj["12110"]?.Value<int>() ?? 0,
				MineralResearchCentre = obj["12111"]?.Value<int>() ?? 0,
				AdvancedRecyclingPlant = obj["12112"]?.Value<int>() ?? 0,				
				AssemblyLine = obj["13101"]?.Value<int>() ?? 0,
				FusionCellFactory = obj["13102"]?.Value<int>() ?? 0,
				RoboticsResearchCentre = obj["13103"]?.Value<int>() ?? 0,
				UpdateNetwork = obj["13104"]?.Value<int>() ?? 0,
				QuantumComputerCentre = obj["13105"]?.Value<int>() ?? 0,
				AutomatisedAssemblyCentre = obj["13106"]?.Value<int>() ?? 0,
				HighPerformanceTransformer = obj["13107"]?.Value<int>() ?? 0,
				MicrochipAssemblyLine = obj["13108"]?.Value<int>() ?? 0,
				ProductionAssemblyHall = obj["13109"]?.Value<int>() ?? 0,
				HighPerformanceSynthesiser = obj["13110"]?.Value<int>() ?? 0,
				ChipMassProduction = obj["13111"]?.Value<int>() ?? 0,
				NanoRepairBots = obj["13112"]?.Value<int>() ?? 0,				
				Sanctuary = obj["14101"]?.Value<int>() ?? 0,
				AntimatterCondenser = obj["14102"]?.Value<int>() ?? 0,
				VortexChamber = obj["14103"]?.Value<int>() ?? 0,
				HallsOfRealisation = obj["14104"]?.Value<int>() ?? 0,
				ForumOfTranscendence = obj["14105"]?.Value<int>() ?? 0,
				AntimatterConvector = obj["14106"]?.Value<int>() ?? 0,
				CloningLaboratory = obj["14107"]?.Value<int>() ?? 0,
				ChrysalisAccelerator = obj["14108"]?.Value<int>() ?? 0,
				BioModifier = obj["14109"]?.Value<int>() ?? 0,
				PsionicModulator = obj["14110"]?.Value<int>() ?? 0,
				ShipManufacturingHall = obj["14111"]?.Value<int>() ?? 0,
				SupraRefractor = obj["14112"]?.Value<int>() ?? 0
			};
			cel.LFTechs = new LFTechs {
				IntergalacticEnvoys = obj["11201"]?.Value<int>() ?? 0,
				HighPerformanceExtractors = obj["11202"]?.Value<int>() ?? 0,
				FusionDrives = obj["11203"]?.Value<int>() ?? 0,
				StealthFieldGenerator = obj["11204"]?.Value<int>() ?? 0,
				OrbitalDen = obj["11205"]?.Value<int>() ?? 0,
				ResearchAI = obj["11206"]?.Value<int>() ?? 0,
				HighPerformanceTerraformer = obj["11207"]?.Value<int>() ?? 0,
				EnhancedProductionTechnologies = obj["11208"]?.Value<int>() ?? 0,
				LightFighterMkII = obj["11209"]?.Value<int>() ?? 0,
				CruiserMkII = obj["11210"]?.Value<int>() ?? 0,
				ImprovedLabTechnology = obj["11211"]?.Value<int>() ?? 0,
				PlasmaTerraformer = obj["11212"]?.Value<int>() ?? 0,
				LowTemperatureDrives = obj["11213"]?.Value<int>() ?? 0,
				BomberMkII = obj["11214"]?.Value<int>() ?? 0,
				DestroyerMkII = obj["11215"]?.Value<int>() ?? 0,
				BattlecruiserMkII = obj["11216"]?.Value<int>() ?? 0,
				RobotAssistants = obj["11217"]?.Value<int>() ?? 0,
				Supercomputer = obj["11218"]?.Value<int>() ?? 0,
				VolcanicBatteries = obj["12201"]?.Value<int>() ?? 0,
				AcousticScanning = obj["12202"]?.Value<int>() ?? 0,
				HighEnergyPumpSystems = obj["12203"]?.Value<int>() ?? 0,
				CargoHoldExpansionCivilianShips = obj["12204"]?.Value<int>() ?? 0,
				MagmaPoweredProduction = obj["12205"]?.Value<int>() ?? 0,
				GeothermalPowerPlants = obj["12206"]?.Value<int>() ?? 0,
				DepthSounding = obj["12207"]?.Value<int>() ?? 0,
				IonCrystalEnhancementHeavyFighter = obj["12208"]?.Value<int>() ?? 0,
				ImprovedStellarator = obj["12209"]?.Value<int>() ?? 0,
				HardenedDiamondDrillHeads = obj["12210"]?.Value<int>() ?? 0,
				SeismicMiningTechnology = obj["12211"]?.Value<int>() ?? 0,
				MagmaPoweredPumpSystems = obj["12212"]?.Value<int>() ?? 0,
				IonCrystalModules = obj["12213"]?.Value<int>() ?? 0,
				OptimisedSiloConstructionMethod = obj["12214"]?.Value<int>() ?? 0,
				DiamondEnergyTransmitter = obj["12215"]?.Value<int>() ?? 0,
				ObsidianShieldReinforcement = obj["12216"]?.Value<int>() ?? 0,
				RuneShields = obj["12217"]?.Value<int>() ?? 0,
				RocktalCollectorEnhancement = obj["12218"]?.Value<int>() ?? 0,
				CatalyserTechnology = obj["13201"]?.Value<int>() ?? 0,
				PlasmaDrive = obj["13202"]?.Value<int>() ?? 0,
				EfficiencyModule = obj["13203"]?.Value<int>() ?? 0,
				DepotAI = obj["13204"]?.Value<int>() ?? 0,
				GeneralOverhaulLightFighter = obj["13205"]?.Value<int>() ?? 0,
				AutomatedTransportLines = obj["13206"]?.Value<int>() ?? 0,
				ImprovedDroneAI = obj["13207"]?.Value<int>() ?? 0,
				ExperimentalRecyclingTechnology = obj["13208"]?.Value<int>() ?? 0,
				GeneralOverhaulCruiser = obj["13209"]?.Value<int>() ?? 0,
				SlingshotAutopilot = obj["13210"]?.Value<int>() ?? 0,
				HighTemperatureSuperconductors = obj["13211"]?.Value<int>() ?? 0,
				GeneralOverhaulBattleship = obj["13212"]?.Value<int>() ?? 0,
				ArtificialSwarmIntelligence = obj["13213"]?.Value<int>() ?? 0,
				GeneralOverhaulBattlecruiser = obj["13214"]?.Value<int>() ?? 0,
				GeneralOverhaulBomber = obj["13215"]?.Value<int>() ?? 0,
				GeneralOverhaulDestroyer = obj["13216"]?.Value<int>() ?? 0,
				ExperimentalWeaponsTechnology = obj["13217"]?.Value<int>() ?? 0,
				MechanGeneralEnhancement = obj["13218"]?.Value<int>() ?? 0,
				HeatRecovery = obj["14201"]?.Value<int>() ?? 0,
				SulphideProcess = obj["14202"]?.Value<int>() ?? 0,
				PsionicNetwork = obj["14203"]?.Value<int>() ?? 0,
				TelekineticTractorBeam = obj["14204"]?.Value<int>() ?? 0,
				EnhancedSensorTechnology = obj["14205"]?.Value<int>() ?? 0,
				NeuromodalCompressor = obj["14206"]?.Value<int>() ?? 0,
				NeuroInterface = obj["14207"]?.Value<int>() ?? 0,
				InterplanetaryAnalysisNetwork = obj["14208"]?.Value<int>() ?? 0,
				OverclockingHeavyFighter = obj["14209"]?.Value<int>() ?? 0,
				TelekineticDrive = obj["14210"]?.Value<int>() ?? 0,
				SixthSense = obj["14211"]?.Value<int>() ?? 0,
				Psychoharmoniser = obj["14212"]?.Value<int>() ?? 0,
				EfficientSwarmIntelligence = obj["14213"]?.Value<int>() ?? 0,
				OverclockingLargeCargo = obj["14214"]?.Value<int>() ?? 0,
				GravitationSensors = obj["14215"]?.Value<int>() ?? 0,
				OverclockingBattleship = obj["14216"]?.Value<int>() ?? 0,
				PsionicShieldMatrix = obj["14217"]?.Value<int>() ?? 0,
				KaeleshDiscovererEnhancement = obj["14218"]?.Value<int>() ?? 0
			};
			if (lfBonuses != null)
				cel.LFBonuses = lfBonuses;
			cel.Facilities = new Facilities {
				AllianceDepot = obj["34"]?.Value<int>() ?? 0,
				RoboticsFactory = obj["14"]?.Value<int>() ?? 0,
				Shipyard = obj["21"]?.Value<int>() ?? 0,
				ResearchLab = obj["31"]?.Value<int>() ?? 0,
				MissileSilo = obj["44"]?.Value<int>() ?? 0,
				NaniteFactory = obj["15"]?.Value<int>() ?? 0,
				Terraformer = obj["33"]?.Value<int>() ?? 0,
				SpaceDock = obj["36"]?.Value<int>() ?? 0,
				LunarBase = obj["41"]?.Value<int>() ?? 0,
				SensorPhalanx = obj["42"]?.Value<int>() ?? 0,
				JumpGate = obj["43"]?.Value<int>() ?? 0
			};
			cel.Constructions = new();
			cel.ResourcesProduction = new ResourcesProduction {
				Metal = new Resource {
					Available = obj["metal"]?.Value<long>() ?? 0,
					StorageCapacity = obj["metalStorage"]?.Value<long>() ?? 0,
					CurrentProduction = obj["production"]?["hourly"]?["0"]?.Value<long>() ?? 0
				},
				Crystal = new Resource {
					Available = obj["crystal"]?.Value<long>() ?? 0,
					StorageCapacity = obj["crystalStorage"]?.Value<long>() ?? 0,
					CurrentProduction = obj["production"]?["hourly"]?["1"]?.Value<long>() ?? 0
				},
				Deuterium = new Resource {
					Available = obj["deuterium"]?.Value<long>() ?? 0,
					StorageCapacity = obj["deuteriumStorage"]?.Value<long>() ?? 0,
					CurrentProduction = obj["production"]?["hourly"]?["2"]?.Value<long>() ?? 0
				},
				Food = new Food {
					Available = obj["food"]?.Value<long>() ?? 0,
					StorageCapacity = obj["foodStorage"]?.Value<long>() ?? 0
				},
				Population = new Population {
					Available = obj["population"]?.Value<long>() ?? 0,
					LivingSpace = obj["populationStorage"]?.Value<long>() ?? 0,
					BunkerSpace = obj["populationHide"]?.Value<long>() ?? 0
				},
				Energy = new Energy {
					Available = obj["production"]?["hourly"]?["3"]?.Value<long>() ?? 0
				}
			};
			cel.Researches = new Researches {
				EspionageTechnology = obj["106"]?.Value<int>() ?? 0,
				ComputerTechnology = obj["108"]?.Value<int>() ?? 0,
				WeaponsTechnology = obj["109"]?.Value<int>() ?? 0,
				ShieldingTechnology = obj["110"]?.Value<int>() ?? 0,
				ArmourTechnology = obj["111"]?.Value<int>() ?? 0,
				EnergyTechnology = obj["113"]?.Value<int>() ?? 0,
				HyperspaceTechnology = obj["114"]?.Value<int>() ?? 0,
				CombustionDrive = obj["115"]?.Value<int>() ?? 0,
				ImpulseDrive = obj["117"]?.Value<int>() ?? 0,
				HyperspaceDrive = obj["118"]?.Value<int>() ?? 0,
				LaserTechnology = obj["120"]?.Value<int>() ?? 0,
				IonTechnology = obj["121"]?.Value<int>() ?? 0,
				PlasmaTechnology = obj["122"]?.Value<int>() ?? 0,
				IntergalacticResearchNetwork = obj["123"]?.Value<int>() ?? 0,
				Astrophysics = obj["124"]?.Value<int>() ?? 0,
				GravitonTechnology = obj["199"]?.Value<int>() ?? 0
			};

			foreach (var prop in obj.Properties()) {
				if (!prop.Name.EndsWith("_html", StringComparison.OrdinalIgnoreCase))
					continue;
				if (prop.Value.Type != JTokenType.String)
					continue;
				string html = prop.Value.Value<string>();
				if (!html.Contains("align='absmiddle'"))
					continue;
				string rawName = prop.Name.Substring(0, prop.Name.Length - "_html".Length);
				if (!int.TryParse(rawName, out int id))
					continue;
				
				if (Enum.IsDefined(typeof(Buildables), id)) {
					if (typeof(Defences).GetProperty(((Buildables)id).ToString()) != null || typeof(Ships).GetProperty(((Buildables)id).ToString()) != null)
						continue;
					if (typeof(Researches).GetProperty(((Buildables)id).ToString()) != null)
						cel.Constructions.ResearchID = id;
					else
						cel.Constructions.BuildingID = id;
				} else if (Enum.IsDefined(typeof(LFBuildables), id)) {
					cel.Constructions.LFBuildingID = id;
				} else if (Enum.IsDefined(typeof(LFTechno), id)) {
					cel.Constructions.LFResearchID = id;
				}
			}
			return cel;
		}
		public async Task<List<Celestial>> GetEmpirePlanets() {
			List<Celestial> result = new();
			var empire = await GetAsync<JObject>("/bot/empire/type/0");
			LFBonuses lfBonuses = await GetLFBonuses();
			if (empire["planets"] is JArray planets)
				foreach (JObject planet in planets)
					result.Add(ParseEmpireEntry(planet, Celestials.Planet, lfBonuses));
			return result;
		}
		public async Task<List<Celestial>> GetEmpireMoons() {
			List<Celestial> result = new();
			var empire = await GetAsync<JObject>("/bot/empire/type/1");
			LFBonuses lfBonuses = await GetLFBonuses();
			if (empire["planets"] is JArray planets)
				foreach (JObject planet in planets)
					result.Add(ParseEmpireEntry(planet, Celestials.Moon, lfBonuses));
			return result;
		}

		public async Task<List<Planet>> GetPlanets() {
			return await GetAsync<List<Planet>>("/bot/planets");
		}

		public async Task<Planet> GetPlanet(Planet planet) {
			return await GetAsync<Planet>($"/bot/planets/{planet.ID}");
		}

		public async Task<List<Moon>> GetMoons() {
			return await GetAsync<List<Moon>>("/bot/moons");
		}

		public async Task<Moon> GetMoon(Moon moon) {
			return await GetAsync<Moon>($"/bot/moons/{moon.ID}");
		}

		public async Task<List<Celestial>> GetCelestials() {
			var planets = await GetPlanets();
			var moons = await GetMoons();
			List<Celestial> celestials = new();
			celestials.AddRange(planets);
			celestials.AddRange(moons);
			return celestials;
		}

		public async Task<Celestial> GetCelestial(Celestial celestial) {
			if (celestial is Moon)
				return await GetMoon(celestial as Moon);
			else if (celestial is Planet)
				return await GetPlanet(celestial as Planet);
			else
				return celestial;
		}

		public async Task<Techs> GetTechs(Celestial celestial) {
			return await GetAsync<Techs>($"/bot/celestials/{celestial.ID}/techs");
		}

		public async Task<Resources> GetResources(Celestial celestial) {
			return await GetAsync<Resources>($"/bot/planets/{celestial.ID}/resources");
		}

		public async Task<Buildings> GetBuildings(Celestial celestial) {
			return await GetAsync<Buildings>($"/bot/planets/{celestial.ID}/resources-buildings");
		}

		public async Task<LFBuildings> GetLFBuildings(Celestial celestial) {
			return await GetAsync<LFBuildings>($"/bot/planets/{celestial.ID}/lifeform-buildings");
		}

		public async Task<LFBonuses> GetLFBonuses() {
			//return await GetAsync<LFBonuses>($"/bot/planets/{celestial.ID}/lifeform-bonuses");
			return await GetAsync<LFBonuses>($"/bot/lfbonuses");
		}

		public async Task<LFTechs> GetLFTechs(Celestial celestial) {
			return await GetAsync<LFTechs>($"/bot/planets/{celestial.ID}/lifeform-techs");
		}

		public async Task<Facilities> GetFacilities(Celestial celestial) {
			return await GetAsync<Facilities>($"/bot/planets/{celestial.ID}/facilities");
		}

		public async Task<Defences> GetDefences(Celestial celestial) {
			return await GetAsync<Defences>($"/bot/planets/{celestial.ID}/defence");
		}

		public async Task<Ships> GetShips(Celestial celestial) {
			return await GetAsync<Ships>($"/bot/planets/{celestial.ID}/ships");
		}

		public async Task<bool> IsUnderAttack() {
			return await GetAsync<bool>("/bot/is-under-attack");
		}

		public async Task<bool> IsVacationMode() {
			return await GetAsync<bool>("/bot/is-vacation-mode");
		}

		public async Task<bool> HasCommander() {
			return await GetAsync<bool>("/bot/has-commander");
		}

		public async Task<bool> HasAdmiral() {
			return await GetAsync<bool>("/bot/has-admiral");
		}

		public async Task<bool> HasEngineer() {
			return await GetAsync<bool>("/bot/has-engineer");
		}

		public async Task<bool> HasGeologist() {
			return await GetAsync<bool>("/bot/has-geologist");
		}

		public async Task<bool> HasTechnocrat() {
			return await GetAsync<bool>("/bot/has-technocrat");
		}

		public async Task<AllianceClass> GetAllianceClass() {
			return await GetAsync<AllianceClass>("/bot/alliance-class");
		}

		public async Task<Staff> GetStaff() {
			try {
				return new() {
					Commander = await HasCommander(),
					Admiral = await HasAdmiral(),
					Engineer = await HasEngineer(),
					Geologist = await HasGeologist(),
					Technocrat = await HasTechnocrat()
				};
			} catch {
				return new();
			}
		}

		public async Task<OfferOfTheDayStatus> BuyOfferOfTheDay() {
			OfferOfTheDayStatus sts = OfferOfTheDayStatus.OfferOfTheDayUnknown;
			// 200 means it has been bought
			// 400 {"Status":"error","Code":400,"Message":"Offer already accepted","Result":null}
			// Other 400s (e.g. GF rate-limit) should stay Unknown, not AlreadyBought.
			try {
				await GetAsync<object>("/bot/buy-offer-of-the-day");
				sts = OfferOfTheDayStatus.OfferOfTheDayBougth;
			} catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.BadRequest) {
				sts = OfferOfTheDayStatus.OfferOfTheDayAlreadyBought;
			} catch (OgamedException e) {
				var msg = e.Message ?? string.Empty;
				if (msg.Contains("already", StringComparison.OrdinalIgnoreCase) ||
					msg.Contains("bereits", StringComparison.OrdinalIgnoreCase) ||
					msg.Contains("déjà", StringComparison.OrdinalIgnoreCase) ||
					msg.Contains("deja", StringComparison.OrdinalIgnoreCase)) {
					sts = OfferOfTheDayStatus.OfferOfTheDayAlreadyBought;
				}
				// else leave Unknown (rate-limit / try again / other GF errors)
			} catch {
				// Unknown!
			}
			return sts;
		}

		public async Task<List<AttackerFleet>> GetAttacks() {
			return await GetAsync<List<AttackerFleet>>("/bot/attacks");
		}

		public async Task<List<Fleet>> GetFleets() {
			return await GetAsync<List<Fleet>>("/bot/fleets");
		}

		public async Task<Slots> GetSlots() {
			return await GetAsync<Slots>("/bot/fleets/slots");
		}

		public async Task<Researches> GetResearches() {
			return await GetAsync<Researches>("/bot/get-research");
		}

		public async Task<List<Production>> GetProductions(Celestial celestial) {
			return await GetAsync<List<Production>>($"/bot/planets/{celestial.ID}/production");
		}

		public async Task<ResourceSettings> GetResourceSettings(Planet planet) {
			return await GetAsync<ResourceSettings>($"/bot/planets/{planet.ID}/resource-settings");
		}

		public async Task<ResourcesProduction> GetResourcesProduction(Planet planet) {
			return await GetAsync<ResourcesProduction>($"/bot/planets/{planet.ID}/resources-details");
		}

		public async Task<Constructions> GetConstructions(Celestial celestial) {
			return await GetAsync<Constructions>($"/bot/planets/{celestial.ID}/constructions");
		}

		public async Task CancelConstruction(Celestial celestial) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/cancel-building");
		}

		public async Task CancelResearch(Celestial celestial) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/cancel-research");
		}

		public async Task<Fleet> SendFleet(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload) {
			try {
				List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>();
				parameters.Add(new KeyValuePair<string, string>("galaxy", destination.Galaxy.ToString()));
				parameters.Add(new KeyValuePair<string, string>("system", destination.System.ToString()));
				parameters.Add(new KeyValuePair<string, string>("position", destination.Position.ToString()));
				parameters.Add(new KeyValuePair<string, string>("type", ((int)destination.Type).ToString()));

				var request = new RestRequest {
					Resource = $"/bot/planets/{origin.ID}/send-fleet",
					Method = Method.Post,
				};
				foreach (PropertyInfo prop in ships.GetType().GetProperties()) {
					long qty = (long) prop.GetValue(ships, null);
					if (qty == 0)
						continue;
					if (Enum.TryParse<Buildables>(prop.Name, out Buildables buildable)) {
						parameters.Add(new KeyValuePair<string, string>("ships", (int) buildable + "," + prop.GetValue(ships, null)));
					}
				}
				parameters.Add(new KeyValuePair<string, string>("mission", ((int) mission).ToString()));

				//if (mission == Missions.Expedition && duration > 0)
				//	parameters.Add(new KeyValuePair<string, string>("duration", duration.ToString()));

				parameters.Add(new KeyValuePair<string, string>("speed", speed.ToString()));
				parameters.Add(new KeyValuePair<string, string>("metal", payload.Metal.ToString()));
				parameters.Add(new KeyValuePair<string, string>("crystal", payload.Crystal.ToString()));
				parameters.Add(new KeyValuePair<string, string>("deuterium", payload.Deuterium.ToString()));
				parameters.Add(new KeyValuePair<string, string>("food", payload.Food.ToString()));

				return await PostAsync<Fleet>($"/bot/planets/{origin.ID}/send-fleet", parameters.ToArray());
			} catch (OgamedException e) when (e.Message.Contains("no ships to send")) {
				return null;
			}
		}

		public async Task CancelFleet(Fleet fleet) {
			await PostAsync<object>($"/bot/fleets/{fleet.ID}/cancel");
		}

		public async Task<GalaxyInfo> GetGalaxyInfo(Coordinate coordinate) {
			return await GetAsync<GalaxyInfo>($"/bot/galaxy-infos/{coordinate.Galaxy}/{coordinate.System}");
		}

		public async Task<GalaxyInfo> GetGalaxyInfo(int galaxy, int system) {
			Coordinate coordinate = new() { Galaxy = galaxy, System = system };
			return await GetGalaxyInfo(coordinate);
		}

		public async Task BuildCancelable(Celestial celestial, Buildables buildable) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/cancelable/{(int) buildable}");
		}

		public async Task BuildCancelable(Celestial celestial, LFBuildables buildable) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/cancelable/{(int) buildable}");
		}

		public async Task BuildCancelable(Celestial celestial, LFTechno buildable) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/cancelable/{(int) buildable}");
		}

		public async Task BuildConstruction(Celestial celestial, Buildables buildable) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/building/{(int) buildable}");
		}

		public async Task BuildTechnology(Celestial celestial, Buildables buildable) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/technology/{(int) buildable}");
		}

		public async Task BuildMilitary(Celestial celestial, Buildables buildable, long quantity) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/production/{(int) buildable}/{quantity}");
		}

		public async Task BuildShips(Celestial celestial, Buildables buildable, long quantity) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/ships/{(int) buildable}/{quantity}");
		}

		public async Task BuildDefences(Celestial celestial, Buildables buildable, long quantity) {
			await PostAsync<object>($"/bot/planets/{celestial.ID}/build/defence/{(int) buildable}/{quantity}");
		}

		public async Task<Resources> GetPrice(Buildables buildable, long levelOrQuantity) {
			return await GetAsync<Resources>($"/bot/price/{(int) buildable}/{levelOrQuantity}");
		}

		public async Task<Resources> GetPrice(LFBuildables buildable, long levelOrQuantity) {
			return await GetAsync<Resources>($"/bot/price/{(int) buildable}/{levelOrQuantity}");
		}

		public async Task<Resources> GetPrice(LFTechno buildable, long levelOrQuantity) {
			return await GetAsync<Resources>($"/bot/price/{(int) buildable}/{levelOrQuantity}");
		}

		public async Task<Auction> GetCurrentAuction() {
			return await GetAsync<Auction>("/bot/get-auction");
		}

		public async Task DoAuction(Celestial celestial, Resources resources) {
			await PostAsync<object>("/bot/do-auction",
				new KeyValuePair<string, string>($"{celestial.ID}", $"{resources.Metal}:{resources.Crystal}:{resources.Deuterium}"));
		}

		public async Task SendMessage(int playerID, string message) {
			await PostAsync<object>("/bot/send-message",
				new KeyValuePair<string, string>("playerID", playerID.ToString()),
				new KeyValuePair<string, string>("message", message));
		}

		public async Task DeleteReport(int reportID) {
			await PostAsync<object>($"/bot/delete-report/{reportID}");
		}

		public async Task DeleteAllEspionageReports() {
			await PostAsync<object>("/bot/delete-all-espionage-reports");
		}

		public async Task<List<EspionageReportSummary>> GetEspionageReports() {
			return await GetAsync<List<EspionageReportSummary>>("/bot/espionage-report");
		}

		public async Task<EspionageReport> GetEspionageReport(Coordinate coordinate) {
			return await GetAsync<EspionageReport>($"/bot/espionage-report/{coordinate.Galaxy}/{coordinate.System}/{coordinate.Position}");
		}

		public async Task<EspionageReport> GetEspionageReport(int msgId) {
			return await GetAsync<EspionageReport>($"/bot/espionage-report/{msgId}");
		}

		public async Task<JumpGateResult> JumpGate(Celestial origin, Celestial destination, Ships ships) {
			List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>();

			parameters.Add(new KeyValuePair<string, string>("moonDestination", destination.ID.ToString()));

			foreach (PropertyInfo prop in ships.GetType().GetProperties()) {
				long qty = (long) prop.GetValue(ships, null);
				if (qty == 0)
					continue;

				if (Enum.TryParse(prop.Name, out Buildables buildable)) {
					parameters.Add(new KeyValuePair<string, string>("ships", $"{(int) buildable},{prop.GetValue(ships, null)}"));
				}
			}

			try {
				var result = await PostAsync<JumpGateResult>($"/bot/moons/{origin.ID}/jump-gate", parameters.ToArray());
				return result;
			} catch (HttpRequestException ex) {
				return new JumpGateResult { Success = false, RechargeCountdown = 0, Error = ex.Message };
			} catch (OgamedException ex) {
				return new JumpGateResult { Success = false, RechargeCountdown = 0, Error = ex.Message };
			} catch (Exception ex) {
				return new JumpGateResult { Success = false, RechargeCountdown = 0, Error = ex.Message };
			}
		}

		public async Task<List<Fleet>> Phalanx(Celestial origin, Coordinate coords) {
			List<Fleet> phalanxedFleets = new();
			try {
				phalanxedFleets = await GetAsync<List<Fleet>>($"/bot/moons/{origin.ID}/phalanx/{coords.Galaxy}/{coords.System}/{coords.Position}");
			} catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.BadRequest) {
				// Means not fleet or can't phalanx. Got to check better with ogamed
			}
			return phalanxedFleets;
		}
		public async Task<bool> SendDiscovery(Celestial origin, Coordinate coords) {
			bool success = false;
			try {
				List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>();
				parameters.Add(new KeyValuePair<string, string>("galaxy", coords.Galaxy.ToString()));
				parameters.Add(new KeyValuePair<string, string>("system", coords.System.ToString()));
				parameters.Add(new KeyValuePair<string, string>("position", coords.Position.ToString()));
				success = await PostAsync<bool>($"/bot/planets/{origin.ID}/send-discovery", parameters.ToArray());
			} catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.BadRequest) {
				success = false;
			} catch (OgamedException e) {
				success = false;
			} catch (Exception e) {
				success = false;
			}
			return success;
		}

		        public async Task<List<Coordinate>> GetPositionsAvailableForDiscoveryFleet(Celestial celestial, Coordinate coordinate) {
            List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>();
            parameters.Add(new KeyValuePair<string, string>("galaxy", coordinate.Galaxy.ToString()));
            parameters.Add(new KeyValuePair<string, string>("system", coordinate.System.ToString()));
            parameters.Add(new KeyValuePair<string, string>("position", coordinate.Position.ToString()));
            return await PostAsync<List<Coordinate>>($"/bot/planets/{celestial.ID}/get-system-available-discovery", parameters.ToArray());
        }

        public async Task<int> GetAvailableDiscoveries(Celestial celestial) {
            return await GetAsync<int>($"/bot/planets/{celestial.ID}/get-available-discoveries");
        }

        public async Task<bool> AbandonCelestial(Celestial celestial) {
			bool success = false;
			try {
				Abandon result = await GetAsync<Abandon>($"/bot/celestials/{celestial.ID}/abandon");
				if (result.Result == "succeed") {
					success = true;
				} else {
					success = false;
				}
			} catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.BadRequest) {
				success = false;
			} catch (OgamedException e) {
				success = false;
			} catch (Exception e) {
				success = false;
			}
			return success;
		}
	}
}
