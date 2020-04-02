﻿using Akka.Actor;
using Master40.DB.Nominal;
using Master40.SimulationCore.Agents.HubAgent.Types;
using Master40.SimulationCore.Agents.ResourceAgent;
using Master40.SimulationCore.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using Master40.DB.DataModel;
using static FOperations;
using static FProposals;
using static FRequestProposalForSetups;
using static FResourceInformations;
using static FUpdateStartConditions;
using static IJobResults;
using static IJobs;
using ResourceManager = Master40.SimulationCore.Agents.HubAgent.Types.CapabilityManager;
using static FAcknowledgeProposals;
using static FSetupDefinitions;

namespace Master40.SimulationCore.Agents.HubAgent.Behaviour
{
    public class DefaultSetup : SimulationCore.Types.Behaviour
    {
        internal DefaultSetup(SimulationType simulationType = SimulationType.DefaultSetup)
                        : base(childMaker: null, simulationType: simulationType) { }


        //internal List<FOperation> _operationList { get; set; } = new List<FOperation>();
        internal CapabilityManager _capabilityManager { get; set; } = new CapabilityManager();
        internal ProposalManager _proposalManager { get; set; } = new ProposalManager();

        public override bool Action(object message)
        {
            switch (message)
            {
                case Hub.Instruction.Default.EnqueueJob msg: EnqueueJob(fOperation: msg.GetObjectFromMessage as FOperation); break;
                case Hub.Instruction.Default.ProposalFromResource msg: ProposalFromResource(fProposal: msg.GetObjectFromMessage); break;
                case BasicInstruction.UpdateStartConditions msg: UpdateAndForwardStartConditions(msg.GetObjectFromMessage); break;
                case BasicInstruction.WithdrawRequiredArticles msg: WithdrawRequiredArticles(operationKey: msg.GetObjectFromMessage); break;
                case BasicInstruction.FinishJob msg: FinishJob(jobResult: msg.GetObjectFromMessage); break;
                case Hub.Instruction.Default.AddResourceToHub msg: AddResourceToHub(resourceInformation: msg.GetObjectFromMessage); break;
                //case BasicInstruction.ResourceBrakeDown msg: ResourceBreakDown(breakDown: msg.GetObjectFromMessage); break;
                default: return false;
            }
            return true;
        }

        internal virtual void EnqueueJob(FOperation fOperation)
        {
            var localItem = _proposalManager.GetJobBy(fOperation.Key) as FOperation;
            // If item is not Already in Queue Add item to Queue
            // // happens i.e. Machine calls to Requeue item.
            if (localItem == null)
            {
                localItem = fOperation;
                localItem.UpdateHubAgent(hub: Agent.Context.Self);
                _proposalManager.Add(localItem, _capabilityManager.GetAllSetupDefintions(localItem.RequiredCapability));

                Agent.DebugMessage(msg: $"Got New Item to Enqueue: {fOperation.Operation.Name} | with start condition: {fOperation.StartConditions.Satisfied} with Id: {fOperation.Key}");
            }
            else
            {
                // reset Item.
                Agent.DebugMessage(msg: $"Got Item to Requeue: {fOperation.Operation.Name} | with start condition: {fOperation.StartConditions.Satisfied} with Id: {fOperation.Key}");
                _proposalManager.RemoveAllProposalsFor(localItem);
                localItem.UpdateSetupDefinition();

            }

            var capabilityDefinition = _capabilityManager.GetResourcesByCapability(fOperation.RequiredCapability);
            
            foreach (var setupDefinition in capabilityDefinition.GetAllSetupDefinitions)
            {
                foreach (var actorRef in setupDefinition.RequiredResources)
                {
                    Agent.DebugMessage(msg: $"Ask for proposal at resource {actorRef.Path.Name}");
                    Agent.Send(instruction: Resource.Instruction.Default.RequestProposal.Create(message: new FRequestProposalForSetup(localItem, setupDefinition.SetupKey), target: actorRef));
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="proposal"></param>
        internal virtual void ProposalFromResource(FProposal fProposal)
        {
            // get related operation and add proposal.
            var fOperation = _proposalManager.GetJobBy(fProposal.JobKey) as FOperation;

            Agent.DebugMessage(msg: $"Proposal for {fOperation.Operation.Name} with Schedule: {fProposal.PossibleSchedule} Id: {fProposal.JobKey} from: {fProposal.ResourceAgent}!");
            
            _proposalManager.AddProposal(fProposal);

            // if all resources answered
            if (_proposalManager.AllProposalForSetupDefinitionReceived(fOperation))
            {
                // item Postponed by All Machines ? -> requeue after given amount of time.
                if (_proposalManager.AllSetupDefintionsPostponed(fOperation))
                {
                    var postPonedFor = _proposalManager.PostponedUntil(fOperation);
                    Agent.DebugMessage(msg: $"{fOperation.Operation.Name} {fOperation.Key} postponed to {postPonedFor}");

                    _proposalManager.RemoveAllProposalsFor(fOperation);

                    Agent.Send(instruction: Hub.Instruction.Default.EnqueueJob.Create(message: fOperation, target: Agent.Context.Self), waitFor: postPonedFor);
                    return;
                }

                // acknowledge resources -> therefore get Machine -> send acknowledgement
                var acknowledgedProposal = _proposalManager.GetValidProposalForSetupDefinitionFor(fOperation);
                var possibleSchedule = acknowledgedProposal.EarliestStart();

                fOperation = ((IJob)fOperation).UpdateEstimations(possibleSchedule, acknowledgedProposal.GetFSetupDefinition) as FOperation;

                _proposalManager.Update(fOperation);
                
                // TODO maybe not required
                var assignedSetupDefintion = _proposalManager.GetAssignedSetupDefinition(fOperation);

                foreach(IActorRef resource in assignedSetupDefintion.RequiredResources) {

                    Agent.DebugMessage(msg: $"Start AcknowledgeProposal for {fOperation.Operation.Name} {fOperation.Key} on resource {resource}");

                    Agent.Send(instruction: Resource.Instruction.Default.AcknowledgeProposal
                        .Create(new FAcknowledgeProposal(fOperation
                                , possibleSchedule
                                , assignedSetupDefintion.RequiredResources)
                            , target: resource));
                }

            }
        }

        internal virtual void UpdateAndForwardStartConditions(FUpdateStartCondition startCondition)
        {
            var operation = _proposalManager.GetJobBy(startCondition.OperationKey) as FOperation;
            operation.SetStartConditions(startCondition: startCondition);
            // if Agent has no ResourceAgent the operation is not queued so here is nothing to do
            if (operation.SetupKey == -1)
                return;

            

            foreach (var resource in operation.SetupDefinition.RequiredResources)
            {
                Agent.DebugMessage(msg: $"Update and forward start condition: {operation.Operation.Name} {operation.Key}" +
                                        $"| ArticleProvided: {operation.StartConditions.ArticlesProvided} " +
                                        $"| PreCondition: {operation.StartConditions.PreCondition} " +
                                        $"to resource {resource}");

                Agent.Send(instruction: BasicInstruction.UpdateStartConditions.Create(message: startCondition, target: resource));
            }
            
        }

        /// <summary>
        /// Source: ResourceAgent put operation onto processingQueue and will work on it soon
        /// Target: Method should forward message to the associated production agent
        /// </summary>
        /// <param name="key"></param>
        internal virtual void WithdrawRequiredArticles(Guid operationKey)
        {
            var operation = _proposalManager.GetJobBy(operationKey) as FOperation;

            Agent.Send(instruction: BasicInstruction.WithdrawRequiredArticles
                                                    .Create(message: operation.Key
                                                           , target: operation.ProductionAgent));
        }

        internal virtual void FinishJob(IJobResult jobResult)
        {
            var operation = _proposalManager.GetJobBy(jobResult.Key) as FOperation;

            Agent.DebugMessage(msg: $"Resource called Item {operation.Operation.Name} {jobResult.Key} finished.");
            Agent.Send(instruction: BasicInstruction.FinishJob.Create(message: jobResult, target: operation.ProductionAgent));
            _proposalManager.Remove(operation);
        }

        internal virtual void AddResourceToHub(FResourceInformation resourceInformation)
        {
            foreach (var setup in resourceInformation.ResourceSetups)
            {
                var capabilityDefinition = _capabilityManager.GetCapabilityDefinition(setup.ResourceCapability);
               
                var setupDefinition = capabilityDefinition.GetSetupDefinitionBy(setup);
                setupDefinition.RequiredResources.Add(resourceInformation.Ref);
        
            }
            // Added Machine Agent To Machine Pool
            Agent.DebugMessage(msg: "Added Machine Agent " + resourceInformation.Ref.Path.Name + " to Machine Pool: " + resourceInformation.RequiredFor);
        }

        /*
         //TODO not working at the moment - implement and change to _resourceManager
        private void ResourceBreakDown(FBreakDown breakDown)
        {
            var brockenMachine = _resourceAgents.Single(predicate: x => breakDown.Resource == x.Value).Key;
            _resourceAgents.Remove(key: brockenMachine);
            Agent.Send(instruction: BasicInstruction.ResourceBrakeDown.Create(message: breakDown, target: brockenMachine, logThis: true), waitFor: 45);

            System.Diagnostics.Debug.WriteLine(message: "Break for " + breakDown.Resource, category: "Hub");
        }
        */
    }
}
