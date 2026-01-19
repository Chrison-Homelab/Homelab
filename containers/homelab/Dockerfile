# Use Debian 13 (Trixie) as base image
FROM debian:trixie

# Set environment variables
ENV DEBIAN_FRONTEND=noninteractive
ENV LANG=C.UTF-8

# Update package lists and install basic utilities
RUN apt-get update && apt-get install -y \
    curl \
    wget \
    ca-certificates \
    gnupg \
    lsb-release \
    apt-transport-https \
    software-properties-common \
    sudo \
    bash \
    util-linux \
    procps \
    net-tools \
    iproute2 \
    pciutils \
    dmidecode \
    nfs-common \
    && rm -rf /var/lib/apt/lists/*

# Install PowerShell Core
RUN curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg && \
    echo "deb [arch=amd64,armhf,arm64 signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/microsoft-debian-bullseye-prod bullseye main" > /etc/apt/sources.list.d/microsoft.list && \
    apt-get update && \
    apt-get install -y powershell && \
    rm -rf /var/lib/apt/lists/*

# Create a working directory
WORKDIR /homelab

# Create a test user (optional, for non-root testing)
RUN useradd -m -s /bin/bash testuser && \
    echo 'testuser ALL=(ALL) NOPASSWD:ALL' >> /etc/sudoers

# Copy scripts into the container
COPY src/Proxmox/*.sh /homelab/
COPY src/Proxmox/*.ps1 /homelab/

# Make scripts executable
RUN chmod +x /homelab/*.sh /homelab/*.ps1

# Set default command
CMD ["/bin/bash"]