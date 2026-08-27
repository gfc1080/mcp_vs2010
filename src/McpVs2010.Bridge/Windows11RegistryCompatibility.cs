using System;
using System.Runtime.InteropServices;

namespace McpVs2010.Bridge
{
    /// <summary>
    /// Windows 11의 32비트 RegQueryMultipleValuesW는 15개 DWORD를 조회할 때
    /// 8바이트 정렬을 적용하여 116바이트를 요구하고, ERROR_MORE_DATA를 반환하면서도
    /// VS2010이 제공한 60바이트 버퍼 다음 DWORD를 변경한다. msenv.dll의 해당 import만
    /// 교체하여 VS2010이 기대한 연속 15 DWORD 결과를 만든다.
    /// </summary>
    internal static class Windows11RegistryCompatibility
    {
        private const int ErrorSuccess = 0;
        private const int ErrorInvalidData = 13;
        private const int ErrorMoreData = 234;
        private const int RegDword = 4;
        private const uint PageExecuteReadWrite = 0x40;

        private static readonly object SyncRoot = new object();
        private static RegQueryMultipleValuesDelegate _original;
        private static RegQueryMultipleValuesDelegate _replacement;
        private static bool _installed;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RegQueryMultipleValuesDelegate(
            IntPtr key,
            IntPtr values,
            int valueCount,
            IntPtr valueBuffer,
            IntPtr totalSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(
            IntPtr address,
            UIntPtr size,
            uint newProtection,
            out uint oldProtection);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(
            IntPtr process,
            IntPtr baseAddress,
            UIntPtr size);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueEx(
            IntPtr key,
            IntPtr valueName,
            IntPtr reserved,
            out int type,
            out int data,
            ref int dataSize);

        public static void Install()
        {
            lock (SyncRoot)
            {
                if (_installed)
                    return;

                if (IntPtr.Size != 4)
                    throw new PlatformNotSupportedException("VS2010 레지스트리 호환성 처리는 32비트 프로세스에서만 지원됩니다.");

                IntPtr module = GetModuleHandle("msenv.dll");
                if (module == IntPtr.Zero)
                    throw new InvalidOperationException("msenv.dll 모듈을 찾지 못했습니다.");

                IntPtr importSlot = FindImportAddressSlot(module, "ADVAPI32.dll", "RegQueryMultipleValuesW");
                if (importSlot == IntPtr.Zero)
                    throw new InvalidOperationException("msenv.dll의 RegQueryMultipleValuesW import 항목을 찾지 못했습니다.");

                IntPtr originalAddress = Marshal.ReadIntPtr(importSlot);
                _original = (RegQueryMultipleValuesDelegate)Marshal.GetDelegateForFunctionPointer(
                    originalAddress, typeof(RegQueryMultipleValuesDelegate));
                _replacement = QueryMultipleValuesSafely;
                IntPtr replacementAddress = Marshal.GetFunctionPointerForDelegate(_replacement);

                uint oldProtection;
                if (!VirtualProtect(importSlot, new UIntPtr(4), PageExecuteReadWrite, out oldProtection))
                    throw new InvalidOperationException("RegQueryMultipleValuesW import 항목의 메모리 보호를 변경하지 못했습니다.");

                try
                {
                    Marshal.WriteIntPtr(importSlot, replacementAddress);
                }
                finally
                {
                    uint ignored;
                    VirtualProtect(importSlot, new UIntPtr(4), oldProtection, out ignored);
                }

                FlushInstructionCache(GetCurrentProcess(), importSlot, new UIntPtr(4));
                _installed = true;
            }
        }

        private static int QueryMultipleValuesSafely(
            IntPtr key,
            IntPtr values,
            int valueCount,
            IntPtr valueBuffer,
            IntPtr totalSize)
        {
            try
            {
                if (valueCount != 15 || valueBuffer == IntPtr.Zero || totalSize == IntPtr.Zero ||
                    Marshal.ReadInt32(totalSize) != 60)
                {
                    return _original(key, values, valueCount, valueBuffer, totalSize);
                }

                const int valueEntrySize = 16;
                for (int index = 0; index < valueCount; index++)
                {
                    IntPtr entry = Add(values, index * valueEntrySize);
                    IntPtr valueName = Marshal.ReadIntPtr(entry, 0);
                    int type;
                    int data;
                    int dataSize = 4;
                    int status = RegQueryValueEx(key, valueName, IntPtr.Zero, out type, out data, ref dataSize);
                    if (status != ErrorSuccess)
                    {
                        Marshal.WriteInt32(totalSize, 0);
                        return status;
                    }

                    if (type != RegDword || dataSize != 4)
                    {
                        Marshal.WriteInt32(totalSize, 60);
                        return ErrorMoreData;
                    }

                    IntPtr destination = Add(valueBuffer, index * 4);
                    Marshal.WriteInt32(destination, data);
                    Marshal.WriteInt32(entry, 4, 4);
                    Marshal.WriteIntPtr(entry, 8, destination);
                    Marshal.WriteInt32(entry, 12, RegDword);
                }

                Marshal.WriteInt32(totalSize, 60);
                return ErrorSuccess;
            }
            catch
            {
                if (totalSize != IntPtr.Zero)
                    Marshal.WriteInt32(totalSize, 0);
                return ErrorInvalidData;
            }
        }

        private static IntPtr FindImportAddressSlot(IntPtr module, string importedModule, string importedFunction)
        {
            if (Marshal.ReadInt16(module) != 0x5A4D)
                return IntPtr.Zero;

            int peOffset = Marshal.ReadInt32(module, 0x3C);
            IntPtr ntHeaders = Add(module, peOffset);
            if (Marshal.ReadInt32(ntHeaders) != 0x00004550)
                return IntPtr.Zero;

            IntPtr optionalHeader = Add(ntHeaders, 24);
            if ((ushort)Marshal.ReadInt16(optionalHeader) != 0x10B)
                return IntPtr.Zero;

            int importDirectoryRva = Marshal.ReadInt32(optionalHeader, 104);
            if (importDirectoryRva == 0)
                return IntPtr.Zero;

            for (int descriptorOffset = 0; ; descriptorOffset += 20)
            {
                IntPtr descriptor = Add(module, importDirectoryRva + descriptorOffset);
                int originalFirstThunkRva = Marshal.ReadInt32(descriptor, 0);
                int nameRva = Marshal.ReadInt32(descriptor, 12);
                int firstThunkRva = Marshal.ReadInt32(descriptor, 16);
                if (originalFirstThunkRva == 0 && nameRva == 0 && firstThunkRva == 0)
                    break;

                string moduleName = Marshal.PtrToStringAnsi(Add(module, nameRva));
                if (!string.Equals(moduleName, importedModule, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (originalFirstThunkRva == 0)
                    return IntPtr.Zero;

                for (int index = 0; ; index++)
                {
                    int lookup = Marshal.ReadInt32(Add(module, originalFirstThunkRva + index * 4));
                    if (lookup == 0)
                        break;
                    if ((lookup & unchecked((int)0x80000000)) != 0)
                        continue;

                    IntPtr importByName = Add(module, lookup);
                    string functionName = Marshal.PtrToStringAnsi(Add(importByName, 2));
                    if (string.Equals(functionName, importedFunction, StringComparison.Ordinal))
                        return Add(module, firstThunkRva + index * 4);
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr Add(IntPtr pointer, int offset)
        {
            return new IntPtr(pointer.ToInt32() + offset);
        }
    }
}
