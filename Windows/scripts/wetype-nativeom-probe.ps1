Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Probe {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, out IntPtr value);
    public static string Name(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
    public static string Query(IntPtr h, string id) { var g = new Guid(id); IntPtr value; int hr = AccessibleObjectFromWindow(h, 0xFFFFFFF0, ref g, out value); if(value != IntPtr.Zero) Marshal.Release(value); return hr.ToString("X8"); }
}
'@
$path = Join-Path $env:LOCALAPPDATA 'Glint/Diagnostics/wetype-nativeom.json'
$results = [System.Collections.Generic.List[object]]::new()
$end = [DateTime]::UtcNow.AddSeconds(75)
while ([DateTime]::UtcNow -lt $end) {
    $handles = [System.Collections.Generic.List[object]]::new()
    $visit = [Probe+EnumProc]{ param($h, $unused)
        $name = [Probe]::Name($h)
        if ($name -like 'WeType.NativeCandidate.*' -and [Probe]::IsWindowVisible($h)) {
            [uint32]$processId = 0
            [void][Probe]::GetWindowThreadProcessId($h, [ref]$processId)
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($process.ProcessName -eq 'wetype_renderer') {
                $handles.Add([pscustomobject]@{ cls=$name; dispatch=[Probe]::Query($h,'00020400-0000-0000-C000-000000000046'); candidate=[Probe]::Query($h,'ea1ea138-19df-11d7-a6d2-00065b84435c') })
            }
        }
        return $true
    }
    $top = [Probe+EnumProc]{ param($h, $unused) [void]$visit.Invoke($h, $unused); if ([Probe]::Name($h) -eq 'WeType.NativeCandidate.Decoration.v2') { [void][Probe]::EnumChildWindows($h,$visit,[IntPtr]::Zero) }; return $true }
    [void][Probe]::EnumWindows($top,[IntPtr]::Zero)
    $results.Add([pscustomobject]@{ elapsedSeconds = 75 - [int]($end - [DateTime]::UtcNow).TotalSeconds; windows = @($handles) })
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
    Start-Sleep -Milliseconds 700
}
