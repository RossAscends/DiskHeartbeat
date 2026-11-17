# Verification

The source can be built and checked with the .NET 8 SDK on Windows. No SMART utility, vendor SDK, third-party driver or external test runner is needed. Public fixtures contain entirely fabricated identities, counters, capacities, hours and timestamps; no private machine inventory or telemetry capture is included.

## Automated coverage

The SDK console checks exercise:

- NVMe and ATA byte conversions, the 80-year clock, session percentages and durations, and behavior beyond rated TBW.
- ATA IDENTIFY word-swapped fields, media type and 512 / 4096-byte logical sectors.
- Standard Device Statistics, supported/valid/normalized flags, zero, truncation, unsupported commands, and status/length/checksum validation.
- Explicit model/firmware SMART mappings with 512-byte and 32 MiB units, unknown mappings, absent host counters and NAND-only data.
- Firmware-specific lower-32-bit statistics validation, using only fabricated counts.
- Stable identity versus disk routing, source changes/decreases, no cross-source deltas, and legacy source-tagged history migration.
- Recorded/inferred projections, missing ratings/hours, zero pace and unknown daily allocation across gaps.
- Portable Data storage, original-preserving migration, backups, corruption handling, overrides, and actual EXE location versus a bundle extraction directory.

WPF fixture checks use two injected synthetic drives. They verify both cards at once, independent background reads/persistence, independent simultaneous sessions, per-drive process attribution, source-change invalidation, one failed drive not blocking another, valid zero, missing lifetime telemetry with separate observed ETW activity, and continued recording/minimize/restore through the tray. Queries execute off the UI thread and do not overlap for the same drive.

```powershell
dotnet run --project .\tools\Checks.csproj -c Release
$env:DISKHEARTBEAT_DATA_DIR = Join-Path $PWD '.tools\new-ui-check'
dotnet run --project .\tools\Checks.csproj -c Release -- --ui-smoke
```

Use a fresh fixture data directory for each UI run. This does not access hardware. Binary fixtures can be regenerated with the built-in PowerShell script `tools/GenerateFixtures.ps1`; they are not exports of a real drive.

## Native and published-app checks

Native Windows testing has exercised SATA IDENTIFY DEVICE, Device Statistics and mapped SMART reads, including the documented firmware-specific width exception, plus a monotonic before/after file-copy comparison. Native NVMe SMART and elevated ETW event collection have also been exercised. Access-denied behavior is tested without elevation. Machine-specific inventories, serials/WWNs, source paths, counter captures, power-on hours and local timestamps are intentionally not published.

Self-contained single-file builds have been launched directly from an EXE-only folder without a dotnet host or installed runtime dependency. The bundled app creates Data/history.json beside its actual executable, discovers drives, records available native readings and closes normally. The WPF layout has been visually checked. No claim is made for all controllers/firmware, real hot-removal, 4Kn hardware, Intel hardware or a long-running soak; unsupported decoding/discontinuity cases use synthetic fixtures.

To run native checks on your own machine:

```powershell
# Run elevated for ATA and ETW:
dotnet run --project .\tools\Checks.csproj -c Release -- --probe
# The copy helper requires an explicitly selected volume and model, then validates
# that the volume's native disk extents match that drive before writing test data:
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\VerifySata.ps1 -DestinationRoot X:\ -Model YOUR_SATA_MODEL
```

The copy test creates unique temporary files, reads before/after with the production reader, and removes its test files. Output goes under the ignored local tools workspace. It is never included in source or release packages.

## Public release checks

Before publication, source and release contents are checked for private identifiers, raw hardware telemetry, local paths, credentials and runtime Data files. Release builds disable PDBs, map compiler source paths to a generic path, and exclude local source revision metadata. ZIP entry timestamps are normalized rather than carrying the build machine's file times. Public Git commits use the repository owner's GitHub no-reply identity with UTC dates.

The release ZIP contains the self-contained EXE, generic startup instructions, and required third-party notices. The repository contains source, documentation, scripts and synthetic fixtures; build output, archives, local tools/authentication and metric data are ignored. Version numbers, supported vendor model/firmware definitions, dependencies, the intended public GitHub repository URL and third-party attribution remain as normal product metadata.
