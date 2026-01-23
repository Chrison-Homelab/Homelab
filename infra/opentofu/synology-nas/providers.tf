terraform {
  required_providers {
    synology = {
      source  = "synology-community/synology"
      version = "0.6.9"
    }
  }
}

provider synology {
  endpoint = var.synology_endpoint
  username = var.synology_username
  password = var.synology_password

  # Set to true if using self-signed certs
  insecure = true
}
