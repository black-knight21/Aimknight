using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace KaidenFlux
{
    internal sealed class LocalTuningMemory
    {
        private readonly string path;
        internal bool Enabled {get;private set;}=true;
        internal JArray Entries {get;private set;}=new JArray();
        internal string Warning {get;private set;}
        internal LocalTuningMemory(string path)
        {
            this.path=path;if(path==null||!File.Exists(path))return;
            try {
                if(new FileInfo(path).Length>1024*1024)throw new ArgumentException("Memory file too large.");
                JObject data;
                using(var text=new StringReader(File.ReadAllText(path)))
                using(var reader=new Newtonsoft.Json.JsonTextReader(text){DateParseHandling=Newtonsoft.Json.DateParseHandling.None})
                    data=JObject.Load(reader,new JsonLoadSettings {DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error});
                if((int?)data["schema"]!=1||data["enabled"]?.Type!=JTokenType.Boolean||!(data["entries"] is JArray entries)||entries.Count>50)throw new ArgumentException("Unsupported memory.");
                foreach(var item in entries) {
                    if(!(item is JObject row)||row.Properties().Count()!=5)throw new ArgumentException("Invalid memory record.");
                    foreach(string key in new[]{"utc","event","request","response","changes"})if(row[key]?.Type!=JTokenType.String||((string)row[key]).Length>4000)throw new ArgumentException("Invalid memory record.");
                    DateTime date;if(!DateTime.TryParse((string)row["utc"],null,System.Globalization.DateTimeStyles.RoundtripKind,out date))throw new ArgumentException("Invalid memory date.");
                }
                Entries=(JArray)entries.DeepClone();Enabled=(bool)data["enabled"];
            }catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException||ex is Newtonsoft.Json.JsonException||ex is ArgumentException) {Enabled=false;Warning="Local history could not be read. Memory is disabled; clear history to reset it.";}
        }
        private static string Clean(string value,int max,string key)
        {
            value=value??"";if(!string.IsNullOrEmpty(key)&&key.Length>=4)value=value.Replace(key,"[redacted]");
            value=Regex.Replace(value,@"AIza[0-9A-Za-z_-]{12,}","[redacted]");return value.Length>max?value.Substring(0,max):value;
        }
        internal void Add(string kind,string request,string response,string changes,string key=null)
        {
            if(!Enabled)return;
            Entries.Add(new JObject {["utc"]=DateTime.UtcNow.ToString("O"),["event"]=Clean(kind,40,key),["request"]=Clean(request,2000,key),["response"]=Clean(response,1200,key),["changes"]=Clean(changes,2000,key)});
            while(Entries.Count>50)Entries.RemoveAt(0);Save();
        }
        internal JArray Context() => Enabled?new JArray(Entries.Reverse().Take(5).Reverse().Select(e=>new JObject {["event"]=e["event"].DeepClone(),["request"]=Clean((string)e["request"],500,null),["response"]=e["response"].DeepClone(),["changes"]=Clean((string)e["changes"],1000,null)})):new JArray();
        internal void SetEnabled(bool enabled) {if(Warning!=null)throw new InvalidOperationException(Warning);Enabled=enabled;Save();}
        internal void Clear(){Entries.Clear();Warning=null;Save();}
        private void Save(){if(path!=null)AtomicJson.Write(path,new JObject {["schema"]=1,["enabled"]=Enabled,["entries"]=Entries});}
    }
}
