# link-cs

A **C#** implementation of the **rewrite-bridge passthrough link protocol v1** (contract revision 5), so that a
game mod written for .NET or Unity (BepInEx, MelonLoader, a Valheim or Unturned mod ...) can be a **host** or a
**guest** next to any other game or rewrite that speaks it. The protocol is defined by `docs/CONTRACT.md` in the
rewrite-bridge repository; this library is one implementation of it, next to the Rust crate,
[link-java](https://github.com/patodois/link-java) and [link-c](https://github.com/patodois/link-c), and they talk to
each other (see "Interop"). The API has the shape of link-java's `Link`.

- `netstandard2.0`, so it loads in .NET Framework 4.6.1 and later, Mono, .NET and Unity's .NET Standard 2.0 profile.
  C# 7.3 language level, **no dependencies beyond the BCL**: sockets, `MemoryMappedFile`, `Volatile`, `Thread.MemoryBarrier`.
- Everything binds and connects to `127.0.0.1` only. No threads are created: you call `Poll()`.
- The only `unsafe` code is in `PixelFile` (a pointer into the mapped file, for the seqlock).
- Licence: MIT OR Apache-2.0.

## A host

```csharp
using Patodois.RewriteLink;

var host = Link.Host(new LinkConfig("My Game"));          // listens on 127.0.0.1:47800
var events = new List<LinkEvent>();
while (running)                                           // your frame loop
{
    events.Clear();
    host.Poll(events);                                    // never blocks; sends the heartbeats
    foreach (var e in events)
        if (e is LinkEvent.Poses poses) Apply(poses.Values);
        else if (e is LinkEvent.Connected c) Console.WriteLine("guest: " + c.Peer.Game);
    if (host.IsConnected)
        host.SendCamera(Wire.Camera.LookAt(eye, target, 1.0f, 0.1f, 1000f, 1280, 720));   // once per rendered frame
}
host.Close();
```

## A guest

```csharp
var guest = Link.Guest(new LinkConfig("My Rewrite"));     // connects to 127.0.0.1:47800, retrying
while (running)
{
    events.Clear();
    guest.Poll(events);
    foreach (var e in events)
    {
        if (e is LinkEvent.Connected) guest.Spawn(1, "My Rewrite", "demo/cube", "Cube");
        else if (e is LinkEvent.Camera cam)               // the host's view, once per frame
        {
            var pose = Wire.Pose.Identity(1, Wire.NowUs());
            pose.Translation[1] = 1f;                     // metres, +Y up, in the host's world
            guest.SendPoses(new[] { pose });
        }
    }
}
guest.Close();
```

`demo/LinkDemo` is the longer one, the C# twin of link-java's `LinkDemo`: `dotnet LinkDemo.dll host|guest [--port N]
[--seconds S] [--frames] [--orbit] [--big-collision N]`.

## The API

| | |
|---|---|
| `Link.Host(cfg)`, `Link.Guest(cfg)` | Create one side. `LinkConfig` has `Game`, `ControlPort` (47800), `UdpPort`, `HeartbeatMs` (250, clamped to 10..10000), `TimeoutMs` (2000), `ReconnectMs`, `MaxCollisionTriangles`. |
| `Poll()`, `Poll(List<LinkEvent>)` | **Never blocks.** Handshake, reads both channels, heartbeats, at most 32 collision chunks, the watchdog. The second form appends to your list, so a frame loop allocates no list. |
| `SendCamera`, `SendPoses`, `SendCollision`, `SendFrame` | State datagrams. `SendCollision` takes 9 floats per triangle, copies them and queues the chunks (paced; a newer snapshot replaces the queued one); more than `Wire.MaxCollisionChunks` (4096) chunks, 925696 triangles, throws `ArgumentException` before anything is copied. `SendCamera` returns the `seq` a guest quotes in `FrameReady.CameraSeq`. |
| `Spawn`, `Despawn`, `SetFocus`, `AnnounceFrames` | Control messages. |
| `Disconnect(reason)`, `Close()` / `Dispose()` | `bye`, half-close, drain (CONTRACT 6.1). `Disconnect` does not block; `Close` may block for up to about 250 ms. |
| `IsConnected`, `Peer`, `Focus`, `ControlPort`, `UdpPort`, `Malformed`, `Stale`, `Foreign`, `UnknownEntity`, `SpawnIgnored`, `SendDropped`, `DatagramsSent/Received`, `LinesSent/Received` | State and statistics. |
| `PixelFile.Writer`, `PixelFile.Reader` | The pixel file with the seqlock, colour and the optional linear depth plane. Read a `Frame` datagram with `reader.Read(frameReady, wantDepth)`, which checks its width and height against the file and the slot header. |
| `Wire`, `Control`, `Quat` | The codecs and small helpers in the link's conventions (right-handed, +Y up, +Z front). |

Events are `LinkEvent.Connected`, `Lost`, `HandshakeFailed`, `Camera`, `Poses`, `Collision`, `Frame`, `Spawn`, `Despawn`,
`Focus`, `FrameChannel`. Every public method takes the link's lock, so a game thread can send while a helper thread polls.
Sending without a session throws `NotConnectedException`; a value the contract forbids throws `WireException`.

## Contract revision 5 (and 4)

Revision 5 keeps wire compatibility with revision 4 (protocol v1) and only makes the validation stricter; version 0.1.2 of the library follows it:

- a `\u0000` escape is an error in any string or key of a control line, wherever in the line it is (an unknown field, a nested value, a value
  that a later duplicate key overrides); a string handed to the control writer (`game`, `provider`, `asset`, `name`, a reason, a path) has
  U+0000 replaced by U+FFFD;
- a collision snapshot has at most `Wire.MaxCollisionChunks` = 4096 chunks (925696 triangles): a `chunk_count` above it makes the datagram
  malformed, `Wire.Encode` refuses it, and `Link.SendCollision` and `Wire.Chunks` refuse a bigger snapshot before they copy or allocate for it;
- the pixel file reader skips a `Frame` whose datagram width or height is 0 or above the file's, or whose slot header has a width or height of
  0, above the file's or different from the datagram's, before it copies a pixel (`Reader.Read(FrameReady, bool)` and
  `Reader.Read(frame, slot, width, height, wantDepth)`; the older `Read(frame, slot, wantDepth)` is kept so that existing programs still build, but it cannot compare with the datagram, checks the
  slot header against the file only and is **not conformant** to contract section 9.6). Frame 0 is always skipped. The slot is found by the number the datagram names; it does not have to be `frame % slots`;
- already the case and now written down in the contract: the last of a repeated key wins, a `null` optional field is absent, a `bye` without a
  reason is valid (its `Reason` is `""`), `protocol` and `min_protocol` are `u16`, the decoder delivers the values of a known kind as sent.

Revision 4, all of it implemented, each rule with a test that fails without it:

- paced collision, at most 32 chunks per `Poll` (fewer if the OS granted a small receive buffer: Linux keeps at most
  `net.core.rmem_max`), a newer snapshot replaces queued chunks; a 4 MiB UDP receive buffer is requested;
- a new `frame_channel` resets the frame staleness; datagrams are accepted only from `127.0.0.1:<peer port>`;
- poses only for spawned entities, at most 1024 live ones (`UnknownEntity`, `SpawnIgnored`);
- `heartbeat_ms` 10 to 10000: out of range in a `hello` is a handshake error, in your own config it is clamped;
- the pixel file reader: an absolute path to a regular file, width and height at most 16384, at most 2^26 pixels,
  `stride == width * 4`, checked arithmetic for every size, a length check against the mapping **before every read**; the
  writer: created with `FileMode.CreateNew`, never resized or truncated, the same limits, deleted at the end;
- control lines of at most 65536 bytes, strict UTF-8, and a JSON reader that accepts exactly what Rust's `serde_json`
  accepts (the same corpus as link-java and link-c; number ranges are decided on the digits, so the answer does not depend on
  the runtime's `double.Parse`);
- after `bye`: half-close, drain for at most 200 ms, close; a received `bye` wins over the reset that follows it.

What .NET cannot do, and what is done instead:

- **Regular file check.** There is no `lstat`/`fstat` and no non-blocking open in `netstandard2.0`. The reader refuses a symlink or
  directory by its attributes and a FIFO, device or socket by its size (0, where a pixel file needs at least 64 bytes), all before
  opening the file, so opening something that waits for a writer is never tried. A peer that swaps the file between the check and the
  open can still get through, as with link-java; the contract asks for `O_NONBLOCK` and a device/inode comparison where there are any.
- **Seqlock ordering** is explained in the comment at the top of `PixelFile.cs`: `Volatile.Read` (acquire) and `Volatile.Write`
  (release), and `Thread.MemoryBarrier()` (a full fence, stronger than the contract's acquire and release fences) where the contract has a
  fence. 64-bit processes only (a 64-bit access to the lock word is atomic there); `PixelFile` throws on a big-endian machine.

Choices that the contract leaves open, made as in link-c: a collision snapshot of more than `MaxCollisionTriangles` (2097152, above what 4096
chunks can hold, so only a lower value of your own ever applies) is dropped and counted as malformed; a `hello` with `udp_port` 0 is rejected; one line goes to `Console.Error` per session when a peer
overflows the entity table.

## Loading it in BepInEx or MelonLoader

BepInEx and MelonLoader load managed mods into Unity games, and a mod is a class library. What I know and what follows from it;
**none of this has been run inside either of them**:

1. Reference `RewriteLink.dll` from your mod project (the file from `src/RewriteLink/bin/Release/netstandard2.0/`), and ship it next to
   your mod's DLL (for BepInEx in your plugin's folder under `BepInEx/plugins`; for MelonLoader check whether it wants it in `Mods` or
   `UserLibs`): the loader has to find it when your mod needs it.
2. Create the `Link` in your mod's start-up code, and call `Poll(List<LinkEvent>)` from the code that runs once per frame on the main
   thread (a BepInEx plugin's `Update()`, a MelonLoader mod's `OnUpdate()`). Poll never blocks, so it is safe there; the library starts no
   thread. Reuse the list you pass in.
3. Call `Close()` when the game quits (it can block for about 250 ms), not from a per-frame path.
4. Convert the game's coordinates to the link's (right-handed, +Y up, +Z front, metres) at the boundary, as CONTRACT.md section 4
   describes. Unity is left-handed with +Y up: the conversion is your mod's job, and the contract's example shows how to write one down.
5. `PixelFile` needs `System.IO.MemoryMappedFiles`, which `netstandard2.0` has, but I have not checked that every Unity runtime and API
   profile ships it. The sockets and control channel do not use it, so a mod that only needs poses, cameras and collision works without.
   The assembly contains `unsafe` code (verification is off in Mono's default setup; IL2CPP games are untested).

## Build and test

```
dotnet build RewriteLink.sln -c Release
dotnet run --project tests/RewriteLink.Tests -c Release       # a plain console runner: exits 1 if a test fails
```

The tests are the C# port of link-java's and link-c's: the worked example of CONTRACT.md section 11 byte for byte, the codecs, a fuzz of
malformed datagrams (20000 random buffers, every prefix of every valid one, 20000 mutations: only `WireException` may come out) and of
100000 mutated control lines, 30000 junk datagrams at a live host, the JSON corpus, live sessions over loopback, every revision 4 and 5 case, and
the seqlock under contention (a writer thread lapping a two-slot file against a reader thread; the same copy without the lock check does
tear, and the seqlock reader accepts no torn frame), and the shared test vectors (see below).

## Shared test vectors

`tests/vectors/link/` holds the shared test vectors of the link protocol: control lines, datagrams, pixel files and scripted sessions
over real sockets, each with the section of CONTRACT.md it comes from. They are derived from the contract alone and every implementation
(Rust, Java, C and this one) runs the same files; `tests/vectors/link/README.md` describes the formats and `GAPS.md` what the contract
leaves open. The directory is a **copy** of `tests/vectors/link` in the rewrite-bridge repository, without its generator, pinned by digest:
`tests/RewriteLink.Tests/VectorTests.cs` checks every file against `SHA256SUMS`, that no other file is there, and that the SHA-256 of
`SHA256SUMS` equals its constant `VectorsDigest`. The copy is marked `-text` in `.gitattributes`, so no line-ending conversion touches it.

The runner runs with the other tests (`dotnet run --project tests/RewriteLink.Tests -c Release`). It lists all failing vectors together, with
id, section and basis. `RBL_VECTORS_DIR` points it at another copy, `RBL_VECTORS_SKIP_LARGE=1` skips the cases that need a sparse file of up
to 1 GiB (also useful on a filesystem without sparse files). Vectors this library is known to fail are in `KnownDisagreements` in
`VectorTests.cs`, each with the reason: a known vector is reported but does not fail the run, and one that starts to pass does, so the list
cannot go stale.

To update the vectors, run `scripts/sync-link-vectors.sh <path to this repository>` in the rewrite-bridge repository. It replaces
`tests/vectors/link/`, makes sure `.gitattributes` has the `-text` line and prints `VECTORS_DIGEST = ...`; put that value in `VectorsDigest`,
then run the tests and deal with new or changed vectors (a failing one either gets fixed or goes into `KnownDisagreements` with its reason).

## Interop

`tests/RewriteLink.InteropPeer` is this library's side of the scripted cross-language session of rewrite-bridge (`tests/interop/README.md`
there): one host and one guest process run handshake, spawn and focus, five lock-step frames with camera, poses, a collision snapshot and a
frame with depth through a real pixel file, then despawn and bye, and each prints a result line that the driver there compares. After
`dotnet build RewriteLink.sln -c Release`:

```
dotnet tests/RewriteLink.InteropPeer/bin/Release/net8.0/InteropPeer.dll host  --port 47810 --label cs
dotnet tests/RewriteLink.InteropPeer/bin/Release/net8.0/InteropPeer.dll guest --port 47810 --label cs
```

(start the host first; the guest retries until a host answers). The last stdout line is `RBL-INTEROP-RESULT {json}`, the exit status is 0
if every check passed. For the rewrite-bridge driver, set `RBL_PEER_CS="dotnet <path>/InteropPeer.dll"`.

`tests/interop.sh` runs the demo against itself and against the Rust `fake_host` / `fake_guest`, link-java's `LinkDemo` and link-c's `demo`,
in both roles, each pair twice: with the guest rendering a 320x240 frame with a depth plane for every camera, and with the host sending a
400-chunk (90400 triangles) collision snapshot every second. See the header of the script for the environment variables.

## Notes and limits

- Developed and tested on macOS (arm64, .NET 8). The CI workflow also builds and runs the tests on Linux and Windows; this repository
  has not been pushed from here, so those runs have not happened yet, and the Windows paths (Winsock behaviour of non-blocking connect,
  file sharing of the pixel file) are unverified.
- Strings are UTF-16 in memory and UTF-8 on the wire; text that is not valid UTF-16 is sent with U+FFFD.
