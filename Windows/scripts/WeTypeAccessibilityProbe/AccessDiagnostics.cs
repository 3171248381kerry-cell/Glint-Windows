using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// 只读查询令牌，不请求调整权限；失败时输出系统错误码。
internal static class AccessDiagnostics
{
    internal static object Inspect(int pid)
    {
        using var process = OpenProcess(0x1000, false, (uint)pid);
        if (process.IsInvalid) return new { openProcessError = Marshal.GetLastWin32Error() };
        if (!OpenProcessToken(process, 8, out var token)) return new { openTokenError = Marshal.GetLastWin32Error() };
        using (token)
        {
            object Scalar(int type)
            {
                return GetTokenInformation(token, type, out uint value, 4, out _) ? new { value } : (object)new { error = Marshal.GetLastWin32Error() };
            }
            int? integrity = null;
            GetTokenInformationBuffer(token, 25, 0, 0, out uint size);
            int error = 0;
            if (size is > 0 and <= 4096)
            {
                nint buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetTokenInformationBuffer(token, 25, buffer, size, out _))
                    {
                        nint sid = Marshal.ReadIntPtr(buffer);
                        if (IsValidSid(sid))
                        {
                            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                            if (count > 0) integrity = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                        }
                    }
                    else error = Marshal.GetLastWin32Error();
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return new { integrityRid = integrity, integrityError = error, uiAccess = Scalar(26), elevated = Scalar(20), elevationType = Scalar(18), session = Scalar(12) };
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint rights, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out uint value, uint length, out uint returned);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int type, nint value, uint length, out uint returned);
    [DllImport("advapi32.dll")] private static extern bool IsValidSid(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}
