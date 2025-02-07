using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Kafka;
using Coflnet.Sky.Core;
using Coflnet.Sky.ModCommands.Services;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Coflnet.Sky.Commands.MC;
public class LoadFlipHistory : McCommand
{
    public override async Task Execute(MinecraftSocket socket, string arguments)
    {
        if (!socket.SessionInfo.VerifiedMc)
            throw new CoflnetException("not_verified", "You need to verify your minecraft account before executing this command.");
        var playerId = JsonConvert.DeserializeObject<string>(arguments);
        if (int.TryParse(playerId, out var days) || string.IsNullOrEmpty(playerId))
        {
            playerId = socket.SessionInfo.McUuid;
        }
        else if (!socket.GetService<ModeratorService>().IsModerator(socket))
            throw new CoflnetException("forbidden", "You are not allowed to do this");
        if (days == 0)
            days = 7;
        var redis = socket.GetService<ConnectionMultiplexer>();
        if ((await redis.GetDatabase().StringGetAsync("flipreload" + playerId)).HasValue)
        {
            socket.Dialog(db => db.MsgLine("Flips are already being reloaded, this can take multiple hours. \nLots of number crunshing :)"));
            return;
        }
        await redis.GetDatabase().StringSetAsync("flipreload" + playerId, "true", TimeSpan.FromHours(12));
        socket.SendMessage(COFLNET + $"Started refreshing flips for {playerId}", null, "this might take a while");
        if (playerId.Length < 30)
            playerId = (await socket.GetPlayerUuid(playerId)).Trim('"');

        var config = socket.GetService<IConfiguration>();
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config["KAFKA_HOST"],
            LingerMs = 100
        };
        using var producer = socket.GetService<KafkaCreator>().BuildProducer<string, SaveAuction>();
        var count = 0;
        var maxTime = DateTime.UtcNow; new DateTime(2023, 1, 10);
        var minTime = maxTime.AddDays(-1);
        for (int i = 0; i < days; i++)
            using (var context = new HypixelContext())
            {
                var numericId = await context.Players.Where(p => p.UuId == playerId).Select(p => p.Id).FirstAsync();
                Console.WriteLine($"Loading flips for {playerId} ({numericId})");
                var auctions = context.Auctions
                    .Where(a => a.SellerId == numericId && a.End < maxTime && a.End > minTime && a.HighestBidAmount > 0)
                    .Include(a => a.NbtData)
                    .Include(a => a.Enchantments);
                foreach (var auction in auctions)
                {
                    if (!auction.FlatenedNBT.ContainsKey("uid"))
                        continue;

                    producer.Produce(config["TOPICS:LOAD_FLIPS"], new Message<string, SaveAuction> { Key = auction.Uuid, Value = auction });
                    count++;
                }
                maxTime = minTime;
                minTime = maxTime.AddDays(-1);
            }
        producer.Flush(TimeSpan.FromSeconds(10));
        socket.SendMessage(COFLNET + $"Potential {count} flips for {playerId} found, submitted for processing", null, "this might take a few minutes to complete");
    }
}