# PowerShell Script Template

```powershell
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath
)

function Write-Info {
    param([string]$Message)
    Write-Verbose "[INFO] $Message"
}

function Write-Warn {
    param([string]$Message)
    Write-Warning $Message
}

function Write-Err {
    param([string]$Message)
    Write-Error $Message
}

begin {
    Write-Info "Starting $($MyInvocation.MyCommand.Name)"
}

process {
    if (-not (Test-Path -Path $ConfigPath)) {
        Write-Err "Config file not found: $ConfigPath"
        return
    }

    if ($PSCmdlet.ShouldProcess("Config: $ConfigPath", "Execute core logic")) {
        Write-Info "Using config: $ConfigPath"

        # TODO: implement core logic here

    } else {
        Write-Info "WhatIf: would process config $ConfigPath"
    }
}

end {
    Write-Info "Finished $($MyInvocation.MyCommand.Name)"
}
```
