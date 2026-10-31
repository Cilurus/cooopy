using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Cooopy.Services
{
    /// <summary>用 Windows DPAPI（当前用户作用域）加解密敏感字符串（如 DeepSeek API Key）。
    /// 密文带 "enc:v1:" 前缀；不带前缀的输入按旧版明文原样返回，兼容历史数据。</summary>
    public static class SecretProtector
    {
        private const string Prefix = "enc:v1:";

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain; // 已是密文
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plain);
                var input = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
                Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
                try
                {
                    if (!CryptProtectData(ref input, "cooopy", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                        return Prefix + plain; // 加密失败兜底：加前缀存原文，避免数据丢失
                    try
                    {
                        var cipher = new byte[output.cbData];
                        Marshal.Copy(output.pbData, cipher, 0, output.cbData);
                        return Prefix + Convert.ToBase64String(cipher);
                    }
                    finally
                    {
                        if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(input.pbData);
                }
            }
            catch
            {
                return Prefix + plain;
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // 旧版明文
            try
            {
                var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
                var input = new DATA_BLOB { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
                Marshal.Copy(cipher, 0, input.pbData, cipher.Length);
                try
                {
                    if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                        return ""; // 解密失败（换账户/损坏）：当作没有 Key，用户需重填
                    try
                    {
                        var plain = new byte[output.cbData];
                        Marshal.Copy(output.pbData, plain, 0, output.cbData);
                        return Encoding.UTF8.GetString(plain);
                    }
                    finally
                    {
                        if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(input.pbData);
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
