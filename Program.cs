using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace windows_hosts_writer
{
    class Program
    {
        private const string ENV_ENDPOINT = "endpoint";
        private const string ENV_NETWORK = "network";
        private const string ENV_HOSTPATH = "hosts_path";
        private const string ERROR_HOSTPATH = "could not change hosts file at {0} inside of the container. You can change that path through environment variable " + ENV_HOSTPATH;
        private const string EVENT_MSG = "got a {0} event from {1}";
        private static string LISTEN_NETWORK = "nat";
        private static DockerClient _client;
        private static bool _silent = false;

        static void Main(string[] args)
        {

            if (Environment.GetEnvironmentVariable(ENV_NETWORK) != null)
            {
                LISTEN_NETWORK = Environment.GetEnvironmentVariable(ENV_NETWORK);
            }

            if (Environment.GetEnvironmentVariable("silent") != null)
            {
                _silent = true;
            }

            if (!_silent)
                Console.WriteLine($"Starting Windows hosts writer on network [{LISTEN_NETWORK}]");


            var progress = new Progress<Message>(message =>
            {
                if (message.Action == "connect")
                {
                    if (!_silent)
                        Console.WriteLine(EVENT_MSG, "connect", message.Actor.Attributes["container"]);

                    HandleHosts(true, message.Actor.Attributes["type"], message.Actor.Attributes["container"]);
                }
                else if (message.Action == "disconnect")
                {
                    if (!_silent)
                        Console.WriteLine(EVENT_MSG, "disconnect", message.Actor.Attributes["container"]);

                    HandleHosts(false, message.Actor.Attributes["type"], message.Actor.Attributes["container"]);
                }
            });

            var containerEventsParams = new ContainerEventsParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                    {
                        {
                            "event", new Dictionary<string, bool>()
                            {
                                {
                                    "connect", true
                                },
                                {
                                    "disconnect", true
                                }
                            }
                        },
                        {
                            "type", new Dictionary<string, bool>()
                            {
                                {
                                    "network", true
                                }
                            }
                        }
                    }
            };

            var containerListParams = new ContainersListParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                {
                    {
                        "status", new Dictionary<string, bool>()
                        {
                            {"running", true }
                        }
                    }
                }
            };

            try
            {
                //Handle already running containers on the network
                var containers = GetClient().Containers.ListContainersAsync(containerListParams).Result;

                foreach (var container in containers)
                {
                    if (!_silent)
                        Console.WriteLine($"Adding existing container: {container.ID}");

                    HandleHosts(true, "nat", container.ID);
                }

                GetClient().System.MonitorEventsAsync(containerEventsParams, progress, default(CancellationToken)).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Something went wrong. Likely the Docker engine is not listening at " + GetClient().Configuration.EndpointBaseUri.ToString() + " inside of the container.");
                Console.WriteLine("You can change that path through environment variable " + ENV_ENDPOINT);
                if (!_silent)
                {
                    Console.WriteLine("Exception is " + ex.Message);
                    Console.WriteLine(ex.StackTrace);

                    if (ex.InnerException != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("InnerException is " + ex.InnerException.Message);
                        Console.WriteLine(ex.InnerException.StackTrace);
                    }
                }

            }
        }

        private static void HandleHosts(bool add, string networkType, string containerId)
        {
            FileStream hostsFileStream = null;
            try
            {

                while (hostsFileStream == null)
                {
                    int tryCount = 0;
                    var hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH) ?? "c:\\driversetc\\hosts";

                    try
                    {
                        hostsFileStream = File.Open(hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    }
                    catch (FileNotFoundException)
                    {
                        // no access to hosts
                        Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // no access to hosts
                        Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                        return;
                    }
                    catch (IOException ex)
                    {
                        if (tryCount == 5)
                        {
                            Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                            return;  // only try five times and then give up
                        }
                        Thread.Sleep(1000);
                        tryCount++;
                    }
                }
                if (networkType == "nat")
                {
                    try
                    {
                        var response = GetClient().Containers.InspectContainerAsync(containerId).Result;
                        var networks = response.NetworkSettings.Networks;

                        if (networks.TryGetValue(LISTEN_NETWORK, out var network))
                        {
                            var hostsLines = new List<string>();
                            using (StreamReader reader = new StreamReader(hostsFileStream))
                            using (StreamWriter writer = new StreamWriter(hostsFileStream))
                            {
                                while (!reader.EndOfStream)
                                    hostsLines.Add(reader.ReadLine());

                                hostsFileStream.Position = 0;

                                hostsLines.RemoveAll(l => l.EndsWith($"#{containerId} by whw"));

                                var hostNames = new List<string> { response.Config.Hostname };

                                if (response.Config.Labels.ContainsKey("com.docker.compose.service"))
                                {
                                    hostNames.Add(response.Config.Labels["com.docker.compose.service"]);
                                }

                                hostNames.AddRange(network.Aliases);

                                var allHosts = string.Join(" ", hostNames.Distinct());

                                if (add)
                                {
                                    var hostLine = $"{network.IPAddress}\t{allHosts}\t\t#{containerId} by whw";

                                    Console.WriteLine("Adding Hosts Line: " + hostLine);

                                    hostsLines.Add(hostLine);
                                }

                                foreach (var line in hostsLines)
                                    writer.WriteLine(line);

                                hostsFileStream.SetLength(hostsFileStream.Position);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_silent)
                        {
                            Console.WriteLine("Something went wrong. Maybe looking for a container that is already gone? Exception is " + ex.Message);
                            Console.WriteLine(ex.StackTrace);
                            if (ex.InnerException != null)
                            {
                                Console.WriteLine();
                                Console.WriteLine("InnerException is " + ex.InnerException.Message);
                                Console.WriteLine(ex.InnerException.StackTrace);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (hostsFileStream != null)
                    hostsFileStream.Dispose();
            }
        }

        private static DockerClient GetClient()
        {
            if (_client == null)
            {
                var endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? "npipe://./pipe/docker_engine";
                _client = new DockerClientConfiguration(new System.Uri(endpoint)).CreateClient();
            }
            return _client;
        }
    }
}
