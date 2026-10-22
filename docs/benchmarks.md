# Benchmarks

These are measurements of single runs on named machines, not guarantees (spec §6.1, §9; CLAUDE.md rule 9). The product makes no performance claims from them.

## How to measure

```
dotnet run --project tools/PCHealth.Benchmarks -c Release -- --machine "<label>" --out result.md
```

- The tool writes a fixed fixture tree under the temp folder, then deletes it afterwards. Its defaults are 1,111 folders, 20,000 small files and 200 large files, 50 of them identical pairs.
- It measures the read-only discovery stages one after another, in one process:
  - the recursive scan, 3 runs;
  - the storage summary;
  - duplicate verification of the files of 1 MB or more.
- For each stage it records wall time, CPU time, CPU use across all cores, managed allocations and working set. It also records the peak working set.
- The fixture is freshly written, so the file system cache is warm. A first scan of a real drive after a reboot will be slower.
- The report names the machine only by its `--machine` label, never by its computer name, user name or paths.
- `.github/workflows/benchmark.yml` runs the tool on a GitHub-hosted Windows runner whenever the tool changes, and can also be started by hand.

## Results

### Host

Not measured yet (T-37b). This needs a run on the user's Windows PC.

### GitHub Actions Windows runner (reference only)

A shared virtual machine with unknown neighbors, so its numbers are only a reference point.
