# MSIX packaging (local dev)

## Signing certificate

- `dev-msix.pfx` is **gitignored** (see repo root `.gitignore`). Never commit a private key.
- Create a local test cert with `scripts/new-msix-dev-cert.ps1` (or your org’s signing process) and place the `.pfx` only on your machine.

## CI

- GitHub Actions (`.github/workflows/ci-msix.yml`) generates a **temporary** self-signed certificate in the runner, publishes the MSIX with that cert, and does **not** rely on `dev-msix.pfx`.
- Production/release signing should use a **secret store** (e.g. Azure Key Vault, GitHub encrypted secret + import) or a hardware token — not a file in the repo.
