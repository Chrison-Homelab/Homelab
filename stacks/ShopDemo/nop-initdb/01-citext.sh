#!/bin/bash
# nopCommerce's PostgreSQL schema uses the `citext` (case-insensitive text) type.
# Its installer DROPs and recreates the target database from `template1`, so the
# extension must live in template1 — otherwise the recreated DB loses it and the
# install fails with: type "citext" does not exist (42704).
#
# Runs once, on first init of an empty data dir (docker-entrypoint-initdb.d).
set -e
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname template1 \
  -c "CREATE EXTENSION IF NOT EXISTS citext;"
# Also enable it in the pre-created nopcommerce DB for good measure.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -c "CREATE EXTENSION IF NOT EXISTS citext;"
