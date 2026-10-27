using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FocusGarden.App;

internal static class DataDirectoryIdentity
{
    // Resolve directory junctions, symbolic links and 8.3 names before comparing paths
    // or selecting the mutex. Missing trailing directories inherit their parent's identity.
    internal static string Resolve(string path)
    {
        string parent=Path.GetFullPath(path);var suffix=new Stack<string>();
        while(!Directory.Exists(parent))
        {
            if(File.Exists(parent))throw new IOException("数据目录指向了文件。");
            var previous=Path.GetDirectoryName(parent.TrimEnd(Path.DirectorySeparatorChar));
            if(string.IsNullOrEmpty(previous))throw new DirectoryNotFoundException("找不到数据目录所在的磁盘。");
            suffix.Push(Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar)));parent=previous;
        }
        using var handle=CreateFile(parent,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer=new StringBuilder(512);
        uint length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);
        if(length>=buffer.Capacity){buffer=new StringBuilder(checked((int)length+1));length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Capacity,0);}
        if(length==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        string resolved=buffer.ToString();
        if(resolved.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))resolved=@"\\"+resolved[8..];
        else if(resolved.StartsWith(@"\\?\",StringComparison.Ordinal))resolved=resolved[4..];
        while(suffix.Count>0)resolved=Path.Combine(resolved,suffix.Pop());
        return Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).ToUpperInvariant();
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string name,uint access,uint sharing,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint size,uint flags);
}
