# UnderlayCopy

`UnderlayCopy` is now a .NET console application (C#) for low-level NTFS acquisition and dumping protected/locked forensic artifacts.

It supports two modes without using VSS or standard high-level file copy APIs:
- **MFT mode**: parses `$MFT` records and reconstructs/copies file data by reading raw volume sectors.
- **Metadata mode**: uses filesystem metadata (`fsutil file queryextents`) to map file clusters and copy raw sectors.

## Purpose
Research, red-team exercises, and DFIR acquisition.

> Not for unauthorized access or malicious use.

## Requirements
- Windows host
- Administrator privileges
- .NET 8 SDK/runtime
- NTFS volume access (default `\\.\C:`)

## Build
```powershell
dotnet build
```

## Usage
```powershell
dotnet run -- --mode MFT --source C:\Windows\System32\config\SAM --destination C:\Temp\SAM.dmp

dotnet run -- --mode Metadata --source C:\Windows\NTDS\ntds.dit --destination C:\Temp\ntds.dmp
```

Optional:
```powershell
dotnet run -- --mode MFT --source <sourcePath> --destination <destPath> --volume \\.\D:
```

## Notes for defenders
- Monitor raw volume reads (`\\.\PhysicalDriveN`, `\\.\C:` etc.).
- Alert on suspicious access to protected files and $MFT parsing behavior.
- Track unusual use of `fsutil file queryextents`.
