using System.Linq;
using Master40.DB.Data.Context;
using Master40.DB.Data.WrappersForPrimitives;
using Master40.SimulationCore.DistributionProvider;
using Master40.SimulationCore.Environment.Options;
using Microsoft.EntityFrameworkCore;
using Zpp.DbCache;

namespace Zpp.Test.Configuration.Scenarios
{
    public abstract class TestScenario
    {
        protected readonly IDbMasterDataCache DbMasterDataCache;
        
        public TestScenario(IDbMasterDataCache dbMasterDataCache)
        {
            DbMasterDataCache = dbMasterDataCache;
        }

        public void CreateCustomerOrders(Quantity quantity, ProductionDomainContext productionDomainContext)
        {
            var config = Master40.SimulationCore.Environment.Configuration.Create(args: new object[]
            {
                new Seed(value: 2),
                new SimulationNumber(value: 1),
                new OrderArrivalRate(value: 0.025),
                new MinDeliveryTime(value: 300),
                new MaxDeliveryTime(value: 600),
            });
            var productIds = productionDomainContext.Articles.Include(x => x.ArticleType)
                .Where(x => x.ArticleType.Name == "Product")
                .Select(x => x.Id)
                .ToList();
            var orderGenerator = new OrderGenerator(config, productionDomainContext, productIds);
            for (int i = 0; i < quantity.GetValue(); i++)
            {
                var order = orderGenerator.GetNewRandomOrder(0);
                productionDomainContext.CustomerOrders.Add(order);
                productionDomainContext.SaveChanges();
            }
        }

        public static OrderGenerator GetOrderGenerator(ProductionDomainContext productionDomainContext
                                                     , MinDeliveryTime minDeliveryTime
                                                     , MaxDeliveryTime maxDelivery
                                                     , OrderArrivalRate orderArrivalRate)
        {
            var config = Master40.SimulationCore.Environment.Configuration.Create(args: new object[]
            {
                new Seed(value: 1337),
                new SimulationNumber(value: 1),
                orderArrivalRate,
                maxDelivery,
                minDeliveryTime,
            });
            var productIds = productionDomainContext.Articles.Include(x => x.ArticleType)
                .Where(x => x.ArticleType.Name == "Product")
                .Select(x => x.Id)
                .ToList();
            var orderGenerator = new OrderGenerator(config, productionDomainContext, productIds);
            return orderGenerator;
        }
    }
}