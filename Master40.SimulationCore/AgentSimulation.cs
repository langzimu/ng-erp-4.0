﻿using Akka.Actor;
using Akka.Event;
using AkkaSim;
using AkkaSim.Definitions;
using Master40.DB.Data.Context;
using Master40.SimulationCore.Agents;
using Master40.SimulationCore.Agents.CollectorAgent;
using Master40.SimulationCore.Agents.CollectorAgent.Types;
using Master40.SimulationCore.Agents.ContractAgent;
using Master40.SimulationCore.Agents.DirectoryAgent;
using Master40.SimulationCore.Agents.Guardian;
using Master40.SimulationCore.Agents.Guardian.Options;
using Master40.SimulationCore.Agents.HubAgent;
using Master40.SimulationCore.Agents.SupervisorAgent;
using Master40.SimulationCore.DistributionProvider;
using Master40.SimulationCore.Environment;
using Master40.SimulationCore.Environment.Options;
using Master40.SimulationCore.Helper;
using Master40.SimulationCore.Reporting;
using Master40.Tools.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static FCreateSimulationWorks;
using static FResourceSetupDefinitions;
using static FUpdateSimulationWorkProviders;
using static FUpdateSimulationWorks;
using static FUpdateStockValues;
using static Master40.SimulationCore.Agents.CollectorAgent.Collector.Instruction;

namespace Master40.SimulationCore
{
    public class AgentSimulation
    {
        // public ActorSystem ActorSystem { get; }
        // public IActorRef SimulationContext { get; }
        private ProductionDomainContext _dBContext { get; }
        private IMessageHub _messageHub { get; }
        private bool _debug { get; set; }
        private Simulation _simulation { get; set; }
        public SimulationConfig SimulationConfig { get; private set; }
        public ActorPaths ActorPaths { get; private set; }
        public IActorRef WorkCollector { get; private set; }
        public IActorRef StorageCollector { get; private set; }
        public IActorRef ContractCollector { get; private set; }

        /// <summary>
        /// Prepare Simulation Environment
        /// </summary>
        /// <param name="debug">Enables AKKA-Global message Debugging</param>
        public AgentSimulation(ProductionDomainContext DBContext, IMessageHub messageHub)
        {
            _dBContext = DBContext;
            _messageHub = messageHub;
        }
        public Task<Simulation> InitializeSimulation(Configuration configuration)
        {
            return Task.Run(() =>
            {
                _messageHub.SendToAllClients("Initializing Simulation...");
                var randomWorkTime = new WorkTimeGenerator(
                    seed: configuration.GetOption<Seed>().Value
                    ,deviation: configuration.GetOption<WorkTimeDeviation>().Value
                    ,simNumber: configuration.GetOption<SimulationNumber>().Value);

                // #1 Init Simulation
                SimulationConfig = configuration.GetContextConfiguration();
                _debug = configuration.GetOption<DebugAgents>().Value;
                var _simType = configuration.GetOption<SimulationKind>().Value;
                _simulation = new Simulation(SimulationConfig);
                ActorPaths = new ActorPaths(_simulation.SimulationContext, SimulationConfig.Inbox.Receiver);
                // Create DataCollector

                var resourcelist = new ResourceList();
                resourcelist.AddRange(_dBContext.Resources.Select(x => "Machine(" + x.Name.Replace(" ", "") + ")"));

                WorkCollector = _simulation.ActorSystem.ActorOf(Collector.Props(ActorPaths, CollectorAnalyticsWorkSchedule.Get(resourcelist)
                                                        , _messageHub, configuration, 0, _debug
                                                        , new List<Type> { typeof(FCreateSimulationWork),
                                                                           typeof(FUpdateSimulationWork),
                                                                           typeof(FUpdateSimulationWorkProvider),
                                                                           typeof(UpdateLiveFeed),
                                                                           typeof(Hub.Instruction.AddMachineToHub),
                                                                           typeof(BasicInstruction.ResourceBrakeDown)}));
                StorageCollector = _simulation.ActorSystem.ActorOf(Collector.Props(ActorPaths, CollectorAnalyticsStorage.Get()
                                                        , _messageHub, configuration, 0, _debug
                                                        , new List<Type> { typeof(FUpdateStockValue),
                                                                           typeof(UpdateLiveFeed)}));
                ContractCollector = _simulation.ActorSystem.ActorOf(Collector.Props(ActorPaths, CollectorAnalyticsContracts.Get()
                                                        , _messageHub, configuration, 0, _debug
                                                        , new List<Type> { typeof(Contract.Instruction.StartOrder),
                                                                           typeof(Supervisor.Instruction.OrderProvided),
                                                                           typeof(UpdateLiveFeed)}));

                // Create Guardians and Inject Childcreators
                var contractGuard = _simulation.ActorSystem.ActorOf(Guardian.Props(ActorPaths, 0, _debug), "ContractGuard");
                var contractBehaveiour = GuardianBehaviour.Get(CreateFunctionProvider.ContractCreator, _simType);
                _simulation.SimulationContext.Tell(BasicInstruction.Initialize.Create(contractGuard, contractBehaveiour));

                var dispoGuard = _simulation.ActorSystem.ActorOf(Guardian.Props(ActorPaths, 0, _debug), "DispoGuard");
                var dispoBehaviour = GuardianBehaviour.Get(CreateFunctionProvider.DispoCreator, _simType);
                _simulation.SimulationContext.Tell(BasicInstruction.Initialize.Create(dispoGuard, dispoBehaviour));

                var productionGuard = _simulation.ActorSystem.ActorOf(Guardian.Props(ActorPaths, 0, _debug), "ProductionGuard");
                var productionBehaviour = GuardianBehaviour.Get(CreateFunctionProvider.ProductionCreator, _simType);
                _simulation.SimulationContext.Tell(BasicInstruction.Initialize.Create(productionGuard, productionBehaviour));

                ActorPaths.AddGuardian(GuardianType.Contract, contractGuard);
                ActorPaths.AddGuardian(GuardianType.Dispo, dispoGuard );
                ActorPaths.AddGuardian(GuardianType.Production, productionGuard);

                /// // #1.2 Setup DeadLetter Monitor for Debugging
                 var deadletterWatchMonitorProps = Props.Create(() => new DeadLetterMonitor());
                 var deadletterWatchActorRef = _simulation.ActorSystem.ActorOf(deadletterWatchMonitorProps, "DeadLetterMonitoringActor");
                 //subscribe to the event stream for messages of type "DeadLetter"
                 _simulation.ActorSystem.EventStream.Subscribe(deadletterWatchActorRef, typeof(DeadLetter));

                // #1.3 Setup a TimeMonitor to watch wallclock progress
                Action<long> tm = (timePeriod) => _messageHub.SendToClient("clockListener", timePeriod.ToString());
                var timeMonitor = Props.Create(() => new TimeMonitor((timePeriod) => tm(timePeriod)));
                _simulation.ActorSystem.ActorOf(timeMonitor, "TimeMonitor");

                // #2 Create System Agent
                var productIds = _dBContext.GetProductIds();
                ActorPaths.SetSupervisorAgent(_simulation.ActorSystem.ActorOf(Supervisor.Props(ActorPaths, 0, _debug, _dBContext, _messageHub, configuration, productIds, ActorRefs.Nobody), "Supervisor"));
                
                // #3 Create DirectoryAgents
                ActorPaths.SetHubDirectoryAgent(_simulation.ActorSystem.ActorOf(Directory.Props(ActorPaths, 0, _debug), "HubDirectory"));
                _simulation.SimulationContext.Tell(BasicInstruction.Initialize.Create(ActorPaths.HubDirectory.Ref, Agents.DirectoryAgent.Behaviour.Factory.Get(_simType)));

                ActorPaths.SetStorageDirectory(_simulation.ActorSystem.ActorOf(Directory.Props(ActorPaths, 0, _debug), "StorageDirectory"));
                _simulation.SimulationContext.Tell(BasicInstruction.Initialize.Create(ActorPaths.StorageDirectory.Ref, Agents.DirectoryAgent.Behaviour.Factory.Get(_simType)));

                // #4 Create Machines
                var setups = _dBContext.ResourceSetups.Include(m => m.Resource)
                                                                 .Include(r => r.ResourceSkill)
                                                                 .Include(t => t.ResourceTool)
                                                                 .AsNoTracking().ToListAsync().Result;


                var resourceList = _dBContext.Resources.ToList();

                foreach (var resource in resourceList)
                {
                    var resourceSetups = setups.Where(x => x.ResourceId == resource.Id).ToList();

                    var resourceSetupDefinition = new FResourceSetupDefinition(randomWorkTime, resourceSetups, _debug);

                    _simulation.SimulationContext.Tell(Directory.Instruction
                                                                .CreateMachineAgents
                                                                .Create(resourceSetupDefinition, ActorPaths.HubDirectory.Ref)
                                                                , ActorPaths.HubDirectory.Ref);
                }

                // #5 Create Storages
                foreach (var stock in _dBContext.Stocks
                                                .Include(x => x.StockExchanges)
                                                .Include(x => x.Article).ThenInclude(x => x.ArticleToBusinessPartners)
                                                                        .ThenInclude(x => x.BusinessPartner)
                                                .Include(x => x.Article).ThenInclude(x => x.ArticleType)
                                                .AsNoTracking().ToList()) 
                {
                    _simulation.SimulationContext.Tell(Directory.Instruction
                                                                .CreateStorageAgents
                                                                .Create(stock, ActorPaths.StorageDirectory.Ref)
                                                            , ActorPaths.StorageDirectory.Ref);
                }



                return _simulation;
            });
        }

        public static void Continuation(Inbox inbox, Simulation sim, List<IActorRef> collectors)
        {

            var something = inbox.ReceiveAsync(TimeSpan.FromHours(1)).Result;
            switch (something)
            {
                case SimulationMessage.SimulationState.Started:
                    System.Diagnostics.Debug.WriteLine("AKKA:START AGENT SYSTEM", "AKKA-System:");
                    Continuation(inbox, sim, collectors);
                    break;
                case SimulationMessage.SimulationState.Stopped:
                    System.Diagnostics.Debug.WriteLine("AKKA:STOP AGENT SYSTEM", "AKKA-System:");
                    foreach (var item in collectors)
                    {
                        var waitFor = item.Ask(UpdateLiveFeed.Create(false, inbox.Receiver),TimeSpan.FromHours(1)).Result;
                    }
                    sim.Continue();
                    Continuation(inbox, sim, collectors);
                    break;
                case SimulationMessage.SimulationState.Finished:

                    System.Diagnostics.Debug.WriteLine("SHUTDOWN AGENT SYSTEM", "AKKA-System:");
                    foreach (var item in collectors)
                    {
                        var waitFor = item.Ask(UpdateLiveFeed.Create(true, inbox.Receiver), TimeSpan.FromHours(1)).Result;
                    }
                    sim.ActorSystem.Terminate();
                    break;
                default:
                    break;
            }
        }

    }
}
