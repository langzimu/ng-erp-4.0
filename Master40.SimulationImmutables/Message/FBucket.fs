﻿module FBuckets

open System
open Akka.Actor
open FOperations
open FProposals
open System.Linq
open FStartConditions
open IKeys
open IJobs
open System.Linq
open Master40.DB.DataModel
open FUpdateStartConditions

    type public FBucket =
        { Key : Guid
          Name : string
          IsFixPlanned : bool
          CreationTime : int64
          BackwardEnd : int64 
          BackwardStart : int64
          ForwardEnd : int64 
          ForwardStart : int64 
          Scope : int64
          Start : int64
          End : int64 
          StartConditions : FStartCondition
          Priority : FBucket -> int64 -> double
          ResourceAgent : IActorRef
          mutable HubAgent : IActorRef
          Operations : Set<FOperation>
          Tool : M_ResourceTool
          MaxBucketSize : double
          MinBucketSize : double
          Proposals : System.Collections.Generic.List<FProposal> 
          Bucket : string
          } interface IKey with
                member this.Key  with get() = this.Key
                member this.CreationTime with get() = this.CreationTime
            interface IJob with
                member this.Key  with get() = this.Key
                member this.Name with get() = this.Name
                member this.BackwardEnd with get() = this.BackwardEnd
                member this.BackwardStart with get() = this.BackwardStart
                member this.DueTime = this.Operations.Min(fun y -> y.DueTime)
                member this.End with get() = this.End
                member this.ForwardEnd with get() = this.ForwardEnd
                member this.ForwardStart with get() = this.ForwardStart
                member this.Proposals with get() = this.Proposals
                member this.Start with get() = this.Start
                member this.StartConditions with get() = this.StartConditions
                member this.Priority time = this.Priority this time 
                member this.ResourceAgent with get() = this.ResourceAgent
                member this.HubAgent with get() = this.HubAgent
                member this.Tool with get() = this.Tool
                member this.Duration = this.Operations.Sum(fun y -> (int64)y.Operation.Duration)
                member this.UpdateEstimations estimatedStart resourceAgent = { this with End = estimatedStart +  this.Operations.Sum(fun y -> (int64)y.Operation.Duration);
                                                                                         Start = (int64)estimatedStart;
                                                                                         ResourceAgent = resourceAgent } :> IJob
                member this.Bucket with get() = this.Bucket
                member this.UpdateBucket bucketId = { this with Bucket = bucketId} :> IJob

         // Returns new Object with Updated Due
        member this.UpdateResourceAgent r = { this with ResourceAgent = r }
        member this.UpdateHubAgent hub =  this.HubAgent <- hub 
        member this.AddOperation op = { this with Operations = this.Operations.Add(op)}
        member this.RemoveOperation op = { this with Operations = this.Operations.Remove(op)}
        member this.SetStartConditions(startCondition : FUpdateStartCondition) = this.StartConditions.ArticlesProvided <- startCondition.ArticlesProvided 
                                                                                 this.StartConditions.PreCondition <- startCondition.PreCondition
        member this.SetFixPlanned = { this with IsFixPlanned = true}
        member this.GetCapacityLeft = (this.BackwardStart - this.ForwardStart) - this.Operations.Sum(fun y -> (int64)y.Operation.Duration)
        member this.HasSatisfiedJob = this.Operations.Any(fun y -> y.StartConditions.Satisfied)
        member this.UpdateBucket bucketId = { this with Bucket = bucketId}