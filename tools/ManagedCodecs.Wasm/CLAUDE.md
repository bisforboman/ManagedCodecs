# WebAssembly: notes for working on the WebAssembly build (`tools/wasm-check.sh`)

The repository's CLAUDE.md (process, core idea, cross-cutting gotchas) applies too.

## Gotchas (each cost time once)

- WebAssembly (`tools/ManagedCodecs.Wasm`, `tools/wasm-check.sh`): a browser-wasm build needs the `wasm-tools`
  workload even without AOT (NETSDK1147), hence the project is outside the solution. `dotnet new wasmconsole` needs
  a NuGet search host the sandbox blocks: the project is written by hand. The browser has no MD5 (SHA-256 works),
  Emscripten's file system doesn't see the host's (files come in through `[JSImport]` from main.mjs), and the runtime
  prints `MONO_WASM:` notes on stdout under Node (it can't fetch its symbol file). `dotnet publish` puts the app in
  `bin/Release/net10.0/browser-wasm/AppBundle`, whatever `-o` says.
  Mono's interpreter optimizes a method only after enough calls, and only then uses SIMD instructions, some with other
  semantics: `Vector128.Narrow` saturates there as WebAssembly's narrowing does (lanes read as signed, an unsigned
  0xFFFF becomes 0), so narrow only values the narrower type holds, comparison masks as signed lanes
  (`NarrowMasks`; dotnet/runtime#136688). Such a bug shows only after warm-up, so a file can decode right alone and wrong after others:
  find it with a probe that runs the vector function against its scalar reference many thousand times in the bundle.
- Compiled ahead of time (`wasm-check.sh aot`, .NET 10.0.13), a method whose exception filter
  (`catch (Exception e) when (...)`) had caught an exception read its arguments wrong afterwards, in the catch and after
  it: a string as empty, an int as 1 (`this` too, and an iterator's captured arguments, which live behind it); its
  locals were kept, and so were arguments read in the filter itself. The interpreter and the native build were right,
  and a plain `catch` that rethrows what it doesn't handle kept everything. `Demuxer.Open`'s filter then disposed a
  garbage stream, and the fuzz finding mp4-stsz-size-overflow brought the runtime down (a Mono assertion,
  class-accessors.c). The libraries have no filters since (`ExceptionFilterTests` fails on one), and wasm-check.sh
  replays the fuzz findings, the error paths, on WebAssembly against native. A minimal repro, eight cases, took one
  AOT publish of a scratch console.
