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
//   tamizsim bots  [name]              walk the whole bot admin flow and check what came back
//   tamizsim seedbot [name]            leave one bot with a filled playlist behind, for screenshots
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

    // Every sound file the app ships, decoded the way the app will decode it.
    // A clip that fails to load is a broken build, and this is the cheapest
    // place to find that out.
    var soundsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
    if (Directory.Exists(soundsDir))
    {
        Console.WriteLine();
        Console.WriteLine("--- bundled sound files ---");

        foreach (var file in Directory.GetFiles(soundsDir).OrderBy(f => f))
        {
            try
            {
                var pcm = AudioClip.Load(file);
                var peak = pcm.Length == 0 ? 0 : pcm.Max(v => Math.Abs((int)v));
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-28} " +
                                  $"{pcm.Length / 48,6} ms   peak={peak,6}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-28} FAILED: {ex.Message}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("--- soundboard ---");

    foreach (var clip in Enum.GetValues<SoundEffect>())
    {
        var board = new Soundboard();
        board.Play(BuiltInClips.Render(clip));

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
    var peakRms = 0.0;
    var perIdentity = new Dictionary<string, double>();

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

        // The loudest frame in each second, which is what makes a soundboard
        // clip visible from here: a clip is far louder than room noise, so a
        // spike in this column is the clip actually arriving rather than just
        // the connection being up.
        var rms = Math.Sqrt(frame.Pcm.Select(v => (double)v * v).DefaultIfEmpty(0).Average());
        peakRms = Math.Max(peakRms, rms);

        // Per publisher, because "frames are arriving but they are silent" is
        // impossible to interpret without knowing whose frames they are: a
        // stale participant still publishing silence looks exactly like a live
        // one that has gone quiet.
        perIdentity[frame.Identity] = Math.Max(perIdentity.GetValueOrDefault(frame.Identity), rms);

        if (++received % 100 == 0)
        {
            var who = string.Join("  ", perIdentity.Select(kv => $"{kv.Key}={kv.Value:F0}"));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] audio    {received,5} frames   " +
                              $"loudest this second={peakRms,7:F0}   {who}");
            peakRms = 0;
            perIdentity.Clear();
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

if (mode == "seedbot")
{
    // Leaves a bot with a playlist and a few tracks on the server and exits, so
    // the client's Bots tab has something real to show. Nothing here is checked
    // — `bots` is the test; this is the fixture.
    // Idempotent: a fixture you cannot run twice is a fixture you stop using.
    var existing = TamizChatClient.Deserialize<BotListReply>(
        await client.RequestAsync(MessageTypes.BotList))!;

    foreach (var old in existing.Bots.Where(b => b.Name == "Radio"))
    {
        await client.RequestAsync(MessageTypes.BotDelete, new BotRef { BotId = old.Id });
        Console.WriteLine($"  removed the previous {old.Name}");
    }

    var bot = TamizChatClient.Deserialize<Bot>(await client.RequestAsync(
        MessageTypes.BotCreate,
        new BotSpec
        {
            Name = "Radio",
            Color = "#1abc9c",
            Loop = Environment.GetEnvironmentVariable("TAMIZSIM_ONE") is null,
        }))!;

    var list = TamizChatClient.Deserialize<BotPlaylist>(await client.RequestAsync(
        MessageTypes.BotPlaylistCreate,
        new BotPlaylistSpec { BotId = bot.Id, Name = "Evening set" }))!;

    // Real audio, and long enough to watch: Ingress runs the bytes through
    // GStreamer, so a text file with an .mp3 name is accepted by this server and
    // refused by the transcoder — which then looks like a bug in TamizChat when
    // it is not. A one-second clip is just as useless, because it is over before
    // anybody has subscribed.
    // The bundled ogg clips are about a second each, which is over before anyone
    // has subscribed. Chaining a clip end to end makes a file long enough to
    // watch; ogg is a chained format, so this is a legal stream rather than a
    // trick. A raw WAV is not an option: Ingress hands the bytes to GStreamer,
    // which rejects it with "input caps validation failed".
    // A long single-stream ogg if one is next to the executable, and the short
    // bundled clip otherwise. Chaining the short clip does not work: GStreamer
    // reaches end-of-stream at the first chain boundary, so the "30 second"
    // file played for one second — which looks exactly like a broken bot.
    //
    // TAMIZSIM_TONE, or tone.ogg beside the exe. To make one:
    //   docker run --name tonegen --entrypoint sh livekit/ingress:latest -c     //     "gst-launch-1.0 -q audiotestsrc num-buffers=3000 freq=330 ! audioconvert !     //      audioresample ! vorbisenc ! oggmux ! filesink location=/tmp/tone.ogg"
    //   docker cp tonegen:/tmp/tone.ogg .
    // tone.mp3 first, because a real downloaded track is what actually needs
    // testing: an mp3 without a Xing header has to have its frames counted, and
    // getting that wrong is silence rather than a visible failure.
    var tone = Environment.GetEnvironmentVariable("TAMIZSIM_TONE")
               ?? new[] { "tone.mp3", "tone.ogg" }
                   .Select(name => Path.Combine(AppContext.BaseDirectory, name))
                   .FirstOrDefault(File.Exists)
               ?? Path.Combine(AppContext.BaseDirectory, "tone.ogg");

    var sample = File.Exists(tone)
        ? tone
        : Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "you-joined-server.ogg");

    Console.WriteLine($"  source   {Path.GetFileName(sample)}");

    // One track when TAMIZSIM_ONE is set: with loop off and a single file, the
    // queue cannot advance, so one ingress can be watched from start to finish
    // without our own "next track" replacing it.
    var titles = Environment.GetEnvironmentVariable("TAMIZSIM_ONE") is null
        ? new[] { "01 opening.ogg", "02 middle eight.ogg", "03 closing.ogg" }
        : ["01 opening.ogg"];

    // The client converts on the way up, so the simulator does the same thing
    // with the same code — otherwise this fixture would be testing a path no
    // real user takes.
    if (!TamizChat.Audio.OpusFile.IsOpus(sample))
    {
        var converted = Path.Combine(Path.GetTempPath(), $"tamizsim-{Guid.NewGuid():N}.ogg");
        Console.WriteLine($"  converting {Path.GetFileName(sample)} to Opus…");
        await TamizChat.Audio.OpusFile.ConvertAsync(sample, converted);
        sample = converted;
    }

    var extension = Path.GetExtension(sample);

    foreach (var title in titles.Select(t => Path.ChangeExtension(t, extension)))
    {
        var temp = Path.Combine(Path.GetTempPath(), $"tamizsim-{Guid.NewGuid():N}{extension}");
        File.Copy(sample, temp, overwrite: true);

        var ticket = TamizChatClient.Deserialize<BotTrackUploadTicket>(await client.RequestAsync(
            MessageTypes.BotTrackUploadRequest,
            new BotTrackUploadRequest
            {
                BotId = bot.Id,
                PlaylistId = list.Id,
                Name = title,
                Size = new FileInfo(temp).Length,
            }))!;

        using (var http = new HttpClient())
        using (var body = new StreamContent(File.OpenRead(temp)))
        using (var post = new HttpRequestMessage(HttpMethod.Post, $"{httpUrl}{ticket.Url}") { Content = body })
        {
            post.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ticket.Token);
            using var response = await http.SendAsync(post);
            Console.WriteLine($"  {title,-24} {(int)response.StatusCode}");
        }

        File.Delete(temp);
    }

    await client.RequestAsync(
        MessageTypes.BotPlaylistSelect,
        new BotPlaylistSpec { BotId = bot.Id, PlaylistId = list.Id });

    // Put it in a room and start it, so the whole path — Ingress fetching the
    // track from this server and publishing it — is exercised, not just the
    // database rows.
    var moved = TamizChatClient.Deserialize<Bot>(await client.RequestAsync(
        MessageTypes.BotMove, new BotMove { BotId = bot.Id, RoomId = target.Id }))!;
    Console.WriteLine($"  moved to {target.Name}: state={moved.State}");

    try
    {
        var playing = TamizChatClient.Deserialize<Bot>(await client.RequestAsync(
            MessageTypes.BotControl, new BotControl { BotId = bot.Id, Action = "play" }))!;
        Console.WriteLine($"  play: state={playing.State} track={playing.Track?.Title}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  play FAILED: {ex.Message}");
    }

    Console.WriteLine($"seeded {bot.Name} with {list.Name}");
    return 0;
}

if (mode == "bots")
{
    // The whole administrator flow for bots, end to end against the real
    // server: create one, give it a playlist, upload a track into it, switch to
    // it, look at the queue, then take it all apart again. Every step checks
    // what came back rather than only that nothing threw — a server that
    // accepted the frame and did nothing would otherwise look like success.
    //
    // Needs a client_uuid holding manage_bots; the identity line above prints
    // which one this name maps to.
    var failures = 0;

    void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {what,-34} {detail}");
        if (!ok)
        {
            failures++;
        }
    }

    var botName = $"Sim DJ {DateTime.Now:HHmmss}";

    var created = TamizChatClient.Deserialize<Bot>(await client.RequestAsync(
        MessageTypes.BotCreate,
        new BotSpec { Name = botName, Color = "#f39c12", Shuffle = true }))!;

    Check("bot created", created.Id.Length > 0 && created.Name == botName, created.Id);
    Check("starts idle and empty",
        created.State == "idle" && created.TrackCount == 0 && created.PlaylistId.Length == 0);
    Check("spec applied", created.Shuffle && created.Color == "#f39c12");

    var playlist = TamizChatClient.Deserialize<BotPlaylist>(await client.RequestAsync(
        MessageTypes.BotPlaylistCreate,
        new BotPlaylistSpec { BotId = created.Id, Name = "Simulator set" }))!;

    Check("playlist created", playlist.Id.Length > 0 && playlist.Name == "Simulator set", playlist.Id);

    // A file that is not audio has to be refused before any bytes move.
    var refused = "";
    try
    {
        await client.RequestAsync(
            MessageTypes.BotTrackUploadRequest,
            new BotTrackUploadRequest { BotId = created.Id, PlaylistId = playlist.Id, Name = "notes.txt" });
    }
    catch (Exception ex)
    {
        refused = ex.Message;
    }

    Check("a .txt is refused", refused.Length > 0, refused);

    var temp = Path.Combine(Path.GetTempPath(), $"tamizsim-{Guid.NewGuid():N}.mp3");
    await File.WriteAllTextAsync(temp, "not really an mp3, but the server never decodes it\n");

    var ticket = TamizChatClient.Deserialize<BotTrackUploadTicket>(await client.RequestAsync(
        MessageTypes.BotTrackUploadRequest,
        new BotTrackUploadRequest
        {
            BotId = created.Id,
            PlaylistId = playlist.Id,
            Name = "simulator track.mp3",
            Size = new FileInfo(temp).Length,
        }))!;

    Bot? uploaded = null;
    {
        using var http = new HttpClient();
        using var body = new StreamContent(File.OpenRead(temp));
        using var post = new HttpRequestMessage(HttpMethod.Post, $"{httpUrl}{ticket.Url}") { Content = body };
        post.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ticket.Token);

        using var response = await http.SendAsync(post);
        var replyBody = await response.Content.ReadAsStringAsync();
        Check("track uploaded", response.IsSuccessStatusCode,
            $"{(int)response.StatusCode} {(response.IsSuccessStatusCode ? "" : replyBody)}");
        if (response.IsSuccessStatusCode)
        {
            // The HTTP reply is the bot itself, not an envelope — the upload
            // never went through the socket.
            uploaded = System.Text.Json.JsonSerializer.Deserialize<Bot>(replyBody);
        }
    }

    File.Delete(temp);

    // The bot is still on its own library, so the queue must not have changed.
    Check("unselected playlist leaves the queue alone", uploaded?.TrackCount == 0,
        $"track_count={uploaded?.TrackCount}");

    var listed = TamizChatClient.Deserialize<BotPlaylistList>(await client.RequestAsync(
        MessageTypes.BotPlaylistList, new BotRequest { BotId = created.Id }))!;

    Check("playlist holds the track",
        listed.Playlists.Count == 1 && listed.Playlists[0].TrackCount == 1,
        $"{listed.Playlists.Count} playlists");

    var tracks = TamizChatClient.Deserialize<BotQueueReply>(await client.RequestAsync(
        MessageTypes.BotQueue, new BotRequest { BotId = created.Id, PlaylistId = playlist.Id }))!;

    Check("an unplayed playlist can be read",
        tracks.Tracks.Count == 1 && tracks.Tracks[0].Title == "simulator track",
        tracks.Tracks.Count > 0 ? tracks.Tracks[0].Title : "");

    var selected = TamizChatClient.Deserialize<Bot>(await client.RequestAsync(
        MessageTypes.BotPlaylistSelect,
        new BotPlaylistSpec { BotId = created.Id, PlaylistId = playlist.Id }))!;

    Check("playlist selected",
        selected.PlaylistId == playlist.Id && selected.PlaylistName == "Simulator set",
        selected.PlaylistName);
    Check("queue follows the playlist", selected.TrackCount == 1, $"track_count={selected.TrackCount}");

    var emptied = TamizChatClient.Deserialize<Bot>(await client.RequestAsync(
        MessageTypes.BotTrackDelete,
        new BotTrackRef { BotId = created.Id, PlaylistId = playlist.Id, Index = 0 }))!;

    Check("track deleted", emptied.TrackCount == 0, $"track_count={emptied.TrackCount}");

    await client.RequestAsync(
        MessageTypes.BotPlaylistDelete,
        new BotPlaylistSpec { BotId = created.Id, PlaylistId = playlist.Id });

    var afterDelete = TamizChatClient.Deserialize<BotPlaylistList>(await client.RequestAsync(
        MessageTypes.BotPlaylistList, new BotRequest { BotId = created.Id }))!;

    Check("playlist gone and bot back on its library",
        afterDelete.Playlists.Count == 0 && afterDelete.Active.Length == 0);

    await client.RequestAsync(MessageTypes.BotDelete, new BotRef { BotId = created.Id });

    var remaining = TamizChatClient.Deserialize<BotListReply>(
        await client.RequestAsync(MessageTypes.BotList))!;

    Check("bot deleted", remaining.Bots.All(b => b.Id != created.Id),
        $"{remaining.Bots.Count} bots left");

    Console.WriteLine(failures == 0 ? "bots OK" : $"bots FAILED ({failures})");
    return failures == 0 ? 0 : 1;
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
// WriteTone writes a mono 48 kHz WAV of a steady tone. A generated file keeps
// the fixture self-contained: no asset to ship, any length, and a pitch per
// track so which one is playing can be heard.
static void WriteTone(string path, int hz, int seconds)
{
    const int rate = 48000;
    var samples = rate * seconds;
    var data = new byte[samples * 2];

    for (var i = 0; i < samples; i++)
    {
        var value = (short)(Math.Sin(2 * Math.PI * hz * i / rate) * 8000);
        data[i * 2] = (byte)(value & 0xff);
        data[(i * 2) + 1] = (byte)((value >> 8) & 0xff);
    }

    using var file = new BinaryWriter(File.Create(path));
    file.Write("RIFF"u8.ToArray());
    file.Write(36 + data.Length);
    file.Write("WAVE"u8.ToArray());
    file.Write("fmt "u8.ToArray());
    file.Write(16);            // PCM header size
    file.Write((short)1);      // PCM
    file.Write((short)1);      // mono
    file.Write(rate);
    file.Write(rate * 2);      // byte rate
    file.Write((short)2);      // block align
    file.Write((short)16);     // bits per sample
    file.Write("data"u8.ToArray());
    file.Write(data.Length);
    file.Write(data);
}

static string StableUuid(string name)
{
    var hash = System.Security.Cryptography.MD5.HashData(
        System.Text.Encoding.UTF8.GetBytes("tamizchat-simulator:" + name));
    return new Guid(hash).ToString();
}
