﻿using System;
using Akka.Actor;
using Master40.DB.Nominal;
using Master40.SimulationCore.Helper;
using static Master40.SimulationCore.Agents.Guardian.Instruction;

namespace Master40.SimulationCore.Agents.Guardian
{
    public class GuardianBehaviour : SimulationCore.Types.Behaviour
    {
        internal int counterChilds = 0;

        internal GuardianBehaviour(Func<IUntypedActorContext, AgentSetup, IActorRef> childMaker, SimulationType simulationType) 
            : base(childMaker: childMaker
                 , simulationType: simulationType) { }

        public static GuardianBehaviour Get(Func<IUntypedActorContext, AgentSetup, IActorRef> childMaker, SimulationType simulationType)
        {
            return new GuardianBehaviour(childMaker: childMaker, simulationType: simulationType);
        }

        public override bool Action(object message)
        {
            switch (message)
            {
                case CreateChild m: CreateChild(setup: m.GetObjectFromMessage); break;
                default: return false;
            }
            return true;
        }

        internal void CreateChild(AgentSetup setup)
        {
            counterChilds++;
            //System.Diagnostics.Debug.WriteLine($"({Agent.CurrentTime}) {Agent.Context.Self.Path.Name} add child and has {counterChilds} now");

            var childRef = Agent.Behaviour.ChildMaker(arg1: Agent.Context, arg2: setup);
            Agent.Send(instruction: BasicInstruction.Initialize.Create(target: childRef, message: setup.Behaviour));
        }

        
    }
}
