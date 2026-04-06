# Configuring your machine name for JsonPit

JsonPit uses `Environment.MachineName` as part of its flag file naming scheme.
Each machine+application combination gets its own process flag file:
`{MachineName}-{Subscriber}.flag`, e.g. `Nkosikazi-pits.flag`, `Mzansi-RAIkeep.flag`.

If your machine has a generic hostname like `ubuntu`, `localhost`, or `DESKTOP-A1B2C3D`,
flag file collisions will occur when multiple machines access the same pit.

## Requirements

- **Unique**: No two machines accessing the same cloud-synced pit should share a name.
- **Memorable**: You'll see it in flag files and logs. Pick something you recognize.
- **Short**: Avoid special characters and spaces. Stick to letters, digits, and hyphens.

## macOS

```bash
# Check current name
scutil --get ComputerName
scutil --get LocalHostName

# Set both (they serve different purposes but should match)
sudo scutil --set ComputerName "Nkosikazi"
sudo scutil --set LocalHostName "Nkosikazi"
sudo scutil --set HostName "Nkosikazi"
```

`Environment.MachineName` reads from `HostName` (falling back to `LocalHostName`).
No reboot required — changes take effect for new processes immediately.

## Ubuntu / Debian / Linux

```bash
# Check current name
hostname
cat /etc/hostname

# Set it (persists across reboots)
sudo hostnamectl set-hostname Mzansi

# Verify
hostnamectl
```

Also update `/etc/hosts` so the new name resolves locally:

```bash
sudo sed -i "s/127.0.1.1.*/127.0.1.1\tMzansi/" /etc/hosts
```

No reboot required — new shells and processes pick it up immediately.

### Cloud instances (AWS EC2, Azure, GCP)

Cloud VMs typically get auto-generated hostnames like `ip-172-31-42-17`.
Use `hostnamectl set-hostname` as above. Some cloud providers reset the hostname
on reboot — to prevent this:

**AWS EC2** — edit `/etc/cloud/cloud.cfg` and set:
```yaml
preserve_hostname: true
```

**Azure** — edit `/etc/waagent.conf` and set:
```ini
Provisioning.MonitorHostName=n
```

**GCP** — hostname persists by default after `hostnamectl set-hostname`.

## Windows

```powershell
# Check current name
$env:COMPUTERNAME

# Set it (requires reboot)
Rename-Computer -NewName "MyDevBox" -Force -Restart
```

Or via GUI: Settings > System > About > Rename this PC.

## Docker containers

Containers default to a random hex ID as hostname. Set it explicitly:

```bash
docker run --hostname Mzansi ...
```

Or in `docker-compose.yml`:

```yaml
services:
  myapp:
    hostname: Mzansi
```

## Verifying

After renaming, verify from .NET:

```csharp
Console.WriteLine(Environment.MachineName);
```

Or run the JsonPit test suite — `ValidateMachineName_CurrentMachineHasProperName`
will fail if your hostname is generic.
