FROM mcr.microsoft.com/devcontainers/base:ubuntu

# Install updates & required packages
RUN apt-get update

# PowerShell
# RUN apt-get install -y wget apt-transport-https software-properties-common && \
#     wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb && \
#     dpkg -i packages-microsoft-prod.deb && \
#     apt-get update && \
#     apt-get install -y powershell

# Terraform
# RUN apt-get install -y gnupg && \
#     wget -O- https://apt.releases.hashicorp.com/gpg | gpg --dearmor > hashicorp.gpg && \
#     install -o root -g root -m 644 hashicorp.gpg /usr/share/keyrings/ && \
#     echo "deb [signed-by=/usr/share/keyrings/hashicorp.gpg] https://apt.releases.hashicorp.com $(lsb_release -cs) main" > /etc/apt/sources.list.d/hashicorp.list && \
#     apt-get update && \
#     apt-get install -y terraform

# Ansible
# RUN apt-get update && \
#     apt-get install -y python3 python3-pip ssh && \
#     pip3 install ansible && \
#     pip3 install ansible-lint yamllint

# Synology DSM CLI
# RUN ansible-galaxy collection install community.synology