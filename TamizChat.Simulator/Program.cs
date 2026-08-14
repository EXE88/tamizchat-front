using TamizChat.Core;
using TamizChat.Core.Protocol;

// A fake user, so the client can be tested with more than one person in a room.
//
//   tamizsim check                     connect once, print what the server said, exit
//   tamizsim run  [name] [room]        stay connected and behave like somebody who is there
//
// Both take the server address from TAMIZSIM_SERVER, defaulting to localhost:8080.

var server = Environment.GetEnvironmentVariable("TAMIZSIM_SERVER") ?? "localhost:8080";
var mode = args.Length > 0 ? args[0] : "check";
var username = args.Length > 1 ? args[1] : "Sim";
var roomName = args.Length > 2 ? args[2] : "Lobby";

var httpUrl = $"http://{server}";
var wsUrl = $"ws://{server}/ws";

// One stable identity per name, so reconnecting looks like the same person
// coming back rather than a stranger arriving.
var clientUuid = StableUuid(username);

Console.WriteLine($"server   {server}");
Console.WriteLine($"identity {username} / {clientUuid}");

var info = await ServerProbe.TryGetAsync(httpUrl);
if (info is null)
{
    Console.Error.WriteLine("the server did not answer /api/v1/server-info — is it running?");
    return 1;
}

Console.WriteLine($"probe    {info.Name} v{info.SoftwareVersion} protocol {info.ProtocolVersion} " +
                  $"{info.OnlineUsers}/{info.MaxUsers} online");

await using var client = new TamizChatClient();
client.Disconnected += (_, reason) => Console.WriteLine($"[disconnected] {reason}");
client.ServerEvent += (_, e) => Report(e);

var welcome = await client.ConnectAsync(wsUrl, clientUuid, username);
Console.WriteLine($"welcome  as {welcome.You.Username}, {welcome.Rooms.Count} rooms, " +
                  $"{welcome.Users.Count} users online");

foreach (var room in welcome.Rooms)
{
    Console.WriteLine($"  room   {room.Name,-14} {room.MemberCount}/{room.Capacity}");
}

var target = welcome.Rooms.FirstOrDefault(r =>
    string.Equals(r.Name, roomName, StringComparison.OrdinalIgnoreCase)) ?? welcome.Rooms.FirstOrDefault();

if (target is null)
{
    Console.Error.WriteLine("the server has no rooms; create one from the admin panel");
    return 1;
}

var joined = TamizChatClient.Deserialize<RoomJoined>(
    await client.RequestAsync(MessageTypes.RoomJoin, new RoomJoinRequest { RoomId = target.Id }));
Console.WriteLine($"joined   {joined?.Room.Name} ({joined?.Room.MemberCount} inside)");

if (mode == "check")
{
    await client.SendAsync(MessageTypes.ChatSend, new ChatSend { Text = "hello from the simulator" });
    await Task.Delay(500);
    Console.WriteLine("check OK");
    return 0;
}

Console.WriteLine("running — press Ctrl+C to leave");
await client.SendAsync(MessageTypes.ChatSend, new ChatSend { Text = $"{username} joined from the simulator" });

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

// Say something now and then, so the client has live traffic to render.
var lines = new[]
{
    "still here",
    "anyone around?",
    "testing the room grid",
    "typing indicator check",
};

var index = 0;
try
{
    while (!stopping.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stopping.Token);
        await client.SendAsync(MessageTypes.ChatSend, new ChatSend { Text = lines[index++ % lines.Length] });
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C.
}

Console.WriteLine("leaving");
return 0;

static void Report(ServerEventArgs e)
{
    switch (e.Type)
    {
        case MessageTypes.ChatMessage:
            var message = e.As<ChatMessage>();
            Console.WriteLine($"[chat] {message?.Author.Username}: {message?.Text}");
            break;

        case MessageTypes.UserJoined:
            Console.WriteLine($"[join] {e.As<User>()?.Username}");
            break;

        case MessageTypes.UserLeft:
            Console.WriteLine($"[left] {e.As<User>()?.Username}");
            break;

        case MessageTypes.RoomMemberJoined or MessageTypes.RoomMemberLeft:
            Console.WriteLine($"[room] {e.Type}");
            break;

        case MessageTypes.ServerNotice:
            Console.WriteLine($"[notice] {e.Data}");
            break;
    }
}

// A name-derived UUID, so "Sim" is always the same client to the server.
static string StableUuid(string name)
{
    var hash = System.Security.Cryptography.MD5.HashData(
        System.Text.Encoding.UTF8.GetBytes("tamizchat-simulator:" + name));
    return new Guid(hash).ToString();
}
