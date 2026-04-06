# UnderlayCopy 


**UnderlayCopy** is a PowerShell utility for low-level NTFS acquisition and dumping protected, locked system FORENSIC artifacts. It supports two complementary modes to achieve this without using **VSS** or standard file I/O:
 - **MFT mode**: parse $MFT records and reconstructs/copies file data by reading raw volume sectors.
 - **Metadata mode**: use filesystem metadata (fsutil) to map files to clusters and copy raw sectors.
 
 **Purpose**: research, red-team exercises, and DFIR acquisition.
 **Not for**: unauthorized access or malicious use.
 
 
 ## Features
 - Supports local NTFS volumes.
 - Operates without relying on VSS or standard file I/O APIs.
 

## Usage

### Prerequisites
- Administrator privileges

 ### Example
```powershell
   .\UnderlayCopy.ps1
   Underlay-Copy -Mode MFT -SourceFile C:\Windows\lock -DestinationFile C:\Windows\Temp\lock.dmp
   Underlay-Copy -Mode MFT -SourceFile C:\Windows\lock -DestinationFile C:\Windows\Temp\lock.dmp
   Underlay-Copy -Mode Metadata -SourceFile C:\Windows\NTDS\lock.dit -DestinationFile C:\Windows\Temp\locl.dmp
```



## Auditing and Detection
 - Monitor for raw volume reads (handles opened to \\.\PhysicalDriveN or \\.\C: with RAW access)
 - Monitor direct access to sensitive system files (for example: `C:\Windows\System32\lock`) and alert on unexpected reads or copies of these files.
 - Alert on processes reading $MFT or performing large unbuffered reads on physical volumes.
 - Track unusual use of fsutil and suspicious calls to low-level Win32 APIs (CreateFile with volume or physical path).


