# Performance benchmarks

The benchmark inputs are deterministic and exercise production source files. Run them from the repository root in Release mode.

## Host runtime

```bash
dotnet run --project benchmarks/MyTransportAppWASM.PerformanceBenchmarks -c Release
```

This reports median and p95 elapsed time plus allocated bytes per operation. It includes a generated `Google.Protobuf` comparison for development evidence; that dependency is not part of the application runtime.

## Browser-native Mono/WASM

Publish the harness, serve its static output, and open it in a browser:

```bash
dotnet publish benchmarks/MyTransportAppWASM.BrowserBenchmarks -c Release -o /tmp/mytransport-wasm-bench
python3 -m http.server 8768 --directory /tmp/mytransport-wasm-bench/wwwroot
```

Then browse to `http://localhost:8768/`. Results appear in the page after warmup. The page explicitly reports `browser=True` so host results cannot be mistaken for WebAssembly measurements.

Benchmark comparisons must use the same SDK, browser, build mode, inputs, and warmup settings. Repeat runs and compare medians; do not treat noisy single-run differences as regressions or wins.
