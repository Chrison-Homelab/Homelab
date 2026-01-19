# Bash Script Template

```bash
#!/usr/bin/env bash
set -euo pipefail
IFS=$'\n\t'

SCRIPT_NAME=${0##*/}

print_help() {
  cat <<EOF
${SCRIPT_NAME} - <short description>

Usage:
  ${SCRIPT_NAME} [options]

Options:
  --config PATH      Path to configuration file
  --dry-run          Show what would be done, without making changes
  --help             Show this help message and exit

Examples:
  ${SCRIPT_NAME} --config /etc/homelab/config.yml
EOF
}

log_info()  { printf '[INFO] %s\n' "$*" >&2; }
log_warn()  { printf '[WARN] %s\n' "$*" >&2; }
log_error() { printf '[ERROR] %s\n' "$*" >&2; }

CONFIG_PATH=""
DRY_RUN=false

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --config)
        CONFIG_PATH=${2:-}
        shift 2
        ;;
      --dry-run)
        DRY_RUN=true
        shift
        ;;
      --help)
        print_help
        exit 0
        ;;
      *)
        log_error "Unknown argument: $1"
        print_help
        exit 1
        ;;
    esac
  done
}

main() {
  parse_args "$@"

  if [[ -z "${CONFIG_PATH}" ]]; then
    log_error "Missing required --config"
    exit 1
  fi

  log_info "Using config: ${CONFIG_PATH}"
  if [[ "${DRY_RUN}" == true ]]; then
    log_info "Running in dry-run mode; no changes will be made."
  fi

  # TODO: implement core logic here
}

main "$@"
