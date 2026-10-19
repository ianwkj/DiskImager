#Requires -RunAsAdministrator
# Removes the DiskImager Server 2022 test VM and its disks. Keeps the downloaded ISO unless -RemoveIso.
param([string]$WorkDir = "D:\DiskImagerServerTest", [switch]$RemoveIso)
$vm = Get-VM -Name 'DiskImager-Test' -ErrorAction SilentlyContinue
if ($vm) {
    if ($vm.State -ne 'Off') { Stop-VM $vm.Name -TurnOff -Force }
    Remove-VM $vm.Name -Force
}
Get-ChildItem $WorkDir -Filter 'DITest-*.vhdx' -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item (Join-Path $WorkDir 'DiskImager-Test') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $WorkDir 'vm-admin-password.txt') -Force -ErrorAction SilentlyContinue
if ($RemoveIso) { Remove-Item (Join-Path $WorkDir '*.iso') -Force -ErrorAction SilentlyContinue }
"Test VM removed. Reports are kept in $WorkDir."
