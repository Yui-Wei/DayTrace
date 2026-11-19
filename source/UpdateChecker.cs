using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace DayTrace {
 public sealed class UpdateInfo {
  public Version Latest;
  public string Tag;
  public string PageUrl=UpdateChecker.Repository+"/tags";
  public bool HasUpdate {get{return Latest!=null&&Latest>Version.Parse(AppVersion.Current);}}
 }
 public sealed class UpdateCheckException:Exception {public UpdateCheckException(string message):base(message){}}
 public static class UpdateChecker {
  public const string Repository="https://github.com/Yui-Wei/DayTrace";
  sealed class RepositoryTag {public string name{get;set;}}
  public static async Task<UpdateInfo> Check(CancellationToken token){
   using(var handler=new HttpClientHandler{AllowAutoRedirect=false})
   using(var client=new HttpClient(handler)){
    client.Timeout=TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DayTrace/"+AppVersion.Current);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    return await ReadPages(async (page,cancel)=>{
     using(var response=await client.GetAsync("https://api.github.com/repos/Yui-Wei/DayTrace/tags?per_page=100&page="+page,cancel).ConfigureAwait(false)){
      if((int)response.StatusCode==429||(response.StatusCode==HttpStatusCode.Forbidden&&response.Headers.Contains("X-RateLimit-Remaining")&&response.Headers.GetValues("X-RateLimit-Remaining").Contains("0")))throw new UpdateCheckException("GitHub 请求次数已达上限，请稍后重试，或通过版本记录查看。");
      response.EnsureSuccessStatusCode();return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
     }
    },token).ConfigureAwait(false);
   }
  }
  internal static async Task<UpdateInfo> ReadPages(Func<int,CancellationToken,Task<string>> fetch,CancellationToken token){
   var result=new UpdateInfo();
   for(int page=1;page<=10;page++){
    token.ThrowIfCancellationRequested();
    var tags=Json.Decode<RepositoryTag[]>(await fetch(page,token).ConfigureAwait(false));
    token.ThrowIfCancellationRequested();
    if(tags==null)throw new InvalidOperationException("无效的版本信息");
    foreach(var tag in tags){
     if(tag==null||tag.name==null)continue;
     var match=Regex.Match(tag.name,@"^v?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))$");
     Version version;if(!match.Success||!Version.TryParse(match.Groups[1].Value,out version))continue;
     if(result.Latest==null||version>result.Latest){result.Latest=version;result.Tag=tag.name;result.PageUrl=Repository+"/tree/"+Uri.EscapeDataString(tag.name);}
    }
    if(tags.Length<100)return result;
   }
   throw new InvalidOperationException("版本列表过长，请直接查看项目主页");
  }
 }
}
