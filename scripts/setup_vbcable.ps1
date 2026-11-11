<#
.SYNOPSIS
    Checks VB-Audio Virtual Cable installation and provides direct download/setup help.
#>

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "   MixerOnline — VB-Audio Driver Diagnostic" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$soundDevices = Get-CimInstance Win32_SoundDevice | Where-Object { $_.Name -like "*VB-Audio*" -or $_.Name -like "*CABLE*" }

if ($soundDevices) {
    Write-Host "`n[✓] VB-Audio Virtual Cable is INSTALLED on your system:" -ForegroundColor Green
    foreach ($dev in $soundDevices) {
        Write-Host "  • $($dev.Name) [Status: $($dev.Status)]" -ForegroundColor White
    }
    Write-Host "`nMixerOnline is ready to route audio to your virtual microphone." -ForegroundColor Green
} else {
    Write-Host "`n[!] VB-Audio Virtual Cable was NOT found on your system." -ForegroundColor Yellow
    Write-Host "To use MixerOnline as a virtual microphone in Discord, OBS, Zoom, or TikTok Live:" -ForegroundColor White
    Write-Host "  1. Download the free driver from: https://vb-audio.com/Cable/" -ForegroundColor Cyan
    Write-Host "  2. Extract the ZIP and run VBCABLE_Setup_x64.exe as Administrator." -ForegroundColor White
    Write-Host "  3. Restart MixerOnline. It will automatically detect 'CABLE Input'." -ForegroundColor White
    
    $openUrl = Read-Host "`nWould you like to open the VB-Audio download page now? (Y/N)"
    if ($openUrl -eq 'Y' -or $openUrl -eq 'y') {
        Start-Process "https://vb-audio.com/Cable/"
    }
}
Write-Host "=============================================" -ForegroundColor Cyan
