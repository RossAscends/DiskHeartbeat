# ATA binary fixtures

These files are deterministic **synthetic** byte responses, not captures from a vendor utility. Regenerate with `powershell -NoProfile -ExecutionPolicy Bypass -File tools/GenerateFixtures.ps1` from the repository root. The generator uses only built-in PowerShell / .NET byte-array operations and computes SMART checksums.

- IDENTIFY sectors contain synthetic serial `fixture-serial-001`, word-swapped model/firmware, nonrotating-media word 217, and 512 / 4096-byte logical-sector encodings.
- Device Statistics: revision 1, page 1, power-on hours at 0x10 (10000), logical sectors written at 0x18 (112500000000), flags supported+valid. Variants are zero, unsupported, invalid, normalized, truncated, and a low-32-bit M3CR043 response.
- MX500 SMART: revision 16, attribute 246 = raw48 little-endian 112500000000, attribute 9 = 10000 hours; zero, truncated, bad checksum and NAND-only (248) variants.
- Intel 320 SMART: E1 = 10 raw units, each 32 MiB per Intel 325152-002US Table 12.
- Pass-through buffers have an x64 48-byte header, data offset 48, 512-byte data-in transfer and status 0x50. Variants contain ERR/ABRT or a truncated transfer. Payload fixtures are architecture-independent; x64 envelope checks run only in a 64-bit process.

`AtaFixtureChecks` checks parsing, transport validation, fallback preference, model/firmware constraints, zero, unrecognized/NAND data, stable routing and source continuity. `SataUiChecks` injects a scripted reader on the production WPF dashboard to verify stale errors, source-change sessions, zero/reset, unavailable lifetime telemetry and independently observed process activity. The code runs with the .NET SDK and the application's existing managed references, without external test tools.
