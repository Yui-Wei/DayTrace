using System;
using System.Collections.Generic;
using System.IO;

namespace DayTrace {
public sealed class ClassificationRules {
 public Dictionary<string,bool> WholeSoftware=new Dictionary<string,bool>();
 static readonly HashSet<string> Defaults=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
  "explorer","cloudmusic","qqmusic","kugou","kuwo","spotify","music","applemusic","foobar2000","aimp","lx-music-desktop"
 };
 public static string App(string name){string value=(name??"").Trim().ToLowerInvariant();return value.EndsWith(".exe")?value.Substring(0,value.Length-4):value;}
 public bool IsWholeSoftware(string app){bool value;return WholeSoftware.TryGetValue(App(app),out value)?value:Defaults.Contains(App(app));}
 public string Key(string app,string title){return IsWholeSoftware(app)?BatchAI.AppKey(app):BatchAI.Key(app,title);}
 public ClassificationRules Copy(){return new ClassificationRules{WholeSoftware=new Dictionary<string,bool>(WholeSoftware)};}
 public static ClassificationRules Load(string path){if(!File.Exists(path))return new ClassificationRules();var result=Json.Decode<ClassificationRules>(File.ReadAllText(path));if(result==null)throw new Exception("分类范围设置无效");if(result.WholeSoftware==null)result.WholeSoftware=new Dictionary<string,bool>();return result;}
 public void Save(string path){Storage.Atomic(path,Json.Encode(this));}
}
}
