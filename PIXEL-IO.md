# Pixel readers and presentation migration

This branch proposes contract revision 7 without changing protocol version 1, datagrams,
file headers, slot offsets or depth layout. Writers retain their mapped publication path.

## Presenting frames

A Frame event is an unvalidated announcement. Use read_latest (Rust/C), ReadLatest (C#),
or readLatest (Java) for presentation. Only a successful current-file copy advances the
number. Raw read APIs remain unordered for inspection and re-reading. Rust accepts an
optional depth destination; the other APIs can omit depth using their existing argument.
Output buffers supplied by callers are valid only on success and may contain partial or
previous bytes after a miss. Clear the displayed image and discard the old reader on a new
channel, session loss or a gone reader, including when opening the replacement fails.
Async results must still belong to the active reader before they are published.

Reader stale counters count duplicate/older nonzero presentation reads: stale_reads() in
Rust, pixel stats.stale in C, SkippedStale in C#, staleReads() in Java. Null frame arguments
are caller misses: C returns RBL_ERR_ARGS, C# and Java return null, without counters or state
changes. Rust's borrowed FrameReady cannot be null. Frame number zero is invalid, not a
presentation-staleness event.

## Unix and Windows I/O

Unix readers use bounded positional I/O and never dereference peer-owned mapped pages.
They validate the header and dimensions, observe the lock, copy requested planes, then read
both lock and frame again. Short reads are completed; EOF and I/O errors reject the entire
copy. The metadata size check also rejects shrinking unused tail bytes. It is an early
rejection, not the protection against later truncation.

Normal cropped planes use up to eight rows per vectored read on macOS/Linux (preadv in
Rust/C/C#, scatter read after positioning the private single-threaded FileChannel in Java).
Data goes directly into owned output in Rust/C/C#; padding goes into reusable scratch and
is discarded. Java reuses packed direct buffers before copying into its array results.
This reduces syscall count without a full-plane scratch allocation. Grouping is limited to
strides up to 16 KiB and spans up to four times the useful output. Wider or very sparse files
keep per-row reads: measurements showed that reading their padding costs more than the
saved syscalls. Full-width frames retain contiguous copies. Other Unix targets and a missing
C# preadv binding use bounded 32 KiB packed spans under the same limits, then per-row reads.
Rust/C retry interruptions and continue partial vectored reads, including within a vector.

Java owned direct scratch is reclaimed by the JVM without invoking Unsafe on Unix reader
close. The writer's existing cleaner path is unchanged. C# caches SafeFileHandle and uses
native positional I/O when available; missing or unsupported bindings switch to safe
FileStream copies with explicit positions and buffering disabled. I/O/provider exceptions
inside CopyAt are contained as misses. The fallback is tested by fault injection, not by
running Unity or IL2CPP.

Windows keeps a read-only map and the existing atomic lock observations: an active section
prevents ordinary truncation. Creation also checks that the actual mapping/file extent did
not shrink before protection took effect. Rust and managed ports read the header using
bounded I/O before mapping; C rechecks the extent with the section active before touching
it. Windows was not run or benchmarked. Java mapped-reader cleanup may depend on GC if the
runtime cannot release its section. Readers are not safe for concurrent close/read; Rust
serializes scratch and presentation state but callers still own reader lifetime.

The revised protocol explicitly distinguishes observation checks from a formal acquire
load: POSIX does not specify pread as an atomic acquire from the writer's memory. Tests
reject detected overlapping writes, not every possible publication violation on every
filesystem. ABA and hostile reuse of lock/frame remain outside that guarantee. I/O can
block. Writers still assume nobody truncates their own mapping. Linux, Windows, WASI,
Mono, Unity and IL2CPP have not been run in this task.

## Benchmark method

Synthetic local files, warm cache, two warm-up reads, 30 samples for normal/cropped frames
and 5 for maximum-sized outputs. Times include header validation, requested planes and final
identity checks; exclude writer, GPU, IPC and rendering. Rust/C reuse destination buffers;
C#/Java allocate results according to their public APIs, with GC requested outside timed
regions. Other processes may compete for CPU and memory. p95 with five samples is only the
largest observed value. Do not compare allocation policies across languages as if identical.

Measured on macOS arm64. All numbers are milliseconds per frame.

| File | Frame | Depth | Before p50 | After p10 | After p50 | After p95 |
|---|---|---|---:|---:|---:|---:|
| 1280x720 | 1280x720 | false | 0.4620 | 0.1002 | 0.1820 | 0.2190 |
| 1280x720 | 1280x720 | true | 1.0959 | 0.2057 | 0.2917 | 0.3305 |
| 1920x1080 | 1920x1080 | false | 1.0316 | 0.2324 | 0.2453 | 0.2729 |
| 1920x1080 | 1920x1080 | true | 2.6720 | 0.7452 | 0.8189 | 0.9316 |
| 3840x2160 | 3840x2160 | false | 5.8328 | 1.7691 | 2.1290 | 2.2571 |
| 3840x2160 | 3840x2160 | true | 11.7300 | 3.7848 | 4.6493 | 4.8493 |
| 8192x8192 | 8192x8192 | false | 52.2156 | 19.9975 | 20.3235 | 20.6189 |
| 8192x8192 | 8192x8192 | true | 114.2794 | 39.3200 | 39.7315 | 39.9552 |
| 16384x1 | 16384x1 | false | 0.0100 | 0.0044 | 0.0057 | 0.0077 |
| 16384x1 | 16384x1 | true | 0.0186 | 0.0065 | 0.0067 | 0.0091 |
| 16384x4096 | 16384x4096 | false | 43.1369 | 19.8137 | 20.1201 | 20.3915 |
| 16384x4096 | 16384x4096 | true | 99.6072 | 39.3217 | 40.3796 | 41.4438 |
| 1920x1080 | 1280x720 | false | 2.1050 | 0.1825 | 0.2573 | 0.3106 |
| 1920x1080 | 1280x720 | true | 4.2331 | 0.3695 | 0.4445 | 0.4887 |
| 3840x2160 | 1920x1080 | false | 3.7642 | 0.4998 | 0.5206 | 0.6005 |
| 3840x2160 | 1920x1080 | true | 7.7830 | 1.3099 | 1.4340 | 1.5426 |
| 16384x4096 | 8192x4096 | false | 33.4626 | 11.6118 | 11.8117 | 12.2257 |
| 16384x4096 | 8192x4096 | true | 57.9257 | 24.1610 | 24.3517 | 24.6049 |
| 16384x4096 | 1x4096 | false | 5.3583 | 1.1809 | 1.2094 | 1.2258 |
| 16384x4096 | 1x4096 | true | 16.3734 | 2.4138 | 2.4420 | 2.4916 |

## Matched cropped-frame comparison

The same final API was built with grouping disabled and enabled, one language at a time.
These sequential runs separate the optimization from other reader changes. Scheduling and
memory pressure still vary. Whole-plane scratch and unrestricted grouping were rejected
after measurements showed regressions on wide files. A one-column frame remains per-row.

| File | Frame | Depth | Row-wise p10 / p50 / p95 | Bounded p10 / p50 / p95 |
|---|---|---|---:|---:|
| 1920x1080 | 1280x720 | false | 0.3037 / 0.3966 / 0.4350 | 0.1747 / 0.2507 / 0.3071 |
| 1920x1080 | 1280x720 | true | 0.6342 / 0.7152 / 0.7751 | 0.3811 / 0.4577 / 0.5210 |
| 3840x2160 | 1920x1080 | false | 0.5658 / 0.5819 / 0.6857 | 0.5571 / 0.5912 / 0.7301 |
| 3840x2160 | 1920x1080 | true | 1.3981 / 1.5232 / 1.7860 | 1.4115 / 1.5313 / 1.6851 |
| 16384x4096 | 8192x4096 | false | 12.1742 / 12.6833 / 12.7942 | 12.4083 / 12.5762 / 12.9687 |
| 16384x4096 | 8192x4096 | true | 24.6955 / 24.9678 / 25.5719 | 24.5582 / 24.8845 / 25.9171 |
| 16384x4096 | 1x4096 | false | 1.1897 / 1.2051 / 1.2244 | 1.2220 / 1.2480 / 1.2877 |
| 16384x4096 | 1x4096 | true | 2.4155 / 2.4665 / 2.9684 | 2.4782 / 2.5434 / 2.6302 |
