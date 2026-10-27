# Benchmarks

These are measurements of single runs on named machines, not guarantees (spec §6.1, §9; CLAUDE.md rule 9). The product makes no performance claims from them.

## How to measure

In the app, open **Benchmark** and choose **Run benchmark**. The report is saved as a new file in `Documents\PC Health Monitor\Benchmarks`. The page shows the file's path, with **Open folder** and **Copy path** buttons. From a copy of the repo, the same code runs as:

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

Not measured yet (T-37b). This needs a run on the user's Windows PC: the app's Benchmark page is the easiest way.

### GitHub Actions Windows runner (reference only)

A shared virtual machine with unknown neighbors, so its numbers are only a reference point.

#### 2026-10-22 · GitHub Actions windows-latest runner (Benchmark run 39755975447, commit d2675c3)

Microsoft Windows 10.0.26100, 4 logical processors, 16.0 GiB memory available to .NET, .NET 10.0.13.

The fixture has 1,110 folders and 20,200 files, 660.8 MiB in total, including 50 pairs of identical large files. The cache was warm.

| Stage | Wall time | CPU time | CPU use (of all cores) | Allocated | Working set after |
|-|-|-|-|-|-|
| Scan, run 1 | 0.10 s | 0.11 s | 27 % | 11.6 MiB | 44.5 MiB |
| Scan, run 2 | 0.08 s | 0.09 s | 29 % | 11.6 MiB | 50.8 MiB |
| Scan, run 3 | 0.07 s | 0.06 s | 23 % | 11.6 MiB | 50.9 MiB |
| Storage summary | 0.11 s | 0.11 s | 24 % | 21.5 MiB | 62.3 MiB |
| Duplicates (files of 1 MB or more) | 0.32 s | 0.30 s | 23 % | 112.9 MiB | 72.8 MiB |

The process's peak working set was 73.4 MiB. The run found 50 duplicate groups, as expected.

- Every stage runs on one thread, about one core of four.
- These timings come from a warm cache and a small tree. They say nothing about a cold first scan of a real profile with hundreds of thousands of files.
