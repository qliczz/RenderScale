using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// Manual module mapping for f.e. parsing original code.
/// </summary>
public sealed unsafe class MappedModule : IDisposable
{
    private nint _ptr;

    public MappedModule(string path)
    {
        bool done = false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pe = new PEReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            if (pe.PEHeaders.PEHeader is not { } header)
            {
                throw new InvalidOperationException("Trying to map a non-PE module");
            }

            FService.Assert(header.SizeOfHeaders <= header.SizeOfImage);
            _ptr = (nint) NativeMemory.AlignedAlloc((nuint) header.SizeOfImage, (nuint) sizeof(nint));

            fs.ReadExactly(new Span<byte>(
                (byte*) _ptr,
                header.SizeOfHeaders
            ));

            foreach (var section in pe.PEHeaders.SectionHeaders)
            {
                var size = section.VirtualSize;
                if (size == 0)
                {
                    size = section.SizeOfRawData;
                }

                fs.Seek(section.PointerToRawData, SeekOrigin.Begin);

                FService.Assert(section.VirtualAddress + size <= header.SizeOfImage);
                fs.ReadExactly(new Span<byte>(
                    (byte*) (_ptr + section.VirtualAddress),
                    size
                ));
            }

            done = true;
        }
        finally
        {
            if (!done)
            {
                NativeMemory.AlignedFree((void*) _ptr);
            }
        }
    }

    public nint Base => _ptr;

    public void Dispose()
    {
        var ptr = Interlocked.Exchange(ref _ptr, 0);
        if (ptr != 0)
        {
            NativeMemory.AlignedFree((void*) ptr);
        }
    }
}
