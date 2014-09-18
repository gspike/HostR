﻿#region References

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HostR.Extensions;

#endregion

namespace HostR.Services
{
	/// <summary>
	/// Represents a windows service.
	/// </summary>
	public abstract class WindowsService : ServiceBase
	{
		#region Fields

		private readonly IWindowsServiceWebService _client;
		private readonly string _description;
		private readonly string _displayName;
		private string _applicationDirectory;
		private string _applicationFilePath;
		private string _applicationName;
		private Task _task;
		private CancellationTokenSource _taskToken;
		private string _version;

		#endregion

		#region Constructors

		/// <summary>
		/// Initializes a new instance of the WindowsSerivce class.
		/// </summary>
		protected WindowsService(string displayName, string description, WindowsServiceArguments arguments, IWindowsServiceWebService client)
		{
			_displayName = displayName;
			_description = description;
			_client = client;
			Arguments = arguments;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Gets a value indicating if the service is being cancelled.
		/// </summary>
		public bool CancellationPending
		{
			get { return _taskToken.IsCancellationRequested; }
		}

		/// <summary>
		/// Gets a value indicating if the service is running.
		/// </summary>
		public bool IsRunning
		{
			get { return _task.Status == TaskStatus.Running; }
		}

		/// <summary>
		/// The arguments the service was started with
		/// </summary>
		protected WindowsServiceArguments Arguments { get; private set; }

		#endregion

		#region Methods

		/// <summary>
		/// Checks the client to see if there is an update available. If so it starts the update process
		/// and returns true. If no update is found then return false.
		/// </summary>
		/// <returns>True if an update has started or false otherwise.</returns>
		public bool CheckForUpdate()
		{
			WriteLine("Check for a sync agent update.");
			var serviceDetails = new WindowsServiceDetails { Name = _applicationName, Version = _version };
			var updateSize = _client.CheckForUpdate(serviceDetails);
			WriteLine("Update check returned " + updateSize + ".");

			if (updateSize > 0)
			{
				WriteLine("Starting to update the sync agent.");
				StartServiceUpdate(updateSize);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Allows public access to the OnStop method.
		/// </summary>
		public void DebugStop()
		{
			OnStop();
		}

		/// <summary>
		/// Allows public access to the OnStart method.
		/// </summary>
		public void Start()
		{
			_applicationFilePath = Assembly.GetCallingAssembly().Location;
			_applicationDirectory = Path.GetDirectoryName(_applicationFilePath);
			_applicationName = Path.GetFileNameWithoutExtension(_applicationFilePath);
			_version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

			if (String.IsNullOrEmpty(Arguments.ServiceName))
			{
				Arguments.ServiceName = _applicationName;
			}

			if (Arguments.ShowHelp)
			{
				Console.WriteLine(BuildHelpInformation());
				return;
			}

			if (Arguments.UpdateService)
			{
				UpdateService();
				return;
			}

			if (Arguments.InstallService)
			{
				InstallService();
				return;
			}

			if (Arguments.UninistallService)
			{
				UninstallService();
				return;
			}

			// Check to see if we need to run in service mode.
			if (!Environment.UserInteractive)
			{
				// Run the agent in service mode.
				Run(this);
				return;
			}

			// Start the process in debug mode.
			OnStart(null);
			HandleConsole();
		}

		/// <summary>
		/// Builds the help information that will be displayed with the -h command option is requested.
		/// </summary>
		/// <returns>The string to be displayed.</returns>
		protected virtual string BuildHelpInformation()
		{
			var builder = new StringBuilder();

			builder.AppendFormat("\r\n{0} v{1}\r\n", _displayName, _version);
			builder.AppendFormat("{0} [-i] [-u] [-h]\r\n", _applicationName);
			builder.AppendLine("[-i] Install the service.");
			builder.AppendLine("[-u] Uninstall the service.");
			builder.AppendLine("[-n] The name for the service. Defaults to FileName.");
			builder.AppendLine("[-w] The URI of the service API.");
			builder.AppendLine("[-l] The username for the service API.");
			builder.AppendLine("[-p] The password for the service API.");
			builder.AppendLine("[-d] Wait for debugger.");
			builder.AppendLine("[-r] The path of the installation to upgrade.");
			builder.Append("[-h] Prints the help menu.");

			return builder.ToString();
		}

		/// <summary>
		/// When implemented in a derived class, executes when a Start command is sent to the service by the Service Control Manager (SCM) or when the operating system 
		/// starts (for a service that starts automatically). Specifies actions to take when the service starts.
		/// </summary>
		/// <param name="args">Data passed by the start command.</param>
		protected override void OnStart(string[] args)
		{
			if (_task != null)
			{
				throw new InvalidOperationException("The service is already running.");
			}

			// Create our debug service.
			_taskToken = new CancellationTokenSource();
			_task = new Task(Process, _taskToken.Token);
			_task.Start();

			base.OnStart(args);
		}

		/// <summary>
		/// When implemented in a derived class, executes when a Stop command is sent to the service by the Service Control Manager (SCM). Specifies actions to take when 
		/// a service stops running.
		/// </summary>
		protected override void OnStop()
		{
			_taskToken.Cancel();
			_task.Wait(new TimeSpan(0, 1, 0));
			_task = null;
			base.OnStop();
		}

		/// <summary>
		/// The thread for the service.
		/// </summary>
		protected abstract void Process();

		protected virtual void WriteLine(string message)
		{
			var handler = OnWriteLine;
			if (handler != null)
			{
				handler(message);
			}
		}

		/// <summary>
		/// Copy the source directory into the destination directory.
		/// </summary>
		/// <param name="source">The directory containing the source files and folders.</param>
		/// <param name="destination">The directory to copy the source into.</param>
		private void CopyDirectory(string source, string destination)
		{
			var destinationInfo = new DirectoryInfo(destination);
			destinationInfo.Empty();

			var sourceInfo = new DirectoryInfo(source);
			foreach (var fileInfo in sourceInfo.GetFiles())
			{
				fileInfo.CopyTo(Path.Combine(destination, fileInfo.Name));
			}

			foreach (var directoryInfo in sourceInfo.GetDirectories())
			{
				CopyDirectory(Path.Combine(source, directoryInfo.Name), Path.Combine(destination, directoryInfo.Name));
			}
		}

		private byte[] DownloadUpdate(long size)
		{
			// Get the file from the deployment service.
			var data = new byte[size];
			var request = new WindowsServiceUpdateRequest { Name = _applicationName, Offset = 0 };

			// Read the whole file.
			while (request.Offset < data.Length)
			{
				var chunk = _client.DownloadUpdateChunk(request);
				Array.Copy(chunk, 0, data, request.Offset, chunk.Length);
				request.Offset += chunk.Length;
			}

			// Return the data read.
			return data;
		}

		/// <summary>
		/// Grab the console and wait for it to close.
		/// </summary>
		private void HandleConsole()
		{
			// Redirects the 'X' for the console window so we can close the service cleanly.
			var stopDebugHandler = new HandlerRoutine(OnStop);
			SetConsoleCtrlHandler(stopDebugHandler, true);

			// Loop here while the service is running.
			while (IsRunning)
			{
				// Minor delay for process management.
				Thread.Sleep(50);

				// Check to see if someone pressed a key.
				if (Console.KeyAvailable)
				{
					// Check to see if the key was a space.
					if (Console.ReadKey(true).Key != ConsoleKey.Spacebar)
					{
						// It was not a space so break the running loop and close the agent.
						break;
					}
				}
			}

			// It was not a space so break the running loop and close the agent.
			OnStop();

			// If we don't have this the handler will get garbage collected and will result in a
			// null reference exception when the console windows is closed with the 'X'.
			GC.KeepAlive(stopDebugHandler);
		}

		/// <summary>
		/// Install the service as a windows service.
		/// </summary>
		private void InstallService()
		{
			WindowsServiceInstaller.InstallService(_applicationFilePath, Arguments.ServiceName, _displayName, _description, ServiceStartMode.Automatic);
			WindowsServiceInstaller.SetServiceArguments(Arguments.ServiceName, Arguments.ServiceArguments);
		}

		private string SaveUpdate(byte[] agentBits)
		{
			// Download the latest updated bits.
			var agentUpdateDirectory = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.Machine) + "\\" + Arguments.ServiceName;
			var agentUpdateFilePath = agentUpdateDirectory + "\\Update.zip";

			WriteLine("Cleaning up the update folder.");
			CreateOrCleanDirectory(agentUpdateDirectory + "\\Update");

			WriteLine("Save the new agent to the update folder.");
			File.WriteAllBytes(agentUpdateFilePath, agentBits);

			// Extract the bits to a temp folder.
			WriteLine("Extract the new agent to the update folder.");
			ExtractZipfile(agentUpdateFilePath);

			return agentUpdateDirectory + "\\Update";
		}

		/// <summary>
		/// Shutdown all the other services.
		/// </summary>
		private void ShutdownServices()
		{
			WriteLine("Shutting down all the running sync agents.");

			try
			{
				var service = new ServiceController(Arguments.ServiceName);
				if (service.Status == ServiceControllerStatus.Running)
				{
					service.Stop();
					service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(1));
				}
			}
			catch (InvalidOperationException)
			{
				WriteLine("Service is not currently installed so we cannot stop the service.");
			}

			// Get a list of all other agents except this one.
			var myProcess = System.Diagnostics.Process.GetCurrentProcess();
			var otherProcesses = System.Diagnostics.Process.GetProcessesByName(Arguments.ServiceName)
				.Where(p => p.Id != myProcess.Id)
				.ToList();

			// Cycle through all the other processes and close them gracefully.
			foreach (var process in otherProcesses)
			{
				// Ask the process to close gracefully and give it 10 seconds to do so.
				process.CloseMainWindow();
				process.WaitForExit(10000);

				// See if the process has gracefully shutdown.
				if (!process.HasExited)
				{
					// OK, no more Mr. Nice Guy time to just kill the process.
					process.Kill();
				}
			}
		}

		/// <summary>
		/// Starts a process without waiting for any feedback.
		/// </summary>
		private void StartProcess(string directory, string fileName, string arguments)
		{
			var filePath = directory + "\\" + fileName;
			WriteLine("StartProcess: " + filePath + " " + arguments);

			var processStart = new ProcessStartInfo(filePath);

			if (!String.IsNullOrWhiteSpace(arguments))
			{
				processStart.Arguments = arguments;
			}

			processStart.WorkingDirectory = directory;
			processStart.RedirectStandardOutput = false;
			processStart.RedirectStandardError = false;
			processStart.UseShellExecute = true;
			processStart.CreateNoWindow = true;

			System.Diagnostics.Process.Start(processStart);
		}

		/// <summary>
		/// Restarts the service after the update.
		/// </summary>
		private void StartServiceAfterUpdate()
		{
			if (Environment.UserInteractive)
			{
				// Starts the deployment agent in runtime mode.
				StartProcess(_applicationDirectory, _applicationName + ".exe", Arguments.ServiceArguments);
			}
			else
			{
				// Starts the deployment agent in service mode.
				using (var service = new ServiceController(Arguments.ServiceName))
				{
					service.Start();
					service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(1));
				}
			}
		}

		private void StartServiceUpdate(long size)
		{
			var agentBits = DownloadUpdate(size);
			var directory = SaveUpdate(agentBits);

			StartProcess(directory, _applicationName + ".exe", " -r \"" + _applicationDirectory + "\"");

			WriteLine("Shutting down agent for updating.");
			OnStop();
		}

		/// <summary>
		/// Uninstalls the service.
		/// </summary>
		private void UninstallService()
		{
			WindowsServiceInstaller.UninstallService(Arguments.ServiceName);
		}

		/// <summary>
		/// Updates the service.
		/// </summary>
		private void UpdateService()
		{
			WriteLine("Starting to update the service to v" + _version);

			// Shutdown all other agents.
			ShutdownServices();

			// Make sure all other agents are shutdown.
			WaitForServiceShutdown();

			// Make sure the current upgrading executable is not running in the target path.
			if (Arguments.DirectoryToUpgrade.Equals(_applicationDirectory, StringComparison.OrdinalIgnoreCase))
			{
				WriteLine("You cannot update from the same directory as the target.");
				return;
			}

			// Copy the application directory to the upgrade directory.
			CopyDirectory(_applicationDirectory, Arguments.DirectoryToUpgrade);

			// Finished updating the server so log the success.
			WriteLine("Finished updating the sync agent to v" + _version);

			// Start the service back up.
			var mode = Environment.UserInteractive ? "runtime" : "service";
			WriteLine(String.Format("Starting the updated sync agent in {0} mode.", mode));
			StartServiceAfterUpdate();
		}

		/// <summary>
		/// Wait for the other services to shutdown.
		/// </summary>
		private void WaitForServiceShutdown()
		{
			// Get all the other agent processes other than this one.
			var myProcess = System.Diagnostics.Process.GetCurrentProcess();
			var otherProcesses = System.Diagnostics.Process.GetProcessesByName(Arguments.ServiceName)
				.Where(p => p.Id != myProcess.Id)
				.ToList();

			// Start a timeout timer so we can give up after 30 seconds.
			var timeout = Stopwatch.StartNew();

			// Keep checking for other processes for 30 seconds. The other agent should have stopped within the timeout.
			while ((otherProcesses.Count > 0) && (timeout.Elapsed.TotalSeconds < 30))
			{
				// Display we are waiting.
				WriteLine("Waiting for the other agents to shutdown.");

				// Delay for a second.
				Thread.Sleep(1000);

				// Refresh the process list.
				otherProcesses = System.Diagnostics.Process.GetProcessesByName(Arguments.ServiceName)
					.Where(p => p.Id != myProcess.Id)
					.ToList();
			}

			// See if we timed out waiting for the other agents to stop.
			if (otherProcesses.Count > 0)
			{
				throw new Exception("The service failed to stop so we cannot update the service.");
			}
		}

		#endregion

		#region Static Methods

		private static void CreateOrCleanDirectory(string path)
		{
			if (Directory.Exists(path))
			{
				// Empty the directory.
				new DirectoryInfo(path).Empty();
			}
			else
			{
				// Create a new temp folder and give it some time to complete.
				Directory.CreateDirectory(path);
			}
		}

		/// <summary>
		/// Extracts the zip file contents into the directory then deletes the file.
		/// </summary>
		/// <param name="filePath">The path of the file.</param>
		/// <param name="outputPath">The path to extract the files to.</param>
		private static void ExtractZipfile(string filePath, string outputPath = "")
		{
			if (String.IsNullOrWhiteSpace(filePath))
			{
				throw new ArgumentException("The filePath is required.", "filePath");
			}

			if (String.IsNullOrWhiteSpace(outputPath))
			{
				outputPath = Path.GetDirectoryName(filePath);
				if (outputPath == null)
				{
					throw new ArgumentException("Failed to calculate the output path.", "filePath");
				}

				outputPath += "\\" + Path.GetFileNameWithoutExtension(filePath);
			}

			// Make sure the file exist.
			if (!File.Exists(filePath))
			{
				// Oh no, we could not find the file. Let the caller know.
				throw new ArgumentException("The file does not exist.", "filePath");
			}

			if (!Directory.Exists(outputPath))
			{
				Directory.CreateDirectory(outputPath);
			}

			// Open the zip file and start the extraction.
			using (var stream = File.OpenRead(filePath))
			{
				var zip = new ZipArchive(stream);
				zip.ExtractToDirectory(outputPath);
			}

			// Delete the zip.
			File.Delete(filePath);
		}

		[DllImport("kernel32.dll")]
		private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

		#endregion

		#region Events

		public event Action<string> OnWriteLine;

		#endregion

		#region Delegates

		private delegate void HandlerRoutine();

		#endregion
	}
}