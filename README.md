# Homelab

My Homelab

## Development Environment

In order to develop in an isolated dev environment (and not in prod ;-)) there is a few docker containers I spin up to test my scripts against:

**Proxmox:**
Currently there is no easy way to containerize this. MacOS is ARM64 so you wont be able to run Proxmox in a container. Debian 13 is the way to go.
However, on Linux & Windows you can run a container
[Containerized Proxmox](https://github.com/LongQT-sea/containerized-proxmox/)

```yml
# Common option
x-service: &systemd
  restart: unless-stopped
  stdin_open: true
  tty: true
  cgroup: private
  device_cgroup_rules:
    - "a *:* rwm"
  cap_add:
    - ALL
  security_opt:
    - seccomp=unconfined
    - apparmor=unconfined
    - systempaths=unconfined

x-pve-service: &pve-systemd
  <<: *systemd
  image: ghcr.io/longqt-sea/proxmox-ve
  volumes:
    - /usr/lib/modules:/usr/lib/modules:ro        # Required for loading kernel modules
    - /sys/kernel/security:/sys/kernel/security   # Optional, needed for LXC
    - ./VM-Backup:/var/lib/vz/dump                # Shared storage for VM/LXC backups
    - ./ISOs:/var/lib/vz/template/iso             # Shared storage for ISO files

# Set default root password
x-env: &password
  PASSWORD: "123"


services:
  # First node
  pve-1:
    container_name: pve-1
    hostname: pve-1
    <<: *pve-systemd
    environment:
      <<: *password
    networks:
      dual_stack:
        ipv4_address: 10.0.99.1
        ipv6_address: fd00::1

    # Port mapping only required for Docker Desktop or remote access from other machines.
    ports:
      - "2222:22"
      - "3128:3128"
      - "8006:8006"   # First node container port 8006 maps to host port 8006


  # Second node
  pve-2:
    container_name: pve-2
    hostname: pve-2
    <<: *pve-systemd
    environment:
      <<: *password
    networks:
      dual_stack:
        ipv4_address: 10.0.99.2
        ipv6_address: fd00::2

    # Port mapping only required for Docker Desktop or remote access from other machines.
    ports:
      - "2223:22"
      - "3129:3128"
      - "8007:8006"   # Second node container port 8006 maps to host port 8007


  # Third node
  pve-3:
    container_name: pve-3
    hostname: pve-3
    <<: *pve-systemd
    environment:
      <<: *password
    networks:
      dual_stack:
        ipv4_address: 10.0.99.3
        ipv6_address: fd00::3

    # Port mapping only required for Docker Desktop or remote access from other machines.
    ports:
      - "2224:22"
      - "3130:3128"
      - "8008:8006"   # Third node container port 8006 maps to host port 8008


  # Optional: Proxmox Datacenter Manager
  pdm:
    image: ghcr.io/longqt-sea/proxmox-datacenter-manager
    container_name: pdm
    hostname: pdm
    <<: *systemd
    environment:
      <<: *password
    cap_add:
      - SYS_ADMIN
      - NET_ADMIN
    security_opt:
      - seccomp=unconfined
      - apparmor=unconfined
    networks:
      dual_stack:
        ipv4_address: 10.0.99.4
        ipv6_address: fd00::4
    ports:
      - "2225:22"
      - "8443:8443"

# Dual-stack network for this cluster
networks:
  dual_stack:
    enable_ipv6: true
    ipam:
      config:
        - subnet: 10.0.99.0/24
          gateway: 10.0.99.99
        - subnet: fd00::/64
          gateway: fd00::99
```

**Synology DSM:**
Currently there is no easy way to containerize this. MacOS is ARM64 so you wont be able to run DSM in a container.
However, on Linux & Windows you can run a container
[Virtual Synology DSM](https://github.com/vdsm/virtual-dsm)

```yml
services:
  dsm:
    container_name: dsm
    image: vdsm/virtual-dsm
    environment:
      DISK_SIZE: "10G"
      DISK2_SIZE: "10G"
      DISK3_SIZE: "10G"
    devices:
      - /dev/kvm
      - /dev/net/tun
    cap_add:
      - NET_ADMIN
    ports:
      - 5000:5000
    volumes:
      - ./dsm:/storage
      - ./example2:/storage2
      - ./example3:/storage3
    restart: always
    stop_grace_period: 2m
```

## Infrastructure as Code

### Ansible

**Installation:**

1. Install Ansible
`pip install ansible`
2. Install Community.Synology collection
`ansible-galaxy collection install community.synology`

**Proxmox:**
[Ansible Module](https://github.com/ansible-collections/community.proxmox)

**Synology DSM:**
[Ansible Synology DSM](https://github.com/agaffney/ansible-synology-dsm)

## Install PowerShell Script

Installs PowerShell Core on the Proxmox node for running PowerShell scripts.

**WGET:** `bash <(wget -qO- https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)`

**CURL:** `bash <(curl -fsSL https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/install-powershell.sh)`

## Inventory Script

Grabs the Hardware for inventory purposes and outputs it in a MD format

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/inventory.sh)`

Or use the powershell version:

**Using curl:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/inventory.ps1' -UseBasicParsing).Content"
```

**Using wget (download first):**

```bash
wget https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/inventory.ps1 -O /tmp/inventory.ps1 && pwsh /tmp/inventory.ps1
```

## Setup NFS Shares Script

After a longer session with CoPilot, it turns out that setting NFS shares up on the proxmox node itself and sharing it out from there into LXC container is way better for performance. Plus it makes it easier to mount shares, no more messing around with NFS.
This also allows us to at some point add a SSD to the node and use this for caching.

**WGET:** `bash <(wget -qO- https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

**CURL:** `bash <(curl -fsSL https://github.com/ChrisonSimtian/Homelab/blob/main/src/Proxmox/setup-ds1813-shares.sh)`

Or run the powershell version:

**Direct execution:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content"
```

**With custom parameters:**

```bash
pwsh -c "Invoke-Expression (Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/ChrisonSimtian/Homelab/main/src/Proxmox/setup-nfs-shares.ps1' -UseBasicParsing).Content" -- -NasIP "192.168.1.100" -NasName "MyNAS"
```

## Useful links

- [Synology's DSM API](https://global.download.synology.com/download/Document/Software/DeveloperGuide/Package/FileStation/All/enu/Synology_File_Station_API_Guide.pdf)
- [Proxmox API](https://pve.proxmox.com/wiki/Proxmox_VE_API)

## Testing

A Debian 13 container is provided for testing scripts locally before deploying to Proxmox nodes.

**Quick test environment:**
```bash
docker-compose up -d debian-test
docker-compose exec debian-test bash
```

See [TESTING.md](TESTING.md) for detailed testing instructions.
