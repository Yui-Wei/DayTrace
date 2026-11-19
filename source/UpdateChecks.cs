using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace DayTrace {
 public static class UpdateChecks {
  sealed class PayloadHandler:HttpMessageHandler {
   readonly byte[] bytes;
   public PayloadHandler(byte[] content){bytes=content;}
   protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}
  }
  public static async Task Run(Action<string,bool> check,string root){
   string folder=Path.Combine(root,"updater-fixture-"+Guid.NewGuid().ToString("N")+" 更新 测试");Directory.CreateDirectory(folder);
   string current=typeof(UpdateChecks).Assembly.Location;byte[] payload=File.ReadAllBytes(current);string hash=UpdateInstaller.HashFile(current);
   string json=Json.Encode(new UpdateManifest{version=AppVersion.Current,url=UpdateChecker.Repository+"/releases/download/v"+AppVersion.Current+"/DayTrace.exe",sha256=hash,size=payload.Length});
   var info=UpdateChecker.ParseManifest(json);
   check("release manifest accepts verified executable metadata",info.CanInstall&&info.Hash==hash);
   var unsafeManifest=json.Replace("github.com/Yui-Wei/DayTrace","example.com/untrusted");bool rejected=false;try{UpdateChecker.ParseManifest(unsafeManifest);}catch(UpdateCheckException){rejected=true;}
   check("release manifest rejects foreign download origins",rejected);
   using(var client=new HttpClient(new PayloadHandler(payload))){string file=await UpdateInstaller.Download(info,folder,null,CancellationToken.None,client);check("download validates size hash and executable version",File.Exists(file)&&UpdateInstaller.HashFile(file)==hash);}
   var wrong=UpdateChecker.ParseManifest(json);wrong.Hash=new string('0',64);rejected=false;
   using(var client=new HttpClient(new PayloadHandler(payload))){try{await UpdateInstaller.Download(wrong,folder,null,CancellationToken.None,client);}catch(UpdateCheckException){rejected=true;}}
   check("download rejects corrupted executable and removes partial files",rejected&&Directory.GetFiles(folder,"*.partial").Length==0);
   wrong=UpdateChecker.ParseManifest(json);wrong.Size=payload.Length+1;rejected=false;
   using(var client=new HttpClient(new PayloadHandler(payload))){try{await UpdateInstaller.Download(wrong,folder,null,CancellationToken.None,client);}catch(UpdateCheckException){rejected=true;}}
   check("download rejects truncated or mismatched size",rejected);
   using(var cancelled=new CancellationTokenSource())using(var client=new HttpClient(new PayloadHandler(payload))){cancelled.Cancel();rejected=false;try{await UpdateInstaller.Download(info,folder,null,cancelled.Token,client);}catch(OperationCanceledException){rejected=true;}check("download cancellation leaves no partial payload",rejected&&Directory.GetFiles(folder,"*.partial").Length==0);}
   var preferences=new UpdatePreferences{Enabled=true,LastAttempt=DateTime.Today.ToString("yyyy-MM-dd")};string preferenceFile=Path.Combine(folder,"update-settings.json");preferences.Save(preferenceFile);
   var loaded=UpdatePreferences.Load(preferenceFile);check("automatic check setting and last attempt survive restart",loaded.Enabled&&!loaded.ShouldCheck(DateTime.Today)&&loaded.ShouldCheck(DateTime.Today.AddDays(1)));
   string target=Path.Combine(folder,"Fixture.exe"),staged=Path.Combine(folder,".DayTrace-update-test.exe");File.WriteAllText(target,"old-version");File.Copy(current,staged);
   var task=new UpdateTask{Target=target,Payload=staged,Backup=target+".update-backup",Hash=hash,Version=AppVersion.Current,LaunchAfter=false};
   UpdateInstaller.Apply(task);check("updater atomically replaces executable and keeps original backup",UpdateInstaller.HashFile(target)==hash&&File.ReadAllText(task.Backup)=="old-version");
   File.WriteAllText(target,"old-version");File.Copy(current,staged);task.Hash=new string('0',64);rejected=false;try{UpdateInstaller.Apply(task);}catch(UpdateCheckException){rejected=true;}
   check("updater rechecks staged payload before replacing old version",rejected&&File.ReadAllText(target)=="old-version");
   task.Hash=hash;task.LaunchAfter=true;int launches=0;rejected=false;
   try{UpdateInstaller.Apply(task,path=>{launches++;if(launches==1)throw new IOException("Simulated startup failure");});}catch(IOException){rejected=true;}
   check("failed new launch rolls back and restarts original executable",rejected&&launches==2&&File.ReadAllText(target)=="old-version");
   string outside=task.Payload;task.Payload=current;rejected=false;try{UpdateInstaller.ValidateTask(task);}catch(UpdateCheckException){rejected=true;}task.Payload=outside;
   check("updater rejects staged executable outside installation directory",rejected);
   // Exercise the actual helper executable and original-process wait in an isolated directory.
   File.Copy(current,staged);task.LaunchAfter=false;
   string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   using(var previous=Process.Start(new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds 1500\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})){
    task.ProcessId=previous.Id;task.ProcessStartTicks=previous.StartTime.ToUniversalTime().Ticks;
    string id=Guid.NewGuid().ToString("N"),taskFile=Path.Combine(folder,"update-"+id+".json");File.Copy(current,Path.Combine(folder,"DayTrace-Updater-"+id+".exe"));Storage.Atomic(taskFile,Json.Encode(task));
    UpdateInstaller.Start(taskFile);await Task.Delay(150);
    check("helper waits until original process has exited",File.ReadAllText(target)=="old-version"&&!File.Exists(taskFile+".result"));
    for(int i=0;i<150&&!File.Exists(taskFile+".result");i++)await Task.Delay(100);
    check("real helper completes verified replacement after original exit",File.Exists(taskFile+".result")&&File.ReadAllText(taskFile+".result")=="success"&&UpdateInstaller.HashFile(target)==hash);
   }
  }
 }
}
