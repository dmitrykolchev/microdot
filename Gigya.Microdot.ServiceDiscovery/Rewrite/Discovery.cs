﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.ServiceDiscovery.Config;
using Gigya.Microdot.SharedLogic.Exceptions;
using Gigya.Microdot.SharedLogic.Rewrite;

namespace Gigya.Microdot.ServiceDiscovery.Rewrite
{

    /// <inheritdoc />
    internal sealed class Discovery : IDiscovery
    {

        private Func<DeploymentIdentifier, ReachabilityCheck, TrafficRouting, ILoadBalancer> CreateLoadBalancer { get; }
        private IDateTime DateTime { get; }
        private Func<DiscoveryConfig> GetConfig { get; }
        private Func<DeploymentIdentifier, LocalNodeSource> CreateLocalNodeSource { get; }        
        private Func<DeploymentIdentifier, ConfigNodeSource> CreateConfigNodeSource { get; }
        private Dictionary<string, INodeSourceFactory> NodeSourceFactories { get; }

        class NodeSourceAndAccesstime
        {
            public string NodeSourceType;
            public Task<INodeSource> NodeSourceTask;
            public DateTime LastAccessTime;
        }

        private readonly ConcurrentDictionary<DeploymentIdentifier, Lazy<NodeSourceAndAccesstime>> _nodeSources
            = new ConcurrentDictionary<DeploymentIdentifier, Lazy<NodeSourceAndAccesstime>>();



        /// <inheritdoc />
        public Discovery(Func<DiscoveryConfig> getConfig, 
            Func<DeploymentIdentifier, ReachabilityCheck, TrafficRouting, ILoadBalancer> createLoadBalancer, 
            IDateTime dateTime,
            INodeSourceFactory[] nodeSourceFactories, 
            Func<DeploymentIdentifier, LocalNodeSource> createLocalNodeSource, 
            Func<DeploymentIdentifier, ConfigNodeSource> createConfigNodeSource)
        {
            GetConfig = getConfig;
            CreateLoadBalancer = createLoadBalancer;
            DateTime = dateTime;
            CreateLocalNodeSource = createLocalNodeSource;
            CreateConfigNodeSource = createConfigNodeSource;
            NodeSourceFactories = nodeSourceFactories.ToDictionary(factory => factory.Type);
            Task.Run(() => CleanupLoop()); // Use default task scheduler
        }



        /// <inheritdoc />
        public async Task<ILoadBalancer> TryCreateLoadBalancer(DeploymentIdentifier deploymentIdentifier, ReachabilityCheck reachabilityCheck, TrafficRouting trafficRouting)
        {
            var nodes = await GetNodes(deploymentIdentifier);
            if (nodes != null)
                return CreateLoadBalancer(deploymentIdentifier, reachabilityCheck, trafficRouting);
            else
                return null;
        }



        /// <inheritdoc />
        public async Task<Node[]> GetNodes(DeploymentIdentifier deploymentIdentifier)
        {
            // We have a cached node source; query it
            if (_nodeSources.TryGetValue(deploymentIdentifier, out Lazy<NodeSourceAndAccesstime> lazySource))
            {
                lazySource.Value.LastAccessTime = DateTime.UtcNow;
                var nodeSource = await lazySource.Value.NodeSourceTask.ConfigureAwait(false);
                return nodeSource.GetNodes();
            }

            // No node source but the service is deployed; create one and query it
            else if (NodeSourceMayBeCreated(deploymentIdentifier))
            {
                string sourceType = GetConfiguredSourceType(deploymentIdentifier);
                lazySource = _nodeSources.GetOrAdd(deploymentIdentifier, _ => new Lazy<NodeSourceAndAccesstime>(() =>
                    new NodeSourceAndAccesstime {
                        NodeSourceType = sourceType,
                        LastAccessTime = DateTime.UtcNow,
                        NodeSourceTask = CreateNodeSource(sourceType, deploymentIdentifier)
                    }));
                var nodeSource = await lazySource.Value.NodeSourceTask.ConfigureAwait(false);
                return nodeSource.GetNodes();
            }

            // No node source and the service is not deployed; return empty list of nodes
            else return null;
        }



        private bool NodeSourceMayBeCreated(DeploymentIdentifier deploymentIdentifier)
        {
            var sourceType = GetConfiguredSourceType(deploymentIdentifier);
            switch (sourceType)
            {
                case "Config":
                case "Local":
                    return true;
                default:
                    if (NodeSourceFactories.TryGetValue(sourceType, out var factory))
                        return factory.IsServiceDeployed(deploymentIdentifier);
                    else throw new ConfigurationException($"Discovery Source '{sourceType}' is not supported.");                    
            }
        }



        /// <inheritdoc />
        private async Task<INodeSource> CreateNodeSource(string sourceType, DeploymentIdentifier deploymentIdentifier)
        {
            INodeSource nodeSource;
            switch (sourceType)
            {
                case "Config":
                    nodeSource = CreateConfigNodeSource(deploymentIdentifier); break;
                case "Local":
                    nodeSource = CreateLocalNodeSource(deploymentIdentifier); break;
                default:
                    if (NodeSourceFactories.TryGetValue(sourceType, out var factory))
                        nodeSource = await factory.CreateNodeSource(deploymentIdentifier).ConfigureAwait(false);
                    else throw new ConfigurationException($"Discovery Source '{sourceType}' is not supported.");
                    break;
            }

            return nodeSource;        
        }



        private string GetConfiguredSourceType(DeploymentIdentifier deploymentIdentifier)
        {
            var serviceConfig = GetConfig().Services[deploymentIdentifier.ServiceName];
            return serviceConfig.Source;
        }



        // Continuously scans the list of node sources and removes ones that haven't been used in a while or whose type
        // differs from configuration.
        private async void CleanupLoop()
        {
            while (!_shutdownTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var expiryTime = DateTime.UtcNow - GetConfig().MonitoringLifetime;

                    foreach (var nodeSource in _nodeSources)
                        if (   nodeSource.Value.Value.LastAccessTime < expiryTime
                            || nodeSource.Value.Value.NodeSourceType != GetConfiguredSourceType(nodeSource.Key))
                        {
    #pragma warning disable 4014
                            nodeSource.Value.Value.NodeSourceTask.ContinueWith(t => t.Result.Dispose());
    #pragma warning restore 4014
                            _nodeSources.TryRemove(nodeSource.Key, out _);
                        }

                    await DateTime.Delay(TimeSpan.FromSeconds(1), _shutdownTokenSource.Token);
                }
                catch {} // Shouldn't happen, but just in case. Cleanup musn't stop.
            }
        }



        private readonly CancellationTokenSource _shutdownTokenSource = new CancellationTokenSource();

        public void Dispose()
        {
            _shutdownTokenSource.Cancel();
            _shutdownTokenSource.Dispose();
        }
    }
}
