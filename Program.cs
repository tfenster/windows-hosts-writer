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
        //Settings
        private const string ENV_ENDPOINT = "endpoint";
        private const string ENV_NETWORK = "network";
        private const string ENV_HOSTPATH = "hosts_path";

        private static string LISTEN_NETWORK = "nat";
        private static DockerClient _client;
        private static bool _silent;

        private static Dictionary<string, string> _hostsEntries = new Dictionary<string, string>();
        private static bool _isDirty = true;
        private static Timer _timer;
        private static readonly object _lockobject = new object();

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
            if (Environment.GetEnvironmentVariable("silent") != null)
            {
                _silent = true;
            }

            if (Environment.GetEnvironmentVariable(ENV_NETWORK) != null)
            {
                LISTEN_NETWORK = Environment.GetEnvironmentVariable(ENV_NETWORK);
            }

            try
            {
                _client = GetClient();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Something went wrong. Likely the Docker engine is not listening at [{_client.Configuration.EndpointBaseUri}] inside of the container.");
                Console.WriteLine("You can change that path through environment variable " + ENV_ENDPOINT);

                Console.WriteLine("Exception is " + ex.Message);
                Console.WriteLine(ex.StackTrace);

                if (ex.InnerException != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("InnerException is " + ex.InnerException.Message);
                    Console.WriteLine(ex.InnerException.StackTrace);
                }

            }

            Console.WriteLine($"Starting Windows hosts writer on network [{LISTEN_NETWORK}]");

            //Give us some closure
            //AppDomain.CurrentDomain.ProcessExit += (eSender, eArgs) => { Exit(); };
            // AssemblyLoadContext.Default.Unloading += (eContext) => { Exit(); };

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

                    WriteHosts(null);

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


        public static bool ShouldProcessContainer(string containerId)
        {
            try
            {
                var response = _client.Containers.InspectContainerAsync(containerId).Result;

                var networks = response.NetworkSettings.Networks;

                return networks.TryGetValue(LISTEN_NETWORK, out var network);

            }
            catch (Exception e)
            {
                Console.WriteLine($"Error Checking ShouldProcess: {e.Message}");
                return false;
            }
        }

        public static void AddHost(string containerId)
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


        private static string GetHostsValue(string containerId)
        {
            var containerDetails = _client.Containers.InspectContainerAsync(containerId).Result;

            var network = containerDetails.NetworkSettings.Networks[LISTEN_NETWORK];

            var hostNames = new List<string> { containerDetails.Config.Hostname };

            hostNames.AddRange(network.Aliases);

            var allHosts = string.Join(" ", hostNames.Distinct());

            return $"{network.IPAddress}\t{allHosts}\t\t#{containerId} by whw";
        }

        private static void WriteHosts(object state)
        {
            //Keep some sanity and don't jack with things while in flux.
            lock (_lockobject)
            {
                //Do what we need, only when we need to.
                if (!_isDirty)
                    return;

                _isDirty = false;

                var hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH) ?? "c:\\driversetc\\hosts";

                if (!File.Exists(hostsPath))
                {
                    Console.WriteLine($"Could not find hosts file at: {hostsPath}");

                }

                var hostsLines = File.ReadAllLines(hostsPath).ToList();

                var newHostLines = new List<string>();

                //Purge the old ones out
                hostsLines.ForEach(l =>
                {
                    if (!l.EndsWith($"whw"))
                    {
                        newHostLines.Add(l);
                    }
                });

                //Add the new ones in
                foreach (var entry in _hostsEntries)
                {
                    newHostLines.Add(entry.Value);
                }

                File.WriteAllLines(hostsPath, newHostLines);
            }
        }


        public static void Exit()
        {
            Console.WriteLine("Graceful exit");

            if (_timer != null)
            {
                _timer.Dispose();

                _hostsEntries = new Dictionary<string, string>();
                _isDirty = true;
                WriteHosts(null);
                _timer = null;
            }
        }


        private static object LO = new object();

        private static DockerClient GetClient()
        {
            var endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? "npipe://./pipe/docker_engine";

            return new DockerClientConfiguration(new Uri(endpoint)).CreateClient();
        }
    }
}
