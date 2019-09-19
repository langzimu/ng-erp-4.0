﻿using Akka.Actor;
using AkkaSim;
using Master40.DB.Data.WrappersForPrimitives;
using Master40.DB.Enums;
using Master40.SimulationCore.Helper;
using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Master40.DB.DataModel;
using Zpp.Common.DemandDomain.Wrappers;
using Zpp.Common.ProviderDomain.Wrappers;
using Zpp.DbCache;
using Zpp.Mrp.MachineManagement;
using Zpp.OrderGraph;
using Zpp.Simulation.Agents.JobDistributor.Skills;
using Zpp.Simulation.Agents.JobDistributor.Types;

namespace Zpp.Simulation.Agents.JobDistributor
{
    partial class JobDistributor : SimulationElement
    {
        private readonly IDbTransactionData _dbTransactionData;
        
        private ResourceManager ResourceManager { get; } = new ResourceManager();

        private IProductionOrderToOperationGraph<INode> ProductionOrderToOperationGraph { get; set; }

        public static Props Props(IActorRef simulationContext, long time, IDbTransactionData dbTransactionData)
        {
            return Akka.Actor.Props.Create(() => new JobDistributor(simulationContext, time, dbTransactionData));
        }
        public JobDistributor(IActorRef simulationContext, long time, IDbTransactionData dbTransactionData)
            : base(simulationContext, time)
        {
            _dbTransactionData = dbTransactionData;
        }

        protected override void Do(object o)
        {
            switch (o)
            {
                case AddResources m: CreateMachines(m.GetMachines, TimePeriod); break;
                case OperationsToDistribute m: InitializeDistribution(m.GetProductionOrderToOperations); break;
                case ProductionOrderFinished m: ProvideMaterial(m.GetOperation); break;
                case WithDrawMaterialsFor m: WithDrawMaterials(m.GetOperation, _dbTransactionData); break;
                default: new Exception("Message type could not be handled by SimulationElement"); break;
            }
        }

        private void InitializeDistribution(ProductionOrderToOperationGraph productionOrderToOperationGraph)
        {
            // ResourceManager.AddOperationQueue(operations);
            // TODO Check is Item is in Stock ? 

            ProductionOrderToOperationGraph = productionOrderToOperationGraph;
            PushWork();
        }

        private void PushWork()
        {
            var operationLeafs = new StackSet<INode>();
            ProductionOrderToOperationGraph.GetLeafOperations(operationLeafs);
            if (operationLeafs.Any() == false)
            {
                Debug.WriteLine("No more leafs in OperationManager");
                return;
            }

            // split into machineGroups 
            var operationGroupedBySkill = operationLeafs.GetAllAs<ProductionOrderOperation>()
                .GroupBy(x => x.GetValue().ResourceId, (resourceId, operations) => new { resourceId, operations });

            // get next item for each resource.
            foreach (var machine in operationGroupedBySkill)
            {
                // TODO:  no idea what performs the best
                // var first = resource.operations.Select(p => (p.GetValue().Start, p)).Min().p.GetValue();
                var first = machine.operations.OrderBy(x => x.GetValue().Start).FirstOrDefault();
                if (first == null) continue;

                // should never happen. but who knows...
                Debug.Assert(machine.resourceId != null, "resource.machineId is null");
                if (ScheduleOperation(productionOrderOperation: first, machineId: machine.resourceId.Value))
                {
                    ProductionOrderToOperationGraph.RemoveNode(first);
                }
            }
        }

        private void WithDrawMaterials(ProductionOrderOperation productionOrderOperation, IDbTransactionData dbTransactionData)
        {
           
            var productionOrderBom = dbTransactionData.GetAggregator()
                .GetAnyProductionOrderBomByProductionOrderOperation(productionOrderOperation);


            var demands =
                dbTransactionData.GetAggregator().GetAllParentDemandsOf(
                    productionOrderBom.GetProductionOrder(dbTransactionData));
            foreach (var demand in demands)
            {
                var stockExchangeProvider = (StockExchangeDemand) demand;
                var stockExchange = (T_StockExchange) stockExchangeProvider.ToIDemand();
                stockExchange.State = State.Finished;
                stockExchange.Time = (int) TimePeriod;
            }
        }

        private bool ScheduleOperation(ProductionOrderOperation productionOrderOperation, int machineId)
        {
            ResourceDetails machine = ResourceManager.GetResourceRefById(new Id(machineId));
            if (machine.IsWorking)
            {
                Debug.WriteLine("Resource is still Working.");
                return false;
            }

            machine.IsWorking = true;
            Resource.Skills.Work msg = Resource.Skills.Work.Create(productionOrderOperation, machine.ResourceRef);

            if (productionOrderOperation.GetValue().Start <= TimePeriod)
            {
                _SimulationContext.Tell(message: msg, sender: Self);
                return true;
            } // else operation starts in the future and has to wait.

            var delay = productionOrderOperation.GetValue().Start - TimePeriod;
            Schedule(delay, msg);
            return true;
        }

        private void CreateMachines(ResourceDictionary machineGroup, long time)
        {
            foreach (var machines in machineGroup)
            {
                foreach (var machine in machines.Value)
                {
                    var machineNumber = ResourceManager.Count + 1;
                    var agentName = $"{machine.GetValue().Name}({machineNumber})".ToActorName();
                    var resourceRef = Context.ActorOf(Resource.Resource.Props(_SimulationContext, time)
                        , agentName);
                    var resource = new ResourceDetails(machine, resourceRef);
                    ResourceManager.AddResource(resource);
                }
            }
        }
        
        public void InsertMaterialsIntoStock(ProductionOrderOperation operation, long time, IDbTransactionData dbTransactionData)
        {
            var productionOrderBom = dbTransactionData.GetAggregator()
                .GetAnyProductionOrderBomByProductionOrderOperation(operation);


            var demands =
                dbTransactionData.GetAggregator().GetAllParentDemandsOf(
                    productionOrderBom.GetProductionOrder(dbTransactionData));
            foreach (var demand in demands)
            {
                var stockExchangeProvider = (StockExchangeDemand) demand;
                var stockExchange = (T_StockExchange) stockExchangeProvider.ToIDemand();
                stockExchange.State = State.Finished;
                stockExchange.Time = (int) time;
            }
        }

        private void ProvideMaterial(ProductionOrderOperation operation)
        {
            // TODO Check for Preconditions (Previous job is finished and Material is Provided.)
            var rawOperation = operation.GetValue();
            rawOperation.ProducingState = ProducingState.Finished;
            var resourceId = rawOperation.ResourceId;
            if (resourceId == null)
                throw new Exception("Resource not found.");

            InsertMaterialsIntoStock(operation, TimePeriod, _dbTransactionData);

            var resource = ResourceManager.GetResourceRefById(new Id(resourceId.Value));
            resource.IsWorking = false;

            PushWork();
        }

        protected override void Finish()
        {
            Debug.WriteLine(Sender.Path + " has been Killed");
        }
    }
}
