variable "synology_endpoint" {
  type        = string
  description = "URL or IP of the Synology DSM API endpoint"
}

variable "synology_username" {
  type        = string
  description = "DSM username"
}

variable "synology_password" {
  type        = string
  description = "DSM password"
  sensitive   = true
}
