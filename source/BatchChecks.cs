// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Yui-Wei

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DayTrace {
public static class BatchChecks {
 static Dictionary<string,object> Payload(List<Message> messages) {
  string text=messages.Last().content;int at=text.IndexOf("\n{",StringComparison.Ordinal);
  return Json.Decode<Dictionary<string,object>>(text.Substring(at+1));
 }
 static Dictionary<string,object>[] Activities(List<Message> messages) {
  var payload=Payload(messages);
  if(!payload.ContainsKey("activities"))return new Dictionary<string,object>[0];
  return ((ArrayList)payload["activities"]).Cast<Dictionary<string,object>>().ToArray();
 }
 static string Response(List<Message> messages) {
  var groups=Activities(messages);
  if(groups.Length==0)return "# 日报\n已根据本地统计完成汇总。";
  return Json.Encode(new Api.Classification{categories=groups.Where(x=>x["category"]==null).Select(x=>new Api.CategoryItem{id=(string)x["id"],category="工作"}).ToList(),report="# 今日回顾\n本批活动主要用于工作。"});
 }
 public static async Task Run(Action<string,bool> check) {
  DateTime day=DateTime.Today.AddHours(9).ToUniversalTime();
  var rows=Enumerable.Range(0,916).Select(i=>new Activity{Start=day.AddSeconds(i*10),End=day.AddSeconds(i*10+8),App="app"+(i%3),Title="窗口"+(i%4)}).ToList();
  var groups=BatchAI.Compact(rows);
  check("916 fragments compress into 12 distinct app/title activities",groups.Count==12&&groups.Sum(x=>x.count)==916&&groups.SelectMany(x=>x.Sources).Select(x=>x.Id).Distinct().Count()==916);
  check("effective seconds and hourly totals stay exact",groups.Sum(x=>x.seconds)==7328&&groups.Sum(x=>x.hours.Sum(h=>h.Value))==7328);
  string serialized=Json.Encode(groups);
  check("request omits original GUIDs and record objects",!serialized.Contains(rows[0].Id)&&!serialized.Contains("Sources")&&!serialized.Contains("CacheKey"));
  int oldSize=Api.Build(rows,new Conversation(),"",true,true).Sum(x=>x.content.Length);
  check("repeated-record payload reduced by at least 70 percent",serialized.Length<oldSize*.30);
  var gapRows=new List<Activity>{new Activity{Start=day,End=day.AddMinutes(1),App="word",Title="方案"},new Activity{Start=day.AddMinutes(2),End=day.AddMinutes(3),App="word",Title="方案"}};
  var gap=BatchAI.Compact(gapRows).Single();
  check("merged window excludes inactive gap",gap.seconds==120&&gap.windows.Count==1&&gap.windows[0].seconds==120&&gap.windows[0].from!=gap.windows[0].to);
  var boundary=BatchAI.Compact(new List<Activity>{new Activity{Start=DateTime.Today.AddHours(9).AddMinutes(59).ToUniversalTime(),End=DateTime.Today.AddHours(10).AddMinutes(1).ToUniversalTime(),App="editor",Title="文档"}}).Single();
  check("activity crossing hour boundary split into exact hourly totals",boundary.hours["09"]==60&&boundary.hours["10"]==60);
  int calls=0;var progress=new List<string>();
  var engine=new BatchAI((messages,token)=>{calls++;token.ThrowIfCancellationRequested();return Task.FromResult(Response(messages));},progress.Add);
  var result=await engine.Run(rows,true,CancellationToken.None);
  check("one request generates report and maps all 916 categories",calls==1&&result.Requests==1&&result.Categories.Count==916&&result.Report.Contains("今日回顾")&&result.Groups==12);
  check("compressed request substantially smaller than old full input",result.InputCharacters<oldSize*.30);
  var cachedEngine=new BatchAI((m,t)=>{throw new Exception("Unexpected API call");},delegate{},result.CachedCategories);
  var cached=await cachedEngine.Run(rows,false,CancellationToken.None);
  check("classification cache avoids all repeat network calls",cached.Requests==0&&cached.Categories.Count==916);
  int reportCalls=0;
  var cachedReport=await new BatchAI((m,t)=>{reportCalls++;return Task.FromResult(Response(m));},delegate{},result.CachedCategories).Run(rows,true,CancellationToken.None);
  check("cached categories still permit one-shot report",reportCalls==1&&cachedReport.Report.Contains("今日回顾"));
  var manual=gapRows.Select(x=>x.Copy()).ToList();manual[0].Category="学习";
  var manualResult=await new BatchAI((m,t)=>{throw new Exception("Unexpected API call");},delegate{}).Run(manual,false,CancellationToken.None);
  check("known manual category reused without overwriting labeled row",manualResult.Requests==0&&!manualResult.Categories.ContainsKey(manual[0].Id)&&manualResult.Categories[manual[1].Id]=="学习");
  check("browser titles are not collapsed into one program category",BatchAI.Compact(new List<Activity>{new Activity{App="edge",Title="项目",Start=day,End=day.AddMinutes(1)},new Activity{App="edge",Title="视频",Start=day.AddMinutes(1),End=day.AddMinutes(2)}}).Count==2);
  var distinct=Enumerable.Range(0,241).Select(i=>new Activity{Start=day.AddSeconds(i*10),End=day.AddSeconds(i*10+8),App="editor",Title="独立活动"+i}).ToList();
  int summaries=0;
  var large=await new BatchAI((m,t)=>{if(Activities(m).Length==0)summaries++;return Task.FromResult(Response(m));},delegate{}).Run(distinct,true,CancellationToken.None);
  check("many distinct activities retain bounded batching and final merge",large.Categories.Count==241&&large.Requests>1&&summaries>0);
  int splits=0;
  var adaptive=await new BatchAI((m,t)=>{if(Activities(m).Length>10){splits++;throw new Exception("API 返回 HTTP 413");}return Task.FromResult(Response(m));},delegate{}).Run(distinct.Take(30).ToList(),false,CancellationToken.None);
  check("oversized compact batch automatically splits",splits>0&&adaptive.Categories.Count==30);
  int authCalls=0;bool failed=false;
  try{await new BatchAI((m,t)=>{authCalls++;throw new Exception("API 返回 HTTP 401");},delegate{}).Run(rows,false,CancellationToken.None);}catch{failed=true;}
  check("authentication failure makes only one call",failed&&authCalls==1);
  using(var cancel=new CancellationTokenSource()) {
   int cancellationCalls=0;bool cancelled=false;
   try{await new BatchAI((m,t)=>{cancellationCalls++;cancel.Cancel();return Task.FromResult(Response(m));},delegate{}).Run(distinct,false,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
   check("cancel stops before another batch",cancelled&&cancellationCalls==1);
  }
  check("aggregation never modifies original records",rows.All(x=>x.Category=="未分类")&&rows.Count==916);
  check("classification cache contains only hashed keys and category values",result.CachedCategories.All(x=>x.Key.Length==64&&BatchAI.ValidCategory(x.Value)));
  var alternating=Enumerable.Range(0,20).Select(i=>new Activity{App="word",Title="方案",Start=day.AddMinutes(i*10),End=day.AddMinutes(i*10+1)}).ToList();
  var manyWindows=BatchAI.Compact(alternating).Single();
  check("disjoint appearances use hourly totals without unbounded window lists",manyWindows.windows==null&&manyWindows.seconds==1200&&manyWindows.hours.Sum(x=>x.Value)==1200);
  var midnight=BatchAI.Compact(new List<Activity>{new Activity{App="word",Title="跨日",Start=DateTime.Today.AddHours(23).AddMinutes(59).ToUniversalTime(),End=DateTime.Today.AddDays(1).ToUniversalTime()}}).Single();
  check("midnight endpoint represented as 24:00",midnight.last=="24:00:00"&&midnight.windows[0].to=="24:00:00"&&midnight.seconds==60);
  int awayCalls=0;
  var away=await new BatchAI((m,t)=>{awayCalls++;return Task.FromResult("");},delegate{}).Run(new List<Activity>{new Activity{App="离开",Start=day,End=day.AddMinutes(5)}},true,CancellationToken.None);
  check("away-only day avoids AI and generates exact local totals",awayCalls==0&&away.Report.Contains("300"));
  var mixedSoftware=new List<Activity>();
  for(int i=0;i<50;i++)mixedSoftware.Add(new Activity{App="cloudmusic",Title="歌曲 "+i,Start=day.AddMinutes(i),End=day.AddMinutes(i).AddSeconds(30)});
  for(int i=0;i<20;i++)mixedSoftware.Add(new Activity{App="explorer",Title="文件夹 "+i,Start=day.AddHours(1).AddMinutes(i),End=day.AddHours(1).AddMinutes(i).AddSeconds(30)});
  mixedSoftware.Add(new Activity{App="msedge",Title="项目网页",Start=day.AddHours(2),End=day.AddHours(2).AddMinutes(1)});
  mixedSoftware.Add(new Activity{App="msedge",Title="视频网页",Start=day.AddHours(2).AddMinutes(1),End=day.AddHours(2).AddMinutes(2)});
  var softwareGroups=BatchAI.Compact(mixedSoftware);
  check("music and explorer group by software while browser groups by title",softwareGroups.Count==4&&softwareGroups.Single(x=>x.app=="cloudmusic").Sources.Count==50&&softwareGroups.Count(x=>x.app=="msedge")==2);
  check("software grouping omits changing song and folder titles",softwareGroups.Where(x=>x.scope=="software").All(x=>x.title==null)&&!Json.Encode(softwareGroups).Contains("歌曲"));
  var softwareResult=await new BatchAI((m,t)=>Task.FromResult(Response(m)),delegate{}).Run(mixedSoftware,false,CancellationToken.None);
  check("software AI classification maps to every original fragment",softwareResult.Groups==4&&softwareResult.Categories.Count==72&&softwareResult.CachedCategories.ContainsKey(BatchAI.AppKey("cloudmusic")));
  var unseenSong=new Activity{App="cloudmusic",Title="全新的歌曲",Start=day,End=day.AddSeconds(10)};
  var softwareCached=await new BatchAI((m,t)=>{throw new Exception("unexpected call");},delegate{},softwareResult.CachedCategories).Run(new List<Activity>{unseenSong},false,CancellationToken.None);
  check("software cache covers brand-new titles without API call",softwareCached.Requests==0&&softwareCached.Categories[unseenSong.Id]=="工作");
  var overrideRules=new ClassificationRules();overrideRules.WholeSoftware[ClassificationRules.App("cloudmusic.exe")]=false;overrideRules.WholeSoftware[ClassificationRules.App("msedge")]=true;
  var overrideGroups=BatchAI.Compact(mixedSoftware,null,overrideRules);
  check("per-app overrides support both scope directions",overrideGroups.Count(x=>x.app=="cloudmusic")==50&&overrideGroups.Count(x=>x.app=="msedge")==1);
  check("app-level keys separate from empty window titles",BatchAI.AppKey("explorer")!=BatchAI.Key("explorer","")&&overrideRules.Key("msedge.exe","any title")==BatchAI.AppKey("msedge"));
  string scopeFile=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"daytrace-scope-test-"+Guid.NewGuid().ToString("N")+".json");
  try{overrideRules.Save(scopeFile);var restored=ClassificationRules.Load(scopeFile);check("software scope preferences persist and reload",!restored.IsWholeSoftware("cloudmusic")&&restored.IsWholeSoftware("msedge"));}finally{if(System.IO.File.Exists(scopeFile))System.IO.File.Delete(scopeFile);}
  // Persist local measurements as a test result, without claiming production latency.
  check("measured fixture: old input "+oldSize+" chars, compact input "+result.InputCharacters+" chars, "+calls+" request",true);
 }
}
}
