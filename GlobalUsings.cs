global using System.IO;

// Load native DLLs (wlanapi, dwmapi, user32) only from System32 – never from the app's folder,
// so a planted wlanapi.dll next to AirScope.exe can't be picked up.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
