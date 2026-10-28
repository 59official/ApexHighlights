Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Disp {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet=CharSet.Ansi)] public static extern int EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Disp]::SetProcessDPIAware() | Out-Null
$dm = New-Object Disp+DEVMODE
$dm.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dm)
[void][Disp]::EnumDisplaySettings($null, -1, [ref]$dm)
"current: $($dm.dmPelsWidth)x$($dm.dmPelsHeight) @$($dm.dmDisplayFrequency)Hz"
$seen = @{}
$maxw = 0
for ($i = 0; $i -lt 400; $i++) {
  $d2 = New-Object Disp+DEVMODE
  $d2.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($d2)
  if ([Disp]::EnumDisplaySettings($null, $i, [ref]$d2) -eq 0) { break }
  $key = "$($d2.dmPelsWidth)x$($d2.dmPelsHeight)@$($d2.dmDisplayFrequency)"
  if (-not $seen.ContainsKey($key)) { $seen[$key] = 1; if ($d2.dmPelsWidth -ge 1900) { "  $key" } }
  if ($d2.dmPelsWidth -gt $maxw) { $maxw = $d2.dmPelsWidth }
}
"max width: $maxw"
Add-Type -AssemblyName System.Windows.Forms
"screen (dpi-aware): " + [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
