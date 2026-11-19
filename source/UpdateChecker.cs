using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace DayTrace {
 public sealed class UpdatePreferences {
  public bool Enabled;
  public string LastAttempt="";
  public bool ShouldCheck(DateTime today){return Enabled&&LastAttempt!=today.ToString("yyyy-MM-dd");}
  public static UpdatePreferences Load(string path){try{return File.Exists(path)?Json.Decode<UpdatePreferences>(File.ReadAllText(path))??new UpdatePreferences():new UpdatePreferences();}catch{return new UpdatePreferences();}}
  public void Save(string path){Storage.Atomic(path,Json.Encode(this));}
 }
 public sealed class UpdateManifest {
  public string version,url,sha256;
  public long size;
 }
 public sealed class UpdateInfo {
  public Version Latest;
  public string Tag;
  public string PageUrl=UpdateChecker.Repository+"/releases";
  public string DownloadUrl,Hash;
  public long Size;
  public bool HasUpdate {get{return Latest!=null&&Latest>Version.Parse(AppVersion.Current);}}
  public bool CanInstall {get{return UpdateChecker.ValidDownload(this);}}
 }
 public sealed class UpdateCheckException:Exception {public UpdateCheckException(string message):base(message){}}
 public static class UpdateChecker {
  public const string Repository="https://github.com/Yui-Wei/DayTrace";
  public const long MaximumDownload=100*1024*1024;
  sealed class RepositoryTag {public string name{get;set;}}
  internal static bool ParseVersion(string text,out Version version){
   version=null;if(text==null||!Regex.IsMatch(text,@"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$"))return false;return Version.TryParse(text,out version);
  }
  internal static bool ValidDownload(UpdateInfo info){
   if(info==null||info.Latest==null||info.Size<=0||info.Size>MaximumDownload||info.Hash==null||!Regex.IsMatch(info.Hash,@"\A[0-9a-fA-F]{64}\z"))return false;
   return info.DownloadUrl==Repository+"/releases/download/v"+info.Latest+"/DayTrace.exe";
  }
  internal static UpdateInfo ParseManifest(string json){
   var manifest=Json.Decode<UpdateManifest>(json);Version version;
   if(manifest==null||!ParseVersion(manifest.version,out version))throw new UpdateCheckException("更新信息无效，请稍后重试。");
   var result=new UpdateInfo{Latest=version,Tag="v"+version,PageUrl=Repository+"/releases/tag/v"+version,DownloadUrl=manifest.url,Hash=manifest.sha256,Size=manifest.size};
   if(!ValidDownload(result))throw new UpdateCheckException("更新文件信息不完整，请通过版本页面查看。");return result;
  }
  public static async Task<UpdateInfo> Check(CancellationToken token){
   using(var client=CreateClient(TimeSpan.FromSeconds(20))){
    using(var response=await client.GetAsync(Repository+"/releases/latest/download/update.json",token).ConfigureAwait(false)){
     if(response.IsSuccessStatusCode){
      if(response.Content.Headers.ContentLength>65536)throw new UpdateCheckException("更新信息过大，请通过版本页面查看。");
      var json=await response.Content.ReadAsStringAsync().ConfigureAwait(false);if(json.Length>65536)throw new UpdateCheckException("更新信息过大，请通过版本页面查看。");
      return ParseManifest(json);
     }
     if(response.StatusCode!=HttpStatusCode.NotFound){CheckResponse(response);}
    }
    // Older tags can still be discovered before the first installable release is published.
    return await ReadPages(async (page,cancel)=>{
     using(var response=await client.GetAsync("https://api.github.com/repos/Yui-Wei/DayTrace/tags?per_page=100&page="+page,cancel).ConfigureAwait(false)){
      CheckResponse(response);return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
     }
    },token).ConfigureAwait(false);
   }
  }
  internal static HttpClient CreateClient(TimeSpan timeout){
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
   var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=true});client.Timeout=timeout;
   client.DefaultRequestHeaders.UserAgent.ParseAdd("DayTrace/"+AppVersion.Current);return client;
  }
  static void CheckResponse(HttpResponseMessage response){
   if((int)response.StatusCode==429||(response.StatusCode==HttpStatusCode.Forbidden&&response.Headers.Contains("X-RateLimit-Remaining")&&response.Headers.GetValues("X-RateLimit-Remaining").Contains("0")))throw new UpdateCheckException("GitHub 请求次数已达上限，请稍后重试，或通过版本记录查看。");
   response.EnsureSuccessStatusCode();
  }
  internal static async Task<UpdateInfo> ReadPages(Func<int,CancellationToken,Task<string>> fetch,CancellationToken token){
   var result=new UpdateInfo();
   for(int page=1;page<=10;page++){
    token.ThrowIfCancellationRequested();var tags=Json.Decode<RepositoryTag[]>(await fetch(page,token).ConfigureAwait(false));token.ThrowIfCancellationRequested();
    if(tags==null)throw new UpdateCheckException("无效的版本信息");
    foreach(var tag in tags){Version version;if(tag==null||!ParseVersion(((tag.name??"").StartsWith("v")?tag.name.Substring(1):tag.name),out version))continue;
     if(result.Latest==null||version>result.Latest){result.Latest=version;result.Tag=tag.name;result.PageUrl=Repository+"/tree/"+Uri.EscapeDataString(tag.name);}
    }
    if(tags.Length<100)return result;
   }
   throw new UpdateCheckException("版本列表过长，请直接查看项目主页");
  }
 }
}
