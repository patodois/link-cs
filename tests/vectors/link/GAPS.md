# What the contract did not say, and where revision 5 says it

Revision 4 of `docs/CONTRACT.md` was silent or ambiguous in 26 places, and a review of revision 5 found a 27th. Revision 5
settles every one of them, and the vectors in this directory now cover all but three (gap 12, a "may"; gap 22, which no
static file can show; and the one case of gap 23 described there).

How each rule was chosen. For every gap the four implementations (Rust `R`, link-java `J`, link-c `C`, link-cs `S`)
were measured with probe vectors that ask the question in both directions (the probes are not part of this
directory; the table below is what they showed on 25/10/2026, before revision 5). Then:

* **(a)** when all four did the same thing, that became the rule, whatever it was (a rule that nobody had to change
  cannot break a peer that works today);
* **(b)** when they differed, the safest rule that refuses the doubtful input (a `bye` for a control line, a dropped
  datagram, a skipped frame);
* **(new)** where all four agreed to accept without limit something that no receiver can afford, a limit.

Nothing that a correct revision 4 sender writes is refused by a revision 5 receiver, except `\u0000` in a string, a
collision `chunk_count` above 4096 and a `Frame` datagram whose size differs from its slot (section 12 of the
contract says why none of these is written by a correct peer).

Vector ids below are prefixes; `ctl.*` is `control.json`, `dg.*` is `datagrams.json`, `pf.*` and `pw.*` are
`pixelfiles.json`, `sess.*` is `sessions.json`.

## Control channel

1. **A first line that is valid but not `hello`.** Before: R J C S all answered `bye` and closed, for a `spawn`, an
   `input`, a `despawn`, a `frame_channel` and an unknown type, as host and as guest; a `bye` as first line closed
   without an answer and counted as a failed handshake. Rule (a), contract section 6: the first line is `hello` or
   `bye`; anything else is a protocol error. Vectors `sess.*.handshake.first_line_*`.
2. **A second `hello`.** Before: all four answered `bye` and ended the session, host and guest, with the same or other
   values. Rule (a), section 6. Vectors `sess.*.control.second_hello_*`.
3. **`bye` without `reason`, or with a non-string one.** Before: all four took a missing, `null` or empty reason as
   "" and ended quietly; a number, array, object or `true` was a protocol error answered with a `bye`. Rule (a),
   section 8 (`bye` table). Vectors `ctl.bye.*`, `ctl.bad.bye_reason_*`, `sess.*.control.bye_*`.
4. **Integral numbers written as `7.0` / `7e0`, and duplicate keys.** Before: all four refused `7.0`, `7e0`, `7E+0`, `70e-1`,
   `-0`, `-0.0`, `1e400` and 30 digits in an integer field; all four kept the **last** of a duplicate key, in the top
   object, in nested objects and when the keys differ only by an escape (`"entity"`). Rule (a), section 8,
   "Syntax of a line" 5 and 6, with one addition: the earlier, overridden values still have to be valid (a Rust
   parser that keeps only the last value would not have seen a NUL or a syntax error in them; the Rust parser now
   scans the text). Vectors `ctl.bad.despawn_entity_*`, `ctl.json.duplicate_*`, `ctl.bad.duplicate_*`.
5. **`null` for an optional field.** Before: all four took `null` as absent (`name`, `reason`, a guest's `session`);
   `null` for a required field was already refused. Rule (a), "Syntax of a line" 7. Vectors `ctl.*_null_is_absent`.
6. **Integer widths.** Before: all four read `protocol` and `min_protocol` as 16 bits (65535 accepted, 65536 refused),
   `heartbeat_ms` and `timeout_ms` as 32 bits, the `frame_channel` numbers as 32 bits. Rule (a), section 8 tables.
   Vectors `ctl.hello.*`, `ctl.bad.hello_*`, `sess.host.handshake.protocol_*`.
7. **`timeout_ms` bounds.** Before: all four accepted 0 to 4294967295 and none uses the peer's value (the watchdog is
   `max(own timeout, 4 * peer heartbeat)`). Rule (a): no range beyond the type, and the text says the value is
   informative. A bound such as 10 to 600000 was considered and not taken: it would refuse a peer for a number that
   nothing reads. Vectors `ctl.hello.timeout_ms_*`, `sess.*.handshake.timeout_*`.
8. **`udp_port` 0 in a `hello`.** Before: all four refused it with a `bye`. Rule (a), section 8 (`hello`): the
   announced port is 1 to 65535. Vectors `sess.*.handshake.udp_port_*`.
9. **Lone surrogates, NUL, byte order mark, lone `\r`.** Before: all four refused every lone or misordered surrogate
   escape (in values, in keys, in unknown fields), a byte order mark, form feed, vertical tab, U+00A0 and a NUL byte
   as whitespace, and invalid UTF-8; all four took a CR anywhere in the line as whitespace. **NUL differed:** R, J and
   S accepted `\u0000` and delivered it; C accepted it in the JSON but cut the string at the NUL (a `game` of
   `a\u0000b` arrived as `a`, and a `frame_channel` path with a NUL arrived as no path at all). Rules: (a) for the
   surrogates, the BOM and the CR; (b) for NUL: a `\u0000` escape anywhere is a protocol error, and a sender writes
   U+FFFD instead. "Syntax of a line" 1, 2 and 4. Vectors `ctl.bad.string_*`, `ctl.bad.byte_order_mark`,
   `ctl.json.cr_*`, `ctl.bad.*_is_not_whitespace`, `sess.*.nul_in_*`.
   Also settled while measuring: arrays and objects nest at most 127 deep (all four already did; the text was
   silent), vectors `ctl.json.nesting_*`, `ctl.bad.nesting_*`.
10. **`despawn` of an entity that was never spawned.** Before: all four ignored it silently (no event, no error, also
    for a second `despawn` and for a spawn that the 1024 limit dropped). Rule (a), section 8 (`despawn`). Vectors
    `sess.*.control.despawn_of_*`, `sess.host.control.second_despawn_*`.
11. **Telling the peer that a `spawn` beyond 1024 was ignored.** Before: none sent anything. Rule (a), section 8
    (`spawn`): nothing is sent, there is no message for it. Vector `sess.host.control.entity_limit_1024` (its final
    `expect_no_line`).
12. **A guest reconnecting after a session ends.** Before: all four reconnect by default. Rule: it stays a "may",
    section 6; a host accepts a new guest (vector `sess.host.handshake.new_guest_after_bye`). No vector requires a
    guest to reconnect.

## Datagrams

13. **`Frame` with `frame` 0.** Before: all four refused it at decode. Rule (a), section 9.6: malformed, because an
    unwritten slot holds frame 0 and a clean lock and would otherwise match. The decoder drops it, and (found by the
    review of revision 5, which isolated the rule in a slot that holds frame 0 with a valid header) the reader skips it too
    when a caller hands it one directly: all four readers delivered such a frame until then. Section 9.6 reader step 0.
    Vectors `dg.bad.frame_0`, `pf.read.frame_0_is_skipped_though_the_slot_matches`.
14. **`Camera` values.** Before: all four decoded and delivered every probe: `near` 0, negative, NaN or infinite, `far`
    below or equal to `near`, NaN or infinite matrix elements, `fov_y` NaN, a viewport of 0 or 4294967295. Rule (a),
    section 9.3 "Values": the decoder checks only `depth_range`; the sender must write sane numbers; a receiver
    checks before it builds a projection or allocates from the sizes. Vectors `dg.unchecked.camera_*`. (A decoder
    that dropped such a camera would be the stricter alternative; it would be a change for all four.)
15. **`Pose` values.** Before: all four delivered flag bits other than bit 0 as sent, NaN and infinite numbers, an
    all-zero rotation, a rotation that is not unit length, zero and negative scale. Rule (a), section 9.4 "Values":
    receivers ignore unknown flag bits; the decoder checks nothing else. Vectors `dg.pose.flags_*`, `dg.pose.scale_*`,
    `dg.pose.rotation_not_unit`, `dg.unchecked.pose_*`.
16. **Collision chunks that disagree, repeat, or claim a huge `chunk_count`.** Before: all four built the assembly from
    the first chunk to arrive (`center`, `radius`, `chunk_count`), dropped a chunk with another `chunk_count` without
    discarding the assembly, ignored the repeat of a chunk (the first copy wins and a repeat cannot complete a
    query), and did not compare the `center` or `radius` of later chunks. Rule (a), section 9.5 "Assembly", now
    spelled out. **Size:** R and J let `chunk_count` go to 65535, that is up to 533 MB of triangles for one query; C and
    S capped the sum at 2097152 triangles with a receiver option (`max_collision_triangles`, `MaxCollisionTriangles`;
    about 75 MiB). New rule: `chunk_count` is at most 4096
    (`MAX_COLLISION_CHUNKS`), 925696 triangles, 32 MiB; larger is a malformed datagram, and a sender refuses a bigger
    snapshot. Vectors `sess.host.udp.collision_*`, `dg.collision.chunk_count_4096_*`, `dg.bad.collision_chunk_count_*`.
    The limit bounds the wire form (32 MiB); a receiver holds more than that while it assembles and delivers a full
    snapshot, about twice as much on the JVM (the chunk lists, then the list handed to the application), so a
    receiver short of memory may set a smaller limit of its own.
17. **A `Frame` whose `slot` is not `frame % slots`, or whose size differs from the slot header.** Before: all four
    delivered a frame found by `slot` whatever `frame % slots` says (the slot header's frame number is the check). The
    datagram's width and height: R compared them with the slot header and skipped a mismatch, J, C and S ignored them
    and delivered the header's size. Rules: (a) for the slot, section 9.6 reader step 0; (b) skip a mismatch, step 2.
    Vectors `pf.read.slot_need_not_be_frame_modulo_slots`, `pf.read.datagram_size_differs_from_the_slot_header`.
18. **Datagrams of 8193 bytes or more.** Before: all four dropped a known kind of that size, also when the extra bytes
    follow a valid 8192 byte datagram (their receive buffers are larger than 8192), and ignored an unknown kind of any
    size and counted it as proof of life. Rule (a), section 2 and 9.1. Vectors `dg.bad.*_8193_*`,
    `dg.unknown_kind.*_bytes`, `sess.host.udp.collision_of_8292_bytes_*`, `sess.host.udp.pose_of_9000_bytes_*`,
    `sess.host.udp.unknown_kind_above_8192_bytes_is_proof_of_life`.

## Pixel file

19. **`slot_size` too small or not a multiple of 64; wrong `magic`, `version`, `format`.** Before: all four refused them
    (the vectors already existed, as `table`). Rule (a), now in section 9.6 "Header checks"; the vectors are `MUST`.
20. **Slot header `width` / `height` above the file's.** Before: all four skipped the frame. A size of **0**: R delivered
    a 0 x N frame, J, C and S skipped it. Rule (a) and (b), section 9.6 reader step 2: the slot header's size is 1 to the
    file's. Vectors `pf.read.slot_header_*`.
21. **A slot index outside `0 .. slots-1`.** Before: all four skipped it. Rule (a), reader step 0 (the vector already
    existed).
22. **The seqlock "overwritten while copying" case.** No static file can show it. Rule: unchanged, section 9.6 reader step 3,
    now said in the text; the live tests of each repository (a writer racing a reader) are its evidence.
23. **A writer asked for a frame larger than its file, or for a depth plane it has not got.** Before: J, C and S refused a
    width or height of 0 or above the file's, a colour buffer of the wrong length and a depth for a colour-only file, and
    changed nothing; R refused all of these except a width or height of 0 (it wrote a 0 x N frame). For a call that
    supplies **no** depth to a file that has a plane, R refused and J, C and S filled the plane with `+inf`. Rule: the
    first group is (a) with R fixed; the last is left to the library, section 9.6 (what matters, that the slot never
    keeps older depth, holds either way). **That last case has no vector**: the two outcomes (an error, or a file with
    `+inf`) cannot both be what a vector requires, and picking one would break the callers of the other group (J, C and S
    callers pass null/NULL for "nothing here"; R callers cannot). Vectors `pw.*_refused_writes_change_nothing`.
24. **Creating a writer outside the limits.** Before: all four refused (vectors existed, as `table`). Rule (a), section
    9.6. Vectors `pw.refuses_*`.
25. **Non-zero `reserved` bytes** (file header 56 to 63, slot header 36 to 63). Before: all four ignored them. Rule (a),
    section 9.6 "Header checks". Vectors `pf.read.*reserved*`.

## Found while reviewing revision 5

27. **A guest `hello` that carries `session`.** Section 8 said the field is "host only" and nothing on what a receiver
    does when a guest sends one. Before: R, J, C and S all parsed it as an optional `u32` and ignored it (`null` is
    absent; `-1` or a string is a protocol error). Rule (a), section 8 (`hello`). Vectors `ctl.hello.guest_with_session_*`,
    `ctl.bad.hello_guest_session_*`, `sess.host.handshake.guest_session_*`.

## Sessions

26. **The text of a `bye` reason.** Before: each implementation wrote a short sentence of its own (`peer said bye: ...`
    in the `Lost` event of all four); only `"busy"` is fixed by the contract. Rule (a), section 8 (`bye`): free
    text, only `"busy"` is fixed, a receiver must not branch on the rest. The vectors check that a reason is a
    string (`$string`), never what it says.

## Revision 8: what the shared vectors do not cover

Revision 8 (contract section 12) settles nine places where revision 7 was silent or let a local process with no
token hold up a host. They are all local behaviour, so most can only be seen through sockets and files, and some
not at all from outside. The vectors added for them (all with a `section` ending in `(revision 8)`): `ctl.json.number_*`
and `ctl.bad.number_*` (numbers), `dg.pose.one_*` (records the session vectors send and expect), `pf.bad.path_*` and
`pf.read.path_*` (Windows paths), `pw.file_is_owner_only`, and in `sessions.json` `*.handshake.*candidate*`,
`*.handshake.fifth_*`, `*.flood.*`, `*.udp.second_session_*`, `*.udp.pose_*`, `*.sends.pose_*`, `*.watchdog.*pause*`,
`*.handshake.own_pause_*`, `*_not_echoed` and `*_control_characters_replaced`. What they leave out, and why:

1. **Bounds per poll** (section 2). At most 8 accepts per poll, at most 16 closing sockets, at most 1024 datagrams
   per poll with a session and 64 without: a raw peer cannot tell one poll of the implementation from the next, and
   how many datagrams are waiting depends on each system's receive buffer (Linux grants about 200 KiB of the 4 MiB
   asked for). Whether the oldest closing socket is closed at once, without its drain, is not visible either. The
   vectors show the outcome only: `sess.host.flood.*` (40 connections that never read: the session goes on, a guest
   gets in, every connection is answered). Each implementation tests the numbers in its own suite (Rust: the
   `link.rs` unit tests `a_poll_accepts_at_most_eight_connections`, `a_host_holds_at_most_sixteen_closing_sockets`
   and `datagrams_read_per_poll_are_bounded`).
2. **Candidates** (section 2). Whether the application hears of a candidate closed with `busy` or displaced is the
   implementation's choice, so no vector looks at events then. The reading order (a connection is read as soon as it
   is accepted) shows only through `sess.host.flood.guest_gets_in_after_a_flood`.
3. **The connected UDP socket** (section 2, a SHOULD). A datagram from another address is dropped either by the
   system, before it is queued, or by the address rule: a raw peer cannot tell which, so
   `sess.host.udp.from_other_address_dropped` holds for both. The vectors require that a side uses the port its
   `hello` announced for that session, not that the port changes; a new socket per session where the port was
   automatic is tested in each suite.
4. **Unreachable-port errors** (section 2). They need the peer's UDP port to go away during a session, and only some
   systems report them; the runner has no step that closes its UDP socket. Each suite tests that the session goes on.
5. **Pose order** (sections 5, 7, 9.4). Covered on both sides, except the counter a sender keeps of the times it
   raised (no runner sees it) and a sender's last time at the largest 64-bit value, which stays itself.
6. **Windows paths** (section 9.6, item 5). The vectors run on Windows only. A path the receiver fails to refuse is
   still refused by the later checks (no such file, not a regular file), so the vectors tell the rule apart by time
   (`max_ms`): only a receiver that refuses before any file-system call answers at once for a UNC path. The SHOULD
   (`GetDriveType`, a drive that is not local) and the MAYs (`GetFinalPathNameByHandle`, `statfs` on Unix) have no
   vector: a runner would need a network drive or a directory link to a share. Neither has the sender's rule (no
   `frame_channel` when the temporary directory is not on a local drive).
7. **A side that was not running** (sections 6, 7). Covered for the watchdog, a host's candidate and a guest's wait
   for `hello`, with `pause`. A machine that sleeps looks the same to the implementation. Polling less often than the
   timeout is not conforming, so no vector does it.
8. **Who owns the files** (sections 6.2, 9.6). The endpoint file's owner check needs a file that belongs to another
   user, which only an administrator can make; no vector. (The Rust suite tests the comparison:
   `the_endpoint_file_must_belong_to_the_reader`.) The pixel file's mode is covered on Unix (`pw.file_is_owner_only`);
   on Windows the per-user directory's ACL decides and no vector checks it.
9. **Texts** (sections 6.2, 8). A finite set of lines cannot prove that a text is fixed. The vectors put markers in
   the refused lines (`@`, `Zq7x`, `98765`, `4321`) and check that neither the `bye` nor the event quotes them. A
   position in the line is not checked: a fixed text may hold digits of its own. Cleaning is checked where a runner
   sees the text (`game`, a `spawn` name, the reason of a `lost` or `handshake_failed`); the `reason` of an `input` is
   not reported by any event the runner reads. An implementation that does not hand a peer's text to the application
   at all passes those vectors, as the contract allows.

No vector of revision 7 changed: regenerating them gave the same bytes, and only the `contract` label of each file
(`v1 revision 8`) and `SHA256SUMS` differ.
