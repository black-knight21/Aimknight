using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace KaidenFlux
{
    internal sealed class SecureApiKeyStore
    {
        private readonly string path;
        private readonly Func<byte[],byte[]> protect,unprotect;
        private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("KaidenFlux.GeminiKey.v1");
        internal string Warning {get;private set;}
        internal SecureApiKeyStore(string path,Func<byte[],byte[]> protect=null,Func<byte[],byte[]> unprotect=null){this.path=path;this.protect=protect??(data=>ProtectedData.Protect(data,Entropy,DataProtectionScope.CurrentUser));this.unprotect=unprotect??(data=>ProtectedData.Unprotect(data,Entropy,DataProtectionScope.CurrentUser));}
        internal string Read()
        {
            Warning=null;if(!File.Exists(path))return null;
            try {if(new FileInfo(path).Length>65536)throw new IOException("Invalid saved credential size.");return Encoding.UTF8.GetString(unprotect(File.ReadAllBytes(path)));}
            catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException||ex is CryptographicException){Warning="Saved key cannot be unlocked. Enter it again to replace it.";return null;}
        }
        internal void Save(string key)
        {
            if(string.IsNullOrWhiteSpace(key)){Forget();return;}
            if(key.Length>4096)throw new ArgumentException("API key is too long.");
            byte[] encrypted=protect(Encoding.UTF8.GetBytes(key));
            Directory.CreateDirectory(Path.GetDirectoryName(path));string staged=path+"."+Guid.NewGuid().ToString("N")+".new";
            try {File.WriteAllBytes(staged,encrypted);if(File.Exists(path))File.Replace(staged,path,null);else File.Move(staged,path);}finally{if(File.Exists(staged))File.Delete(staged);}
            Warning=null;
        }
        internal void Forget(){if(File.Exists(path))File.Delete(path);Warning=null;}
    }
}
