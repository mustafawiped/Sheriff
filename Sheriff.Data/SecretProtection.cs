using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Sheriff.Data;

[SupportedOSPlatform("windows")]
public static class SecretProtection
{
    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : "dpapi:" + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value.StartsWith("dpapi:", StringComparison.Ordinal) ? value[6..] : value), false));
    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var output = new Blob();
        nint description = 0;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            var ok = protect ? CryptProtectData(ref source, null, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref source, out description, 0, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, source.Data, input.Length);
            Marshal.FreeHGlobal(source.Data);
            if (output.Data != 0) LocalFree(output.Data);
            if (description != 0) LocalFree(description);
            Array.Clear(input);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, out nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
