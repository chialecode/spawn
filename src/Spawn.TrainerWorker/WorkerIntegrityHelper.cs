using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Spawn.TrainerWorker;

/// <summary>
/// Queries the current worker process Mandatory Integrity Level RID via read-only <c>TOKEN_QUERY</c>.
/// </summary>
internal static class WorkerIntegrityHelper
{
    public const int LowIntegrityRid = 0x1000;
    public const int MediumIntegrityRid = 0x2000;
    public const int HighIntegrityRid = 0x3000;
    public const int SystemIntegrityRid = 0x4000;

    public static unsafe int GetCurrentProcessIntegrityLevel()
    {
        HANDLE tokenHandle = default;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_QUERY, &tokenHandle))
        {
            return MediumIntegrityRid;
        }

        try
        {
            uint requiredLength = 0;
            _ = PInvoke.GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, null, 0, &requiredLength);
            if (requiredLength == 0 || requiredLength > 4096)
            {
                return MediumIntegrityRid;
            }

            byte* buffer = (byte*)NativeMemory.AllocZeroed(requiredLength);
            try
            {
                if (!PInvoke.GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, requiredLength, &requiredLength))
                {
                    return MediumIntegrityRid;
                }

                var label = (TOKEN_MANDATORY_LABEL*)buffer;
                PSID sid = label->Label.Sid;
                if (sid.Value is null)
                {
                    return MediumIntegrityRid;
                }

                byte* countPtr = PInvoke.GetSidSubAuthorityCount(sid);
                if (countPtr is null || *countPtr == 0)
                {
                    return MediumIntegrityRid;
                }

                uint* ridPtr = PInvoke.GetSidSubAuthority(sid, (uint)(*countPtr - 1));
                return ridPtr is not null ? checked((int)*ridPtr) : MediumIntegrityRid;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        finally
        {
            _ = PInvoke.CloseHandle(tokenHandle);
        }
    }

    public static bool IsCurrentProcessElevated() =>
        GetCurrentProcessIntegrityLevel() >= HighIntegrityRid;
}
