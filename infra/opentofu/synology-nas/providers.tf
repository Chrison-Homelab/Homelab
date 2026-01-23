terraform {
  required_providers {
    synology = {
      source  = "synology-community/synology"
      version = "0.6.9"
    }
  }
}

provider synology {
  host = var.synology_endpoint
  user= var.synology_username
  password = var.synology_password

  # Set to true if using self-signed certs
  skip_cert_check = true
}
