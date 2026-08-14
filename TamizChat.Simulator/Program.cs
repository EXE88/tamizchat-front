using TamizChat.Core;
using TamizChat.Audio;
using TamizChat.Core.Media;
using TamizChat.Core.Protocol;

// A fake user, so the client can be tested with more than one person in a room.
//
//   tamizsim check                     connect once, print what the server said, exit
//   tamizsim fx                        run the voice changer and soundboard offline and measure them
//   tamizsim run   [name] [room]       stay connected and behave like somebody who is there
//   tamizsim paint [name] [room]       draw one streamed stroke on the room's board
//   tamizsim file  [name] [room]       upload a small file into the room
//   tamizsim talk  [name] [room]       join the room's voice and publish a tone
//   tamizsim listen [name] [room]      join the voice and PLAY what arrives, to test your mic
//   tamizsim video [name] [room]       publish a moving test pattern, to test video with no webcam
//
// Both take the server address from TAMIZSIM_SERVER, defaulting to localhost:8080.

var server = Environment.GetEnvironmentVariable("TAMIZSIM_SERVER") ?? "localhost:8080";
var mode = args.Length > 0 ? args[0] : "check";
var username = args.Length > 1 ? args[1] : "Sim";
var roomName = args.Length > 2 ? args[2] : "Lobby";

if (mode == "fx")
{
    // Runs the voice changer and the soundboard offline and prints what they
    // produced. No microphone, no server, nobody listening — the point is that
    // "does the DSP work" gets a number rather than an opinion.
    Console.WriteLine("--- voice changer, on a 200 Hz tone ---");

    foreach (var kind in Enum.GetValues<VoiceEffectKind>())
    {
        var effect = new VoiceEffect();
        effect.SetKind(kind);

        var processed = new List<short>();
        var phase = 0.0;

        for (var f = 0; f < 60; f++)
        {
            var frame = new short[480];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (short)(Math.Sin(phase) * 8000);
                phase += 2 * Math.PI * 200 / 48000;
            }

            effect.Process(frame);
            processed.AddRange(frame);
        }

        // The second half only, so the ring buffer has settled.
        var tail = processed.Skip(processed.Count / 2).ToArray();
        var crossings = 0;
        for (var i = 1; i < tail.Length; i++)
        {
            if (tail[i - 1] < 0 != tail[i] < 0)
            {
                crossings++;
            }
        }

        var hz = crossings / 2.0 / (tail.Length / 48000.0);
        var rms = Math.Sqrt(tail.Select(v => (double)v * v).Average());
        Console.WriteLine($"{kind,-10} dominant={hz,6:F0} Hz   rms={rms,7:F0}");
    }

    Console.WriteLine();
    Console.WriteLine("--- soundboard ---");

    foreach (var clip in Enum.GetValues<SoundEffect>())
    {
        var board = new Soundboard();
        board.Play(clip);

        var frames = 0;
        var peak = 0;

        while (board.IsPlaying && frames < 2000)
        {
            var frame = new short[480];
            board.MixInto(frame);
            peak = Math.Max(peak, frame.Max(v => Math.Abs((int)v)));
            frames++;
        }

        Console.WriteLine($"{clip,-10} {frames * 10,5} ms   peak={peak,6}");
    }

    return 0;
}

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

if (mode is "talk" or "listen" or "video")
{
    // Voice, through the same MediaSession the app uses. `talk` is a fake
    // speaker to test the client against; `listen` proves audio really arrives,
    // which is not the same thing as the connection being up.
    var credentials = TamizChatClient.Deserialize<MediaToken>(
        await client.RequestAsync(MessageTypes.MediaToken));

    if (credentials is null || string.IsNullOrEmpty(credentials.Token))
    {
        Console.Error.WriteLine("no media token — is livekit.enabled on, and configured?");
        return 1;
    }

    Console.WriteLine($"media    {credentials.Url} room={credentials.Room} speak={credentials.CanSpeak}");

    await using var media = new MediaSession();
    var received = 0;

    // `listen` plays what it hears out of the speakers, which is how you test
    // your own microphone: talk in the app and hear yourself come back through
    // the round trip. Beware the obvious — this is a real loop, so use a headset
    // or the two will howl at each other.
    using var speakers = new SpeakerPlayback();
    if (mode == "listen")
    {
        speakers.Start();
        Console.WriteLine("audio    playing what arrives through the default speakers");
    }

    media.FrameReceived += (_, frame) =>
    {
        if (mode == "listen")
        {
            speakers.Submit(frame.Identity, frame.Pcm);
        }

        // Only the first of each burst is worth a line; this is 100 a second.
        if (received++ % 100 == 0)
        {
            Console.WriteLine($"audio    {received} frames, latest from {frame.Identity}");
        }
    };

    var videoFrames = 0;
    media.VideoFrameReceived += (_, frame) =>
    {
        if (videoFrames++ % 60 == 0)
        {
            Console.WriteLine($"video    {videoFrames} frames, {frame.Width}x{frame.Height} " +
                              $"{frame.Kind} from {frame.Identity}");
        }
    };

    media.SpeakersChanged += (_, talking) =>
        Console.WriteLine($"speaking {(talking.Count == 0 ? "(nobody)" : string.Join(", ", talking))}");

    await media.ConnectAsync(credentials);
    Console.WriteLine("media    connected");

    var seconds = int.TryParse(Environment.GetEnvironmentVariable("TAMIZSIM_SECONDS"), out var s) ? s : 30;

    if (mode == "video")
    {
        // A moving test pattern instead of a camera: it proves the whole video
        // path — publish, encode, decode, render — on a machine with no webcam,
        // and a moving picture is the only way to tell a live feed from a frozen
        // one at a glance.
        const int width = 640;
        const int height = 360;

        await media.StartVideoAsync(VideoKind.Camera, width, height);
        Console.WriteLine($"video    publishing a {width}x{height} test pattern for {seconds}s");

        var pixels = new byte[width * height * 4];
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var frameNumber = 0;

        while (DateTime.UtcNow < until)
        {
            // Colour bars that slide sideways, plus a bright block that marches
            // across so a frozen frame is obvious.
            var offset = frameNumber * 4 % width;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = ((y * width) + x) * 4;
                    var band = ((x + offset) % width) * 6 / width;

                    pixels[i + 0] = (byte)((band & 1) != 0 ? 220 : 30); // blue
                    pixels[i + 1] = (byte)((band & 2) != 0 ? 220 : 30); // green
                    pixels[i + 2] = (byte)((band & 4) != 0 ? 220 : 30); // red
                    pixels[i + 3] = 255;
                }
            }

            var blockX = offset;
            for (var y = height / 2; y < (height / 2) + 40; y++)
            {
                for (var x = blockX; x < blockX + 40 && x < width; x++)
                {
                    var i = ((y * width) + x) * 4;
                    pixels[i + 0] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                }
            }

            media.SendVideoFrame(VideoKind.Camera, pixels, width, height);
            frameNumber++;
            await Task.Delay(66); // about 15 a second
        }

        Console.WriteLine($"video    sent {frameNumber} frames");
        return 0;
    }

    if (mode == "talk")
    {
        await media.StartPublishingAsync();
        media.SetMuted(false);
        await client.SendAsync(MessageTypes.MediaSetState, new MediaSetState { Mic = true });
        Console.WriteLine($"media    publishing a 440 Hz tone for {seconds}s");

        // 10 ms at a time, paced in real time, because LiveKit's queue is not a
        // place to dump a minute of audio at once.
        var phase = 0.0;
        var frame = new short[MediaSession.SamplesPer10Ms];
        var until = DateTime.UtcNow.AddSeconds(seconds);

        while (DateTime.UtcNow < until)
        {
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (short)(Math.Sin(phase) * 8000);
                phase += 2 * Math.PI * 440 / MediaSession.SampleRate;
                if (phase > 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }
            }

            media.SendCapturedFrame(frame);
            await Task.Delay(10);
        }
    }
    else
    {
        Console.WriteLine($"media    listening for {seconds}s");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"media    received {received} audio frames, {videoFrames} video frames");
        return received > 0 || videoFrames > 0 ? 0 : 1;
    }

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
