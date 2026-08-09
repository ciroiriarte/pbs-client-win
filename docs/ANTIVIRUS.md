# Antivirus & EDR Exclusion Guide for pbs-client-win

This guide details how Microsoft Defender exclusions are handled by the installer, why antivirus/EDR exclusions are critical for backup performance, and how enterprise administrators can manually configure process and path exclusions across major security products.

---

## 1. Overview & Rationale

### Why Antivirus Exclusions Are Necessary
Backup agents process massive volumes of file I/O, generate fixed/dynamic chunk streams, calculate cryptographic hashes (SHA-256), and interact with Volume Shadow Copy (VSS) snapshots. 

When real-time on-access scanning monitors these operations:
- **Performance Degradation**: Real-time scanning introduces an estimated **~25% throughput penalty** and elevated CPU consumption during backup jobs.
- **Lock Contention**: Antivirus file locks on temporary chunk caches or log buffers in `%ProgramData%\pbs-client-win\` can lead to transient I/O exceptions.
- **False Positives**: Rapid generation of binary chunk streams can occasionally trigger heuristic entropy alerts.

> [!NOTE]
> All recommended exclusions are strictly scoped to the `pbs-client-win` binaries and working directories. They do NOT lower global system protection.

---

## 2. Automated Microsoft Defender Exclusions (MSI Installer)

### What the Installer Automates
The WiX v4 installer (`pbs-client-win.msi`) includes a deferred custom action that runs during installation to add Microsoft Defender exclusions automatically via PowerShell:

```powershell
Add-MpPreference -ExclusionProcess "pbs-client-win.exe", "Pbs.Service.exe" -ExclusionPath "C:\ProgramData\pbs-client-win\"
```

Upon uninstallation, the installer automatically cleans up these exclusions:

```powershell
Remove-MpPreference -ExclusionProcess "pbs-client-win.exe", "Pbs.Service.exe" -ExclusionPath "C:\ProgramData\pbs-client-win\"
```

### Key Installer Behavior
1. **Best-Effort / Non-Fatal**: If Microsoft Defender is disabled, managed via Group Policy, or if PowerShell fails, the custom action logs a warning and allows installation to complete normally.
2. **Opt-Out for Administrators**: Enterprise administrators can disable automated exclusion setup during silent or interactive installation by setting `AV_EXCLUSIONS=0`:

```cmd
msiexec /i pbs-client-win-1.0.0-x64.msi AV_EXCLUSIONS=0 /qn
```

---

## 3. Manual & Group Policy Setup for Microsoft Defender

### A. Manual Configuration via Windows Security UI
1. Open **Windows Security** (`start windowsdefender:`).
2. Click **Virus & threat protection**.
3. Under *Virus & threat protection settings*, click **Manage settings**.
4. Scroll to *Exclusions* and click **Add or remove exclusions**.
5. Click **Add an exclusion**:
   - **Process**: `pbs-client-win.exe`
   - **Process**: `Pbs.Service.exe`
   - **Folder**: `C:\Program Files\pbs-client-win\`
   - **Folder**: `C:\ProgramData\pbs-client-win\`

### B. Command-Line / PowerShell Setup
Run an elevated PowerShell prompt:

```powershell
# Process Exclusions
Add-MpPreference -ExclusionProcess "pbs-client-win.exe"
Add-MpPreference -ExclusionProcess "Pbs.Service.exe"

# Path Exclusions
Add-MpPreference -ExclusionPath "C:\Program Files\pbs-client-win"
Add-MpPreference -ExclusionPath "C:\ProgramData\pbs-client-win"
```

### C. Active Directory Group Policy (GPO)
1. Open `gpmc.msc` and edit your targeted Group Policy Object.
2. Navigate to:
   `Computer Configuration` > `Policies` > `Administrative Templates` > `Windows Components` > `Microsoft Defender Antivirus` > `Exclusions`
3. Configure the policies:
   - **Process Exclusions**: Enable and add:
     - `C:\Program Files\pbs-client-win\pbs-client-win.exe` = `0`
     - `C:\Program Files\pbs-client-win\Pbs.Service.exe` = `0`
   - **Path Exclusions**: Enable and add:
     - `C:\Program Files\pbs-client-win\` = `0`
     - `C:\ProgramData\pbs-client-win\` = `0`

---

## 4. Third-Party Antivirus & EDR Configuration

### CrowdStrike Falcon
1. Log into the **CrowdStrike Falcon Console**.
2. Navigate to **Configuration** > **Sensor Policies** > **Exclusions**.
3. **Executable Path Exclusion**:
   - Path: `\Device\HarddiskVolume*\Program Files\pbs-client-win\*`
   - Exclusion Type: `Process Rollup` & `File Inspection`
4. **Process Exclusion**:
   - Add process: `pbs-client-win.exe` and `Pbs.Service.exe`

### SentinelOne
1. Log into the **SentinelOne Management Console**.
2. Go to **Sentinels** > **Policies** > **Exclusions**.
3. Add Exclusion:
   - **Type**: Path
   - **Mode**: Performance / Suppress Alerts
   - **Path**: `C:\Program Files\pbs-client-win\*`
   - **Include Subdirectories**: Yes
4. Add Second Exclusion:
   - **Path**: `C:\ProgramData\pbs-client-win\*`
   - **Include Subdirectories**: Yes

### Sophos (Intercept X / Sophos Central)
1. Log into **Sophos Central Admin**.
2. Go to **Global Settings** > **Scanning Exclusions**.
3. Click **Add Exclusion**:
   - **Exclusion Type**: Folder
   - **Value**: `C:\Program Files\pbs-client-win\` and `C:\ProgramData\pbs-client-win\`
4. Add Process Exclusion:
   - **Exclusion Type**: Process (Windows)
   - **Value**: `C:\Program Files\pbs-client-win\pbs-client-win.exe`
   - **Value**: `C:\Program Files\pbs-client-win\Pbs.Service.exe`

### ESET Endpoint Security / ESET NOD32
1. Open the ESET GUI and press **F5** to open **Advanced Setup**.
2. Expand **Detection Engine** > **Exclusions**.
3. Under **Performance Exclusions**, click **Edit** and add:
   - `C:\Program Files\pbs-client-win\*.*`
   - `C:\ProgramData\pbs-client-win\*.*`
4. Under **Processes Exclusions**, add:
   - `C:\Program Files\pbs-client-win\pbs-client-win.exe`
   - `C:\Program Files\pbs-client-win\Pbs.Service.exe`

---

## 5. Summary Matrix of Exclusions

| Target Binary / Directory | Exclusion Type | Purpose | Recommended Policy |
| :--- | :--- | :--- | :--- |
| `C:\Program Files\pbs-client-win\pbs-client-win.exe` | Process / Executable | High-throughput backup & restore operations | Exclude from real-time on-access scanning |
| `C:\Program Files\pbs-client-win\Pbs.Service.exe` | Process / Service | Supervisor process launching CLI subprocesses | Exclude from real-time process monitoring |
| `C:\Program Files\pbs-client-win\` | Directory / Path | Installation directory for CLI and Service binaries | Exclude path |
| `C:\ProgramData\pbs-client-win\` | Directory / Path | Working directory, config file, and logs | Exclude path |
