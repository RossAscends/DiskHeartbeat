# Recreate deterministic synthetic ATA binary fixtures. No hardware or external utility is used.
$ErrorActionPreference='Stop'
$fixtureDir=Join-Path $PSScriptRoot 'fixtures'
New-Item -ItemType Directory -Force $fixtureDir | Out-Null
function SaveBytes([string]$name,[byte[]]$data) {[IO.File]::WriteAllBytes((Join-Path $fixtureDir $name),$data)}
function SetWord([byte[]]$data,[int]$word,[uint16]$value) {[BitConverter]::GetBytes($value).CopyTo($data,$word*2)}
function SetText([byte[]]$data,[int]$word,[int]$words,[string]$text) {
 $encoded=[Text.Encoding]::ASCII.GetBytes($text.PadRight($words*2))
 for($i=0;$i -lt $words;$i++) {$data[($word+$i)*2]=$encoded[$i*2+1];$data[($word+$i)*2+1]=$encoded[$i*2]}
}
function Identify([string]$model,[string]$firmware,[bool]$large=$false) {
 $data=New-Object byte[] 512; SetWord $data 0 0x40;SetWord $data 217 1;SetWord $data 106 0x4000
 SetText $data 27 20 $model;SetText $data 23 4 $firmware;SetText $data 10 10 'fixture-serial-001'
 if($large){SetWord $data 106 0x5000;SetWord $data 117 2048}
 return ,$data
}
function Smart([byte]$attribute,[uint64]$value) {
 $data=New-Object byte[] 512;SetWord $data 0 16
 $data[2]=$attribute;$data[3]=0x32;$data[5]=100;$data[6]=100
 for($i=0;$i -lt 6;$i++){$data[7+$i]=[byte](($value -shr ($i*8)) -band 255)}
 $data[14]=9;$data[17]=100;$data[18]=100;$data[19]=0x10;$data[20]=0x27
 $sum=0;foreach($v in $data){$sum+=$v};$data[511]=[byte]((256-($sum -band 255)) -band 255)
 return ,$data
}
SaveBytes 'identify-mx500.bin' (Identify 'CT1000MX500SSD1' 'M3CR046')
SaveBytes 'identify-mx500-quirk.bin' (Identify 'CT1000MX500SSD1' 'M3CR043')
SaveBytes 'identify-4kn.bin' (Identify 'CT1000MX500SSD1' 'M3CR046' $true)
SaveBytes 'identify-intel320.bin' (Identify 'INTEL SSDSA2CW080G3' '4PC10362')
SaveBytes 'identify-unknown.bin' (Identify 'Unknown SSD' 'X001')
SaveBytes 'smart-mx500.bin' (Smart 246 112500000000)
SaveBytes 'smart-zero.bin' (Smart 246 0)
SaveBytes 'smart-intel320.bin' (Smart 225 10)
SaveBytes 'smart-nand-only.bin' (Smart 248 112500000000)
$stats=New-Object byte[] 512;SetWord $stats 0 1;$stats[2]=1
[BitConverter]::GetBytes([uint64]112500000000).CopyTo($stats,24);$stats[31]=0xC0
[BitConverter]::GetBytes([uint64]10000).CopyTo($stats,16);$stats[23]=0xC0
SaveBytes 'statistics-valid.bin' $stats
$wrapped=$stats.Clone();[BitConverter]::GetBytes([uint64](112500000000 % 4294967296)).CopyTo($wrapped,24);$wrapped[31]=0xC0;SaveBytes 'statistics-wrapped.bin' $wrapped
$zero=$stats.Clone();[Array]::Clear($zero,24,6);SaveBytes 'statistics-zero.bin' $zero
$invalid=$stats.Clone();$invalid[31]=0x80;SaveBytes 'statistics-invalid.bin' $invalid
$unsupported=$stats.Clone();$unsupported[31]=0;SaveBytes 'statistics-unsupported.bin' $unsupported
$normalized=$stats.Clone();$normalized[31]=0xE0;SaveBytes 'statistics-normalized.bin' $normalized
SaveBytes 'statistics-truncated.bin' ([byte[]]$stats[0..510])
$smart=Smart 246 112500000000;SaveBytes 'smart-truncated.bin' ([byte[]]$smart[0..510])
$bad=$smart.Clone();$bad[7]=$bad[7] -bxor 1;SaveBytes 'smart-bad-checksum.bin' $bad
# Full x64 ATA_PASS_THROUGH_EX response, with a successful SMART READ DATA task file.
$response=New-Object byte[] 560
[BitConverter]::GetBytes([uint16]48).CopyTo($response,0);[BitConverter]::GetBytes([uint16]3).CopyTo($response,2)
[BitConverter]::GetBytes([uint32]512).CopyTo($response,8);[BitConverter]::GetBytes([uint64]48).CopyTo($response,24)
$response[46]=0x50;$smart.CopyTo($response,48);SaveBytes 'pass-through-x64.bin' $response
$errorResponse=$response.Clone();$errorResponse[46]=0x51;$errorResponse[40]=4;SaveBytes 'pass-through-error-x64.bin' $errorResponse
SaveBytes 'pass-through-truncated-x64.bin' ([byte[]]$response[0..558])
