using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.Shared;
using hypixel;

namespace Coflnet.Sky.Commands.MC
{
    public class ReferenceCommand : McCommand
    {
        public override async Task Execute(MinecraftSocket socket, string arguments)
        {
            Console.WriteLine(arguments);
            var uuid = arguments.Trim('"');
            var flip = socket.GetFlip(uuid);
            if (flip?.Finder.HasFlag(LowPricedAuction.FinderType.SNIPER) ?? false)
            {
                await SniperReference(socket, uuid, flip, "sniping");
                return;
            }
            if (flip?.Finder.HasFlag(LowPricedAuction.FinderType.SNIPER_MEDIAN) ?? false)
            {
                await SniperReference(socket, uuid, flip, "median sniper");
                return;
            }
            socket.ModAdapter.SendMessage(new ChatPart("Caclulating references", "https://sky.coflnet.com/auction/" + uuid, "please give it a second"));
            var based = await CoreServer.ExecuteCommandWithCache<string, IEnumerable<BasedOnCommandResponse>>("flipBased", uuid);
            if (based == null)
                socket.ModAdapter.SendMessage(new ChatPart("Woops, sorry but there could be no references found or another error occured :("));
            else
            {
                socket.ModAdapter.SendMessage(
                    based.OrderBy(b => b.highestBid).Skip(based.Count() / 2).Take(3)
                    .Select(ToMessage(socket))
                    .Append(new ChatPart("The 3 most recent references are"))
                    .Concat(based.OrderByDescending(b => b.end).Take(3)
                    .Select(ToMessage(socket)))
                    .Append(new ChatPart("The 3 most expensive references are"))
                    .Concat(based.OrderByDescending(b => b.highestBid).Take(3)
                    .Select(ToMessage(socket)))
                    .ToArray());
            }

            await Task.Delay(200);
            socket.ModAdapter.SendMessage(new ChatPart(MinecraftSocket.COFLNET + "click this to open the auction on the website (in case you want to report an error or share it)", "https://sky.coflnet.com/auction/" + uuid, "please give it a second"));
        }

        private static Func<BasedOnCommandResponse, ChatPart> ToMessage(MinecraftSocket socket)
        {
            return b => new ChatPart(
                                        $"\n-> {b.ItemName} for {McColorCodes.AQUA}{socket.FormatPrice(b.highestBid)}{McColorCodes.GRAY} {b.end}",
                                        "https://sky.coflnet.com/auction/" + b.uuid,
                                        "Click to open this auction");
        }

        private async Task SniperReference(MinecraftSocket socket, string uuid, LowPricedAuction flip, string algo)
        {
            var referenceId = flip.AdditionalProps["reference"];
            if (referenceId == null)
            {
                socket.Log("reference is missing", Microsoft.Extensions.Logging.LogLevel.Error);
                socket.ModAdapter.SendMessage(new ChatPart(COFLNET + "The reference for this flip could not be retrieved. It got lost"));
                return;
            }
            var reference = await AuctionService.Instance.GetAuctionAsync(referenceId);
            Console.WriteLine(JSON.Stringify(flip.AdditionalProps));
            Console.WriteLine(JSON.Stringify(reference));
            socket.ModAdapter.SendMessage(new ChatPart($"{COFLNET}This flip was found by the {algo} algorithm\n", "https://sky.coflnet.com/auction/" + uuid, "click this to open the flip on website"),
                new ChatPart($"It was compared to {McColorCodes.AQUA} this auction {DEFAULT_COLOR}, open ah", $"/viewauction {reference.Uuid}", McColorCodes.GREEN + "open it on ah"),
                new ChatPart($"{McColorCodes.WHITE} open on website", $"https://sky.coflnet.com/auction/{reference.Uuid}", "open it on website"));
        }
    }
}