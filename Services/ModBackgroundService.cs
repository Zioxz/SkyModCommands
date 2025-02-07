using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.MC;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.Core;
using Coflnet.Sky.ModCommands.Controllers;
using Coflnet.Sky.ModCommands.Models;
using Confluent.Kafka;
using fNbt;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Coflnet.Sky.ModCommands.Services
{

    public class ModBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<ModBackgroundService> logger;
        private FlipperService flipperService;

        private static Prometheus.Counter fastTrackSnipes = Prometheus.Metrics.CreateCounter("sky_fast_snipes", "Count of received fast track redis snipes");

        public ModBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<ModBackgroundService> logger, FlipperService flipperService)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
            this.logger = logger;
            this.flipperService = flipperService;
        }
        /// <summary>
        /// Called by asp.net on startup
        /// </summary>
        /// <param name="stoppingToken">is canceled when the applications stops</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            var instances = await GetConnections();
            foreach (var multiplexer in instances)
            {
                SubscribeConnection(multiplexer, stoppingToken);
            }
            logger.LogInformation("set up fast track flipper");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            logger.LogError("Fast track was stopped");
        }

        private async Task<List<ConnectionMultiplexer>> GetConnections()
        {
            List<ConnectionMultiplexer> instances = new();
            var i = 2;
            while (instances.Count == 0)
            {
                try
                {
                    var oldOption = config["FLIP_REDIS_OPTIONS"];
                    if (oldOption != null)
                    {
                        logger.LogInformation("using legacy flip sniper option");
                        AddOption(instances, oldOption);
                        return instances;
                    }
                    var instanceStrings = config.GetSection("REDIS_FLIP_INSTANCES").Get<string[]>();
                    foreach (var item in instanceStrings)
                    {
                        AddOption(instances, item);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "connecting to flip redis");
                    await Task.Delay(i++ * 10000);
                }
            }

            return instances;
        }

        private static void AddOption(List<ConnectionMultiplexer> instances, string item)
        {
            var option = ConfigurationOptions.Parse(item);
            instances.Add(ConnectionMultiplexer.Connect(option));
        }

        private void SubscribeConnection(ConnectionMultiplexer multiplexer, CancellationToken stoppingToken)
        {
            multiplexer.GetSubscriber().Subscribe("snipes", (chan, val) =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var flip = MessagePackSerializer.Deserialize<LowPricedAuction>(val);
                        if (flip.Finder == LowPricedAuction.FinderType.TFM)
                            FixTfmMetadata(flip);

                        if (flip.Auction.Context.ContainsKey("cname"))
                            flip.Auction.Context["cname"] += McColorCodes.DARK_GRAY + "!";
                        flip.AdditionalProps?.TryAdd("bfcs", "redis");
                        await flipperService.DeliverLowPricedAuction(flip, AccountTier.PREMIUM_PLUS).ConfigureAwait(false);
                        if (flip.TargetPrice - flip.Auction.StartingBid > 2000000)
                            logger.LogInformation($"sheduled bfcs {flip.Auction.Uuid} {DateTime.UtcNow.Second}.{DateTime.UtcNow.Millisecond}");
                        fastTrackSnipes.Inc();
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "bfcs error");
                    }
                }, new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token).ConfigureAwait(false);
            });
            logger.LogInformation("Subscribed to " + multiplexer.IsConnected + multiplexer.GetEndPoints().Select(e =>
            {
                var server = multiplexer.GetServer(e);
                return e.ToString();
            }).First());
            multiplexer.GetSubscriber().Subscribe("beat", (chan, val) =>
            {
                if (val == System.Net.Dns.GetHostName())
                    logger.LogInformation("redis heart beat " + val);
            });
            Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
                    multiplexer.GetSubscriber().Publish("beat", System.Net.Dns.GetHostName());
                }
                logger.LogWarning($"redis heart beat stopped; Cancellation Requested: {stoppingToken.IsCancellationRequested}");
            });

            Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(150), stoppingToken);
                    logger.LogInformation($"Status of Redis multiplexer: {multiplexer.IsConnected}");
                }
            });
        }

        private static void FixTfmMetadata(LowPricedAuction flip)
        {
            // rarange nbt
            var compound = flip.Auction.NbtData.Root().Get<NbtList>("i")
                ?.Get<NbtCompound>(0);
            NBT.FillFromTag(flip.Auction, compound, true);
        }

        private ModService GetService()
        {
            return scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ModService>();
        }
    }
}