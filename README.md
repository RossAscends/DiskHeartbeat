# Disk Heartbeat

Windows 11, C# / WPF. Tracks native NVMe and SATA SSD host writes, maps manufacturer-rated TBW to an 80-year clock, and keeps the actual counters visible. No drive utilities, helper processes, vendor SDKs, or third-party drivers are used by the app. The existing Microsoft TraceEvent managed package is used only for ETW process attribution; SATA telemetry adds no package dependency.

## Run the compiled app

Download the Windows x64 ZIP from [GitHub Releases](https://github.com/RossAscends/DiskHeartbeat/releases/latest), extract it, and launch `DiskHeartbeat.exe`. The same executable is produced locally at `dist\DiskHeartbeat.exe`. It is a **self-contained single-file Windows x64 app**. Copy just this EXE to another Windows 11 x64 PC; no .NET runtime, SDK, smartctl, vendor SDK, database, helper executable, or driver installation is needed. The EXE bundles its runtime and libraries and extracts them into the normal per-user temporary .NET bundle cache on first launch. Built-in Windows components are still used. Administrator rights are still needed for ATA telemetry and process attribution; those are permissions, not extra installs.

Close the previous instance before launching the new EXE so only one app writes each history directory. Minimize to keep it recording in the tray; close or tray Exit stops it. When closed, device counters catch up on the next launch, but per-process attribution cannot be reconstructed for that time.

To regenerate the single-file build using the SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

The earlier `--self-contained false` output is a different, framework-dependent build and **does** need the .NET 8 Windows Desktop Runtime. Likewise a source launch needs the SDK. The distributable in `dist` is the runtime-bundled variant. The small (~148 KB) EXE in `bin\Release\net8.0-windows` is only an app launcher and cannot be shared by itself.

## Run and publish from source

Install the **.NET 8 SDK for Windows**:

```powershell
dotnet run --project .\DiskHeartbeat -c Release
dotnet publish .\DiskHeartbeat -c Release --self-contained false -o .\publish
```

The framework-dependent published app needs the .NET 8 Windows Desktop Runtime. For a runtime-bundled x64 build use `dotnet publish .\DiskHeartbeat -c Release -r win-x64 --self-contained true -o .\publish`. Initial package/runtime restore may need internet access. Open the csproj in Visual Studio if preferred; no IDE is needed to build or verify. `run.ps1` also finds the ignored workspace-local SDK when present.

For native ATA and ETW, run as administrator. From a normal PowerShell in the project folder, after publishing:

```powershell
Start-Process -FilePath (Resolve-Path '.\dist\DiskHeartbeat.exe') -Verb RunAs
```

Accept Windows UAC and close the old app first. Without elevation, SATA drives still appear when Windows permits discovery; access-denied details are shown and ETW/lifetime counters are never fabricated.

Minimizing hides the window to the tray and continues recording. Double-click the heartbeat icon or choose **Open Disk Heartbeat** to restore. Closing the window or choosing **Exit** exits; no service or startup task is installed. Tray hover shows activity and its % of rated TBW for all displayed drives. The Windows tray tooltip shows a compact run/recorded summary and budget percentages. If more drives fit than the tooltip can hold, it indicates how many remain; open the dashboard to see every card. Drive details hold counter provenance, timestamps, discovery diagnostics and estimates. Explanations are on hover.

The dashboard shows all discovered SSDs at once, with one card per drive. Cards wrap into more rows as the window narrows. Every drive is recorded regardless of which controls or details are open. Each has its own clock, activity totals, editable rating, session and app breakdown. The global listening/pulse indicator sits beside the title. Technical provenance and explanations are in tooltips and collapsed details. There is no app demo mode; SDK-driven fixtures remain available for testing.

## Discovery, identity and native APIs

`WindowsStorage` discovers Windows `PhysicalDriveN` DOS devices using `QueryDosDevice`, then uses `CreateFile` and `DeviceIoControl` through the disk class device. `IOCTL_STORAGE_QUERY_PROPERTY` / `StorageDeviceProperty` provides bus, model, serial and firmware. Seek-penalty information identifies SSDs on ATA/SATA transports; known model mappings can confirm media type. Known rotating media are excluded. A SATA device whose media type cannot be confirmed remains visible with that caveat rather than disappearing because lifetime telemetry was denied. USB bridges, RAID abstractions, Storage Spaces and controller-specific pass-through are not supported.

Capacity comes from `IOCTL_DISK_GET_LENGTH_INFO`, with `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX` fallback. Stable identity remains the SHA-256 of trimmed model, NUL separator, and serial. This preserves existing NVMe histories and ratings. Disk numbers are routing information only (`DriveInfo.DiskNumber`) and route ETW events to the same stable IDs. Missing or duplicate identities are excluded with diagnostics rather than combining histories.

Each reopened device handle revalidates its Windows identity. ATA IDENTIFY DEVICE also rechecks the returned model/serial before decoding counters. Refresh rediscovers and revalidates drives, preserves each card and its state by stable identity where present, and stops ETW before replacing its routing map. An identity mismatch disables that drive's ETW routing until refresh. Refresh after hot-plugging; hardware hot-plug behavior is not comprehensively tested.

All discovery and device reads run off the WPF thread. All displayed drives are polled each cycle, independently and in parallel, with both dashboard and native-reader guards against overlap for the same stable drive ID. Five-second polling skips overlapping ticks; slow or blocked controllers never create concurrent polls. A failed poll preserves the last good lifetime bytes and timestamp and marks it **stale**. Access denied, controller/driver rejection, and unrecognized counter mappings are distinct diagnostics.

### NVMe

`NvmeReader` uses `IOCTL_STORAGE_QUERY_PROPERTY` (`0x2D1400`), `StorageDeviceProtocolSpecificProperty` (49), NVMe protocol (3), log-page data type (2), SMART log `02h`. The returned descriptor length, protocol, offsets and transfer lengths are validated. Data Units Written is unsigned little-endian 128-bit data at SMART bytes 48–63. One reported unit means **1,000 × 512 bytes**, rounded up, independently of namespace sector size; zero is unreported. Power-on hours are at bytes 128–143. Overflow is reported rather than wrapped.

References: [Microsoft NVMe query path](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices), [NVMe Base 2.0d](https://nvmexpress.org/wp-content/uploads/NVM-Express-Base-Specification-2.0d-2024.01.11-Ratified.pdf), [SMART structure](https://learn.microsoft.com/en-us/windows/win32/api/nvme/ns-nvme-nvme_health_info_log).

### SATA / ATA

`NativeAtaTransport` uses synchronous **IOCTL_ATA_PASS_THROUGH** (`0x4D02C`) and `ATA_PASS_THROUGH_EX`. Its command API only constructs three read-only PIO commands, each transferring exactly 512 bytes:

| Command | Task-file inputs | Purpose |
|---|---|---|
| IDENTIFY DEVICE `ECh` | count 1 | ATA model, serial, firmware, logical sector size, nonrotating-media indication |
| READ LOG EXT `2Fh` | 48-bit flag, count 1, log address `04h`, page `01h` in LBA mid | General Device Statistics |
| SMART READ DATA `B0h / D0h` | count 1, LBA mid `4Fh`, high `C2h` | Explicitly mapped SMART fallback |

The transport uses native pointer-sized layout (48-byte header on x64, 40 on x86), DATA_IN + DRDY_REQUIRED and the 48-bit-command flag only for READ LOG EXT. Timeout is five seconds per command. It checks returned header/offset/data lengths, underruns, and completion status: BSY, DF, DRQ, ERR must be clear and DRDY set. No SMART enable, self-test, reset, write-log, erase, sanitize, cache-setting or vendor command is sent.

ATA pass-through requires a **read/write device handle**, even for these read-only commands. Microsoft states the IOCTL itself does not intrinsically require administrator privilege, but standard Windows physical-disk ACLs normally require elevation to obtain that handle. The app never bypasses the disk class driver. Some controllers/miniports reject ATA pass-through or particular logs even when elevated; elevation cannot fix lack of driver support. No external utility or driver fallback is used.

`AtaDecode` is independent of transport and WPF. It validates IDENTIFY and SMART lengths/revisions and SMART checksum (plus IDENTIFY checksum when its integrity signature is present). IDENTIFY words 106 / 117–118 determine logical sector size; older devices without the extended-size indication use the ATA-defined 512-byte logical sector. Word 217 and Windows media information distinguish SSDs. ATA identity strings are word-swapped ASCII.

**Preferred counter:** Device Statistics general page, revision 1 / page 1, offset `18h`: unsigned 48-bit **Logical Sectors Written × logical sector bytes**. The supported and valid flags (bits 63/62) must be set; normalized values (bit 61) are rejected. Power-on hours at offset `10h` use a supported/valid unnormalized 32-bit count. A supported, valid zero is usable. Unsupported, invalid or malformed statistics fall back to explicitly mapped SMART; no normalized health or NAND counters supply host writes.

References: [Microsoft ATA pass-through](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddscsi/ni-ntddscsi-ioctl_ata_pass_through), [ATA_PASS_THROUGH_EX layout/status registers](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddscsi/ns-ntddscsi-_ata_pass_through_ex), [IDENTIFY structure](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ata/ns-ata-_identify_device_data), [T13 ACS-3 working-draft index, d2161r5](https://www.t13.org/docsearch?title=ATA%2FATAPI+Command+Set+-+3), ACS-3 §7.27 READ LOG EXT / Annex A Device Statistics and ACS-4 §9.5. A manufacturer's [ATA implementation specification](https://documents.westerndigital.com/content/dam/doc-library/en_us/assets/public/western-digital/product/data-center-drives/ultrastar-dc-hc600-series/product-manual-ultrastar-dc-hc670-sata-spec.pdf) also documents the task-file layout.

### Supported SMART mappings

Mappings are deliberately narrow. Attribute IDs alone never identify the units.

| Model / firmware | Host counter | Raw format / conversion | Known power-on hours |
|---|---|---|---|
| `CT1000MX500SSD1`, `M3CR` followed by three digits | 246 / F6 cumulative host sectors | unsigned little-endian raw48 × **512 bytes** | raw48 attribute 9, hours |
| Intel SSD 320 2.5-inch `INTEL SSDSA2CW{040,080,120,160,300,600}G3...` | 225 / E1 Host Writes | unsigned little-endian raw48 × **33,554,432 bytes** (65,536 × 512; 32 MiB) | raw48 attribute 9, hours; DIPM may exclude slumber |

Sources: Micron **TN-FD-22**, client SATA SMART attribute reference, and [Micron Storage Executive user guide, SMART table](https://assets.micron.com/adobe/assets/urn%3Aaaid%3Aaem%3A71922363-0144-4893-baae-5ecfa6f5e0b2/original/as/2400text.pdf) (246 = 512-byte sectors; 9 = hours); the MX500 interpretation has also been checked through native ATA telemetry. Intel [SSD 320 Product Specification, 325152-002US, Table 12](https://www.intel.de/content/dam/www/public/us/en/documents/product-specifications/ssd-320-specification.pdf) defines its different E1 units. Intel's documented units do not vary by firmware, so that mapping has no firmware restriction. No other SMART family is guessed from these attribute IDs.

**MX500 M3CR043 exception found on real hardware:** Device Statistics marks its counter valid/supported but may return only the low 32 bits of F6. A synthetic example is `112500000000 mod 2^32 = 830850304`; the public fixtures use fabricated counts rather than private device readings. For this exact model / firmware / 512-byte logical-sector combination, the reader cross-checks the full-width SMART mapping. When modulo truncation is confirmed (with a second standard sample to bracket intervening writes if needed), it treats the truncated value as unusable for lifetime writes and uses F6, with an explicit detail message. Standard statistics remain preferred when usable and full-width validation agrees. An unresolved discrepancy leaves lifetime telemetry unavailable rather than inventing high bits. This exception is fixture-tested and hardware-tested; it is not applied to unrelated firmware.

The exact `CT1000MX500SSD1` model with a matching ~1 TB capacity gets an initial **360 TBW** rating, based on [Crucial's manufacturer flyer](https://www.crucial.com/content/dam/crucial/ssd-products/mx500/flyer/crucial-mx500-ssd-productflyer-letter-en.pdf). Ratings remain editable; clearing one persists unknown. Existing user ratings are never overwritten. No Intel or unknown-model TBW is inferred. Enter **1200** for a Crucial P5 Plus 2 TB manually.

### Add a documented model mapping

Add an `AtaCounterMapping` in `AtaMappings.All` with a narrow model expression, firmware constraint where applicable, attribute, explicit raw format/width, bytes per raw unit, known hours attribute and its units, a primary documentation link, and a new versioned counter-source ID. Current registry formats are unsigned raw48; implement a separate decoder for different layouts rather than repurposing that field. Never map normalized health, NAND writes or page-program counters as host writes. Add binary fixtures for known nonzero and zero values, unknown model/firmware, missing attribute, malformed checksum/length and source changes. Verify against native reads on matching hardware before claiming hardware support. Put firmware-specific exceptions behind equally narrow, documented predicates with reproducible evidence.

## Numbers, baselines and missing lifetime data

All GB/TB are decimal. The clock is `host bytes / (TBW × 10^12) × 80 years`, flooring to whole months for display. Session clock duration uses 365.2425 days/year. 48 TB / 1,200 TB = 4% = 3 years, 2 months. 12 GB / 1,200 TB = 0.001% ≈ 7.01 clock hours. The clock keeps running past 80 years; at 100% it says **Rated write budget reached**. It is not a drive health/failure prediction.

Every snapshot stores its counter source and conversion version. Deltas, partial-day writes, averages, app-run totals, pulses, compaction and sessions compare **only compatible source IDs on the same stable drive**. Counter decreases or source changes preserve history, create a fresh baseline and invalidate a lifetime-counter session. No negative writes or cross-source delta is invented. Old source-less snapshots migrate as the original NVMe source, preserving their histories. Compact snapshots keep first/latest endpoints per minute while preserving discontinuities.

Launch includes monotonic writes since the previous stored reading. A cross-day interval is one delta with **daily allocation unknown**; it is never spread over unobserved days. Today shows only the measured portion since today's first sample. Average and measured forecast require 24 hours of compatible monotonic history and use up to 30 days, displaying the actual window. The projection is **Time to rated write limit at current pace**: remaining rated bytes / recorded calendar-day pace / 365.2425. Zero pace has no finite forecast. An unknown rating disables the clock/projection but keeps counters and sessions usable.

When there is no recognized lifetime counter, the SATA drive stays selectable. The clock says unavailable; any last good bytes/timestamp remain visible. ETW/process activity remains available where privileged tracing works and is labeled **observed writes**, never substituted into lifetime bytes. An observed ETW session is available after a successful telemetry query that confirms lifetime-counter unavailability; ordinary failed fresh reads do not silently start a lifetime session. Both lifetime and observed sessions include whole-PC writes to the drive. ETW boundaries and missing periods are approximate; observed sessions are labeled accordingly.

The tray switches to explicitly observed ETW totals when lifetime telemetry is unavailable. A failed poll after a good lifetime reading keeps the last good clock/numbers with a stale status. A GB pulse uses whole additional GB since the app's first compatible reading; large jumps produce one pulse and delta. Offline writes do not cause a launch pulse.

## Process attribution and lifetime estimates

Write sources defaults on, with a saved toggle. Live ETW needs administrator rights; without them SMART continues. Recording continues when the panel is collapsed or the window is in the tray. Each card has scopes This run, Recorded totals and Current / last session. Sessions can run independently on multiple drives. Only observed periods contribute; no pre-app/offline process history is reconstructed. Unique ETW sessions enable disk I/O/init and process/thread metadata, with an 8 MB requested buffer and one consumer thread. No file-name tracing, stacks, command lines or raw ETL files are recorded. Work scales with system I/O; no overhead benchmark is claimed. Normal exit disposes the session; forcibly killing an ETW tool can leave an orphan until cleanup/reboot.

ETW disk-write bytes and SMART controller host-write bytes are separate measurements and need not reconcile. Cached writes may be System / unattributed; event loss is displayed as incomplete. Executable instances aggregate by name. The top eight are shown, others summarized; persistent dictionaries cap named entries at 1,000 before grouping further names into Other / unattributed. % TBW is that observed byte amount / rated bytes. See [Microsoft on cached writes](https://devblogs.microsoft.com/oldnewthing/20201126-00/?p=104488) and [kernel tracing permissions](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracea).

Drive details show power-on hours, Tracking began, and an approximate **first use on or before** the last successful reading minus accumulated power-on hours. This assumes an accurate, unreset counter; powered-off/slumber time means actual first use may be earlier. It is not a recorded installation date.

Lifetime inferred average = lifetime bytes / (power-on hours / 24), including pre-app writes, labeled GB per **powered-on** day. Actual calendar-day pace may be lower. The first positive hours/counter pair is saved as a baseline; its separate projection explicitly assumes 24 h/day powered on and uses the latest remaining budget. A decrease/source change resets that baseline. It is never silently substituted into the measured calendar-day forecast.

## Local data and verification

`Data\history.json` beside the EXE contains ratings, timestamped source-tagged counters, hours baselines and process aggregates. This is relative to the executable, not the shell working directory or the runtime extraction cache. The folder is created automatically and must be writable. Move/copy the `Data` folder along with the EXE to keep that portable history; sharing only the EXE starts a new portable history on another PC (unless that user has a legacy history to import). On the first launch with no portable history, an existing `%LOCALAPPDATA%\DiskHeartbeat\history.json` is imported automatically, with the original left unchanged. An existing portable history always wins; corrupt files are preserved and reported rather than silently overwritten. Writes use atomic replacement plus a previous `.bak`. Corrupt history is preserved with recording disabled and a visible error. Save failures stay visible. For isolated testing/portable storage set `DISKHEARTBEAT_DATA_DIR` to a writable directory. Run one app per data directory. No telemetry/network is used by the running app.

Build/tests use only the .NET SDK, built-in Windows APIs/tools and checked-in binary fixtures:

```powershell
dotnet run --project .\tools\Checks.csproj -c Release
$env:DISKHEARTBEAT_DATA_DIR = Join-Path $PWD '.tools\new-sata-ui-test'
dotnet run --project .\tools\Checks.csproj -c Release -- --ui-sata-fixtures
# All-drive card / session / tray checks use two injected fixture drives:
$env:DISKHEARTBEAT_DATA_DIR = Join-Path $PWD '.tools\new-dashboard-test'
dotnet run --project .\tools\Checks.csproj -c Release -- --ui-smoke
Remove-Item Env:DISKHEARTBEAT_DATA_DIR
# Read-only native probe; SATA generally needs elevation:
dotnet run --project .\tools\Checks.csproj -c Release -- --probe
```

Fixtures are synthetic 512-byte IDENTIFY/statistics/SMART payloads plus x64 pass-through response buffers. Recreate with `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\GenerateFixtures.ps1`. No SMART utility, vendor SDK, third-party test runner or driver is used. Hardware verification script `tools/VerifySata.ps1` uses built-in inventory and the same production reader around a unique 64 MiB copy to the explicitly supplied target volume, then removes its test files; run it elevated after building the Checks project. It refuses a volume whose disk extents do not match the selected MX500. See [VERIFICATION.md](VERIFICATION.md) for actual hardware paths, captured counts, firmware exception and fixture coverage.





To build a distributable ZIP with the EXE, startup instructions, third-party notices, and a SHA-256 checksum:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\package-release.ps1
```

Release packages are written to `artifacts/` and never committed. Source control includes code, scripts, documentation, and the small synthetic binary ATA fixtures; it excludes the SDK/tool workspace, compiled outputs, archives, user credentials, and metric data.
