# Agent guide

link-cs implements rewrite-bridge's passthrough link protocol for .NET and Unity mod developers.
It supplies host and guest roles for protocol v1, contract revision 7, alongside the Rust, Java and C implementations.

## Toolchain, build and CI

- Use the .NET 8 SDK, as CI does on Linux, macOS and Windows.
- The library targets `netstandard2.0` with C# 7.3; tests and demo target `net8.0`.
- The library uses only the BCL; the README describes .NET Framework 4.6.1+ compatibility.
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

## Code map

- `src/RewriteLink/`: `Link`, control/JSON/wire codecs, tokens, endpoints and `PixelFile`.
- `demo/LinkDemo/`: standalone host/guest demo.
- `tests/RewriteLink.Tests/`: console test runner; a failed check returns a nonzero status.
- `tests/RewriteLink.InteropPeer/`, `tests/interop.sh`: cross-language peer and demo checks.
- `tests/vectors/link/`, `PIXEL-IO.md`: shared contract vectors and pixel-reader rules.

## Project rules

- Bind and connect only to `127.0.0.1`; call `Poll()` rather than creating a library thread.
- Keep unsafe code confined to `PixelFile` as documented in the README.
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
