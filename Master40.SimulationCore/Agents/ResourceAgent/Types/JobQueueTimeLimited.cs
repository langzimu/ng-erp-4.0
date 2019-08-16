﻿using System;
using System.Linq;
using static IJobs;

namespace Master40.SimulationCore.Agents.ResourceAgent.Types
{
    public class JobQueueTimeLimited : LimitedQueue
    {
        public JobQueueTimeLimited(int limit) : base(limit)
        {
        }
        /// <summary>
        /// To Be Testet
        /// </summary>
        /// <param name="item"></param>
        public bool Enqueue(IJob item)
        {
            if (Limit <= this.jobs.Sum(x => x.Duration)) return false;
            this.jobs.Add(item);
            return true;

        }
    }
}
