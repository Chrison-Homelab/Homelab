# Contributing to Homelab

Thank you for your interest in contributing to this homelab repository! This document provides guidelines and information to help you contribute effectively.

## Repository Structure

This repository is organized into several key areas:

```
Homelab/
├── src/                    # Automation scripts
├── .containers/            # Development/testing containers
├── infra/                # Infrastructure as Code (Ansible)
├── docs/                 # Documentation
├── .ai/                  # AI assistant conventions and patterns
├── .devcontainer/        # VS Code dev container
└── .github/              # GitHub workflows
```

Each directory contains its own README.md with detailed information.

## Development Workflow

### 1. Local Development Setup

**Option A: Using Dev Container (Recommended)**
```bash
# Open the repository in VS Code
# VS Code will prompt to reopen in container
# Or use Command Palette: "Remote-Containers: Reopen in Container"
```

**Option B: Manual Setup**
```bash
# Install dependencies
# macOS:
brew install ansible docker

# Run the setup script
cd .devcontainer
./setup.sh
```

### 2. Testing Scripts

Always test scripts before deploying to production:

```bash
# Start the testing container
cd .containers/homelab
docker-compose up -d debian-test

# Access the container
docker-compose exec debian-test bash

# Test your scripts
./scripts/your-script.sh
```

See [TESTING.md](TESTING.md) for detailed testing procedures.

### 3. Making Changes

1. **Create a branch** for your changes
2. **Follow conventions** - Check `.ai/conventions/` for language-specific guidelines
3. **Document your changes**:
   - Add header comments to new scripts
   - Update relevant README files
   - Update docs/Scripts.md if adding new scripts
4. **Test thoroughly** in the development containers
5. **Commit with clear messages** describing what and why

### 4. Submitting Changes

1. Push your branch
2. Create a Pull Request with:
   - Clear description of changes
   - Why the changes are needed
   - Testing performed
   - Screenshots (for UI changes)

## Coding Conventions

### Bash Scripts

Follow the conventions in [.ai/conventions/bash.md](.ai/conventions/bash.md):

- Use `#!/bin/bash` or `#!/usr/bin/env bash` shebang
- Include comprehensive header documentation
- Use `set -e` for error handling
- Add error checking for critical operations
- Use meaningful variable names (UPPER_CASE for globals)

**Example Script Header:**
```bash
#!/bin/bash
# script-name.sh
#
# Brief description of what the script does
#
# Usage: ./script-name.sh [arguments]
#   arg1: Description of argument
#
# Requirements: List of required tools/packages

set -e
```

### PowerShell Scripts

Follow the conventions in [.ai/conventions/powershell.md](.ai/conventions/powershell.md):

- Use `#!/usr/bin/env pwsh` shebang
- Include comprehensive header documentation
- Use `param()` blocks for parameters
- Set `$ErrorActionPreference` appropriately
- Use approved verbs (Get-, Set-, New-, etc.)

**Example Script Header:**
```powershell
#!/usr/bin/env pwsh
# script-name.ps1
#
# Brief description of what the script does
#
# Usage: pwsh ./script-name.ps1 -Param1 Value1
#
# Parameters:
#   -Param1: Description
#
# Requirements: PowerShell Core, other dependencies

param(
    [string]$Param1 = "default"
)

$ErrorActionPreference = "Stop"
```

### Infrastructure as Code

Follow the conventions in [.ai/conventions/iac.md](.ai/conventions/iac.md):

- Document all Ansible playbooks with headers
- Use Ansible Vault for sensitive data
- Test playbooks against containerized environments first
- Include usage examples in playbook headers

### Documentation

Follow the conventions in [.ai/conventions/documentation.md](.ai/conventions/documentation.md):

- Use clear, concise language
- Include examples where appropriate
- Keep documentation up-to-date with code changes
- Use proper Markdown formatting

## Adding New Scripts

When adding a new script:

1. **Choose the right location**:
   - Proxmox scripts → `src/Proxmox/`
   - Other scripts → Create appropriate subdirectory in `src/`

2. **Include comprehensive documentation**:
   - Header comment with description, usage, requirements
   - Inline comments for complex logic
   - Update `docs/Scripts.md` with usage instructions

3. **Test thoroughly**:
   - Test in development container
   - Test error conditions
   - Test with different parameters

4. **Update documentation**:
   - Add to `docs/Scripts.md`
   - Update relevant README files
   - Add examples of usage

## Adding New Playbooks

When adding Ansible playbooks:

1. **Create in** `infra/ansible/playbooks/`
2. **Follow naming convention**: `<system>_<action>.yml`
3. **Include documentation header**
4. **Test against containerized environments**
5. **Update** `infra/ansible/README.md`

## Adding New Containers

When adding Docker containers:

1. **Create subdirectory** in `.containers/`
2. **Include**:
   - `compose.yml` with header documentation
   - `README.md` with detailed usage
   - `Dockerfile` if custom image needed
3. **Document**:
   - Purpose and use cases
   - Platform compatibility
   - Quick start instructions
4. **Update** `.containers/README.md`

## Documentation Standards

### README Files

Each directory should have a README.md that includes:

- **Purpose** - What's in this directory
- **Quick Start** - How to use it quickly
- **Detailed Instructions** - Step-by-step guides
- **Requirements** - What's needed
- **Examples** - Usage examples
- **Troubleshooting** - Common issues and solutions

### Script Headers

All scripts must include:

```
#!/path/to/interpreter
# script-name.ext
#
# Brief description
#
# Usage: ./script-name.ext [arguments]
#   arg1: Description
#
# Requirements: List dependencies
```

### Inline Comments

- Add comments for complex logic
- Explain "why", not "what" (code shows what)
- Keep comments up-to-date with code

## Testing Requirements

Before submitting changes:

- [ ] Scripts tested in development container
- [ ] Error handling tested
- [ ] Documentation updated
- [ ] Examples provided
- [ ] No hardcoded credentials or secrets

## Security Guidelines

- **Never commit credentials** - Use Ansible Vault or environment variables
- **Validate inputs** - Check user inputs in scripts
- **Use secure defaults** - Fail closed, not open
- **Document security considerations** - Note any security implications
- **Follow least privilege** - Scripts should require minimal permissions

## Getting Help

- **Documentation**: Check README files in each directory
- **Examples**: Look at existing scripts and playbooks
- **Conventions**: Review files in `.ai/conventions/`
- **Issues**: Search existing issues or create a new one

## Code Review

All changes go through review to ensure:

- Code quality and consistency
- Proper documentation
- Security best practices
- Testing completeness

## License

This repository is released into the public domain under the Unlicense. See [LICENSE](LICENSE) for details.

By contributing, you agree to release your contributions under the same license.

## Questions?

Feel free to open an issue for questions or clarifications!
