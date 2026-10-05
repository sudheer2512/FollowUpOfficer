<#
.SYNOPSIS
  Stops and removes the FollowUp Officer Windows services. Data (database, keys, attachments) is not touched.
#>
$ErrorActionPreference = "Stop"
foreach ($name in "FollowUpOfficer.Api", "FollowUpOfficer.Worker") {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -Force -ErrorAction SilentlyContinue
        sc.exe delete $name | Out-Null
        Write-Host "Removed $name"
    }
}
