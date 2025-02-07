using System.Threading.Tasks;

namespace Coflnet.Sky.Commands.MC;

public class GetMcNameForCommand : McCommand
{
    public override async Task Execute(MinecraftSocket socket, string arguments)
    {
        string name = await GetName(socket, arguments);
        socket.Dialog(db => db.Msg("This auction was sold by " + McColorCodes.AQUA + name));
    }

    public static async Task<string> GetName(MinecraftSocket socket, string arguments)
    {
        var uuid = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(arguments);
        var name = await socket.GetPlayerName(uuid);
        if (name == null)
            throw new Coflnet.Sky.Core.CoflnetException("name_not_found", "Could not retrieve the sellers name :(");
        return name;
    }
}