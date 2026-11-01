#:property TargetFramework=net10.0-windows
#:property PublishTrimmed=false
#:property PublishAot=false

using System.Runtime.InteropServices;

const int SystemProcessInformation = 5;
const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
IntPtr buffer = Marshal.AllocHGlobal(512 * 1024);
int size = 512 * 1024;
try
{
    int status;
    while (true)
    {
        status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int required);
        if (status != StatusInfoLengthMismatch)
        {
            break;
        }

        int next = Math.Min(required + (required >> 2) + 65536, 64 * 1024 * 1024);
        if (next <= size)
        {
            break;
        }

        Marshal.FreeHGlobal(buffer);
        buffer = Marshal.AllocHGlobal(next);
        size = next;
    }

    int count = 0;
    long offset = 0;
    while (offset + 64 <= size)
    {
        int next = Marshal.ReadInt32(buffer + (int)offset);
        count++;
        if (next <= 0)
        {
            break;
        }

        offset += next;
    }

    Console.WriteLine($"host={Environment.ProcessPath} 条目={count} pid={Environment.ProcessId}");
}
finally
{
    Marshal.FreeHGlobal(buffer);
}

[DllImport("ntdll.dll")]
static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
