// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Yui-Wei

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
namespace DayTrace {
 public sealed class UpdateTask {
  public string Target,Payload,Backup,Hash,Version;
  public int ProcessId;
  public long ProcessStartTicks;
  public bool LaunchAfter=true;
 }
 public static class UpdateInstaller {
  public static string HashFile(string path){using(var hash=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
  public static async Task<string> Download(UpdateInfo info,string folder,Action<int> progress,CancellationToken token){
   using(var client=UpdateChecker.CreateClient(TimeSpan.FromMinutes(10)))return await Download(info,folder,progress,token,client).ConfigureAwait(false);
  }
  internal static async Task<string> Download(UpdateInfo info,string folder,Action<int> progress,CancellationToken token,HttpClient client){
   if(!info.CanInstall)throw new UpdateCheckException("没有可用的安装文件，请查看版本页面。");
   Directory.CreateDirectory(folder);string partial=Path.Combine(folder,"download-"+Guid.NewGuid().ToString("N")+".partial");string ready=Path.ChangeExtension(partial,".exe");
   try{
    using(var response=await client.GetAsync(info.DownloadUrl,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false)){
     response.EnsureSuccessStatusCode();
     if(response.Content.Headers.ContentLength.HasValue&&response.Content.Headers.ContentLength.Value!=info.Size)throw new UpdateCheckException("下载文件大小不匹配，请重新检查更新。");
     using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
      byte[] buffer=new byte[65536];long total=0;int read;
      while((read=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0){total+=read;if(total>info.Size||total>UpdateChecker.MaximumDownload)throw new UpdateCheckException("更新文件大小异常。");await output.WriteAsync(buffer,0,read,token).ConfigureAwait(false);if(progress!=null)progress((int)(total*100/info.Size));}
      output.Flush(true);if(total!=info.Size)throw new UpdateCheckException("下载未完成，请重试。");
     }
    }
    token.ThrowIfCancellationRequested();
    if(!string.Equals(HashFile(partial),info.Hash,StringComparison.OrdinalIgnoreCase))throw new UpdateCheckException("更新文件校验失败，原程序未修改，请重新下载。");
    var version=FileVersionInfo.GetVersionInfo(partial).ProductVersion;
    if(version!=info.Latest.ToString())throw new UpdateCheckException("更新文件版本不匹配，原程序未修改。");
    File.Move(partial,ready);return ready;
   }catch{if(File.Exists(partial))File.Delete(partial);if(File.Exists(ready))File.Delete(ready);throw;}
  }
  public static string Prepare(string downloaded,UpdateInfo info,string target,string folder){
   if(!info.CanInstall||!string.Equals(HashFile(downloaded),info.Hash,StringComparison.OrdinalIgnoreCase))throw new UpdateCheckException("更新文件校验失败。");
   target=Path.GetFullPath(target);string id=Guid.NewGuid().ToString("N");
   string payload=Path.Combine(Path.GetDirectoryName(target),".DayTrace-update-"+id+".exe");
   try{File.Copy(downloaded,payload,false);}catch(UnauthorizedAccessException){throw new UpdateCheckException("当前安装目录无法写入，请将软件放在可写目录后更新。");}
   try{
    Directory.CreateDirectory(folder);string helper=Path.Combine(folder,"DayTrace-Updater-"+id+".exe");
    File.Copy(typeof(UpdateInstaller).Assembly.Location,helper,false);
    var process=Process.GetCurrentProcess();
    var task=new UpdateTask{Target=target,Payload=payload,Backup=target+".update-backup",Hash=info.Hash,Version=info.Latest.ToString(),ProcessId=process.Id,ProcessStartTicks=process.StartTime.ToUniversalTime().Ticks};
    string taskFile=Path.Combine(folder,"update-"+id+".json");Storage.Atomic(taskFile,Json.Encode(task));
    return taskFile;
   }catch{if(File.Exists(payload))File.Delete(payload);throw;}
  }
  public static void Start(string taskFile){
   string id=Path.GetFileNameWithoutExtension(taskFile).Substring("update-".Length);
   string helper=Path.Combine(Path.GetDirectoryName(taskFile),"DayTrace-Updater-"+id+".exe");
   Process.Start(new ProcessStartInfo(helper,"--apply-update \""+taskFile+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(taskFile)});
  }
  internal static void ValidateTask(UpdateTask task){
   if(task==null||string.IsNullOrWhiteSpace(task.Target)||string.IsNullOrWhiteSpace(task.Payload)||string.IsNullOrWhiteSpace(task.Hash))throw new UpdateCheckException("无效的更新任务。");
   string target=Path.GetFullPath(task.Target),payload=Path.GetFullPath(task.Payload);Version version;
   if(!Path.IsPathRooted(task.Target)||!Path.IsPathRooted(task.Payload)||!string.Equals(Path.GetDirectoryName(target),Path.GetDirectoryName(payload),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(payload).StartsWith(".DayTrace-update-",StringComparison.Ordinal)||task.Backup!=task.Target+".update-backup"||!target.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||!UpdateChecker.ParseVersion(task.Version,out version))throw new UpdateCheckException("更新路径或版本无效。");
  }
  static void Launch(string target){using(var restarted=Process.Start(new ProcessStartInfo(target){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(target)})){if(restarted==null||(restarted.WaitForExit(3000)&&restarted.ExitCode!=0))throw new UpdateCheckException("新版未能启动，正在恢复原版本。");}}
  internal static void Apply(UpdateTask task){Apply(task,Launch);}
  internal static void Apply(UpdateTask task,Action<string> launch){
   ValidateTask(task);
   if(task.ProcessId>0){try{using(var original=Process.GetProcessById(task.ProcessId)){if(original.StartTime.ToUniversalTime().Ticks==task.ProcessStartTicks&&!original.WaitForExit(120000))throw new UpdateCheckException("旧版本尚未退出，请稍后重试。");}}catch(ArgumentException){}catch(InvalidOperationException){}}
   if(!string.Equals(HashFile(task.Payload),task.Hash,StringComparison.OrdinalIgnoreCase)||FileVersionInfo.GetVersionInfo(task.Payload).ProductVersion!=task.Version)throw new UpdateCheckException("更新文件校验失败，原程序未修改。");
   bool replaced=false;
   try{
    File.Replace(task.Payload,task.Target,task.Backup);replaced=true;
    if(!string.Equals(HashFile(task.Target),task.Hash,StringComparison.OrdinalIgnoreCase))throw new UpdateCheckException("替换后的程序校验失败。");
    if(task.LaunchAfter)launch(task.Target);
    // Keep one backup until the next successful update, for manual recovery too.
   }catch{
    if(replaced&&File.Exists(task.Backup)){File.Replace(task.Backup,task.Target,null);if(task.LaunchAfter)launch(task.Target);}
    throw;
   }
  }
  public static int Run(string taskFile){
   UpdateTask task=null;
   try{task=Json.Decode<UpdateTask>(File.ReadAllText(taskFile));Apply(task);Storage.Atomic(taskFile+".result","success");return 0;}
   catch(Exception ex){try{Storage.Atomic(taskFile+".result",ex.Message);}catch{}System.Windows.Forms.MessageBox.Show("更新未完成："+ex.Message+"\n原有记录和设置已保留。","拾时更新");return 1;}
   finally{if(task!=null&&task.Payload!=null&&File.Exists(task.Payload)){try{ValidateTask(task);File.Delete(task.Payload);}catch{}}}
  }
 }
}
