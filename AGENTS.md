# Agent guide

link-cs implements rewrite-bridge's passthrough link protocol for .NET and Unity mod developers.
It supplies host and guest roles for protocol v1, contract revision 7, alongside the Rust, Java and C implementations.

## Toolchain, build and CI

- Use the .NET 8 SDK, as CI does on Linux, macOS and Windows.
- The library targets `netstandard2.0` with C# 7.3; tests and demo target `net8.0`.
- The library has no package references; besides the BCL it calls libc through P/Invoke on Unix (below). The README describes .NET Framework 4.6.1+ compatibility.
- Exact CI build and test commands:

```sh
dotnet build RewriteLink.sln -c Release
dotnet run --project tests/RewriteLink.Tests -c Release --no-build
```

On Linux and macOS, CI additionally runs:

```sh
RBL_CS_DLL=demo/LinkDemo/bin/Release/net8.0/LinkDemo.dll tests/interop.sh cs
```

Gate/workflow regression tests in CI:

```sh
python3 -m venv .workflow-tests
.workflow-tests/bin/python -m pip install -r tools/requirements-test.txt
.workflow-tests/bin/python -m unittest discover -s tools -p 'test_*.py'
```

Switches of the test runner (`tests/RewriteLink.Tests`):

- `RBL_TEST_FILTER=text` runs only the tests whose name contains `text` (to run one test).
- `RBL_VECTORS_DIR=dir` reads the shared vectors from another copy; `RBL_VECTORS_SKIP_LARGE=1` skips the cases that need a sparse file of up to 1 GiB.
- `RBL_CONTENTION_MS=n` sets how long the seqlock contention test runs (default 1500).
- `--pixel-safety` runs only the pixel reader safety tests; `--pixel-bench` prints read timings (`RBL_BENCH_CROPPED_ONLY=1`: cropped frames only);
  `--truncate-child` is the child process that one of the safety tests starts.
- `tests/interop.sh` documents its own variables (`RBL_CS_DLL`, the optional peers, `RBL_LOG_DIR`, `RBL_SECONDS`, `RBL_BASE_PORT`) in its header.

Without the .NET SDK nothing in C# can be built or run here. What can be checked is the Python side only: the tool tests
(`python3 -m unittest discover -s tools -p 'test_*.py'`, which need PyYAML) and the public-tree gate. Everything else needs CI or an SDK.

## Code map

- `src/RewriteLink/`: `Link`, control/JSON/wire codecs, tokens, endpoints and `PixelFile`.
- `demo/LinkDemo/`: standalone host/guest demo.
- `tests/RewriteLink.Tests/`: console test runner; a failed check returns a nonzero status.
- `tests/RewriteLink.InteropPeer/`, `tests/interop.sh`: cross-language peer and demo checks.
- `tests/vectors/link/`, `PIXEL-IO.md`: shared contract vectors and pixel-reader rules.
- `tools/`: the public-tree gate (`check_public_tree.py`), the workflow checks (`workflow_checks.py`), their tests and `requirements-test.txt`.
- `.github/workflows/ci.yml`: the CI jobs (build and tests on three systems, the gate, the tool tests).
- `.public-tree.cfg`: this repository's additions to the gate's rules (which source globs are its own).

## Project rules

- Bind and connect only to `127.0.0.1`; call `Poll()` rather than creating a library thread.
- Keep unsafe code confined to `PixelFile` as documented in the README.
- Native imports, all from libc and Unix only: `pread` and `preadv` in `PixelFile.cs`; `open` (read-only, two arguments), `mkdir`, `chmod`
  and `rename` in `Endpoint.cs` (class `Posix`). `pread`, `preadv` and `open` fall back to managed I/O when the binding is missing;
  `mkdir`, `chmod` and `rename` are used only on runtimes without the .NET 7 calls, which are found by reflection.
- Follow rewrite-bridge's contract and keep shared vectors plus their digest synchronized.
- Present frames with `ReadLatest`; dispose old readers and clear cached images on channel/session loss.
- Endpoint readers refuse token files when the runtime cannot verify permissions.
- Tests synthesize protocol data and need no game files; the README does not claim in-game Unity validation.

## Public files

- Never commit game files, extracted assets, renders, decompiled code or secrets.
- The public-tree gate needs Python 3.9+ and Git; run it from the repository root:
  `python3 tools/check_public_tree.py` (checks Git's index, including staged new files).
- CI checks committed changes with `python3 tools/check_public_tree.py --range BASE..HEAD`.

## Contributing, license and AI

- Open an issue to discuss a problem or send a pull request with the change.
- Before a pull request, run the applicable CI commands above and the public-tree gate.
- License: MIT OR Apache-2.0, at your option; see `LICENSE-MIT` and `LICENSE-APACHE`.

The code was written with AI coding agents (Anthropic's Claude and OpenAI's GPT models) under the maintainer's direction.
AI-assisted contributions are welcome when their use is disclosed and the changes are reviewed.
