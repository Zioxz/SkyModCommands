namespace Coflnet.Sky.ModCommands.Services;

using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Commands.Shared;
using System.Collections.Concurrent;
using Coflnet.Sky.Commands;
using Coflnet.Sky.Commands.MC;
using System.Linq;
using System.Globalization;
using System.Net;
using Payments.Client.Api;

/// <summary>
/// Handles events before the api update
/// </summary>
public class PreApiService : BackgroundService
{
    ConnectionMultiplexer redis;
    ILogger<PreApiService> logger;
    static private ConcurrentDictionary<IFlipConnection, DateTime> localUsers = new();
    IProductsApi productsApi;
    private List<string> preApiUsers = new();
    private ConcurrentDictionary<string, DateTime> sold = new();
    private ConcurrentDictionary<string, DateTime> sent = new();
    public PreApiService(ConnectionMultiplexer redis, FlipperService flipperService, ILogger<PreApiService> logger, IProductsApi productsApi)
    {
        this.redis = redis;
        this.logger = logger;

        flipperService.PreApiLowPriceHandler += PreApiLowPriceHandler;
        this.productsApi = productsApi;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        redis.GetSubscriber().Subscribe("auction_sell", (channel, message) =>
        {
            try
            {
                var sell = MessagePack.MessagePackSerializer.Deserialize<Auction>(message);
                sold.TryAdd(sell.Uuid, DateTime.UtcNow);
                if (sell.Uuid == Dns.GetHostName() && DateTime.UtcNow.Minute % 5 == 0)
                    logger.LogInformation("got mod sell redis heartbeat");
                sent.TryRemove(sell.Uuid, out _);
            }
            catch (System.Exception e)
            {
                logger.LogError(e, "failed to deserialize sell");
            }
        });
        redis.GetSubscriber().Subscribe("auction_sent", (channel, message) =>
        {
            try
            {
                var send = MessagePack.MessagePackSerializer.Deserialize<Auction>(message);
                sent.AddOrUpdate(send.Uuid, DateTime.UtcNow, (key, old) => DateTime.UtcNow);
            }
            catch (System.Exception e)
            {
                logger.LogError(e, "failed to deserialize send");
            }
        });
        // here to trigger the creation of the service
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SendEndWarnings();
                await RefreshUsers();
                PublishSell(Dns.GetHostName());
            }
            catch (System.Exception e)
            {
                logger.LogError(e, "failed to execute pre api service refresh");
            }

            await Task.Delay(45000, stoppingToken);
        }
    }

    private static void SendEndWarnings()
    {
        var now = DateTime.UtcNow;
        foreach (var item in localUsers)
        {
            if (item.Value - TimeSpan.FromMinutes(1) < now && item.Key is IMinecraftSocket socket)
            {
                socket.Dialog(db => db.CoflCommand<PurchaseCommand>($"Your {McColorCodes.RED}pre api{McColorCodes.WHITE} will expire in {McColorCodes.RED}under one minute{McColorCodes.WHITE}\nClick {McColorCodes.RED}here{McColorCodes.WHITE} to renew", "pre_api",
                    $"{McColorCodes.RED}Starts the purchase for another hour of {McColorCodes.RED}pre api{McColorCodes.WHITE}"));
                if (item.Value < now)
                    localUsers.TryRemove(item.Key, out _);
            }
        }
    }

    public bool IsSold(string uuid)
    {
        return sold.ContainsKey(uuid);
    }

    private async Task RefreshUsers()
    {
        try
        {
            preApiUsers = await productsApi.ProductsServiceServiceSlugIdsGetAsync("pre_api");
        }
        catch (System.Exception e)
        {
            logger.LogError(e, "Failed to get pre api users");
        }
    }

    public void AddUser(IFlipConnection connection, DateTime expires)
    {
        localUsers.AddOrUpdate(connection, expires, (key, old) => expires);
        logger.LogInformation($"Added user {connection.UserId} to flip list {localUsers.Count} users {expires}");
        Task.Run(RefreshUsers);
    }

    private async Task PreApiLowPriceHandler(FlipperService sender, LowPricedAuction e)
    {
        try
        {
            await DistributeFlip(e).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            logger.LogError(ex, $"Error while handling pre api low price {JSON.Stringify(localUsers)}\n{JSON.Stringify(e)}");
        }
    }

    private async Task<LowPricedAuction> DistributeFlip(LowPricedAuction e)
    {
        if (e.Auction?.Context?.ContainsKey("cname") ?? false)
            e.Auction.Context["cname"] += McColorCodes.DARK_GRAY + ".";

        var tilPurchasable = e.Auction.Start + TimeSpan.FromSeconds(20) - DateTime.UtcNow;
        if (tilPurchasable < TimeSpan.Zero)
            tilPurchasable = TimeSpan.Zero;
        foreach (var item in localUsers.Keys)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var shouldSendForward = await SendFlipCorrectly(e, tilPurchasable, item).ConfigureAwait(false);
                    if (shouldSendForward)
                        await SendFlipCorrectly(e, tilPurchasable, item).ConfigureAwait(false);
                }
                catch (System.Exception e)
                {
                    logger.LogError(e, "Error while sending flip to user");
                }
            }).ConfigureAwait(false);
        }
        var profit = e.TargetPrice - e.Auction?.StartingBid;
        if (profit > 0)
            logger.LogInformation($"Pre-api low price handler called for {e?.Auction?.Uuid} profit {profit} users {localUsers?.Count}");

        await Task.Delay(tilPurchasable).ConfigureAwait(false);
        // check if flip was sent to anyone 
        await Task.Delay(20_000).ConfigureAwait(false);
        // if not send to all users
        return e;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="flip"></param>
    /// <param name="tilPurchasable"></param>
    /// <param name="connection"></param>
    /// <returns>true if delay should be skipped for others</returns>
    public async Task<bool> SendFlipCorrectly(LowPricedAuction flip, TimeSpan tilPurchasable, IFlipConnection connection)
    {
        var userCount = preApiUsers.Count == 0 ? 1 : preApiUsers.Count;
        var index = connection is MinecraftSocket socket ? preApiUsers.IndexOf(socket.UserId) : Random.Shared.Next(userCount);
        if (index == -1)
            logger.LogError($"User {connection.UserId} is not in pre api list");
        var isMyRR = Math.Abs(flip.Auction.UId) % userCount == index;
        if (!isMyRR)
        {
            logger.LogInformation($"Waiting {tilPurchasable} for {flip.Auction.Uuid} to send to {connection.UserId} active users {JSON.Stringify(preApiUsers)}");
            var toWait = tilPurchasable - TimeSpan.FromSeconds(2);
            if(toWait < TimeSpan.FromSeconds(1.5))
                toWait = TimeSpan.FromSeconds(1.5);
            await Task.Delay(toWait).ConfigureAwait(false);
            // check if rr was sent to user, if not send to all users
            if (sent.ContainsKey(flip.Auction.Uuid))
                await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(3, 5))).ConfigureAwait(false);
            else
            {
                flip.Auction.Context["cname"] = flip.Auction.Context["cname"].Replace(McColorCodes.DARK_GRAY + ".", McColorCodes.BLACK + ".");
            }
        }
        else if (flip.Auction.Context.ContainsKey("cname"))
        {
            // copy the auction so we can modify it without affecting the original
            var context = flip.Auction.Context;
            flip = new LowPricedAuction(flip);
            flip.Auction.Context = new Dictionary<string, string>(context);
            flip.Auction.Context["cname"] = flip.Auction.Context["cname"].Replace(McColorCodes.DARK_GRAY + ".", McColorCodes.RED + ".");
        }
        logger.LogInformation($"Is rr {isMyRR}, Sent flip to {connection.UserId} for {flip.Auction.Uuid} active users {JSON.Stringify(preApiUsers)} index {index} {flip.Auction.UId % userCount} forward {sent.ContainsKey(flip.Auction.Uuid)}");
        var sendSuccessful = await connection.SendFlip(flip).ConfigureAwait(false);
        if (!sendSuccessful)
        {
            logger.LogInformation($"Failed to send flip to {connection.UserId} for {flip.Auction.Uuid}");
            localUsers.TryRemove(connection, out _);
        }
        if (!localUsers.TryGetValue(connection, out var end) || end < DateTime.UtcNow)
        {
            localUsers.TryRemove(connection, out _);
            logger.LogInformation("Removed user from flip list");
        }

        if (!isMyRR)
            return false;

        if (tilPurchasable > TimeSpan.FromSeconds(2.5))
            await Task.Delay(tilPurchasable - TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
        if ((connection as MinecraftSocket)?.LastSent.Contains(flip) ?? false)
        {
            PublishReceive(flip.Auction.Uuid);
            return false;
        }
        return true;
    }

    public void PurchaseMessage(IMinecraftSocket connection, string message)
    {
        var regex = new System.Text.RegularExpressions.Regex(@"^You purchased (.*) for (.*) coins");
        var match = regex.Match(message);
        var itemName = match.Groups[1].Value;
        var priceString = match.Groups[2].Value;
        var price = double.Parse(priceString, NumberStyles.Number, CultureInfo.InvariantCulture);
        var flip = connection.LastSent.Reverse().FirstOrDefault(f => f.Auction.ItemName == itemName && f.Auction.StartingBid == price);
        if (flip != null)
        {
            var uuid = flip.Auction.Uuid;
            logger.LogInformation($"Found flip that was bought by {connection.SessionInfo.McUuid} {uuid} at {DateTime.UtcNow}");
            PublishSell(uuid);
        }
        else
            logger.LogInformation($"Could not find flip that was bought by {connection.SessionInfo.McUuid} {itemName} {price}");
    }

    private void PublishSell(string uuid)
    {
        redis.GetSubscriber().Publish("auction_sell", MessagePack.MessagePackSerializer.Serialize(new Auction { Uuid = uuid }));
    }

    private void PublishReceive(string uuid)
    {
        redis.GetSubscriber().Publish("auction_sent", MessagePack.MessagePackSerializer.Serialize(new Auction { Uuid = uuid }));
    }

    [MessagePack.MessagePackObject]
    public class Auction
    {
        [MessagePack.Key(0)]
        public string Uuid { get; set; }
    }
}
