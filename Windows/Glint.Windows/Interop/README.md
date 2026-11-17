# Windows UI Automation interop

`UIAutomationClient.Interop.dll` contains type-library metadata generated from the Windows system `UIAutomationCore.dll`; it contains no implementation of Windows UI Automation. The project embeds only the COM interface definitions it uses (`EmbedInteropTypes=true`).

To regenerate on a Windows SDK development machine, from the repository root:

```powershell
& 'C:/Program Files (x86)/Microsoft SDKs/Windows/v10.0A/bin/NETFX 4.8 Tools/x64/TlbImp.exe' 'C:/Windows/System32/UIAutomationCore.dll' /out:Windows/Glint.Windows/Interop/UIAutomationClient.Interop.dll /namespace:Glint.Windows.Interop.UIA /silent
```
