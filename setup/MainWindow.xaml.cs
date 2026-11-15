/*
 * Copyright (C) 2014 Patrick Mours
 * SPDX-License-Identifier: BSD-3-Clause
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using ReShade.Setup.Pages;
using ReShade.Setup.Utilities;

namespace ReShade.Setup
{
	public partial class MainWindow
	{
		public MainWindow()
		{
			InitializeComponent();
			isRussian = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName).IndexOf(" RU", StringComparison.OrdinalIgnoreCase) >= 0;
			Dlss5Button.IsEnabled = true;
			Dlss5Button.Opacity = 1.0;
			Dlss5Label.Foreground = Brushes.Black;

			var assembly = Assembly.GetExecutingAssembly();
			var productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
			Title = (isRussian ? "Установщик ReShade v" : "ReShade Setup v") + productVersion;

			if (productVersion.Contains(" "))
			{
				NavigationPanel.Background = Brushes.Crimson;
				productVersion = productVersion.Remove(productVersion.IndexOf(" "));
			}

			// Add support for TLS 1.2 and 1.3, so that HTTPS connection to GitHub succeeds
			if (ServicePointManager.SecurityProtocol != 0 /* Default */)
			{
				ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)0x3000 /* Tls13 */;
			}

			string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

			// Parse command line arguments
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "--headless")
				{
					Visibility = Visibility.Hidden;

					isHeadless = true;
					continue;
				}
				if (args[i] == "--elevated")
				{
					isElevated = true;
					continue;
				}

				if (i + 1 < args.Length)
				{
					if (args[i] == "--api")
					{
						switch (args[++i])
						{
							case "d3d9":
								currentInfo.targetApi = Api.D3D9;
								break;
							case "d3d10":
								currentInfo.targetApi = Api.D3D10;
								break;
							case "d3d11":
								currentInfo.targetApi = Api.D3D11;
								break;
							case "d3d12":
								currentInfo.targetApi = Api.D3D12;
								break;
							case "dxgi":
								currentInfo.targetApi = Api.DXGI;
								break;
							case "ddraw":
								currentInfo.targetApi = Api.DDraw;
								break;
							case "opengl":
								currentInfo.targetApi = Api.OpenGL;
								break;
							case "vulkan":
								currentInfo.targetApi = Api.Vulkan;
								break;
							case "openxr":
								currentInfo.targetOpenXR = true;
								break;
						}
						continue;
					}

					if (args[i] == "--top")
					{
						Top = double.Parse(args[++i]);
						continue;
					}
					if (args[i] == "--left")
					{
						Left = double.Parse(args[++i]);
						continue;
					}

					if (args[i] == "--state")
					{
						switch (args[++i])
						{
							case "finished":
								currentOperation = InstallOperation.Finished;
								break;
							case "update":
								currentOperation = InstallOperation.Update;
								break;
							case "modify":
								currentOperation = InstallOperation.UpdateWithEffects;
								break;
							case "uninstall":
								currentOperation = InstallOperation.Uninstall;
								break;
						}
					}
				}

				if (File.Exists(args[i]))
				{
					currentInfo.targetPath = args[i];
					currentInfo.targetName = Path.GetFileNameWithoutExtension(currentInfo.targetPath);
				}
			}

			if (currentInfo.targetPath != null)
			{
				if (currentOperation == InstallOperation.Finished)
				{
					InstallStep_Finish();
				}
				else if (currentInfo.targetApi != Api.Unknown)
				{
					RunTaskWithExceptionHandling(() =>
					{
						var peInfo = new PEInfo(currentInfo.targetPath);
						currentInfo.is64Bit = peInfo.Type == PEInfo.BinaryType.IMAGE_FILE_MACHINE_AMD64;

						InstallStep_CheckExistingInstallation();
					});
				}
				else
				{
					RunTaskWithExceptionHandling(InstallStep_AnalyzeExecutable);
				}
			}
			else if (isHeadless)
			{
				UpdateStatusAndFinish(false, "No target application was provided.");
			}
			else
			{
				NextButton.IsEnabled = false;

				appPage.PathBox.TextChanged += (sender2, e2) => NextButton.IsEnabled = !string.IsNullOrEmpty(appPage.FileName) && Path.GetExtension(appPage.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(appPage.FileName);

				ResetStatus();

#if RESHADE_ADDON
				MessageBox.Show(this, "This build of ReShade is intended for singleplayer games only and may cause bans in multiplayer games.", "Warning", MessageBoxButton.OK, MessageBoxImage.Exclamation);
#endif

				CheckForUpdate(productVersion);
			}
		}

		readonly bool isHeadless = false;
		readonly bool isElevated = WindowsIdentity.GetCurrent().Owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
		static readonly string commonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade");

		IniFile compatibilityIni;

		readonly StatusPage status = new StatusPage();
		readonly SelectAppPage appPage = new SelectAppPage();

		struct InstallInfo
		{
			public bool is64Bit;
			public Api targetApi;
			public bool targetOpenXR;
			public string targetPath, targetName;
			public string modulePath, configPath, presetPath;
		}

		InstallInfo currentInfo = new InstallInfo();
		InstallOperation currentOperation = InstallOperation.Install;
		bool dlss5OperationInProgress;
		readonly bool isRussian;
		static readonly Dictionary<string, string> RussianUi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["_Back"] = "_Назад", ["_Cancel"] = "_Отмена", ["_Finish"] = "_Готово", ["_Next"] = "_Далее", ["_Skip"] = "_Пропустить",
			["Install DLSS 5"] = "Установить DLSS 5", ["Remove DLSS 5"] = "Удалить DLSS 5", ["Become a patron"] = "Поддержать проект",
			["Select a game or application:"] = "Выберите игру или приложение:", ["Select the rendering API"] = "Выберите графический API",
			["Select the operation to perform:"] = "Выберите операцию:", ["Select effects to install:"] = "Выберите эффекты для установки:",
			["Select add-ons to install:"] = "Выберите дополнения для установки:", ["Sort by:"] = "Сортировка:",
			["Recently Used"] = "Недавно использованные", ["A to Z"] = "От А до Я", ["Z to A"] = "От Я до А",
			["Can't find the desired game in the list?"] = "Не нашли нужную игру в списке?", ["_Browse..."] = "_Обзор...",
			["Update _ReShade only"] = "Обновить только _ReShade", ["Update ReShade and _effects"] = "Обновить ReShade и _эффекты",
			["_Uninstall ReShade and effects"] = "_Удалить ReShade и эффекты"
		};

		static void MoveFiles(string sourcePath, string targetPath)
		{
			if (Directory.Exists(targetPath) == false)
			{
				Directory.CreateDirectory(targetPath);
			}

			foreach (string source in Directory.EnumerateFiles(sourcePath))
			{
				string ext = Path.GetExtension(source);
				if (ext == ".addon" || ext == ".addon32" || ext == ".addon64")
				{
					continue;
				}

				string target = targetPath + source.Replace(sourcePath, string.Empty);

				File.Copy(source, target, true);
			}

			// Copy files recursively
			foreach (string source in Directory.EnumerateDirectories(sourcePath))
			{
				string target = targetPath + source.Replace(sourcePath, string.Empty);

				MoveFiles(source, target);
			}
		}
		static bool IsWritable(string targetPath)
		{
			try
			{
				File.Create(Path.Combine(targetPath, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose);
				return true;
			}
			catch
			{
				return false;
			}
		}
		static bool MakeWritable(string targetPath)
		{
			try
			{
				// Ensure the file exists
				File.Open(targetPath, FileMode.OpenOrCreate).Dispose();

				FileSecurity access = File.GetAccessControl(targetPath);
				access.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.Modify, AccessControlType.Allow));
				File.SetAccessControl(targetPath, access);
				return true;
			}
			catch
			{
				return false;
			}
		}

		static string GetModuleProductName(string path)
		{
			if (File.Exists(path))
			{
				return FileVersionInfo.GetVersionInfo(path).ProductName;
			}
			else
			{
				return null;
			}
		}

		void AddSearchPath(List<string> searchPaths, string newPath)
		{
			const string wildcard = "**";

			// Use a wildcard search path by default
			if (searchPaths.Count == 0)
			{
				searchPaths.Add(newPath + Path.DirectorySeparatorChar + wildcard);
				return;
			}

			// Avoid adding search paths already covered by an existing wildcard search path
			if (searchPaths.Any(searchPath => searchPath.EndsWith(wildcard) && newPath.StartsWith(searchPath.Remove(searchPath.Length - 1 - wildcard.Length))))
			{
				return;
			}

			// Filter out invalid search paths (and those with remaining wildcards that were not handled above)
			var validSearchPaths = searchPaths.Where(searchPath =>
				{
					if (searchPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || searchPath.IndexOf('*') >= 0)
					{
						return false;
					}
					try
					{
						Path.GetFullPath(searchPath);
						return true;
					}
					catch
					{
						return false;
					}
				});

			// Avoid adding duplicate search paths (relative or absolute)
			if (validSearchPaths.Any(searchPath => Path.GetFullPath(searchPath) == Path.GetFullPath(newPath)))
			{
				return;
			}

			searchPaths.Add(newPath);
		}
		void WriteSearchPaths(string targetPathEffects, string targetPathTextures)
		{
			string basePath = Path.GetDirectoryName(currentInfo.configPath);

			// Change current directory so that "Path.GetFullPath" resolves correctly
			string currentPath = Directory.GetCurrentDirectory();
			Directory.SetCurrentDirectory(basePath);

			var iniFile = new IniFile(currentInfo.configPath);
			List<string> paths = null;

			if (!string.IsNullOrEmpty(targetPathEffects))
			{
				iniFile.GetValue("GENERAL", "EffectSearchPaths", out string[] effectSearchPaths);

				paths = new List<string>(effectSearchPaths ?? new string[0]);
				paths.RemoveAll(string.IsNullOrWhiteSpace);

				AddSearchPath(paths, targetPathEffects);
				iniFile.SetValue("GENERAL", "EffectSearchPaths", paths.ToArray());
			}

			if (!string.IsNullOrEmpty(targetPathTextures))
			{
				iniFile.GetValue("GENERAL", "TextureSearchPaths", out string[] textureSearchPaths);

				paths = new List<string>(textureSearchPaths ?? new string[0]);
				paths.RemoveAll(string.IsNullOrWhiteSpace);

				AddSearchPath(paths, targetPathTextures);
				iniFile.SetValue("GENERAL", "TextureSearchPaths", paths.ToArray());
			}

			iniFile.SaveFile();

			Directory.SetCurrentDirectory(currentPath);
		}
		void GetEffectSearchPaths(out List<KeyValuePair<string, bool>> searchPaths)
		{
			const string wildcard = "**";

			string basePath = Path.GetDirectoryName(currentInfo.configPath);

			// Change current directory so that "Path.GetFullPath" resolves correctly
			string currentPath = Directory.GetCurrentDirectory();
			Directory.SetCurrentDirectory(basePath);

			var iniFile = new IniFile(currentInfo.configPath);

			if (iniFile.GetValue("GENERAL", "EffectSearchPaths", out string[] effectSearchPaths))
			{
				searchPaths = effectSearchPaths
					.Where(searchPath => !string.IsNullOrWhiteSpace(searchPath))
					.Select(searchPath => searchPath.EndsWith(wildcard) ? new KeyValuePair<string, bool>(searchPath.Remove(searchPath.Length - 1 - wildcard.Length), true) : new KeyValuePair<string, bool>(searchPath, false))
					.Where(searchPath =>
						{
							if (searchPath.Key.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || searchPath.Key.IndexOf('*') >= 0)
							{
								return false;
							}
							try
							{
								Path.GetFullPath(searchPath.Key);
								return true;
							}
							catch
							{
								return false;
							}
						})
					.Select(searchPath => new KeyValuePair<string, bool>(Path.GetFullPath(searchPath.Key), searchPath.Value))
					.ToList();
			}
			else
			{
				searchPaths = new List<KeyValuePair<string, bool>>();
			}

			Directory.SetCurrentDirectory(currentPath);
		}

		void ResetStatus()
		{
			currentInfo = new InstallInfo();
			currentOperation = InstallOperation.Install;

			Dispatcher.Invoke(() =>
			{
				CurrentPage.Navigate(appPage);

				int statusTitleIndex = Title.IndexOf(" was");
				if (statusTitleIndex > 0)
				{
					Title = Title.Remove(statusTitleIndex);
				}
			});
		}
		void UpdateStatus(string message)
		{
			Dispatcher.Invoke(() =>
			{
				status.UpdateStatus(message);

				if (CurrentPage.Content != status)
				{
					CurrentPage.Navigate(status);
				}

				DWM.HideSystemMenu(this, true);
			});

			if (isHeadless)
			{
				Console.WriteLine(message);
			}
		}
		void UpdateStatusAndFinish(bool success, string message)
		{
			currentOperation = InstallOperation.Finished;
			dlss5OperationInProgress = false;

			Dispatcher.Invoke(() =>
			{
				Dlss5Button.IsEnabled = true;
				Dlss5Button.Opacity = 1.0;
				Dlss5Label.Foreground = Brushes.Black;
				RemoveDlss5Button.IsEnabled = true;
				status.UpdateStatus(message, success);

				CurrentPage.Navigate(status);

				if (!Title.Contains("successfull"))
				{
					Title += success ? " was successful!" : " was not successful!";
				}

				DWM.HideSystemMenu(this, false);
			});

			if (isHeadless)
			{
				Console.WriteLine(message);

				Environment.Exit(success ? 0 : 1);
			}
		}

		void RunTaskWithExceptionHandling(Action action)
		{
			Task.Run(action).ContinueWith(c => UpdateStatusAndFinish(false, "Unhandled exception during installation:\n" + c.Exception.InnerException.Message + "\n\n" + c.Exception.InnerException.StackTrace), TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
		}

		bool RestartWithElevatedPrivileges()
		{
			var startInfo = new ProcessStartInfo
			{
				Verb = "runas",
				FileName = Assembly.GetExecutingAssembly().Location,
				Arguments = $"\"{currentInfo.targetPath}\" --elevated --left {Left} --top {Top}"
			};

			switch (currentInfo.targetApi)
			{
				case Api.D3D9:
					startInfo.Arguments += " --api d3d9";
					break;
				case Api.D3D10:
					startInfo.Arguments += " --api d3d10";
					break;
				case Api.D3D11:
					startInfo.Arguments += " --api d3d11";
					break;
				case Api.D3D12:
					startInfo.Arguments += " --api d3d12";
					break;
				case Api.DXGI:
					startInfo.Arguments += " --api dxgi";
					break;
				case Api.DDraw:
					startInfo.Arguments += " --api ddraw";
					break;
				case Api.OpenGL:
					startInfo.Arguments += " --api opengl";
					break;
				case Api.Vulkan:
					startInfo.Arguments += " --api vulkan";
					break;
			}

			if (currentInfo.targetOpenXR)
			{
				startInfo.Arguments += " --api openxr";
			}

			switch (currentOperation)
			{
				case InstallOperation.Finished:
					startInfo.Arguments += " --state finished";
					break;
				case InstallOperation.Update:
					startInfo.Arguments += " --state update";
					break;
				case InstallOperation.UpdateWithEffects:
					startInfo.Arguments += " --state modify";
					break;
				case InstallOperation.Uninstall:
					startInfo.Arguments += " --state uninstall";
					break;
			}

			try
			{
				Process.Start(startInfo);
				Close();
				return true;
			}
			catch
			{
				return false;
			}
		}

		void CheckForUpdate(string currentVersion)
		{
			using (var client = new WebClient())
			{
				// GitHub API requests require a user agent to be set
				client.Headers["User-Agent"] = "reshade";

				client.DownloadStringCompleted += (s, e) =>
				{
					if (e.Error != null)
					{
						return;
					}

					string data = e.Result;
					if (data.Length == 0)
					{
						return;
					}

					int tagBeg = data.IndexOf("\"v");
					if (tagBeg < 0)
					{
						return;
					}

					tagBeg += 2;

					int tagEnd = data.IndexOf('\"', tagBeg);
					if (tagEnd < 0)
					{
						return;
					}

					string latestVersion = data.Substring(tagBeg, tagEnd - tagBeg);

					if (new Version(latestVersion) > new Version(currentVersion))
					{
						MessageBox.Show(this, "This build of ReShade is outdated. A newer version (v" + latestVersion + ") can be downloaded from https://reshade.me.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
					}
				};

				try
				{

					client.DownloadStringAsync(new Uri("https://api.github.com/repos/crosire/reshade/tags"));
				}
				catch (WebException)
				{
					// Ignore if update check failed
				}
			}
		}

		void DownloadCompatibilityIni()
		{
			if (compatibilityIni != null)
			{
				return;
			}

			UpdateStatus("Downloading compatibility information ...");

			// Attempt to download compatibility list
			using (var client = new WebClient())
			{
				// Ensure files are downloaded again if they changed
				client.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.Revalidate);

				try
				{
					using (Stream compatibilityStream = client.OpenRead("https://raw.githubusercontent.com/crosire/reshade-shaders/list/Compatibility.ini"))
					{
						compatibilityIni = new IniFile(compatibilityStream);
					}
				}
				catch (WebException)
				{
					// Ignore if this list failed to download, since setup can still proceed without them
				}
			}
		}

		void InstallStep_AnalyzeExecutable()
		{
			DownloadCompatibilityIni();

			UpdateStatus("Analyzing executable ...");

			// In case this is the bootstrap executable of an Unreal Engine game, try and find the actual game executable for it
			string targetPathUnrealEngine = PEInfo.ReadResourceString(currentInfo.targetPath, 201); // IDI_EXEC_FILE (see BootstrapPackagedGame.cpp in Unreal Engine source code)
			if (!string.IsNullOrEmpty(targetPathUnrealEngine) && File.Exists(targetPathUnrealEngine = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentInfo.targetPath), targetPathUnrealEngine))))
			{
				currentInfo.targetPath = targetPathUnrealEngine;
			}

			var info = FileVersionInfo.GetVersionInfo(currentInfo.targetPath);
			currentInfo.targetName = info.FileDescription;
			if (currentInfo.targetName is null || currentInfo.targetName.Trim().Length == 0)
			{
				currentInfo.targetName = Path.GetFileNameWithoutExtension(currentInfo.targetPath);
				if (char.IsLower(currentInfo.targetName[0]))
				{
					currentInfo.targetName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(currentInfo.targetName);
				}
			}

			var peInfo = new PEInfo(currentInfo.targetPath);
			currentInfo.is64Bit = peInfo.Type == PEInfo.BinaryType.IMAGE_FILE_MACHINE_AMD64;

			bool isApiD3D9 = false;
			bool isApiDXGI = false;
			bool isApiDDraw = false;
			bool isApiOpenGL = false;
			bool isApiVulkan = false;
			currentInfo.targetApi = Api.Unknown;
			currentInfo.targetOpenXR = false;

			string basePath = Path.GetDirectoryName(currentInfo.targetPath);
			if (basePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
			{
				UpdateStatusAndFinish(false, "Installation to the Windows directory is prohibited.");
				return;
			}

			// Check whether the API is specified in the compatibility list, in which case setup can continue right away
			string executableName = Path.GetFileName(currentInfo.targetPath);
			if (compatibilityIni?.GetString(executableName, "Banned") == "1")
			{
				// Automatically uninstall ReShade from banned applications
				foreach (string conflictingModuleName in new[] { "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll" })
				{
					string conflictingModulePath = Path.Combine(basePath, conflictingModuleName);

					try
					{
						if (GetModuleProductName(conflictingModulePath) == "ReShade")
						{
							File.Delete(conflictingModulePath);
						}
					}
					catch (SystemException)
					{
						// Ignore errors
						continue;
					}
				}

				UpdateStatusAndFinish(false, "The target application is known to have blocked or banned the usage of ReShade. Cannot continue installation.");
				return;
			}

			if (peInfo.StackSize != 0 && peInfo.StackSize < 1000000 && !executableName.Equals("gamelaunchhelper.exe", StringComparison.OrdinalIgnoreCase))
			{
				UpdateStatus("Waiting for user confirmation ...");

				Dispatcher.Invoke(() =>
				{
					MessageBox.Show(this, "The target application uses a small default stack size.\nIn order to use ReShade without crashing you'll have to patch the executable to increase the stack size from " + peInfo.StackSize + " bytes to at least 1 MB.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
				});
			}

			if (compatibilityIni != null && compatibilityIni.HasValue(executableName, "RenderApi"))
			{
				if (compatibilityIni.HasValue(executableName, "InstallTarget"))
				{
					basePath = Path.Combine(basePath, compatibilityIni.GetString(executableName, "InstallTarget"));

					if (compatibilityIni.HasValue(executableName, "Is64Bit"))
					{
						currentInfo.is64Bit = compatibilityIni.GetString(executableName, "Is64Bit") == "1";
					}
				}

				string api = compatibilityIni.GetString(executableName, "RenderApi");

				if (api == "D3D8" || api == "D3D9")
				{
					isApiD3D9 = true;
				}
				else if (api == "D3D10" || api == "D3D11" || api == "D3D12" || api == "DXGI")
				{
					isApiDXGI = true;
				}
				else if (api == "DDraw")
				{
					isApiDDraw = true;
				}
				else if (api == "OpenGL")
				{
					isApiOpenGL = true;
				}
				else if (api == "Vulkan")
				{
					isApiVulkan = true;
				}
			}
			else
			{
				bool isApiD3D8 = peInfo.Modules.Any(s => s.StartsWith("d3d8", StringComparison.OrdinalIgnoreCase));
				isApiD3D9 = isApiD3D8 || peInfo.Modules.Any(s => s.StartsWith("d3d9", StringComparison.OrdinalIgnoreCase));
				isApiDXGI = peInfo.Modules.Any(s => s.StartsWith("dxgi", StringComparison.OrdinalIgnoreCase) || s.StartsWith("d3d1", StringComparison.OrdinalIgnoreCase) || s.Contains("GFSDK")); // Assume DXGI when GameWorks SDK is in use
				isApiDDraw = peInfo.Modules.Any(s => s.StartsWith("ddraw", StringComparison.OrdinalIgnoreCase));
				isApiOpenGL = peInfo.Modules.Any(s => s.StartsWith("opengl32", StringComparison.OrdinalIgnoreCase));
				isApiVulkan = peInfo.Modules.Any(s => s.StartsWith("vulkan-1", StringComparison.OrdinalIgnoreCase));
				// currentInfo.targetOpenXR = peInfo.Modules.Any(s => s.StartsWith("openxr_loader", StringComparison.OrdinalIgnoreCase));

				if (isApiD3D9 && isApiDXGI)
				{
					isApiD3D9 = false; // Prefer DXGI over D3D9
				}
				if (isApiD3D8 && !isHeadless)
				{
					UpdateStatus("Waiting for user confirmation ...");

					Dispatcher.Invoke(() =>
					{
						MessageBox.Show(this, "It looks like the target application uses Direct3D 8.\nIn order to use ReShade you'll have to download an additional wrapper from 'https://github.com/crosire/d3d8to9/releases' which converts all API calls to Direct3D 9.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
					});
				}
				if (isApiDXGI && isApiVulkan)
				{
					isApiDXGI = false; // Prefer Vulkan over Direct3D 12
				}
				if (isApiOpenGL && (isApiD3D8 || isApiD3D9 || isApiDXGI || isApiVulkan))
				{
					isApiOpenGL = false; // Prefer Vulkan and Direct3D over OpenGL
				}
			}

			// In case DXVK is installed, default to Vulkan
			if (GetModuleProductName(Path.Combine(basePath, "d3d9.dll")) == "DXVK" ||
				GetModuleProductName(Path.Combine(basePath, "dxgi.dll")) == "DXVK")
			{
				isApiVulkan = true;
			}

			// In case this game is modded with NVIDIA RTX Remix, install to the Remix Bridge
			string targetPathRemixBridge = Path.Combine(basePath, ".trex", "NvRemixBridge.exe");
			if (File.Exists(targetPathRemixBridge))
			{
				isApiVulkan = true;
				currentInfo.is64Bit = true;
				currentInfo.targetPath = targetPathRemixBridge;
			}

			if (isApiVulkan)
			{
				currentInfo.targetApi = Api.Vulkan;
			}
			else if (isApiD3D9)
			{
				currentInfo.targetApi = Api.D3D9;
			}
			else if (isApiDXGI)
			{
				currentInfo.targetApi = Api.DXGI;
			}
			else if (isApiOpenGL)
			{
				currentInfo.targetApi = Api.OpenGL;
			}
			else if (isApiDDraw)
			{
				currentInfo.targetApi = Api.DDraw;
			}

			if (isHeadless)
			{
				InstallStep_CheckExistingInstallation();
				return;
			}

			Dispatcher.Invoke(() =>
			{
				CurrentPage.Navigate(new SelectApiPage(currentInfo.targetName)
				{
					SelectedApi = currentInfo.targetApi,
					SelectedOpenXR = currentInfo.targetOpenXR
				});
			});
		}
		void InstallStep_CheckExistingInstallation()
		{
			DownloadCompatibilityIni();

			UpdateStatus("Checking installation status ...");

			string basePath = Path.GetDirectoryName(currentInfo.targetPath);
			string executableName = Path.GetFileName(currentInfo.targetPath);

			if (currentInfo.targetApi != Api.Vulkan && compatibilityIni != null)
			{
				if (compatibilityIni.HasValue(executableName, "InstallTarget"))
				{
					basePath = Path.Combine(basePath, compatibilityIni.GetString(executableName, "InstallTarget"));

					var globalConfig = new IniFile(Path.Combine(Path.GetDirectoryName(currentInfo.targetPath), "ReShade.ini"));
					globalConfig.SetValue("INSTALL", "BasePath", basePath);
					globalConfig.SaveFile();
				}

				if (compatibilityIni.GetString(executableName, "ForceInstallApi") == "1")
				{
					string api = compatibilityIni.GetString(executableName, "RenderApi");

					if (api == "D3D8" || api == "D3D9")
					{
						currentInfo.targetApi = Api.D3D9;
					}
					else if (api == "D3D10")
					{
						currentInfo.targetApi = Api.D3D10;
					}
					else if (api == "D3D11")
					{
						currentInfo.targetApi = Api.D3D11;
					}
					else if (api == "D3D12")
					{
						currentInfo.targetApi = Api.D3D12;
					}
					else if (api == "DDraw")
					{
						currentInfo.targetApi = Api.DDraw;
					}
					else if (api == "OpenGL")
					{
						currentInfo.targetApi = Api.OpenGL;
					}
				}
			}

			currentInfo.configPath = Path.Combine(basePath, "ReShade.ini");

			if (currentInfo.targetApi == Api.Vulkan || currentInfo.targetOpenXR)
			{
				string moduleName = currentInfo.is64Bit ? "ReShade64" : "ReShade32";
				currentInfo.modulePath = Path.Combine(commonPath, moduleName, moduleName + ".dll");

				if (currentOperation == InstallOperation.Install && File.Exists(currentInfo.configPath))
				{
					if (isHeadless)
					{
						UpdateStatusAndFinish(false, "Existing ReShade installation found. Please uninstall it first.");
					}
					else
					{
						Dispatcher.Invoke(() =>
						{
							CurrentPage.Navigate(new SelectOperationPage());
						});
					}
					return;
				}
			}
			else
			{
				switch (currentInfo.targetApi)
				{
					case Api.D3D9:
						currentInfo.modulePath = "d3d9.dll";
						break;
					case Api.D3D10:
						currentInfo.modulePath = "d3d10.dll";
						break;
					case Api.D3D11:
						currentInfo.modulePath = "d3d11.dll";
						break;
					case Api.D3D12:
						currentInfo.modulePath = "d3d12.dll";
						break;
					case Api.DXGI:
						currentInfo.modulePath = "dxgi.dll";
						break;
					case Api.DDraw:
						currentInfo.modulePath = "ddraw.dll";
						break;
					case Api.OpenGL:
						currentInfo.modulePath = "opengl32.dll";
						break;
					default: // No API selected, abort immediately
						UpdateStatusAndFinish(false, "Could not detect the rendering API used by the application.");
						return;
				}

				currentInfo.modulePath = Path.Combine(basePath, currentInfo.modulePath);

				if (currentOperation == InstallOperation.Install && GetModuleProductName(currentInfo.modulePath) != null)
				{
					if (GetModuleProductName(currentInfo.modulePath) == "ReShade")
					{
						if (isHeadless)
						{
							UpdateStatusAndFinish(false, "Existing ReShade installation found. Please uninstall it first.");
							return;
						}

						Dispatcher.Invoke(() =>
						{
							CurrentPage.Navigate(new SelectOperationPage());
						});
					}
					else
					{
						UpdateStatusAndFinish(false, Path.GetFileName(currentInfo.modulePath) + " already exists, but does not belong to ReShade.\nPlease make sure this is not a system file required by the game.");
					}
					return;
				}
			}

			foreach (string conflictingModuleName in new[] { "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll" })
			{
				string conflictingModulePath = Path.Combine(basePath, conflictingModuleName);

				if (currentOperation == InstallOperation.Install && GetModuleProductName(conflictingModulePath) == "ReShade")
				{
					if (isHeadless)
					{
						UpdateStatusAndFinish(false, "Existing ReShade installation for another API found.\nMultiple simultaneous ReShade installations are not supported. Please uninstall the existing one first.");
					}
					else
					{
						Dispatcher.Invoke(() =>
						{
							CurrentPage.Navigate(new SelectOperationPage());
						});
					}
					return;
				}
			}

			if (currentOperation != InstallOperation.Uninstall)
			{
				InstallStep_InstallReShadeModule();
			}
			else
			{
				InstallStep_UninstallReShadeModule();
			}
		}
		void InstallStep_InstallReShadeModule()
		{
			UpdateStatus("Installing ReShade ...");

			ZipArchive zip;

			try
			{
				// Extract archive attached to this executable
				MemoryStream output;

				using (FileStream input = File.OpenRead(Assembly.GetExecutingAssembly().Location))
				{
					output = new MemoryStream((int)input.Length);

					byte[] block = new byte[512];
					byte[] signature = { 0x50, 0x4B, 0x03, 0x04 }; // PK..

					// Look for archive at the end of this executable and copy it to a memory stream
					while (input.Read(block, 0, block.Length) >= signature.Length)
					{
						if (block.Take(signature.Length).SequenceEqual(signature) && block.Skip(signature.Length).Take(26).Max() != 0)
						{
							output.Write(block, 0, block.Length);
							input.CopyTo(output);
							break;
						}
					}
				}

				zip = new ZipArchive(output, ZipArchiveMode.Read, false);

				// Validate archive contains the ReShade DLLs
				if (zip.GetEntry("ReShade32.dll") == null || zip.GetEntry("ReShade64.dll") == null)
				{
					throw new InvalidDataException();
				}
			}
			catch (InvalidDataException)
			{
				UpdateStatusAndFinish(false, "This setup archive is corrupted! Please download from https://reshade.me again.");
				return;
			}

			string basePath = Path.GetDirectoryName(currentInfo.configPath);

			// Delete any existing and conflicting ReShade installations first
			foreach (string conflictingModuleName in new[] { "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll" })
			{
				string conflictingModulePath = Path.Combine(basePath, conflictingModuleName);

				try
				{
					if (GetModuleProductName(conflictingModulePath) == "ReShade")
					{
						File.Delete(conflictingModulePath);
					}
				}
				catch (SystemException)
				{
					// Ignore errors
					continue;
				}
			}

			if (currentInfo.targetApi == Api.Vulkan || currentInfo.targetOpenXR)
			{
				if (!isElevated)
				{
					Dispatcher.Invoke(() =>
					{
						if (!RestartWithElevatedPrivileges())
						{
							UpdateStatusAndFinish(false, "Failed to acquire elevated privileges to install Vulkan layer.");
						}
					});
					return;
				}

				try
				{
					if (!Directory.Exists(commonPath))
					{
						Directory.CreateDirectory(commonPath);
					}

					// Make sure the DLLs will have permissions set up for 'ALL_APPLICATION_PACKAGES', so that loading the layer won't fail in UWP apps
					var sid = new SecurityIdentifier("S-1-15-2-1");

					DirectorySecurity access = Directory.GetAccessControl(commonPath);
					access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
					Directory.SetAccessControl(commonPath, access);
				}
				catch (SystemException ex)
				{
					UpdateStatusAndFinish(false, "Failed to create installation directory:\n" + ex.Message);
					return;
				}

				try
				{
					string commonPathLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReShade");
					string appConfigPath = Path.Combine(commonPath, "ReShadeApps.ini");
					string appConfigPathLocal = Path.Combine(commonPathLocal, "ReShadeApps.ini");

					// Try to migrate previous ReShade installation
					if (!File.Exists(appConfigPath) && File.Exists(appConfigPathLocal))
					{
						File.Move(appConfigPathLocal, appConfigPath);
					}

					// Unregister any layers from previous ReShade installations
					using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Khronos\Vulkan\ExplicitLayers", true))
					{
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade32", "ReShade32.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade64", "ReShade64.json"), false);
					}
					using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Khronos\Vulkan\ImplicitLayers", true))
					{
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "VkLayer_override.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade32_vk_override_layer.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade64_vk_override_layer.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade32", "ReShade32.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade64", "ReShade64.json"), false);
					}
					using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\Khronos\Vulkan\ImplicitLayers", true))
					{
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade32", "ReShade32.json"), false);
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade64", "ReShade64.json"), false);
					}
					using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\Wow6432Node\Khronos\Vulkan\ImplicitLayers", true))
					{
						key.DeleteValue(Path.Combine(commonPathLocal, "ReShade32", "ReShade32.json"), false);
					}
				}
				catch (SystemException)
				{
					// Ignore errors
				}

				foreach (string layerModuleName in new[] { "ReShade32", "ReShade64" })
				{
					string layerModulePath = Path.Combine(commonPath, layerModuleName + ".dll");

					try
					{
						ZipArchiveEntry module = zip.GetEntry(Path.GetFileName(layerModulePath)) ?? throw new FileFormatException("Setup archive is missing ReShade DLL file.");
						module.ExtractToFile(layerModulePath, true);
					}
					catch (SystemException ex)
					{
						UpdateStatusAndFinish(false, "Failed to install " + Path.GetFileName(layerModulePath) + ":\n" + ex.Message);
						return;
					}

					if (currentInfo.targetApi == Api.Vulkan)
					{
						string layerManifestPath = Path.Combine(commonPath, layerModuleName + ".json");

						try
						{
							ZipArchiveEntry manifest = zip.GetEntry(Path.GetFileName(layerManifestPath)) ?? throw new FileFormatException("Setup archive is missing Vulkan layer manifest file.");
							manifest.ExtractToFile(layerManifestPath, true);

							// Register this layer manifest
							using (RegistryKey key = Registry.LocalMachine.CreateSubKey(Environment.Is64BitOperatingSystem && layerModuleName == "ReShade32" ? @"Software\Wow6432Node\Khronos\Vulkan\ImplicitLayers" : @"Software\Khronos\Vulkan\ImplicitLayers"))
							{
								key.SetValue(layerManifestPath, 0, RegistryValueKind.DWord);
							}
						}
						catch (SystemException ex)
						{
							UpdateStatusAndFinish(false, "Failed to install Vulkan layer manifest:\n" + ex.Message);
							return;
						}
					}

					if (currentInfo.targetOpenXR)
					{
						string layerManifestPathXR = Path.Combine(commonPath, layerModuleName + "_XR.json");

						try
						{
							ZipArchiveEntry manifest = zip.GetEntry(Path.GetFileName(layerManifestPathXR)) ?? throw new FileFormatException("Setup archive is missing OpenXR layer manifest file.");
							manifest.ExtractToFile(layerManifestPathXR, true);

							// Register this layer manifest
							using (RegistryKey key = Registry.LocalMachine.CreateSubKey(Environment.Is64BitOperatingSystem && layerModuleName == "ReShade32" ? @"Software\Wow6432Node\Khronos\OpenXR\1\ApiLayers\Implicit" : @"Software\Khronos\OpenXR\1\ApiLayers\Implicit"))
							{
								key.SetValue(layerManifestPathXR, 0, RegistryValueKind.DWord);
							}
						}
						catch (SystemException ex)
						{
							UpdateStatusAndFinish(false, "Failed to install OpenXR layer manifest:\n" + ex.Message);
							return;
						}
					}
				}

				var appConfig = new IniFile(Path.Combine(commonPath, "ReShadeApps.ini"));
				if (appConfig.GetValue(string.Empty, "Apps", out string[] appKeys) == false || !appKeys.Contains(currentInfo.targetPath))
				{
					List<string> appKeysList = appKeys?.ToList() ?? new List<string>();
					appKeysList.Add(currentInfo.targetPath);

					appConfig.SetValue(string.Empty, "Apps", appKeysList.ToArray());
					appConfig.SaveFile();
				}
			}
			else
			{
				string parentPath = Path.GetDirectoryName(currentInfo.modulePath);

				try
				{
					ZipArchiveEntry module = zip.GetEntry(currentInfo.is64Bit ? "ReShade64.dll" : "ReShade32.dll") ?? throw new FileFormatException("Setup archive is missing ReShade DLL file.");
					module.ExtractToFile(currentInfo.modulePath, true);
				}
				catch (SystemException ex)
				{
					UpdateStatusAndFinish(false, "Failed to install " + Path.GetFileName(currentInfo.modulePath) + ":\n" + ex.Message +
							(currentOperation != InstallOperation.Install ? "\n\nMake sure the target application is not still running!" : string.Empty));
					return;
				}

				// Create a default log file for troubleshooting
				File.WriteAllText(Path.Combine(Path.GetDirectoryName(currentInfo.targetPath), "ReShade.log"), @"
If you are reading this after launching the game at least once, it likely means ReShade was not loaded by the game.

In that event here are some steps you can try to resolve this:

1) Make sure this file and the related DLL are really in the same directory as the game executable.
   If that is the case and it does not work regardless, check if there is a 'bin' directory, move them there and try again.

2) Try running the game with elevated user permissions by doing a right click on its executable and choosing 'Run as administrator'.

3) If the game crashes, try disabling all game overlays (like Origin), recording software (like Fraps), FPS displaying software,
   GPU overclocking and tweaking software and other proxy DLLs (like ENB, Helix or Umod).

4) If none of the above helps, you can get support on the forums at https://reshade.me/forum. But search for your problem before
   creating a new topic, as somebody else may have already found a solution.
");
			}

			// Copy potential pre-made configuration file to target
			if (!File.Exists(currentInfo.configPath))
			{
				try
				{
					foreach (string premadeConfigPath in new[] { "ReShade.ini", Path.Combine(Path.GetDirectoryName(currentInfo.configPath), "GShade.ini") })
					{
						if (File.Exists(premadeConfigPath))
						{
							File.Copy(premadeConfigPath, currentInfo.configPath);
							break;
						}
					}
				}
				catch (SystemException ex)
				{
					UpdateStatusAndFinish(false, "Failed to install " + Path.GetFileName(currentInfo.configPath) + ":\n" + ex.Message);
					return;
				}
			}

			// Add default configuration
			var config = new IniFile(currentInfo.configPath);
			if (compatibilityIni != null && !config.HasValue("GENERAL", "PreprocessorDefinitions"))
			{
				string executableName = Path.GetFileName(currentInfo.targetPath);

				string depthReversed = compatibilityIni.GetString(executableName, "DepthReversed", "0");
				string depthUpsideDown = compatibilityIni.GetString(executableName, "DepthUpsideDown", "0");
				string depthLogarithmic = compatibilityIni.GetString(executableName, "DepthLogarithmic", "0");
				if (!compatibilityIni.HasValue(executableName, "DepthReversed"))
				{
					var info = FileVersionInfo.GetVersionInfo(currentInfo.targetPath);
					if (info.LegalCopyright != null)
					{
						Match match = new Regex(@"(20[0-9]{2})", RegexOptions.RightToLeft).Match(info.LegalCopyright);
						if (match.Success && int.TryParse(match.Groups[1].Value, out int year))
						{
							// Modern games usually use reversed depth
							depthReversed = (year >= 2012) ? "1" : "0";
						}
					}
				}

				config.SetValue("GENERAL", "PreprocessorDefinitions",
					"RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0",
					"RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=" + depthUpsideDown,
					"RESHADE_DEPTH_INPUT_IS_REVERSED=" + depthReversed,
					"RESHADE_DEPTH_INPUT_IS_LOGARITHMIC=" + depthLogarithmic);

				if (compatibilityIni.HasValue(executableName, "DepthCopyBeforeClears") ||
					compatibilityIni.HasValue(executableName, "DepthCopyAtClearIndex") ||
					compatibilityIni.HasValue(executableName, "DrawStatsHeuristic") ||
					compatibilityIni.HasValue(executableName, "UseAspectRatioHeuristics"))
				{
					config.SetValue("DEPTH", "DepthCopyBeforeClears",
						compatibilityIni.GetString(executableName, "DepthCopyBeforeClears", "0"));
					config.SetValue("DEPTH", "DepthCopyAtClearIndex",
						compatibilityIni.GetString(executableName, "DepthCopyAtClearIndex", "0"));
					config.SetValue("DEPTH", "DrawStatsHeuristic",
						compatibilityIni.GetString(executableName, "DrawStatsHeuristic", "0"));
					config.SetValue("DEPTH", "UseAspectRatioHeuristics",
						compatibilityIni.GetString(executableName, "UseAspectRatioHeuristics", "1"));
				}
			}

			// Always add input section
			if (!config.HasValue("INPUT"))
			{
				config.SetValue("INPUT", "KeyOverlay", "36,0,0,0");
				// Only enable gamepad input in cases where keyboard and mouse input is known to not work (when installed to UWP apps or the NVIDIA RTX Remix Bridge)
				config.SetValue("INPUT", "GamepadNavigation", currentInfo.targetPath.Contains("WindowsApps") || Path.GetFileName(currentInfo.targetPath) == "NvRemixBridge.exe" ? "1" : "0");
			}

			if (!config.HasValue("PROXY"))
			{
				config.SetValue("PROXY", "EnableProxyLibrary", "0");
				config.SetValue("PROXY", "ProxyLibrary", "");
			}

			config.SaveFile();

			// Change file permissions for files ReShade needs write access to
			MakeWritable(currentInfo.configPath);
			MakeWritable(Path.Combine(Path.GetDirectoryName(currentInfo.targetPath), "ReShade.log"));
			MakeWritable(Path.Combine(basePath, "ReShadePreset.ini"));

			if (!isHeadless && currentOperation != InstallOperation.Update)
			{
				currentInfo.presetPath = config.GetString("GENERAL", "PresetPath", string.Empty);

				if (!string.IsNullOrEmpty(currentInfo.presetPath))
				{
					// Change current directory so that "Path.GetFullPath" resolves correctly
					string currentPath = Directory.GetCurrentDirectory();
					Directory.SetCurrentDirectory(basePath);
					currentInfo.presetPath = Path.GetFullPath(currentInfo.presetPath);
					Directory.SetCurrentDirectory(currentPath);
				}

				Dispatcher.Invoke(() =>
				{
					CurrentPage.Navigate(new SelectEffectsPage
					{
						PresetPath = currentInfo.presetPath
					});
				});
				return;
			}

			// Add default search paths if no config exists
			if (!config.HasValue("GENERAL", "EffectSearchPaths") && !config.HasValue("GENERAL", "TextureSearchPaths"))
			{
				WriteSearchPaths(".\\reshade-shaders\\Shaders\\**", ".\\reshade-shaders\\Textures\\**");
			}

			InstallStep_Finish();
		}
		void InstallStep_UninstallReShadeModule()
		{
			if (currentInfo.targetApi == Api.Vulkan || currentInfo.targetOpenXR)
			{
				if (!isElevated)
				{
					Dispatcher.Invoke(() =>
					{
						if (!RestartWithElevatedPrivileges())
						{
							UpdateStatusAndFinish(false, "Failed to acquire elevated privileges to uninstall Vulkan layer.");
						}
					});
					return;
				}

				var appConfig = new IniFile(Path.Combine(commonPath, "ReShadeApps.ini"));
				if (appConfig.GetValue(string.Empty, "Apps", out string[] appKeys))
				{
					List<string> appKeysList = appKeys.ToList();
					appKeysList.Remove(currentInfo.targetPath);
					appConfig.SetValue(string.Empty, "Apps", appKeysList.ToArray());

					if (appKeysList.Count == 0)
					{
						try
						{
							Directory.Delete(commonPath, true);

							using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"Software\Khronos\Vulkan\ImplicitLayers"))
							{
								key.DeleteValue(Path.Combine(commonPath, "ReShade32.json"), false);
								key.DeleteValue(Path.Combine(commonPath, "ReShade64.json"), false);
							}

							using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"Software\Khronos\OpenXR\1\ApiLayers\Implicit"))
							{
								key.DeleteValue(Path.Combine(commonPath, "ReShade32_XR.json"), false);
								key.DeleteValue(Path.Combine(commonPath, "ReShade64_XR.json"), false);
							}

							if (Environment.Is64BitOperatingSystem)
							{
								using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"Software\Wow6432Node\Khronos\Vulkan\ImplicitLayers"))
								{
									key.DeleteValue(Path.Combine(commonPath, "ReShade32.json"), false);
								}

								using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"Software\Wow6432Node\Khronos\OpenXR\1\ApiLayers\Implicit"))
								{
									key.DeleteValue(Path.Combine(commonPath, "ReShade32_XR.json"), false);
								}
							}
						}
						catch (SystemException ex)
						{
							UpdateStatusAndFinish(false, "Failed to delete Vulkan layer manifest:\n" + ex.Message);
							return;
						}
					}
					else
					{
						appConfig.SaveFile();
					}
				}
			}

			try
			{
				string basePath = Path.GetDirectoryName(currentInfo.configPath);

				if (currentInfo.modulePath != null && currentInfo.targetApi != Api.Vulkan && !currentInfo.targetOpenXR)
				{
					File.Delete(currentInfo.modulePath);
				}

				// Read search paths from config file before deleting it
				GetEffectSearchPaths(out List<KeyValuePair<string, bool>> effectSearchPaths);

				if (File.Exists(currentInfo.configPath))
				{
					File.Delete(currentInfo.configPath);
				}

				if (File.Exists(Path.Combine(Path.GetDirectoryName(currentInfo.targetPath), "ReShade.log")))
				{
					File.Delete(Path.Combine(Path.GetDirectoryName(currentInfo.targetPath), "ReShade.log"));
				}

				foreach (KeyValuePair<string, bool> searchPath in effectSearchPaths)
				{
					if (searchPath.Key.StartsWith(basePath) && Directory.Exists(searchPath.Key))
					{
						string[] extensions = { "*.fx", "*.fxh" };

						foreach (string effectFile in extensions.SelectMany(ext => Directory.EnumerateFiles(searchPath.Key, ext, searchPath.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)))
						{
							File.Delete(effectFile);
						}

						foreach (string parentPath in Directory.EnumerateDirectories(searchPath.Key, "*", searchPath.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Union(new string[] { searchPath.Key }).OrderByDescending(path => path.Length))
						{
							if (Directory.EnumerateFiles(parentPath, "*", SearchOption.AllDirectories).Any() == false)
							{
								Directory.Delete(parentPath, false);
							}
						}
					}
				}

				// Delete add-ons
				foreach (string addonFile in Directory.EnumerateFiles(Path.GetDirectoryName(currentInfo.targetPath), currentInfo.is64Bit ? "*.addon64" : "*.addon32", SearchOption.TopDirectoryOnly))
				{
					File.Delete(addonFile);
				}

				// Delete all other existing ReShade installations too
				foreach (string conflictingModuleName in new[] { "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "opengl32.dll" })
				{
					string conflictingModulePath = Path.Combine(basePath, conflictingModuleName);

					if (GetModuleProductName(conflictingModulePath) == "ReShade")
					{
						File.Delete(conflictingModulePath);
					}
				}
			}
			catch (SystemException ex)
			{
				UpdateStatusAndFinish(false, "Failed to delete some ReShade files:\n" + ex.Message +
					(currentOperation != InstallOperation.Install ? "\n\nMake sure the target application is not still running!" : string.Empty));
				return;
			}

			InstallStep_Finish();
		}
		void InstallStep_CheckPreset()
		{
			if (!string.IsNullOrEmpty(currentInfo.presetPath) && File.Exists(currentInfo.presetPath))
			{
				string basePath = Path.GetDirectoryName(currentInfo.configPath);
				string presetPath = currentInfo.presetPath;
				if (presetPath.StartsWith(basePath))
				{
					// Try and make preset path relative
					presetPath = "." + presetPath.Substring(basePath.Length);
				}

				var config = new IniFile(currentInfo.configPath);
				config.SetValue("GENERAL", "PresetPath", presetPath);
				config.SaveFile();

				MakeWritable(currentInfo.presetPath);
			}
		}
		void InstallStep_DownloadEffectPackage(EffectPackage package)
		{
			UpdateStatus("Downloading " + package.Name + " from " + package.DownloadUrl + " ...");

			string downloadPath = Path.Combine(Path.GetTempPath(), "ReShadeSetupDownload.tmp");

			using (var client = new WebClient())
			{
				client.DownloadProgressChanged += (object sender, DownloadProgressChangedEventArgs e) =>
				{
					// Avoid negative percentage values
					if (e.TotalBytesToReceive > 0)
					{
						UpdateStatus("Downloading " + package.Name + " ... (" + ((100 * e.BytesReceived) / e.TotalBytesToReceive) + "%)");
					}
				};

				try
				{
					client.DownloadFile(new Uri(package.DownloadUrl), downloadPath);
				}
				catch (WebException ex)
				{
					UpdateStatusAndFinish(false, "Failed to download from " + package.DownloadUrl + ":\n" + ex.Message);
					return;
				}
			}

			InstallStep_InstallEffectPackage(package, downloadPath);
		}
		void InstallStep_InstallEffectPackage(EffectPackage package, string downloadPath)
		{
			UpdateStatus("Extracting " + package.Name + " ...");

			string tempPath = Path.Combine(Path.GetTempPath(), "ReShadeSetup");
			string tempPathEffects = null;
			string tempPathTextures = null;

			string basePath = Path.GetDirectoryName(currentInfo.configPath);
			string targetPathEffects = Path.GetFullPath(Path.Combine(basePath, package.InstallPath));
			string targetPathTextures = Path.GetFullPath(Path.Combine(basePath, package.TextureInstallPath));

			IEnumerable<string> effects = null;

			try
			{
				// Delete existing directories since extraction fails if the target is not empty
				if (Directory.Exists(tempPath))
				{
					Directory.Delete(tempPath, true);
				}

				ZipFile.ExtractToDirectory(downloadPath, tempPath);

				effects = Directory.GetFiles(tempPath, "*.fx", SearchOption.AllDirectories);

				// First check for a standard directory name
				tempPathEffects = Directory.EnumerateDirectories(tempPath, "Shaders", SearchOption.AllDirectories).FirstOrDefault();
				tempPathTextures = Directory.EnumerateDirectories(tempPath, "Textures", SearchOption.AllDirectories).FirstOrDefault();

				// If that does not exist, look for the first directory that contains shaders/textures
				if (tempPathEffects == null)
				{
					tempPathEffects = effects.Select(x => Path.GetDirectoryName(x)).OrderBy(x => x.Length).FirstOrDefault();
				}
				if (tempPathTextures == null)
				{
					string[] extensions = { "*.png", "*.jpg", "*.jpeg" };

					string path = extensions.SelectMany(ext => Directory.EnumerateFiles(tempPath, ext, SearchOption.AllDirectories)).Select(x => Path.GetDirectoryName(x)).OrderBy(x => x.Length).FirstOrDefault();
					if (!string.IsNullOrEmpty(path))
					{
						tempPathTextures = tempPathTextures != null ? tempPathTextures.Union(path).ToString() : path;
					}
				}

				// Skip any effects not in the shader directory
				effects = effects.Where(filePath => filePath.StartsWith(tempPathEffects));

				// Delete denied effects
				if (package.DenyEffectFiles != null)
				{
					var denyEffectFiles = effects.Where(effectPath => package.DenyEffectFiles.Contains(Path.GetFileName(effectPath)));

					foreach (string effectPath in denyEffectFiles)
					{
						File.Delete(effectPath);
					}

					effects = effects.Except(denyEffectFiles);
				}

				// Delete all unselected effects
				if (package.Selected == null)
				{
					var disabledEffectFiles = effects.Where(effectPath => package.EffectFiles.Any(effectFile => !effectFile.Selected && effectFile.FileName == Path.GetFileName(effectPath)));

					foreach (string effectPath in disabledEffectFiles)
					{
						File.Delete(effectPath);
					}

					effects = effects.Except(disabledEffectFiles);
				}
			}
			catch (SystemException ex)
			{
				UpdateStatusAndFinish(false, "Failed to extract " + package.Name + ":\n" + ex.Message);
				return;
			}

			GetEffectSearchPaths(out List<KeyValuePair<string, bool>> effectSearchPaths);

			try
			{
				var existingEffectFiles = effectSearchPaths
					.Where(searchPath => (searchPath.Value ? !targetPathEffects.StartsWith(searchPath.Key) : searchPath.Key != targetPathEffects) && Directory.Exists(searchPath.Key))
					.SelectMany(searchPath => Directory.EnumerateFiles(searchPath.Key, "*.fx", searchPath.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
					.Where(effectPath => effects.Select(x => Path.GetFileName(x)).Contains(Path.GetFileName(effectPath)) == true);

				if (existingEffectFiles.Any())
				{
					string existingPathEffects = Path.GetDirectoryName(existingEffectFiles.First());

					bool overwriteExistingEffectFiles = false;
					if (!isHeadless)
					{
						UpdateStatus("Waiting for user confirmation ...");

						Dispatcher.Invoke(() =>
						{
							if (MessageBox.Show(this, "Found " + string.Join(", ", existingEffectFiles.Select(x => Path.GetFileName(x))) + " in \"" + existingPathEffects + "\" instead of in the default location \"" + targetPathEffects + "\".\n\nDo you want to overwrite the files in \"" + existingPathEffects + "\"?", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
							{
								overwriteExistingEffectFiles = true;
							}
						});
					}

					if (!overwriteExistingEffectFiles)
					{
						UpdateStatusAndFinish(false, "Existing effect files found in non-default location. Please remove them first to avoid duplicates.");
						return;
					}

					targetPathEffects = existingPathEffects;
					package.InstallPath = null; // Prevent install path getting written to config below
				}

				// Move only the relevant files to the target
				if (tempPathEffects != null)
				{
					MoveFiles(tempPathEffects, targetPathEffects);
				}
				if (tempPathTextures != null)
				{
					MoveFiles(tempPathTextures, targetPathTextures);
				}

				File.Delete(downloadPath);
				Directory.Delete(tempPath, true);
			}
			catch (SystemException ex)
			{
				UpdateStatusAndFinish(false, "Failed to install " + package.Name + ":\n" + ex.Message);
				return;
			}

			WriteSearchPaths(package.InstallPath, package.TextureInstallPath);
		}
		void InstallStep_CheckAddons()
		{
#if RESHADE_ADDON
			if (!isHeadless)
			{
				Dispatcher.Invoke(() =>
				{
					CurrentPage.Navigate(new SelectAddonsPage(currentInfo.is64Bit));
				});
				return;
			}
#endif

			InstallStep_Finish();
		}
		void InstallStep_DownloadAddon(Addon addon)
		{
			UpdateStatus("Downloading " + addon.Name + " from " + addon.DownloadUrl + " ...");

			string downloadPath = Path.Combine(Path.GetTempPath(), "ReShadeSetupDownload.tmp");

			using (var client = new WebClient())
			{
				client.DownloadProgressChanged += (object sender, DownloadProgressChangedEventArgs e) =>
				{
					// Avoid negative percentage values
					if (e.TotalBytesToReceive > 0)
					{
						UpdateStatus("Downloading " + addon.Name + " ... (" + ((100 * e.BytesReceived) / e.TotalBytesToReceive) + "%)");
					}
				};

				try
				{
					client.DownloadFile(new Uri(addon.DownloadUrl), downloadPath);
				}
				catch (WebException ex)
				{
					UpdateStatusAndFinish(false, "Failed to download from " + addon.DownloadUrl + ":\n" + ex.Message);
					return;
				}
			}

			InstallStep_InstallAddon(addon, downloadPath);
		}
		void InstallStep_InstallAddon(Addon addon, string downloadPath)
		{
			string ext = Path.GetExtension(new Uri(addon.DownloadUrl).AbsolutePath);

			string tempPath = null;
			string tempPathEffects = null;

			if (ext != ".addon" && ext != ".addon32" && ext != ".addon64")
			{
				UpdateStatus("Extracting " + addon.Name + " ...");

				tempPath = Path.Combine(Path.GetTempPath(), "reshade-addons");

				try
				{
					// Delete existing directories since extraction fails if the target is not empty
					if (Directory.Exists(tempPath))
					{
						Directory.Delete(tempPath, true);
					}

					ZipFile.ExtractToDirectory(downloadPath, tempPath);

					string addonPath = Directory.EnumerateFiles(tempPath, currentInfo.is64Bit ? "*.addon64" : "*.addon32", SearchOption.AllDirectories).FirstOrDefault();
					if (addonPath == null)
					{
						IEnumerable<string> addonPaths = Directory.EnumerateFiles(tempPath, "*.addon");
						if (addonPaths.Count() == 1)
						{
							addonPath = addonPaths.First();
						}
						else
						{
							addonPath = addonPaths.FirstOrDefault(x => x.Contains(currentInfo.is64Bit ? "x64" : "x86") || Path.GetFileNameWithoutExtension(x).EndsWith(currentInfo.is64Bit ? "64" : "32"));
						}
					}
					if (addonPath == null)
					{
						Directory.Delete(tempPath, true);
						File.Delete(downloadPath);

						throw new FileFormatException("Add-on archive is missing add-on binary.");
					}

					downloadPath = addonPath;

					// Check for any effect files to install
					IEnumerable<string> effects = Directory.EnumerateFiles(tempPath, "*.fx", SearchOption.TopDirectoryOnly);
					effects = effects.Concat(Directory.EnumerateFiles(tempPath, "*.addonfx", SearchOption.TopDirectoryOnly));

					tempPathEffects = effects.Select(x => Path.GetDirectoryName(x)).OrderBy(x => x.Length).FirstOrDefault();
				}
				catch (SystemException ex)
				{
					UpdateStatusAndFinish(false, "Failed to extract " + addon.Name + ":\n" + ex.Message);
					return;
				}
			}

			string basePath = Path.GetDirectoryName(currentInfo.configPath);
			string targetPathAddon = Path.GetDirectoryName(currentInfo.targetPath);
			string targetPathEffects = Path.GetFullPath(Path.Combine(basePath, addon.EffectInstallPath));

			string globalConfigPath = Path.Combine(targetPathAddon, "ReShade.ini");
			if (File.Exists(globalConfigPath))
			{
				var globalConfig = new IniFile(globalConfigPath);
				targetPathAddon = globalConfig.GetString("ADDON", "AddonPath", targetPathAddon);
			}

			try
			{
				File.Copy(downloadPath, Path.Combine(targetPathAddon, Path.GetFileNameWithoutExtension(tempPath != null ? downloadPath : new Uri(addon.DownloadUrl).AbsolutePath) + (currentInfo.is64Bit ? ".addon64" : ".addon32")), true);

				if (tempPathEffects != null)
				{
					MoveFiles(tempPathEffects, targetPathEffects);

					WriteSearchPaths(addon.EffectInstallPath, null);
				}

				File.Delete(downloadPath);
				if (tempPath != null)
				{
					Directory.Delete(tempPath, true);
				}
			}
			catch (SystemException ex)
			{
				UpdateStatusAndFinish(false, "Failed to install " + addon.Name + ":\n" + ex.Message);
				return;
			}
		}
		void InstallStep_Finish()
		{
			if (currentOperation != InstallOperation.Uninstall)
			{
				UpdateStatusAndFinish(true, "Successfully installed ReShade." +
					(isHeadless ? string.Empty : "\nClick the \"Finish\" button to exit the setup tool.\n\nTo uninstall, run this setup tool and select the application again to be presented with an uninstall option."));
			}
			else
			{
				UpdateStatusAndFinish(true, "Successfully uninstalled ReShade." + (isHeadless ? string.Empty : "\nClick the \"Finish\" button to exit the setup tool."));
			}
		}

		void InstallDlss5(DlssGpuMode mode)
		{
			if (currentInfo.targetPath == null || !File.Exists(currentInfo.targetPath))
			{
				UpdateStatusAndFinish(false, "No target application was provided for DLSS 5 installation.");
				return;
			}

			string targetDirectory = Path.GetDirectoryName(currentInfo.targetPath);
			UpdateStatus("Installing DLSS 5 components...");
			string downloadDirectory = Path.Combine(Path.GetTempPath(), "reshade-dlss5-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(downloadDirectory);
			string streamlineArchive = DownloadDlss5Asset(downloadDirectory, "DLSS310.8.0-Streamline2.13.zip", "3FDB7CB25250259332550F419DBAC516A2EBA778D8592DA75CB2FE8ABFC781D8");
			string neuralAsset = mode == DlssGpuMode.Rtx50
				? DownloadDlss5Asset(downloadDirectory, "nvngx_dlssnr_2026_10_16.zip", "764F94A32EC302B9FE4DA9A1246FD7DD9176E490D8766667687C9FD89DA2AB12")
				: DownloadDlss5Asset(downloadDirectory, "nvngx_dlssnr_compat-fp16-310.9.16-cuda13.4.3.dll", "CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED");
			string renodxAddon = DownloadDlss5Asset(downloadDirectory, "renodx-dlss.addon64", "A8FD34B531FD22C3DBD008ED8FD3CBFCCA2FB451729E19CD1FC2E4C85B5068D4");
			string backupDirectory = Path.Combine(targetDirectory, ".reshade-dlss5-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
			Directory.CreateDirectory(backupDirectory);
			var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var installedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			void Backup(string destination)
			{
				if (!File.Exists(destination) || !backedUp.Add(destination)) return;
				string relative = destination.Substring(targetDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				string backupPath = Path.Combine(backupDirectory, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
				File.Copy(destination, backupPath, true);
			}
			void CopyEntry(string source, string destination)
			{
				Backup(destination);
				Directory.CreateDirectory(Path.GetDirectoryName(destination));
				File.Copy(source, destination, true);
				installedFiles.Add(destination);
			}
			using (var archive = ZipFile.OpenRead(streamlineArchive))
			{
				foreach (var entry in archive.Entries)
				{
					if (string.IsNullOrEmpty(entry.Name)) continue;
					string destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
					if (!destination.StartsWith(Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in Streamline archive.");
					string temporary = Path.Combine(Path.GetTempPath(), "reshade-dlss5-" + Guid.NewGuid().ToString("N") + ".bin");
					try { entry.ExtractToFile(temporary, true); CopyEntry(temporary, destination); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
				}
			}
			if (mode == DlssGpuMode.Rtx50)
			{
				using (var archive = ZipFile.OpenRead(neuralAsset))
				{
					var entry = archive.GetEntry("nvngx_dlssnr.dll") ?? throw new InvalidDataException("nvngx_dlssnr.dll is missing from the neural rendering archive.");
					string temporary = Path.Combine(Path.GetTempPath(), "reshade-dlss5-nr-" + Guid.NewGuid().ToString("N") + ".dll");
					try { entry.ExtractToFile(temporary, true); CopyEntry(temporary, Path.Combine(targetDirectory, "nvngx_dlssnr.dll")); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
				}
			}
			else CopyEntry(neuralAsset, Path.Combine(targetDirectory, "nvngx_dlssnr.dll"));
			CopyEntry(renodxAddon, Path.Combine(targetDirectory, "renodx-dlss.addon64"));
			string manifestPath = Path.Combine(backupDirectory, "manifest.txt");
			using (var manifest = new StreamWriter(manifestPath, false))
			{
				manifest.WriteLine("ReShade DLSS 5 manifest v1");
				foreach (string installedFile in installedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
				{
					string relative = installedFile.Substring(targetDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					manifest.WriteLine(HashFile(installedFile) + "|" + relative);
				}
			}
			UpdateStatusAndFinish(true, "Successfully installed DLSS 5, Streamline and RenoDX. A backup was created in .reshade-dlss5-backup.");
			try { Directory.Delete(downloadDirectory, true); } catch { }
		}

		static string HashFile(string path)
		{
			using (var sha = SHA256.Create())
			using (var input = File.OpenRead(path))
				return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", string.Empty);
		}

		string FindDlss5Manifest(string targetDirectory)
		{
			string backupRoot = Path.Combine(targetDirectory, ".reshade-dlss5-backup");
			if (!Directory.Exists(backupRoot)) return null;
			return Directory.GetFiles(backupRoot, "manifest.txt", SearchOption.AllDirectories)
				.OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
		}

		void RemoveDlss5()
		{
			if (currentInfo.targetPath == null || !File.Exists(currentInfo.targetPath))
			{
				UpdateStatusAndFinish(false, "No target application was provided for DLSS 5 removal.");
				return;
			}
			string targetDirectory = Path.GetDirectoryName(currentInfo.targetPath);
			string manifestPath = FindDlss5Manifest(targetDirectory);
			if (manifestPath == null)
			{
				UpdateStatusAndFinish(false, "No DLSS 5 installation was found for this game.");
				return;
			}
			string backupDirectory = Path.GetDirectoryName(manifestPath);
			var skipped = new List<string>();
			foreach (string line in File.ReadAllLines(manifestPath).Skip(1))
			{
				int separator = line.IndexOf('|');
				if (separator <= 0) continue;
				string expectedHash = line.Substring(0, separator);
				string relative = line.Substring(separator + 1);
				string destination = Path.GetFullPath(Path.Combine(targetDirectory, relative));
				if (!destination.StartsWith(Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
				if (File.Exists(destination) && !HashFile(destination).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
				{
					skipped.Add(relative);
					continue;
				}
				string original = Path.Combine(backupDirectory, relative);
				if (File.Exists(original))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(destination));
					File.Copy(original, destination, true);
				}
				else if (File.Exists(destination)) File.Delete(destination);
			}
			if (skipped.Count == 0)
			{
				try
				{
					Directory.Delete(backupDirectory, true);
					string backupRoot = Path.Combine(targetDirectory, ".reshade-dlss5-backup");
					if (Directory.Exists(backupRoot) && !Directory.EnumerateFileSystemEntries(backupRoot).Any()) Directory.Delete(backupRoot);
				}
				catch { }
			}
			string message = skipped.Count == 0
				? "DLSS 5 was removed and the original files were restored. ReShade was left untouched."
				: "DLSS 5 was removed where files were unchanged. Modified files were preserved:\n" + string.Join("\n", skipped);
			UpdateStatusAndFinish(true, message);
		}

		enum DlssGpuMode { Rtx50, Rtx2040 }

		static (bool rtx50, bool rtx2040) DetectDlssGpuFamilies()
		{
			bool rtx50 = false, rtx2040 = false;
			try
			{
				using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
				foreach (ManagementObject item in searcher.Get())
				{
					string name = item["Name"] as string ?? string.Empty;
					if (name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if (name.IndexOf("RTX 50", StringComparison.OrdinalIgnoreCase) >= 0) rtx50 = true;
					if (name.IndexOf("RTX 20", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("RTX 30", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("RTX 40", StringComparison.OrdinalIgnoreCase) >= 0) rtx2040 = true;
				}
			}
			catch { }
			return (rtx50, rtx2040);
		}

		static string GetDetectedGpuDescription()
		{
			var names = new List<string>();
			try
			{
				using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
				foreach (ManagementObject item in searcher.Get())
				{
					string name = item["Name"] as string ?? string.Empty;
					if (name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
				}
			}
			catch { }
			return names.Count == 0 ? "NVIDIA RTX не обнаружена" : string.Join(", ", names);
		}

		DlssGpuMode? SelectDlssGpuMode()
		{
			var families = DetectDlssGpuFamilies();
			if (!families.rtx50 && !families.rtx2040)
			{
				MessageBox.Show(this, "NVIDIA RTX 20/30/40 или RTX 50 не обнаружена. Установка DLSS 5 остановлена.", "DLSS 5", MessageBoxButton.OK, MessageBoxImage.Warning);
				return null;
			}
			var dialog = new Window { Title = "Выбор профиля DLSS 5", Width = 430, Height = 215, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)), Foreground = Brushes.White };
			var panel = new StackPanel { Margin = new Thickness(18) };
			panel.Children.Add(new TextBlock { Text = "Выберите профиль под вашу видеокарту:", Margin = new Thickness(0, 0, 0, 4), FontSize = 15, Foreground = Brushes.White });
			panel.Children.Add(new TextBlock { Text = "Обнаружено: " + GetDetectedGpuDescription(), Margin = new Thickness(0, 0, 0, 12), FontSize = 12, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
			DlssGpuMode? selected = null;
			var button50 = new Button { Content = new TextBlock { Text = "RTX 50", Foreground = families.rtx50 ? Brushes.Black : Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center }, Height = 34, Margin = new Thickness(0, 0, 0, 8), Background = SystemColors.ControlBrush };
			var button2040 = new Button { Content = new TextBlock { Text = "RTX 20 / 30 / 40", Foreground = families.rtx2040 ? Brushes.Black : Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center }, Height = 34, Background = SystemColors.ControlBrush };
			button50.Click += (sender, args) => { if (families.rtx50) { selected = DlssGpuMode.Rtx50; dialog.DialogResult = true; } };
			button2040.Click += (sender, args) => { if (families.rtx2040) { selected = DlssGpuMode.Rtx2040; dialog.DialogResult = true; } };
			panel.Children.Add(button50); panel.Children.Add(button2040); dialog.Content = panel;
			dialog.ShowDialog();
			return selected;
		}

		string DownloadDlss5Asset(string directory, string fileName, string expectedHash)
		{
			string path = Path.Combine(directory, fileName);
			string url = "https://github.com/n7nsk/dlss5/releases/download/v0.1.0/" + Uri.EscapeDataString(fileName);
			using (var client = new WebClient())
			{
				client.Headers[HttpRequestHeader.UserAgent] = "ReShade-DLSS5-Installer";
				client.DownloadProgressChanged += (sender, args) => Dispatcher.Invoke(() => status.UpdateDownloadProgress(fileName, args.ProgressPercentage));
				client.DownloadFileTaskAsync(new Uri(url), path).GetAwaiter().GetResult();
			}
			using (var sha = SHA256.Create())
			using (var input = File.OpenRead(path))
			{
				string actualHash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", string.Empty);
				if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 verification failed for " + fileName + ".");
			}
			return path;
		}

		void OnWindowInit(object sender, EventArgs e)
		{
			DWM.HideIcon(this);
			DWM.HideSystemMenu(this, currentInfo.targetPath != null);
		}

		void OnNextButtonClick(object sender, RoutedEventArgs e)
		{
			if (CurrentPage.Content is SelectAppPage appPage)
			{
				appPage.Cancel();

				currentInfo.targetPath = appPage.FileName;

				if (!isElevated && !IsWritable(Path.GetDirectoryName(currentInfo.targetPath)))
				{
					RestartWithElevatedPrivileges();
				}
				else
				{
					RunTaskWithExceptionHandling(InstallStep_AnalyzeExecutable);
				}
				return;
			}

			if (CurrentPage.Content is SelectApiPage apiPage)
			{
				currentInfo.targetApi = apiPage.SelectedApi;
				currentInfo.targetOpenXR = apiPage.SelectedOpenXR;

				RunTaskWithExceptionHandling(InstallStep_CheckExistingInstallation);
				return;
			}

			if (CurrentPage.Content is SelectOperationPage operationPage)
			{
				currentOperation = operationPage.SelectedOperation;

				switch (currentOperation)
				{
					case InstallOperation.Update:
					case InstallOperation.UpdateWithEffects:
						RunTaskWithExceptionHandling(InstallStep_InstallReShadeModule);
						break;
					case InstallOperation.Uninstall:
						RunTaskWithExceptionHandling(InstallStep_UninstallReShadeModule);
						break;
				}
				return;
			}

			if (CurrentPage.Content is SelectEffectsPage effectsPage)
			{
				currentInfo.presetPath = effectsPage.PresetPath;

				var packages = new List<EffectPackage>(effectsPage.SelectedItems);

				RunTaskWithExceptionHandling(() =>
				{
					InstallStep_CheckPreset();

					foreach (EffectPackage package in packages)
					{
						InstallStep_DownloadEffectPackage(package);

						if (currentOperation == InstallOperation.Finished)
						{
							return;
						}
					}

					InstallStep_CheckAddons();
				});
				return;
			}

			if (CurrentPage.Content is SelectAddonsPage addonsPage)
			{
				var addons = new List<Addon>(addonsPage.SelectedItems);

				RunTaskWithExceptionHandling(() =>
				{
					foreach (Addon addon in addons)
					{
						InstallStep_DownloadAddon(addon);

						if (currentOperation == InstallOperation.Finished)
						{
							return;
						}
					}

					InstallStep_Finish();
				});
				return;
			}
		}
		void OnBackButtonClick(object sender, RoutedEventArgs e)
		{
			if (currentOperation == InstallOperation.Finished)
			{
				ResetStatus();
			}
		}
		void OnFinishButtonClick(object sender, RoutedEventArgs e)
		{
			Close();
		}
		void OnDlss5ButtonClick(object sender, RoutedEventArgs e)
		{
			if (dlss5OperationInProgress) return;
			var mode = SelectDlssGpuMode();
			if (mode == null) return;
			dlss5OperationInProgress = true;
			Dlss5Button.Opacity = 1.0;
			Dlss5Label.Foreground = Brushes.Gray;
			RunTaskWithExceptionHandling(() => InstallDlss5(mode.Value));
		}
		void OnRemoveDlss5ButtonClick(object sender, RoutedEventArgs e)
		{
			if (MessageBox.Show(this, "Remove DLSS 5 files and restore the original files? ReShade itself will not be removed.", "Remove DLSS 5", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
			RemoveDlss5Button.IsEnabled = false;
			RunTaskWithExceptionHandling(RemoveDlss5);
		}
		void OnDlss5EnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (!dlss5OperationInProgress && !Dlss5Button.IsEnabled) Dlss5Button.IsEnabled = true;
		}

		void OnSkipButtonClick(object sender, RoutedEventArgs e)
		{
			if (CurrentPage.Content is SelectEffectsPage)
			{
				InstallStep_CheckAddons();
			}
			else
			{
				InstallStep_Finish();
			}
		}
		void OnCancelButtonClick(object sender, RoutedEventArgs e)
		{
			if (CurrentPage.Content is SelectAppPage appPage)
			{
				appPage.Cancel();
			}

			Close();
		}

		void OnCurrentPageNavigated(object sender, NavigationEventArgs e)
		{
			if (isRussian) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ApplyRussianLocalization(e.Content as DependencyObject)));
			bool isFinished = currentOperation == InstallOperation.Finished;
			bool isSkippable = e.Content is SelectAddonsPage || e.Content is SelectEffectsPage;
			bool hasDlss5 = currentInfo.targetPath != null && FindDlss5Manifest(Path.GetDirectoryName(currentInfo.targetPath)) != null;

			NextButton.Visibility = isFinished ? Visibility.Collapsed : Visibility.Visible;
			FinishButton.Visibility = isFinished ? Visibility.Visible : Visibility.Collapsed;
			Dlss5Button.Visibility = isFinished && currentOperation != InstallOperation.Uninstall && !isHeadless && !hasDlss5 ? Visibility.Visible : Visibility.Collapsed;
			if (isFinished && currentOperation != InstallOperation.Uninstall) Dlss5Button.IsEnabled = true;
			if (isFinished) NavigationPanel.IsEnabled = true;
			RemoveDlss5Button.Visibility = isFinished && currentOperation != InstallOperation.Uninstall && !isHeadless && hasDlss5 ? Visibility.Visible : Visibility.Collapsed;
			if (hasDlss5) RemoveDlss5Button.IsEnabled = true;

			SkipButton.Visibility = isSkippable ? Visibility.Visible : Visibility.Collapsed;
			CancelButton.Visibility = isSkippable ? Visibility.Collapsed : Visibility.Visible;

			BackButton.IsEnabled = isFinished;
			CancelButton.IsEnabled = !(e.Content is StatusPage);

			if (!(e.Content is SelectAppPage))
			{
				NextButton.IsEnabled = isFinished || !(e.Content is StatusPage);
			}
		}

		void ApplyRussianLocalization(DependencyObject root)
		{
			if (root == null) return;
			if (root is TextBlock textBlock && RussianUi.TryGetValue(textBlock.Text, out string text)) textBlock.Text = text;
			if (root is Label label && label.Content is string labelText && RussianUi.TryGetValue(labelText, out string translatedLabel)) label.Content = translatedLabel;
			if (root is Button button && button.Content is string buttonText && RussianUi.TryGetValue(buttonText, out string translatedButton)) button.Content = translatedButton;
			if (root is TextBox textBox && RussianUi.TryGetValue(textBox.Text, out string translatedTextBox)) textBox.Text = translatedTextBox;
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) ApplyRussianLocalization(VisualTreeHelper.GetChild(root, i));
			foreach (object child in LogicalTreeHelper.GetChildren(root))
				if (child is DependencyObject dependencyChild && !ReferenceEquals(child, root)) ApplyRussianLocalization(dependencyChild);
		}
	}
}
