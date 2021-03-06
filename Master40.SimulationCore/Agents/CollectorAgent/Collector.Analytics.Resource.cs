using AkkaSim;
using Master40.DB.Data.Context;
using Master40.DB.Nominal;
using Master40.DB.ReportingModel;
using Master40.DB.ReportingModel.Interface;
using Master40.SimulationCore.Agents.CollectorAgent.Types;
using Master40.SimulationCore.Agents.HubAgent;
using Master40.SimulationCore.Environment.Options;
using Master40.SimulationCore.Types;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static FAgentInformations;
using static FBreakDowns;
using static FBuckets;
using static FCreateSimulationJobs;
using static FCreateSimulationResourceSetups;
using static FOperations;
using static FThroughPutTimes;
using static FUpdateSimulationJobs;
using static FUpdateSimulationWorkProviders;
using static Master40.SimulationCore.Agents.CollectorAgent.Collector.Instruction;

namespace Master40.SimulationCore.Agents.CollectorAgent
{
    public class CollectorAnalyticResource : Behaviour, ICollectorBehaviour
    {
        private CollectorAnalyticResource(ResourceList resources) : base()
        {
            _resources = resources;
        }

        private List<SimulationResourceJob> simulationJobs { get; } = new List<SimulationResourceJob>();
        private List<SimulationResourceJob> simulationJobsForDb { get; } = new List<SimulationResourceJob>();
        private List<SimulationResourceSetup> simulationResourceSetups { get; } = new List<SimulationResourceSetup>();
        private List<SimulationResourceSetup> simulationResourceSetupsForDb { get; } = new List<SimulationResourceSetup>();
        
        private KpiManager kpiManager { get; } = new KpiManager();

        private long lastIntervalStart { get; set; } = 0;

        private List<FUpdateSimulationJob> _updatedSimulationJob { get; } = new List<FUpdateSimulationJob>();
        private List<FThroughPutTime> _ThroughPutTimes { get; } = new List<FThroughPutTime>();
        private ResourceList _resources { get; set; } = new ResourceList();
        public Collector Collector { get; set; }
        /// <summary>
        /// Required to get Number output with . instead of ,
        /// </summary>
        private CultureInfo _cultureInfo { get; } = CultureInfo.GetCultureInfo(name: "en-GB");
        private List<Kpi> Kpis { get; } = new List<Kpi>();

        internal static List<Type> GetStreamTypes()
        {
            return new List<Type> { typeof(FCreateSimulationJob),
                                     typeof(FUpdateSimulationJob),
                                     typeof(FUpdateSimulationWorkProvider),
                                     typeof(UpdateLiveFeed),
                                     typeof(FThroughPutTime),
                                     typeof(Hub.Instruction.Default.AddResourceToHub),
                                     typeof(BasicInstruction.ResourceBrakeDown),
                                     typeof(FCreateSimulationResourceSetup)

            };
        }

        public static CollectorAnalyticResource Get(ResourceList resources)
        {
            return new CollectorAnalyticResource(resources: resources);
        }

        public override bool Action(object message) => throw new Exception(message: "Please use EventHandle method to process Messages");

        public bool EventHandle(SimulationMonitor simulationMonitor, object message)
        {
            switch (message)
            {
                case FCreateSimulationJob m: CreateSimulationOperation(simJob: m); break;
                case FUpdateSimulationJob m: UpdateSimulationOperation(simJob: m); break;
                case FCreateSimulationResourceSetup m: CreateSimulationResourceSetup(m); break;
                case FUpdateSimulationWorkProvider m: UpdateSimulationWorkItemProvider(uswp: m); break;
                case FThroughPutTime m: UpdateThroughputTimes(m); break;
                case Collector.Instruction.UpdateLiveFeed m: UpdateFeed(finalCall: m.GetObjectFromMessage); break;
                //case Hub.Instruction.AddResourceToHub m: RecoverFromBreak(item: m.GetObjectFromMessage); break;
                case BasicInstruction.ResourceBrakeDown m: BreakDwn(item: m.GetObjectFromMessage); break;
                default: return false;
            }
            // Collector.messageHub.SendToAllClients(msg: $"Just finished {message.GetType().Name}");
            return true;
        }

        /// <summary>
        /// collect the resourceSetups of resource, cant be updated afterward
        /// </summary>
        /// <param name="simulationResourceSetup"></param>
        private void CreateSimulationResourceSetup(FCreateSimulationResourceSetup simulationResourceSetup)
        {
            var _SimulationResourceSetup = new SimulationResourceSetup
            {
                SimulationConfigurationId = Collector.simulationId.Value,
                SimulationNumber = Collector.simulationNumber.Value,
                SimulationType = Collector.simulationKind.Value,
                Time = (int)(Collector.Time),
                Resource = simulationResourceSetup.Resource,
                ResourceTool = simulationResourceSetup.ResourceTool,
                Start = (int)simulationResourceSetup.Start,
                End = simulationResourceSetup.Start + simulationResourceSetup.Duration,
                ExpectedDuration = simulationResourceSetup.ExpectedDuration
            };
            simulationResourceSetups.Add(item: _SimulationResourceSetup);

        }

        private void UpdateThroughputTimes(FThroughPutTime m)
        {
            _ThroughPutTimes.Add(m);
        }

        private void BreakDwn(FBreakDown item)
        {
            Collector.messageHub.SendToClient(listener: item.Resource + "_State", msg: "offline");
        }

        private void RecoverFromBreak(FAgentInformation item)
        {
            Collector.messageHub.SendToClient(listener: item.RequiredFor + "_State", msg: "online");
        }

        private void UpdateFeed(bool finalCall)
        {
            //Collector.messageHub.SendToAllClients(msg: "(" + Collector.Time + ") Update Feed from WorkSchedule");
            // var mbz = agent.Context.AsInstanceOf<Akka.Actor.ActorCell>().Mailbox.MessageQueue.Count;
            // Debug.WriteLine("Time " + agent.Time + ": " + agent.Context.Self.Path.Name + " Mailbox left " + mbz);
            // check if Update has been processed this time step.
            

            if (lastIntervalStart != Collector.Time)
            {
                ResourceUtilization();
                var OEE = OverallEquipmentEffectiveness(resources: _resources, Collector.Time - 1440L, Collector.Time);
                
                lastIntervalStart = Collector.Time;
            }
            ThroughPut(finalCall);
            CallTotal(finalCall);
            LogToDB(writeResultsToDB: finalCall);
            Collector.Context.Sender.Tell(message: true, sender: Collector.Context.Self);
            Collector.messageHub.SendToAllClients(msg: "(" + Collector.Time + ") Finished Update Feed from WorkSchedule");
        }

        private void CallTotal(bool finalCall)
        {
            if (finalCall)
            {
                List<ISimulationResourceData> allSimulationData = new List<ISimulationResourceData>();
                allSimulationData.AddRange(simulationJobs);
                allSimulationData.AddRange(simulationJobsForDb);

                List<ISimulationResourceData> allSimulationSetupData = new List<ISimulationResourceData>();
                allSimulationSetupData.AddRange(simulationResourceSetups);
                allSimulationSetupData.AddRange(simulationResourceSetupsForDb);

                var settlingStart = Collector.Config.GetOption<SettlingStart>().Value;
                var resourcesDatas = kpiManager.GetSimulationDataForResources(resources: _resources, simulationResourceData: allSimulationData, simulationResourceSetupData: allSimulationSetupData, startInterval: settlingStart, endInterval: Collector.Time);

                foreach (var resource in resourcesDatas) { 
                    var tuple = resource._workTime + " " + resource._setupTime;
                    var machine = resource._resource.Replace(oldValue: ")", newValue: "").Replace(oldValue: "Resource(", newValue: "");
                    Collector.messageHub.SendToClient(listener: machine, msg: tuple);
                    CreateKpi(Collector, resource._workTime.Replace(".",","), machine, KpiType.ResourceUtilizationTotal, true);
                    CreateKpi(Collector, resource._setupTime.Replace(".", ","), machine, KpiType.ResourceSetupTotal, true);
                }

                var toSend = new
                {
                    WorkTime = resourcesDatas.Sum(x => x._totalWorkTime),
                    WorkTimePercent = Math.Round((resourcesDatas.Sum(x => Convert.ToDouble(x._workTime.Replace(".", ","))) / resourcesDatas.Count * 100), 4).ToString(_cultureInfo),
                    SetupTime = resourcesDatas.Sum(x => x._totalSetupTime),
                    SetupTimePercent = Math.Round((resourcesDatas.Sum(x => Convert.ToDouble(x._setupTime.Replace(".", ","))) / resourcesDatas.Count * 100), 4).ToString(_cultureInfo)
                };

                // Einschwingzeit - Ende der Simulation
                var OEE = OverallEquipmentEffectiveness(resources: _resources, 0 + Collector.Config.GetOption<TimePeriodForThroughputCalculation>().Value, Collector.Time);
                CreateKpi(Collector, OEE, "OEE", KpiType.Ooe, true);

                Collector.messageHub.SendToClient(listener: "totalUtilizationListener", msg: JsonConvert.SerializeObject(value: toSend ));
            }
        }

        private void LogToDB(bool writeResultsToDB)
        {
            if (Collector.saveToDB.Value && writeResultsToDB)
            {
                using (var ctx = ResultContext.GetContext(resultCon: Collector.Config.GetOption<DBConnectionString>().Value))
                {
                    ctx.SimulationJobs.AddRange(entities: simulationJobsForDb);
                    ctx.SaveChanges();
                    ctx.SimulationResourceSetups.AddRange(entities: simulationResourceSetupsForDb);
                    ctx.SaveChanges();
                    ctx.Kpis.AddRange(entities: Kpis);
                    ctx.SaveChanges();
                    ctx.Dispose();
                }
            }
        }

        private void ThroughPut(bool finalCall)
        {

            var leadTime = from lt in _ThroughPutTimes
                           where Math.Abs(value: lt.End) >= Collector.Time - Collector.Config.GetOption<TimePeriodForThroughputCalculation>().Value
                           group lt by lt.ArticleName into so
                           select new
                           {
                               ArticleName = so.Key,
                               Dlz = so.Select(selector: x => (double)x.End - x.Start).ToList()
                           };

            var thoughput = JsonConvert.SerializeObject(value: new { leadTime });
            Collector.messageHub.SendToClient(listener: "Throughput", msg: thoughput);

            if (finalCall)
            {
                CreateKpi(Collector, (leadTime.Average(x => x.Dlz.Average()) / leadTime.Count()).ToString() , "AverageLeadTime", KpiType.LeadTime, true);    
            }
            
            // foreach (var item in leadTime)
            // {
            //     var boxPlot = item.Dlz.FiveNumberSummary();
            //     var upperQuartile = Convert.ToInt64(value: boxPlot[4]);
            //     // Collector.actorPaths.SimulationContext.Ref.Tell(
            //     //     message: SupervisorAgent.Supervisor.Instruction.SetEstimatedThroughputTime.Create(
            //     //         message: new FSetEstimatedThroughputTime(articleId: 0, time: upperQuartile, articleName: item.ArticleName)
            //     //         , target: Collector.actorPaths.SystemAgent.Ref
            //     //     )
            //     //     , sender: ActorRefs.NoSender);
            //     // 
            //     // Debug.WriteLine(message: $"({Collector.Time}) Update Throughput time for article {item.ArticleName} to {upperQuartile}");
            // }


            var v2 = simulationJobs.Where(predicate: a => a.ArticleType == "Product"
                                                   && a.HierarchyNumber == 20
                                                   && a.End == 0);

            Collector.messageHub.SendToClient(listener: "ContractsV2", msg: JsonConvert.SerializeObject(value: new { Time = Collector.Time, Processing = v2.Count().ToString() }));
        }

        /// <summary>
        /// OEE for dashboard
        /// </summary>
        private string OverallEquipmentEffectiveness(ResourceList resources, long startInterval, long endInterval)
        {
            /* ------------- Total Production Time --------------------*/
            var totalInterval = endInterval - startInterval;
            var totalProductionTime = totalInterval * resources.Count;

            /* ------------- RunTime --------------------*/
            var totalPlannedDowntime = 0L;
            var totalBreakTime = 0L;
            double runTime = totalProductionTime - (totalPlannedDowntime - totalBreakTime);
            
            /* ------------- WorkTime --------------------*/
            var breakDown = 0L;

            //TODO Selstame Konstante - muss gekl�rt werden

            List<ISimulationResourceData> allSimulationResourceSetups = new List<ISimulationResourceData>();
            allSimulationResourceSetups.AddRange(simulationResourceSetups.Where(x => x.Start >= startInterval + 50).ToList());
            allSimulationResourceSetups.AddRange(simulationResourceSetupsForDb.Where(x => x.Start >= startInterval + 50).ToList());

            var setupTime = kpiManager.GetTotalTimeForInterval(resources, allSimulationResourceSetups, startInterval, endInterval);

            var totalUnplannedDowntime = breakDown + setupTime;

            double workTime = runTime - totalUnplannedDowntime;

            /* ------------- PerformanceTime --------------------*/
            List<ISimulationResourceData> allSimulationResourceJobs = new List<ISimulationResourceData>();
            allSimulationResourceJobs.AddRange(simulationJobs.Where(x => x.Start >= startInterval + 50).ToList());
            allSimulationResourceJobs.AddRange(simulationJobsForDb.Where(x => x.Start >= startInterval + 50).ToList());
            var jobTime = kpiManager.GetTotalTimeForInterval(resources, allSimulationResourceJobs, startInterval, endInterval);

            var idleTime = workTime - jobTime;

            // var reducedSpeed = 0L; //TODO if this is implemented the GetTotalTimeForInterval must change. to reflect speed div.

            double performanceTime = jobTime;

            /* ------------- zeroToleranceTime --------------------*/

            //TODO Max Quality Goods
            var goodGoods = 35L;
            var badGoods = 0L;
            var totalGoods = goodGoods + badGoods;

            double zeroToleranceTime = performanceTime / totalGoods * goodGoods;

            //1.Parameter Availability calculation
            double availability =  workTime /runTime;

            //2. Parameter Performance calculation
            double performance = performanceTime / workTime;

            //3. Parameter Quality calculation
            double quality = zeroToleranceTime / performanceTime;

            //Total OEE
            var totalOEE = availability * performance * quality;

            var totalOEEString = Math.Round(totalOEE * 100, 2).ToString();
            if (totalOEEString == "NaN") totalOEEString = "0";

            Collector.messageHub.SendToClient(listener: "oeeListener", msg: totalOEEString);

            return totalOEEString;

            //TODO Implement View for GUI

        }

        //TODO solution = KpiManager
        private void ResourceUtilization()
        {
            double divisor = Collector.Time - lastIntervalStart;
            var tupleList = new List<Tuple<string, string>>();
            Collector.messageHub.SendToAllClients(msg: "(" + Collector.Time + ") Update Feed from DataCollection");
            Collector.messageHub.SendToAllClients(msg: "(" + Collector.Time + ") Time since last Update: " + divisor + "min");

            //simulationWorkschedules.WriteCSV( @"C:\Users\mtko\source\output.csv");

            //JobWorkingTimes for interval
            var lower_borders = from sw in simulationJobs
                                where sw.Start < lastIntervalStart
                                      && sw.End > lastIntervalStart
                                      && sw.Resource != null
                                group sw by sw.Resource
                        into rs
                                select new Tuple<string, long>(rs.Key,
                                    rs.Sum(selector: x => x.End - lastIntervalStart));


            var upper_borders = from sw in simulationJobs
                                where sw.Start < Collector.Time
                                   && sw.End > Collector.Time
                                   && sw.Resource != null
                                group sw by sw.Resource
                                into rs
                                select new Tuple<string, long>(rs.Key,
                                    rs.Sum(selector: x => Collector.Time - x.Start));


            var from_work = from sw in simulationJobs
                            where sw.Start >= lastIntervalStart
                               && sw.End <= Collector.Time
                               && sw.Resource != null
                            group sw by sw.Resource
                            into rs
                            select new Tuple<string, long>(rs.Key,
                                rs.Sum(selector: x => x.End - x.Start));

            var reourceList = _resources.Select(selector: x => new Tuple<string, long>(x, 0 ));
            var merge = from_work.Union(second: lower_borders).Union(second: upper_borders).Union(second: reourceList).ToList();

            var final = from m in merge
                group m by m.Item1
                into mg
                select new Tuple<string, long>(mg.Key, mg.Sum(x => x.Item2));

            foreach (var item in final.OrderBy(keySelector: x => x.Item1))
            {
                var value = Math.Round(value: item.Item2 / divisor, digits: 3).ToString(provider: _cultureInfo);
                if (value == "NaN") value = "0";
                tupleList.Add(new Tuple<string, string>(item.Item1, value));
                CreateKpi(agent: Collector, value: value.Replace(".", ","), name: item.Item1, kpiType: KpiType.ResourceUtilization);
            }


            var totalLoad = Math.Round(value: final.Sum(selector: x => x.Item2) / divisor / final.Count() * 100, digits: 3).ToString(provider: _cultureInfo);
            if (totalLoad == "NaN") totalLoad = "0";
            CreateKpi(agent: Collector, value: totalLoad.Replace(".", ","), name: "TotalWork", kpiType: KpiType.ResourceUtilization);

            //ResourceSetupTimes for interval
            var setups_lower_borders = from sw in simulationResourceSetups
                                       where sw.Start < lastIntervalStart
                                             && sw.End > lastIntervalStart
                                             && sw.Resource != null
                                       group sw by sw.Resource
                into rs
                                       select new Tuple<string, long>(rs.Key,
                                           rs.Sum(selector: x => x.End - lastIntervalStart));

            var setups_upper_borders = from sw in simulationResourceSetups
                                       where sw.Start < Collector.Time
                                             && sw.End > Collector.Time
                                             && sw.Resource != null
                                       group sw by sw.Resource
                      into rs
                                       select new Tuple<string, long>(rs.Key,
                                           rs.Sum(selector: x => Collector.Time - x.Start));

            var totalSetups = from m in simulationResourceSetups
                              where m.Start >= lastIntervalStart
                                 && m.End <= Collector.Time
                              group m by m.Resource
                              into rs
                              select new Tuple<string, long>(rs.Key,
                                                              rs.Sum(selector: x => x.End - x.Start));

            var emptyResources = _resources.Select(selector: x => new Tuple<string, long>(x, 0));
            var union = totalSetups.Union(setups_lower_borders).Union(setups_upper_borders).Union(emptyResources).ToList();

            var finalSetup = from m in union
                group m by m.Item1
                into mg
                select new Tuple<string, long>(mg.Key, mg.Sum(x => x.Item2));

            foreach (var resource in finalSetup.OrderBy(keySelector: x => x.Item1))
            {
                var value = Math.Round(value: resource.Item2 / divisor, digits: 3).ToString(provider: _cultureInfo);
                if (value == "NaN") value = "0";
                var machine = resource.Item1.Replace(oldValue: ")", newValue: "").Replace(oldValue: "Resource(", newValue: "");
                var workValue = tupleList.Single(x => x.Item1 == resource.Item1).Item2;
                var all = workValue + " " + value;
                Collector.messageHub.SendToClient(listener: machine, msg: all);
                CreateKpi(agent: Collector, value: value.Replace(".", ","), name: resource.Item1, kpiType: KpiType.ResourceSetup);
            }

            var totalSetup = Math.Round(value: finalSetup.Sum(selector: x => x.Item2) / divisor / finalSetup.Count() * 100, digits: 3).ToString(provider: _cultureInfo);
            if (totalSetup == "NaN") totalSetup = "0";
            CreateKpi(agent: Collector, value: totalSetup.Replace(".", ","), name: "TotalSetup", kpiType: KpiType.ResourceSetup);

            Collector.messageHub.SendToClient(listener: "TotalTimes", msg: JsonConvert.SerializeObject(value: 
                new { Time = Collector.Time
                    , Load = new { Work = totalLoad, Setup = totalSetup }}));

            //Persist Jobs
            // TODO make it better
            simulationJobsForDb.AddRange(simulationJobs.Where(op => op.CreatedForOrderId != string.Empty
                                                                         && op.End < lastIntervalStart));
            simulationJobs.RemoveAll(op => op.CreatedForOrderId != string.Empty
                                                && op.End < lastIntervalStart);

            simulationResourceSetupsForDb.AddRange(simulationResourceSetups.Where(rs => rs.End < lastIntervalStart));
            simulationResourceSetups.RemoveAll(rs => rs.End < lastIntervalStart);

            //Collector.messageHub.SendToAllClients(msg: $"({Collector.Time}) Removed");
        }

        private void CreateKpi(Collector agent, string value, string name, KpiType kpiType, bool isFinal = false)
        {
            var k = new Kpi
            {
                Name = name,
                Value = Math.Round(Convert.ToDouble(value: value), 2),
                Time = (int)agent.Time,
                KpiType = kpiType,
                SimulationConfigurationId = agent.simulationId.Value,
                SimulationNumber = agent.simulationNumber.Value,
                IsFinal = isFinal,
                IsKpi = true,
                SimulationType = agent.simulationKind.Value
            };
            Kpis.Add(item: k);
        }

        //TODO implement Interface for ISimulationJob (FCreateSimluationOperation, FCreateSimulationBucket)
        private void CreateSimulationOperation(FCreateSimulationJob simJob)
        {
            var fOperation = ((FOperation)simJob.Job);
            var simulationJob = new SimulationResourceJob
            {
                JobId = simJob.Job.Key.ToString(),
                JobName = fOperation.Operation.Name,
                JobType = simJob.JobType,
                DueTime = (int)simJob.Job.DueTime,
                SimulationConfigurationId = Collector.simulationId.Value,
                SimulationNumber = Collector.simulationNumber.Value,
                SimulationType = Collector.simulationKind.Value,
                CreatedForOrderId = string.Empty,
                Article = fOperation.Operation.Article.Name,
                OrderId = "[" + simJob.CustomerOrderId + "]",
                HierarchyNumber = fOperation.Operation.HierarchyNumber,
                //Remember this is now a fArticleKey (Guid)
                ProductionOrderId =  simJob.ProductionAgent,
                Parent = simJob.IsHeadDemand.ToString(),
                FArticleKey = simJob.fArticleKey.ToString(),
                Time = (int)(Collector.Time),
                ExpectedDuration = fOperation.Operation.Duration,
                ArticleType = simJob.ArticleType,
                ResourceTool = fOperation.Tool.Name
            };

            var edit = _updatedSimulationJob.FirstOrDefault(predicate: x => x.Job.Key.Equals(fOperation.Key));
            if (edit != null)
            {
                simulationJob.Start = (int)edit.Start;
                simulationJob.End = (int)(edit.Start + edit.Duration);
                simulationJob.Resource = edit.Resource;
                _updatedSimulationJob.Remove(item: edit);
            }
            simulationJobs.Add(item: simulationJob);
        }
        
        private void UpdateSimulationOperation(FUpdateSimulationJob simJob)
        {
            var edit = simulationJobs.FirstOrDefault(predicate: x => x.JobId.Equals(value: simJob.Job.Key.ToString()));
            if (edit != null)
            {
                edit.JobType = simJob.JobType;
                edit.Start = (int)simJob.Start;
                edit.End = (int)(simJob.Start + simJob.Duration); // to have Time Points instead of Time Periods
                edit.Resource = simJob.Resource;
                edit.Bucket = simJob.Bucket;

                return;
            }
            _updatedSimulationJob.Add(item: simJob);

            //tuples.Add(new Tuple<string, long>(uws.Machine, uws.Duration));
        }

        private void UpdateSimulationWorkItemProvider(FUpdateSimulationWorkProvider uswp)
        {
            foreach (var fpk in uswp.FArticleProviderKeys)
            {
                var items = simulationJobs.Where(predicate: x => x.FArticleKey.Equals(value: fpk.ProvidesArticleKey.ToString())
                                                            && x.ProductionOrderId == fpk.ProductionAgentKey); 
                foreach (var item in items)
                {
                    item.ParentId = item.Parent.Equals(value: false.ToString()) ? "[" + uswp.RequestAgentId + "]" : "[]";
                    item.Parent = uswp.RequestAgentName;
                    item.CreatedForOrderId = item.OrderId;
                    item.OrderId = "[" + uswp.CustomerOrderId + "]";
                }
            }

        }
    }
}
