using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Docker.DotNet;
using Docker.DotNet.Models;
using Timer = System.Threading.Timer;

namespace windows_hosts_writer
{
    class Program
    {
        //Settings keys
        private const string ENV_ENDPOINT = "endpoint";
        private const string ENV_NETWORK = "network";
        private const string ENV_HOSTPATH = "hosts_path";
        private const string ENV_SESSION = "session_id";
        private const string CONST_ANYNET = "ANY_NETWORK";
        //Client used to read the container details
        private static DockerClient _client;

        //Listening to a specific network
        private static string _listenNetwork = CONST_ANYNET;

        //Location of the hosts file IN the container.  Mapped through a volume share to your hosts file
        private static string _hostsPath = "c:\\driversetc\\hosts";

        //All host file entries we're tracking
        private static Dictionary<string, string> _hostsEntries = new Dictionary<string, string>();

        //Flag to track whether or not to actually update the hosts file
        private static bool _isDirty = true;

        //Uniquely identify records created by this. Allows for simultaneous execution
        private static string _sessionId = "";

        //Our time used for sync
        private static Timer _timer;



        //  Due to how windows container handle the terminate events in windows, there's
        //  not a clear-cut way to handle graceful exists.  Having to resort to this stuff
        //  isn't ideal, but the standard approaches of AppDomain.CurrentDomain.ProcessExit
        //  and AssemblyLoadContext.Default.Unloading don't handle the events correctly
        [DllImport("Kernel32")]
        internal static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool Add);

        internal delegate bool HandlerRoutine(CtrlTypes ctrlType);

        internal enum CtrlTypes
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT,
            CTRL_CLOSE_EVENT,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT
        }

        static void Main(string[] args)
        {

            if (Environment.GetEnvironmentVariable(ENV_HOSTPATH) != null)
            {
                _hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH);
                Console.Write($"Overriding hosts path '{_hostsPath}'");
            }

            if (Environment.GetEnvironmentVariable(ENV_NETWORK) != null)
            {
                _listenNetwork = Environment.GetEnvironmentVariable(ENV_NETWORK);
                Console.Write($"Overriding listen network '{_listenNetwork}'");
            }
            else
            {
                Console.WriteLine("Listening to any network");
            }

            if (Environment.GetEnvironmentVariable(ENV_SESSION) != null)
            {
                _sessionId = Environment.GetEnvironmentVariable(ENV_SESSION);
                Console.Write($"Overriding Session Key  '{_sessionId}'");
            }

            try
            {
                _client = GetClient();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Something went wrong. Likely the Docker engine is not listening at [{_client.Configuration.EndpointBaseUri}] inside of the container.");
                Console.WriteLine($"You can change that path through environment variable '{ENV_ENDPOINT}'");

                Console.WriteLine("Exception is " + ex.Message);
                Console.WriteLine(ex.StackTrace);

                if (ex.InnerException != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("InnerException is " + ex.InnerException.Message);
                    Console.WriteLine(ex.InnerException.StackTrace);
                }

                //Exit Gracefully
                return;
            }

            Console.WriteLine($"Starting Windows Hosts Writer");

            //We want to only find running containers
            var containerListParams = new ContainersListParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                {
                    {
                        "status", new Dictionary<string, bool>()
                        {
                            {"running", true}
                        }
                    }
                }
            };

            try
            {
                _timer = new Timer((s) =>
                {
                    _hostsEntries = new Dictionary<string, string>();

                    //Handle already running containers on the network
                    var containers = _client.Containers.ListContainersAsync(containerListParams).Result;

                    foreach (var container in containers)
                    {
                        if (ShouldProcessContainer(container.ID))
                            AddHost(container.ID);
                    }

                    WriteHosts();

                }, null, 1000, 5000);

                var shutdown = new ManualResetEvent(false);
                var complete = new ManualResetEventSlim();
                var hr = new HandlerRoutine(type =>
                {
                    Console.WriteLine($"ConsoleCtrlHandler got signal: {type}");

                    shutdown.Set();
                    complete.Wait();

                    return false;
                });

                SetConsoleCtrlHandler(hr, true);

                //Hold here until we get that shutdown event
                shutdown.WaitOne();

                Console.WriteLine("Stopping server...");

                Exit();

                complete.Set();

                GC.KeepAlive(hr);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unhandled Exception: {ex.Message}");
                Console.WriteLine(ex.StackTrace);

                if (ex.InnerException != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("InnerException is " + ex.InnerException.Message);
                    Console.WriteLine(ex.InnerException.StackTrace);
                }
            }
        }

        //Checks whether the container needs to be added to the list or not.  Has to be on the right network
        public static bool ShouldProcessContainer(string containerId)
        {
            try
            {
                if (_listenNetwork == CONST_ANYNET)
                    return true;

                var response = _client.Containers.InspectContainerAsync(containerId).Result;

                var networks = response.NetworkSettings.Networks;

                return networks.TryGetValue(_listenNetwork, out _);

            }
            catch (Exception e)
            {
                Console.WriteLine($"Error Checking ShouldProcess: {e.Message}");
                return false;
            }
        }

        private static readonly object _hostLock = new object();

        /// <summary>
        /// Adds the hosts entry to the list for writing later
        /// </summary>
        /// <param name="containerId">The ID of the container to add</param>
        public static void AddHost(string containerId)
        {
            lock (_hostLock)
            {
                if (!_hostsEntries.ContainsKey(containerId))
                {
                    _hostsEntries.Add(containerId, GetHostsValue(containerId));
                    _isDirty = true;
                    return;
                }

                var hostsValue = GetHostsValue(containerId);

                if (_hostsEntries[containerId] != hostsValue)
                {
                    _hostsEntries[containerId] = hostsValue;
                    _isDirty = true;
                }
            }
        }


        /// <summary>
        ///  Calculates the appropriate hosts entry using all the aliases and 
        /// </summary>
        /// <param name="containerId">The ID of the container to calculate</param>
        /// <returns></returns>
        private static string GetHostsValue(string containerId)
        {
            var containerDetails = _client.Containers.InspectContainerAsync(containerId).Result;

            var hostNames = new List<string> { containerDetails.Config.Hostname };

            EndpointSettings network = null;

            if (_listenNetwork == CONST_ANYNET)
            {
                foreach(var key in containerDetails.NetworkSettings.Networks.Keys)
                {
                    network = containerDetails.NetworkSettings.Networks[key];

                    if (network.Aliases != null)
                        hostNames.AddRange(network.Aliases);
                }
            }
            else
            {
                network = containerDetails.NetworkSettings.Networks[_listenNetwork];

                if (network.Aliases != null)
                    hostNames.AddRange(network.Aliases);

            }

            var allHosts = string.Join(" ", hostNames.Distinct());

            return $"{network.IPAddress}\t{allHosts}\t\t#{containerId} by {GetTail()}";

        }

        private static readonly object _lockobject = new object();

        /// <summary>
        /// Actually write the hosts file, only when dirty.
        /// </summary>
        private static void WriteHosts()
        {
            //Keep some sanity and don't jack with things while in flux.
            lock (_lockobject)
            {
                //Do what we need, only when we need to.
                if (!_isDirty)
                    return;

                //Keep from repeating
                _isDirty = false;


                if (!File.Exists(_hostsPath))
                {
                    Console.WriteLine($"Could not find hosts file at: {_hostsPath}");
                    return;
                }

                var hostsLines = File.ReadAllLines(_hostsPath).ToList();

                var newHostLines = new List<string>();

                //Purge the old ones out
                hostsLines.ForEach(l =>
                {
                    if (!l.EndsWith(GetTail()))
                    {
                        newHostLines.Add(l);
                    }
                });

                //Add the new ones in
                foreach (var entry in _hostsEntries)
                {
                    newHostLines.Add(entry.Value);
                }

                File.WriteAllLines(_hostsPath, newHostLines);
            }
        }

        /// <summary>
        /// Returns the unique ID to identify the rows
        /// </summary>
        /// <returns></returns>
        public static string GetTail()
        {
            return !string.IsNullOrEmpty(_sessionId) ? $"whw-{_sessionId}" : "whw";
        }

        /// <summary>
        /// Cleans up the hsots file by nuking the timer and doing one last write with an empty list
        /// </summary>
        public static void Exit()
        {
            Console.WriteLine("Graceful exit");

            _timer.Dispose();

            _hostsEntries = new Dictionary<string, string>();
            _isDirty = true;
            WriteHosts();
        }

        private static DockerClient GetClient()
        {
            var endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? "npipe://./pipe/docker_engine";

            return new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
        }
    }
}
