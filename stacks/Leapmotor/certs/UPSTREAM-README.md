# Leapmotor API Certificates

TLS client certificates required for communicating with Leapmotor cloud APIs (mTLS).

Extracted via reverse engineering of the official Leapmotor app.

## Files

| File | Description |
|------|-------------|
| `app_crt.pem` | Client certificate (PEM) |
| `app_key.pem` | Private key (PEM) |

## Usage

These certificates are used by [LeapConnect](https://github.com/markoceri/leapconnect) and the [leapmotor-api](https://github.com/markoceri/leapmotor-api) Python client.

During LeapConnect setup, upload these two files in the **Certificate Setup** step.

## Disclaimer

These files are provided for interoperability and research purposes only. Use at your own risk.
