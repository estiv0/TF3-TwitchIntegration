# Export format vs. performance

**Status:** measured on 2026-10-02. **Decision: JSON in Lua stays** (details in the "Results" section). `cfg.benchmark = false`, and `bench.lua` stays for possible re-runs.

## Where the cost is (measured on 2026-10-02)

| Stage | Network (~8.7k edges) | Dynamic |
|---|---|---|
| collecting data from the API | ~20 ms | ~5 ms |
| **JSON encoding in Lua** | **150–200 ms** | ~6 ms |
| writing | a few to a dozen ms | ~1 ms |
| size | ~1 MB | ~120 KB |

On top of that, `app.saveUserdata` serializes our JSON as a Lua string literal. Every `"` becomes `\"`, so the file grows by ~15–20%, and we pay for encoding twice.

## Proposals (from the largest expected gain)

1. **Lua tables instead of JSON in the mod.** The mod passes plain tables to `app.saveUserdata`, and the game serializes them in C++. The middle app reads the `.lua` file (e.g. with the MoonSharp library for .NET or a simple literal parser) and turns it into JSON. The files are always in the form `function data() return {...} end`.
   - Gain: encoding in Lua goes away.
   - To measure: the game serializer's speed, file size and number formatting.
   - Consequence: network diffs (`networkDelta`) have to be computed on numbers, not on encoded rows. That's a simple field comparison.
2. **A columnar format for dynamic data too.** Like `fields` + `edges[][]` in the network. It removes repeated key names, so the file is about 2× smaller and encodes faster.
3. **Lower number precision.** Positions to 0.1 m (or whole meters), tangents and factors to 0.01. Fewer characters and fewer false differences.

### What not to do
- **A binary format (MessagePack/CBOR) in the mod.** Lua 5.2 has no `string.pack`, so assembling bytes in Lua is slow. On top of that `saveUserdata` writes bytes as `\ddd` text, so the file comes out bigger.

## Middle app → website (a separate decision)

The format for the website doesn't have to match the mod's format:
- upload to Azure compressed (gzip/brotli), because edge JSON compresses several times over;
- the middle app can rewrite the network geometry into a binary format (e.g. `Float32` arrays) that the browser reads without parsing. Worth it for large maps.

## Measurement (`bench.lua`)

It runs once per session, right after the first full network dump. That one frame will be long. It compares, on the same edges:

| Variant | What it does | File |
|---|---|---|
| A | `json.encode` in Lua + `saveUserdata({ json = string })` (current) | `local\tf3export\bench_A.lua` |
| B | `saveUserdata({ fields, edges = tables })` | `bench_B.lua` |
| B2 | like B, numbers rounded to 0.1 | `bench_B2.lua` |

Results are in the log, in `[tf3export] bench ...` lines. After the measurement:
- compare the times and file sizes;
- check how the game formats numbers and nested tables in B;
- delete the `bench_*` files;
- turn off `cfg.benchmark`.

## Results (2026-10-02, 8698 edges, one GUI frame)

| Variant | Time | File size |
|---|---|---|
| collecting rows from the API (shared) | 80 ms | – |
| **A** JSON in Lua + `saveUserdata(string)` | 80 ms encoding + 5 ms writing = **85 ms** | **1.08 MB** |
| **B** `saveUserdata(table)` | **74 ms** | 1.26 MB |
| **B2** like B, rounded to 0.1 | 15 ms rounding + 70 ms = 85 ms | 1.16 MB |

**Conclusions:**
- The game serializer **is not faster**: 74 ms vs 85 ms, within noise. But the file is 16% bigger.
- The game serializer **cuts precision** to about 6 significant digits. For example it wrote `11412.62` as `11412.6`, so coordinates above 10 km lose accuracy.
- Collecting data from the API costs as much as encoding (~80 ms). With the rebuild spread over frames the whole thing takes about 280 ms of work over about 3 s.
- **Decision: we stay with JSON in Lua** (variant A). Proposals 2 (a columnar format for dynamic) and 3 (lower precision) remain possible size optimizations, not time optimizations.

**Side discovery:** `onPostBuildProposal` arrives every ~3 s even without network changes, e.g. for town buildings. Without a filter the network was rebuilt almost constantly: 2 ms on every GUI frame. Fix: `touchesNetwork()` in `tf3export.script.lua` bumps `netVersion` only when the proposal:
- has `proposal.addedSegments`/`removedSegments`, or
- removes (`onPreBuildProposal`, `toRemove`) or adds (`onPostBuildProposal`, result entities `param[3]`) a construction with `frozenEdges`.

`networkDelta` works: building or demolishing produces 260–600 B files. The filter has not been tested in the game yet.
