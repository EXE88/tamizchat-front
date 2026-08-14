using TamizChat.Core;
using TamizChat.Core.Protocol;

// A fake user, so the client can be tested with more than one person in a room.
//
//   tamizsim check                     connect once, print what the server said, exit
//   tamizsim run   [name] [room]       stay connected and behave like somebody who is there
//   tamizsim paint [name] [room]       draw one streamed stroke on the room's board
//   tamizsim file  [name] [room]       upload a small file into the room
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

if (mode == "paint")
{
    // Draws a diagonal in normalized coordinates, streamed the way a real
    // client would: begin, a few appends, end.
    var begin = await client.RequestAsync(MessageTypes.PaintBegin, new PaintBegin
    {
        Tool = "pen",
        Color = "#E5484D",
        Width = 0.006,
        Points = [new PaintPoint { X = 0.1, Y = 0.1 }],
    });

    var drawn = TamizChatClient.Deserialize<Stroke>(begin);
    Console.WriteLine($"stroke   {drawn?.Id}");

    for (var i = 1; i <= 20; i++)
    {
        await client.SendAsync(MessageTypes.PaintAppend, new PaintAppend
        {
            StrokeId = drawn!.Id,
            Points = [new PaintPoint { X = 0.1 + (i * 0.04), Y = 0.1 + (i * 0.03) }],
        });
        await Task.Delay(40);
    }

    // No reply on success, so this is sent rather than requested.
    await client.SendAsync(MessageTypes.PaintEnd, new PaintEnd { StrokeId = drawn!.Id });

    var state = TamizChatClient.Deserialize<PaintState>(
        await client.RequestAsync(MessageTypes.PaintState));
    Console.WriteLine($"board    {state?.Strokes.Count}/{state?.MaxStrokes} strokes, " +
                      $"{state?.Strokes.FirstOrDefault()?.Points.Count} points in the first");
    Console.WriteLine("paint OK");
    return 0;
}

if (mode == "undo")
{
    // Proves the shape the client depends on: the server answers the caller with
    // an id-correlated reply and excludes them from the broadcast, so the reply
    // is the only place the undone stroke id ever appears.
    var started = TamizChatClient.Deserialize<Stroke>(await client.RequestAsync(
        MessageTypes.PaintBegin,
        new PaintBegin
        {
            Tool = "pen",
            Color = "#3E9BFF",
            Width = 0.005,
            Points = [new PaintPoint { X = 0.2, Y = 0.8 }, new PaintPoint { X = 0.8, Y = 0.2 }],
        }));

    await client.SendAsync(MessageTypes.PaintEnd, new PaintEnd { StrokeId = started!.Id });
    await Task.Delay(200);

    var before = TamizChatClient.Deserialize<PaintState>(
        await client.RequestAsync(MessageTypes.PaintState));
    Console.WriteLine($"before   strokes={before?.Strokes.Count}");

    var undoReply = await client.RequestAsync(MessageTypes.PaintUndo);
    var undone = TamizChatClient.Deserialize<PaintUndo>(undoReply);
    Console.WriteLine($"reply    type carries stroke_id={!string.IsNullOrEmpty(undone?.StrokeId)} " +
                      $"({undone?.StrokeId})");

    var after = TamizChatClient.Deserialize<PaintState>(
        await client.RequestAsync(MessageTypes.PaintState));
    Console.WriteLine($"after    strokes={after?.Strokes.Count}");

    var ok = undone is not null
             && !string.IsNullOrEmpty(undone.StrokeId)
             && undone.StrokeId == started.Id
             && after?.Strokes.Count == before?.Strokes.Count - 1;

    Console.WriteLine(ok ? "undo OK" : "undo FAILED");
    return ok ? 0 : 1;
}

if (mode == "board")
{
    // Reports what is on the room's board and leaves. Used to measure when the
    // server actually purges a room's content.
    var state = TamizChatClient.Deserialize<PaintState>(
        await client.RequestAsync(MessageTypes.PaintState));

    Console.WriteLine($"BOARD {DateTime.Now:HH:mm:ss} strokes={state?.Strokes.Count}");
    return 0;
}

if (mode == "file")
{
    // Permission over the socket, bytes over HTTP — the same two-step every
    // real client uses.
    var temp = Path.Combine(Path.GetTempPath(), $"tamizsim-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(temp, "a file from the simulator\n");
    var upload = new FileInfo(temp);

    var ticketReply = await client.RequestAsync(
        MessageTypes.FileUploadRequest,
        new FileUploadRequest { Name = "simulator-note.txt", Size = upload.Length });

    var ticket = TamizChatClient.Deserialize<FileUploadTicket>(ticketReply)!;
    Console.WriteLine($"ticket   {ticket.UploadId} -> {ticket.Url}");

    bool ok;
    // Scoped so the request — and the file handle inside it — is disposed before
    // the temporary file is deleted.
    {
        using var http = new HttpClient();
        using var body = new StreamContent(File.OpenRead(temp));
        using var post = new HttpRequestMessage(HttpMethod.Post, $"{httpUrl}{ticket.Url}") { Content = body };
        post.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ticket.Token);

        using var uploaded = await http.SendAsync(post);
        ok = uploaded.IsSuccessStatusCode;
        Console.WriteLine($"upload   {(int)uploaded.StatusCode} {uploaded.StatusCode}");
    }

    File.Delete(temp);
    await Task.Delay(800);
    Console.WriteLine(ok ? "file OK" : "file FAILED");
    return ok ? 0 : 1;
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
