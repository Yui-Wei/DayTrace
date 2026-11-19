using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms=System.Windows.Forms;
namespace DayTrace {
public static class Desktop {
 [STAThread] public static int Main(string[] args) {
  if(args.Length==2&&args[0]=="--apply-update")return UpdateInstaller.Run(args[1]);
  int result=RunDesktop(args);
  // UI shutdown and instance disposal have completed; do not let framework worker threads keep a closed app alive.
  Environment.Exit(result);return result;
 }
 static int RunDesktop(string[] args){
  bool demo=args.Contains("--demo");
  using(var instance=new SingleInstance("Local\\DayTrace."+Environment.UserName+(demo?".Demo":""))) {
   if(!instance.TryAcquire(0)){
    if(instance.ActivateExisting())return 0;
    if(!instance.TryAcquire(2500)){
     if(!demo&&!instance.PeerUsesAcknowledgements&&NativeAppearance.ShowExistingWindow())return 0;
     Forms.MessageBox.Show("后台拾时没有响应，未能打开窗口。请先结束旧的拾时进程后重试。","拾时");return 1;
    }
   }
   var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
   using(var stream=typeof(Desktop).Assembly.GetManifestResourceStream("Theme.xaml"))app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
   string root=demo?Path.Combine(Path.GetTempPath(),"DayTrace-v6-"+Guid.NewGuid().ToString("N")):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DayTrace");
   try{
    var w=new DesktopWindow(new Storage(root),demo);app.MainWindow=w;
    instance.Listen(()=>{if(!app.Dispatcher.HasShutdownStarted)app.Dispatcher.BeginInvoke((Action)delegate{if(w.ActivateExistingWindow())instance.AcknowledgeActivation();});});
    w.Closed+=delegate{instance.Dispose();Environment.Exit(0);};
    if(demo)w.Loaded+=async delegate{
     if(args.Contains("--linger-worker"))new Thread(()=>Thread.Sleep(20000)){IsBackground=false}.Start();
     if(args.Contains("--slow-exit"))w.exitCheckDelay=3000;
     if(args.Contains("--activation-check")){await w.VerifyActivationLifecycle(args.Last());return;}
     if(args.Contains("--exit-check"))await w.VerifyTrayExit(args.Last(),args.Contains("--hidden"),args.Contains("--native-tray"));else await w.Verify(args.Last());
    };
    w.Show();if(args.Contains("--minimized"))w.Hide();app.Run();return 0;
   }catch(Exception ex){if(demo)File.WriteAllText(Path.Combine(Environment.CurrentDirectory,"work","wpf-error.txt"),ex.ToString());else Forms.MessageBox.Show(ex.ToString(),"启动失败");return 1;}
  }
 }

}
public sealed class ActivityRow {
 public Activity Source{get;set;} public bool Send{get;set;} public string Mode{get;set;}
 public string Start{get{return Source.Start.ToLocalTime().ToString("HH:mm:ss");}} public string End{get{return Source.End.ToLocalTime().ToString("HH:mm:ss");}}
 public string Duration{get{return DesktopWindow.Duration(Source.Seconds);}} public string App{get{return Source.App;}} public string Title{get{return Source.Title;}} public string Category{get{return Source.Category;}}
}
public sealed class DesktopWindow:Window {
 ClassificationRules classificationRules=new ClassificationRules();
 readonly ComboBox categoryScope=Select(new[]{"仅此窗口标题","整个软件"});
 readonly Dictionary<string,string> categoryCache=new Dictionary<string,string>();
 readonly Storage store; readonly Recorder recorder;readonly Tracker tracker;Settings settings;readonly bool demo;
 readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
 readonly Forms.NotifyIcon tray=new Forms.NotifyIcon();
 readonly ContextMenu trayMenu=new ContextMenu();
 MenuItem trayPauseItem;
 bool themeIsDark;
 readonly Grid main=new Grid();readonly ContentControl content=new ContentControl();readonly List<FrameworkElement> pages=new List<FrameworkElement>();readonly List<Button> navigation=new List<Button>();
 readonly TextBlock title=Text("今日概览",32,true),status=Text("正在记录",15),notice=Text("",16);
 readonly TextBox date=Input(); readonly Button pause,nextDay;
 readonly Button classifyButton,reportButton,cancelButton;
 readonly TextBlock updateStatus=Text("点击检查更新，获取最新版本信息",18);
 Button checkUpdateButton,viewUpdateButton,installUpdateButton,cancelUpdateButton;
 readonly CheckBox autoUpdate=new CheckBox();
 readonly ProgressBar downloadProgress=new ProgressBar{Height=6,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,12)};
 UpdatePreferences updatePreferences;
 UpdateInfo availableUpdate;
 CancellationTokenSource downloadRequest;
 bool installing,updatingPreference;
 Action<UpdateInfo> notifyUpdate;
 string UpdatePreferencesPath{get{return Path.Combine(store.Root,"update-settings.json");}}
 CancellationTokenSource updateRequest;
 Func<CancellationToken,Task<UpdateInfo>> fetchUpdate=UpdateChecker.Check;
 string updatePage=UpdateChecker.Repository+"/releases";
 readonly TextBlock aiProgress=Text("选择日期后点击 AI 分类或生成日报",18);
 readonly ProgressBar aiBusy=new ProgressBar{Height=5,IsIndeterminate=true,Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,14)};
 readonly DataGrid activities=new DataGrid();readonly ComboBox category=Select(Api.Categories),theme=Select(new[]{"跟随系统","浅色","深色"});
 readonly TextBox retention=Input(),excluded=Input(),continuous=Input(),baseUrl=Input(),model=Input();readonly PasswordBox key=new PasswordBox();
 readonly TextBlock keyStatus=Text("",15),savedKeyHint=Text("",16);
 Func<string,string> readCredential=Credentials.Read;readonly CheckBox startup=new CheckBox();
 readonly RichTextBox reportView=new RichTextBox{IsReadOnly=true};readonly StackPanel overview=new StackPanel();readonly WrapPanel dateTools=new WrapPanel();readonly ScrollViewer settingsScroll;
 CancellationTokenSource request;bool busy,quitting,locked,stopped;int ticks,selectedPage;int? timelineHour,timelineMinute;DateTime day=DateTime.Today,lastCleanup=DateTime.Today,observedToday=DateTime.Today;
 public DesktopWindow(Storage storage,bool demonstration){
  store=storage;demo=demonstration;updatePreferences=UpdatePreferences.Load(UpdatePreferencesPath);notifyUpdate=NotifyUpdate;settings=store.LoadSettings();classificationRules=ClassificationRules.Load(RulesPath);LoadCategoryCache();recorder=new Recorder(store);recorder.CategoryLookup=LookupCategory;tracker=new Tracker(recorder,settings);
  Title="拾时 · DayTrace";FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=18;Width=Math.Min(1320,SystemParameters.WorkArea.Width);Height=Math.Min(880,SystemParameters.WorkArea.Height);MinWidth=940;MinHeight=630;WindowStartupLocation=WindowStartupLocation.CenterScreen;SetResourceReference(BackgroundProperty,"Canvas");SetResourceReference(ForegroundProperty,"Ink");ApplyTheme(demo?"light":settings.Theme);
  SourceInitialized+=delegate{NativeAppearance.Apply(this,themeIsDark);};
  var shell=new Grid();shell.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(240)});shell.ColumnDefinitions.Add(new ColumnDefinition());Content=shell;
  var side=new Grid{Margin=new Thickness(15,20,15,18)};side.RowDefinitions.Add(new RowDefinition{Height=new GridLength(76)});side.RowDefinitions.Add(new RowDefinition());side.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});shell.Children.Add(side);var brand=new StackPanel{Orientation=Orientation.Horizontal};
  using(var iconStream=typeof(Desktop).Assembly.GetManifestResourceStream("App.ico")){var decoder=new IconBitmapDecoder(iconStream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);var bitmap=decoder.Frames.OrderByDescending(frame=>frame.PixelWidth).First();bitmap.Freeze();Icon=bitmap;brand.Children.Add(new Image{Source=bitmap,Width=36,Height=36,Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center});}
  brand.Children.Add(Text("拾时",36,true));side.Children.Add(brand);var nav=new StackPanel();Grid.SetRow(nav,1);side.Children.Add(nav);
  string[] names={"⌂   今日概览","▤   活动记录","◇   AI 助手","☷   设置","ⓘ   关于"};for(int i=0;i<names.Length;i++){int index=i;var b=Button(names[i],delegate{Navigate(index);});b.BorderThickness=new Thickness(0);b.Margin=new Thickness(0,3,0,5);b.Padding=new Thickness(14,13,10,13);b.SetResourceReference(StyleProperty,"NavigationButton");navigation.Add(b);nav.Children.Add(b);}
  var bottom=new StackPanel();bottom.Children.Add(status);pause=Button("暂停记录",TogglePause);pause.SetResourceReference(StyleProperty,"NavigationButton");pause.Margin=new Thickness(0,10,0,6);bottom.Children.Add(pause);var exitButton=Button("退出",Exit);exitButton.SetResourceReference(StyleProperty,"NavigationButton");exitButton.Margin=new Thickness(0,0,0,6);bottom.Children.Add(exitButton);bottom.Children.Add(Text("v"+AppVersion.Current,14));Grid.SetRow(bottom,2);side.Children.Add(bottom);
  Grid.SetColumn(main,1);main.Margin=new Thickness(26,18,24,14);shell.Children.Add(main);main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});main.RowDefinitions.Add(new RowDefinition());main.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var header=new Grid{Margin=new Thickness(0,0,0,15)};header.Children.Add(title);main.Children.Add(header);
  date.Text=day.ToString("yyyy-MM-dd");date.Width=150;date.Margin=new Thickness(0,0,8,0);date.ToolTip="日期格式 yyyy-MM-dd";date.LostKeyboardFocus+=delegate{CommitDate();};date.KeyDown+=delegate(object s,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Enter){CommitDate();e.Handled=true;}};dateTools.Children.Add(date);dateTools.Children.Add(Button("日历",ShowCalendar));dateTools.Children.Add(Button("前一天",delegate{SetDay(day.AddDays(-1));}));nextDay=Button("后一天",delegate{SetDay(day.AddDays(1));});dateTools.Children.Add(nextDay);UpdateDateNavigation(DateTime.Today);dateTools.Children.Add(Button("今天",delegate{SetDay(DateTime.Today);}));dateTools.Children.Add(Button("刷新",Refresh));dateTools.Margin=new Thickness(0,0,0,14);Grid.SetRow(dateTools,1);main.Children.Add(dateTools);Grid.SetRow(content,2);main.Children.Add(content);notice.Margin=new Thickness(0,8,0,0);Grid.SetRow(notice,3);main.Children.Add(notice);
  pages.Add(Scroll(overview));pages.Add(BuildActivities());classifyButton=Button("AI 分类",async delegate{await RunAI(false);});reportButton=Button("生成日报",async delegate{await RunAI(true);});cancelButton=Button("取消生成",delegate{if(request!=null)request.Cancel();});cancelButton.IsEnabled=false;pages.Add(BuildAnalysis());settingsScroll=Scroll(BuildSettings());var settingsPage=new DockPanel();var saveBar=Bar(Button("保存设置",SaveSettings),Button("恢复已保存设置",LoadSettings));saveBar.Margin=new Thickness(0,10,0,0);DockPanel.SetDock(saveBar,Dock.Bottom);settingsPage.Children.Add(saveBar);settingsPage.Children.Add(settingsScroll);pages.Add(settingsPage);pages.Add(Scroll(BuildAbout()));LoadSettings();Navigate(0);
  BuildTrayMenu();using(var iconStream=typeof(Desktop).Assembly.GetManifestResourceStream("App.ico")){tray.Icon=new System.Drawing.Icon(iconStream);}tray.Text="拾时 · 正在记录";tray.MouseUp+=delegate(object sender,Forms.MouseEventArgs e){if(e.Button==Forms.MouseButtons.Right)Dispatcher.BeginInvoke(DispatcherPriority.Background,(Action)ShowTrayMenu);};tray.Visible=!demo;tray.DoubleClick+=delegate{Dispatcher.BeginInvoke(DispatcherPriority.Background,(Action)Restore);};tray.BalloonTipClicked+=delegate{Dispatcher.BeginInvoke(DispatcherPriority.Background,(Action)delegate{Restore();Navigate(4);});};
  Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(!quitting){e.Cancel=true;Hide();return;}try{if(!demo){if(!stopped)tracker.Feed(Sample());recorder.Flush();}}catch(Exception ex){e.Cancel=true;quitting=false;exitPending=false;Show();WindowState=WindowState.Normal;Activate();Error(ex);Forms.MessageBox.Show("保存记录失败，尚未退出："+ex.Message,"拾时");return;}trayMenu.IsOpen=false;if(updateRequest!=null)updateRequest.Cancel();if(downloadRequest!=null)downloadRequest.Cancel();timer.Stop();SystemEvents.SessionSwitch-=Session;SystemEvents.PowerModeChanged-=Power;tray.Dispose();};SystemEvents.SessionSwitch+=Session;SystemEvents.PowerModeChanged+=Power;
  timer.Tick+=delegate{try{if(!stopped)tracker.Feed(Sample());CheckDayRollover(DateTime.Today);if(++ticks%5==0)recorder.Flush();if(ticks%15==0&&IsVisible){if(settings.Theme=="system")ApplyTheme(settings.Theme);if(selectedPage==0)Refresh();}if(lastCleanup!=DateTime.Today){recorder.Flush();store.Cleanup(settings.RetentionDays,DateTime.Today);recorder.Cache.Clear();lastCleanup=DateTime.Today;}}catch(Exception ex){stopped=true;timer.Stop();UpdateRecordingStatus();Error(ex);}};
  if(demo){var start=day.AddHours(9).ToUniversalTime();recorder.Add(start,start.AddMinutes(42),"WINWORD","项目方案","工作");recorder.Add(start.AddMinutes(42),start.AddMinutes(60),"msedge","C# 文档","学习");recorder.Add(start.AddMinutes(60),start.AddMinutes(75),"离开","无操作","离开");status.Text="演示数据";}else{store.Cleanup(settings.RetentionDays,DateTime.Today);tracker.Feed(Sample());timer.Start();}Loaded+=async delegate{if(!demo)await CheckAutomaticUpdates(DateTime.Today);};Refresh();LoadReport();UpdateRecordingStatus();
 }
 static TextBlock Text(string text,double size=18,bool bold=false){return new TextBlock{Text=text,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(0,4,0,4),TextWrapping=TextWrapping.Wrap};}
 static TextBox Input(){var input=new TextBox();input.SetResourceReference(Control.ForegroundProperty,"Ink");return input;}
 static ComboBox Select(IEnumerable<string> values){var c=new ComboBox{MinWidth=120};foreach(var s in values)c.Items.Add(s);c.SelectedIndex=0;return c;}
 static Button Button(string text,Action action){var b=new Button{Content=text};b.Click+=delegate{action();};return b;}
 static WrapPanel Bar(params UIElement[] items){var p=new WrapPanel();foreach(var x in items){var f=x as FrameworkElement;if(f!=null)f.Margin=new Thickness(0,0,8,7);p.Children.Add(x);}return p;}
 static ScrollViewer Scroll(UIElement child){
  var scroll=new ScrollViewer{Content=child,Background=Brushes.Transparent,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,PanningMode=PanningMode.VerticalOnly};
  scroll.PreviewMouseWheel+=delegate(object sender,System.Windows.Input.MouseWheelEventArgs e){
   if(e.Delta==0||(e.Delta>0&&scroll.VerticalOffset>.5)||(e.Delta<0&&scroll.VerticalOffset<scroll.ScrollableHeight-.5))return;
   // A nested viewer at its limit must let the enclosing page continue scrolling.
   var parent=VisualTreeHelper.GetParent(scroll);
   while(parent!=null){var outer=parent as ScrollViewer;if(outer!=null){
    if((e.Delta>0&&outer.VerticalOffset>.5)||(e.Delta<0&&outer.VerticalOffset<outer.ScrollableHeight-.5)){
     double step=SystemParameters.WheelScrollLines<0?outer.ViewportHeight:Math.Max(1,SystemParameters.WheelScrollLines)*16;
     outer.ScrollToVerticalOffset(outer.VerticalOffset-e.Delta/120.0*step);e.Handled=true;return;
    }
   }parent=VisualTreeHelper.GetParent(parent);}
  };
  return scroll;
 }
 static Border Card(UIElement body){var b=new Border{Child=body,Padding=new Thickness(18),Margin=new Thickness(0,0,0,16),CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1)};b.SetResourceReference(Border.BackgroundProperty,"Surface");b.SetResourceReference(Border.BorderBrushProperty,"Line");return b;}
 public static string Duration(double seconds){var t=TimeSpan.FromSeconds(seconds);return t.TotalHours>=1?string.Format("{0} 小时 {1} 分",(int)t.TotalHours,t.Minutes):string.Format("{0} 分 {1} 秒",(int)t.TotalMinutes,t.Seconds);}
 void ApplyTheme(string choice){bool dark=choice=="dark";if(choice=="system")using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))dark=k!=null&&Convert.ToInt32(k.GetValue("AppsUseLightTheme",1))==0;string[] names={"Canvas","Surface","Field","Ink","Muted","Line","Accent","Selection","NavigationHover"};string[] colors=dark?new[]{"#18191D","#28292F","#373942","#ECEDF2","#9296A2","#3A3C44","#0A84FF","#364657","#303946"}:new[]{"#F2F7E1","#FCFCF7","#FAFAFA","#1C1E26","#65666B","#E1E4DC","#0066B6","#DDE7D4","#E3EDDA"};for(int i=0;i<names.Length;i++)Application.Current.Resources[names[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));if(!dark)Application.Current.Resources["Canvas"]=new LinearGradientBrush(Color.FromRgb(234,249,231),Color.FromRgb(250,244,222),20);foreach(string resourceName in names)trayMenu.Resources[resourceName]=Application.Current.Resources[resourceName];trayMenu.Background=(Brush)Application.Current.Resources["Surface"];trayMenu.Foreground=(Brush)Application.Current.Resources["Ink"];themeIsDark=dark;NativeAppearance.Apply(this,dark);if(pages.Count==5)Navigate(selectedPage);UpdateRecordingStatus();}
 void BuildTrayMenu(){
  trayMenu.SetResourceReference(StyleProperty,"TrayContextMenu");trayMenu.Placement=PlacementMode.MousePoint;trayMenu.StaysOpen=false;
  trayMenu.Opened+=delegate{NativeAppearance.FocusMenu(trayMenu);};
  AddTrayAction("打开拾时",Restore);
  trayPauseItem=AddTrayAction("暂停记录",TogglePause);
  AddTrayAction("设置",delegate{Restore();Navigate(3);});
  var separator=new Separator();separator.SetResourceReference(StyleProperty,"TraySeparator");trayMenu.Items.Add(separator);
  AddTrayAction("退出",Exit,true);
 }
 MenuItem AddTrayAction(string name,Action action,bool exits=false){
  var item=new MenuItem{Header=name};item.SetResourceReference(StyleProperty,"TrayMenuItem");
  item.Click+=delegate{if(exits)exitPending=true;trayMenu.IsOpen=false;Dispatcher.BeginInvoke(DispatcherPriority.Background,action);};trayMenu.Items.Add(item);return item;
 }
 void ShowTrayMenu(){
  if(quitting)return;
  if(settings.Theme=="system")ApplyTheme(settings.Theme);
  trayPauseItem.Header=tracker.Paused?"继续记录":"暂停记录";
  trayPauseItem.IsEnabled=!stopped;
  trayMenu.IsOpen=true;
 }
 FrameworkElement BuildActivities(){var root=new DockPanel();var tools=Bar(Button("导出 CSV",Export),Button("删除当天",delegate{Delete(false);}),Button("删除全部历史",delegate{Delete(true);}));DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);var footer=Bar(categoryScope,category,Button("修改分类",ApplyManualCategory),AIScopeButton());DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);Columns(activities);activities.Columns.Add(new DataGridTextColumn{Header="分类范围",Binding=new Binding("Mode"),Width=new DataGridLength(1.1,DataGridLengthUnitType.Star)});activities.SelectionChanged+=delegate{var selected=activities.SelectedItem as ActivityRow;if(selected!=null)categoryScope.SelectedIndex=classificationRules.IsWholeSoftware(selected.App)?1:0;};root.Children.Add(Card(activities));return root;}
 Button AIScopeButton(){
  var button=Button("仅设置 AI 分类范围  ⓘ",ApplyAIScope);
  button.ToolTip="只保存范围，具体分到哪类由下次 AI 分类决定。";
  ToolTipService.SetInitialShowDelay(button,250);ToolTipService.SetShowDuration(button,20000);
  return button;
 }
 static void Columns(DataGrid grid){string[] names={"开始","结束","时长","软件","窗口标题","分类"},paths={"Start","End","Duration","App","Title","Category"};for(int i=0;i<paths.Length;i++)grid.Columns.Add(new DataGridTextColumn{Header=names[i],Binding=new Binding(paths[i]),Width=new DataGridLength(i==4?3:i==2||i==3?1.5:1,DataGridLengthUnitType.Star)});}
 FrameworkElement BuildAnalysis(){
  var root=new DockPanel();
  var top=new StackPanel();
  top.Children.Add(Bar(classifyButton,reportButton,cancelButton,Button("删除日报",delegate{if(!busy&&Confirm("删除所选日期的日报？")){string path=ReportPath(day);if(File.Exists(path))File.Delete(path);store.SaveChat(day,new Conversation());LoadReport();}})));
  top.Children.Add(aiProgress);top.Children.Add(aiBusy);top.Margin=new Thickness(0,0,0,10);
  DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);root.Children.Add(Card(reportView));return root;
 }
 FrameworkElement BuildSettings(){var root=new StackPanel();root.Children.Add(Text("常规设置",22,true));root.Children.Add(Group(Row("外观主题","跟随 Windows 或选择浅色、深色",theme),Row("开机自启动","登录 Windows 后在托盘记录",startup),Row("记录保留","天，0 表示永久保留",retention)));excluded.AcceptsReturn=continuous.AcceptsReturn=true;excluded.TextWrapping=continuous.TextWrapping=TextWrapping.Wrap;excluded.Height=continuous.Height=90;excluded.VerticalScrollBarVisibility=continuous.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;root.Children.Add(Text("活动采集",22,true));root.Children.Add(Group(Row("排除软件","每行填写一个进程名",excluded),Row("持续计时软件","前台视频和会议，锁屏仍算离开",continuous)));root.Children.Add(Text("AI 接口",22,true));root.Children.Add(Group(Row("API 地址","支持 OpenAI 兼容 HTTPS 接口",baseUrl),Row("模型名称","填写服务商提供的模型名",model),Row("API Key","保存在 Windows 凭据管理器中，留空保留现有密钥",KeyEditor()),Row("快捷配置","预设或删除当前接口密钥",Bar(Button("DeepSeek",delegate{baseUrl.Text="https://api.deepseek.com";model.Text="deepseek-flash";key.Clear();}),Button("删除 Key",delegate{if(Confirm("删除此接口的 API Key？")){Credentials.Delete(baseUrl.Text);key.Clear();UpdateKeyStatus();notice.Text="密钥已删除";}})))));return root;}
 FrameworkElement BuildAbout(){
  var root=new StackPanel();
  var intro=new StackPanel();intro.Children.Add(Text("拾时 · DayTrace",30,true));
  intro.Children.Add(Text("版本 "+AppVersion.Current,20));
  intro.Children.Add(Text("记录时间的去向，回顾一天的工作与生活。",17));
  root.Children.Add(Card(intro));
  root.Children.Add(Text("软件更新",22,true));
  var updates=new StackPanel();autoUpdate.IsChecked=updatePreferences.Enabled;
  autoUpdate.Checked+=async delegate{await SetAutomaticUpdates(true);};autoUpdate.Unchecked+=async delegate{await SetAutomaticUpdates(false);};
  updates.Children.Add(Row("自动检查更新","每天首次启动或打开软件时检查一次",autoUpdate));updates.Children.Add(updateStatus);
  var hint=Text("发现新版后可直接下载并更新，完成后自动重新打开软件。",16);hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");updates.Children.Add(hint);
  checkUpdateButton=Button("检查更新",async delegate{await CheckForUpdates();});
  viewUpdateButton=Button("查看最新版本",delegate{OpenLink(updatePage);});viewUpdateButton.Visibility=Visibility.Collapsed;
  installUpdateButton=Button("立即更新",async delegate{await InstallUpdate();});installUpdateButton.Visibility=Visibility.Collapsed;
  cancelUpdateButton=Button("取消下载",delegate{if(downloadRequest!=null)downloadRequest.Cancel();});cancelUpdateButton.Visibility=Visibility.Collapsed;
  updates.Children.Add(downloadProgress);
  var actions=Bar(checkUpdateButton,installUpdateButton,viewUpdateButton,cancelUpdateButton);actions.Margin=new Thickness(0,12,0,0);updates.Children.Add(actions);root.Children.Add(Card(updates));
  root.Children.Add(Text("项目与反馈",22,true));
  root.Children.Add(Card(Bar(Button("项目主页",delegate{OpenLink(UpdateChecker.Repository);}),Button("版本记录",delegate{OpenLink(UpdateChecker.Repository+"/releases");}),Button("问题反馈",delegate{OpenLink(UpdateChecker.Repository+"/issues");}))));
  return root;
 }
 void OpenLink(string url){try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}catch{notice.Text="无法打开浏览器，请访问 "+url;}}
 async Task CheckForUpdates(bool automatic=false){
  if(updateRequest!=null||installing)return;
  var cancellation=new CancellationTokenSource();updateRequest=cancellation;
  checkUpdateButton.IsEnabled=false;checkUpdateButton.Content="正在检查…";viewUpdateButton.Visibility=Visibility.Collapsed;installUpdateButton.Visibility=Visibility.Collapsed;availableUpdate=null;updateStatus.Text="正在检查更新…";
  try{
   var result=await fetchUpdate(cancellation.Token);if(quitting)return;
   updatePage=result.PageUrl;availableUpdate=result;
   updateStatus.Text=result.Latest==null?"暂未找到正式版本，可前往版本页面查看。":result.HasUpdate?"发现新版本 v"+result.Latest+" · 当前版本 v"+AppVersion.Current:result.Latest==Version.Parse(AppVersion.Current)?"当前已是最新版本（v"+AppVersion.Current+"）":"当前版本无需更新（v"+AppVersion.Current+"）";
   viewUpdateButton.Visibility=result.Latest==null||result.HasUpdate?Visibility.Visible:Visibility.Collapsed;
   installUpdateButton.Visibility=result.HasUpdate&&result.CanInstall?Visibility.Visible:Visibility.Collapsed;
   if(automatic&&result.HasUpdate)notifyUpdate(result);
  }catch(OperationCanceledException){if(!quitting)updateStatus.Text="检查超时，请稍后重试。";}
  catch(UpdateCheckException ex){if(!quitting)updateStatus.Text=ex.Message;}
  catch{if(!quitting)updateStatus.Text="检查更新失败，请检查网络后重试，或通过版本记录查看。";}
  finally{updateRequest=null;cancellation.Dispose();if(!quitting){checkUpdateButton.IsEnabled=true;checkUpdateButton.Content="检查更新";}}
 }
 async Task SetAutomaticUpdates(bool enabled){
  if(updatingPreference)return;
  bool previous=updatePreferences.Enabled;updatePreferences.Enabled=enabled;
  try{if(!demo)updatePreferences.Save(UpdatePreferencesPath);}catch(Exception ex){updatePreferences.Enabled=previous;updatingPreference=true;autoUpdate.IsChecked=previous;updatingPreference=false;notice.Text="更新设置未保存："+ex.Message;return;}
  if(enabled&&!demo)await CheckAutomaticUpdates(DateTime.Today);
 }
 async Task CheckAutomaticUpdates(DateTime today,bool allowDemo=false){
  if((demo&&!allowDemo)||updateRequest!=null||installing||!updatePreferences.ShouldCheck(today))return;
  updatePreferences.LastAttempt=today.ToString("yyyy-MM-dd");
  try{if(!demo)updatePreferences.Save(UpdatePreferencesPath);}catch(Exception ex){notice.Text="无法保存更新检查日期："+ex.Message;return;}
  await CheckForUpdates(true);
 }
 void NotifyUpdate(UpdateInfo info){
  notice.Text="发现新版本 v"+info.Latest+"，可在「关于」页面更新。";
  navigation[4].Content="ⓘ   关于  ·";
  if(!demo)tray.ShowBalloonTip(8000,"拾时发现新版本","v"+info.Latest+" 已可用，点击查看。",Forms.ToolTipIcon.Info);
 }
 async Task InstallUpdate(){
  if(installing||updateRequest!=null)return;
  if(busy){notice.Text="请先完成或取消 AI 处理，再进行更新。";return;}
  var info=availableUpdate;if(info==null||!info.HasUpdate||!info.CanInstall)return;
  if(!Confirm("下载并安装 v"+info.Latest+"？\n完成后会自动重新打开拾时，记录和设置会保留。"))return;
  installing=true;var cancellation=new CancellationTokenSource();downloadRequest=cancellation;
  checkUpdateButton.IsEnabled=installUpdateButton.IsEnabled=false;cancelUpdateButton.Visibility=Visibility.Visible;downloadProgress.Visibility=Visibility.Visible;downloadProgress.Value=0;
  try{
   updateStatus.Text="正在下载更新…";
   string folder=Path.Combine(store.Root,"updates",Guid.NewGuid().ToString("N"));
   string file=await UpdateInstaller.Download(info,folder,percent=>Dispatcher.BeginInvoke((Action)delegate{if(!quitting){downloadProgress.Value=percent;updateStatus.Text="正在下载更新… "+percent+"%";}}),cancellation.Token);
   cancellation.Token.ThrowIfCancellationRequested();if(quitting)return;
   updateStatus.Text="校验通过，正在准备重新启动…";
   if(!demo&&!stopped)tracker.Feed(Sample());recorder.Flush();
   string task=UpdateInstaller.Prepare(file,info,typeof(Desktop).Assembly.Location,folder);
   UpdateInstaller.Start(task);quitting=true;Close();
  }catch(OperationCanceledException){if(!quitting)updateStatus.Text="已取消下载，原程序未修改。";}
  catch(UpdateCheckException ex){if(!quitting)updateStatus.Text=ex.Message;}
  catch(Exception ex){if(!quitting)updateStatus.Text="更新未完成："+ex.Message;}
  finally{installing=false;downloadRequest=null;cancellation.Dispose();if(!quitting){checkUpdateButton.IsEnabled=installUpdateButton.IsEnabled=true;cancelUpdateButton.Visibility=downloadProgress.Visibility=Visibility.Collapsed;}}
 }
 static FrameworkElement Group(params UIElement[] rows){var p=new StackPanel();foreach(var row in rows)p.Children.Add(row);return Card(p);}
 static FrameworkElement Row(string name,string detail,FrameworkElement field){var g=new Grid{Margin=new Thickness(0,7,0,9)};g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.05,GridUnitType.Star)});g.ColumnDefinitions.Add(new ColumnDefinition());var label=new StackPanel{Margin=new Thickness(0,0,18,0),VerticalAlignment=VerticalAlignment.Center};label.Children.Add(Text(name,20));var hint=Text(detail,15);hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");label.Children.Add(hint);g.Children.Add(label);field.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(field,1);g.Children.Add(field);return g;}
 void Navigate(int index){selectedPage=index;if(pages.Count==5)content.Content=pages[index];title.Text=new[]{day==DateTime.Today?"⌂  今日概览":"⌂  当日概览","▤  活动记录","◇  AI 助手","☷  设置","ⓘ  关于"}[index];dateTools.Visibility=index>=3?Visibility.Collapsed:Visibility.Visible;for(int i=0;i<navigation.Count;i++)navigation[i].SetResourceReference(System.Windows.Controls.Button.BackgroundProperty,i==index?"Selection":"Canvas");}
 Popup calendarPopup;
 void ShowCalendar(){if(calendarPopup==null){var calendarBody=new StackPanel();DateTime month=new DateTime(day.Year,day.Month,1);Action render=null;render=delegate{calendarBody.Children.Clear();calendarBody.Children.Add(Bar(Button("‹",delegate{month=month.AddMonths(-1);render();}),Text(month.ToString("yyyy 年 M 月"),18,true),Button("›",delegate{month=month.AddMonths(1);render();})));var grid=new UniformGrid{Columns=7};foreach(string label in new[]{"一","二","三","四","五","六","日"})grid.Children.Add(Text(label,15));int gap=((int)month.DayOfWeek+6)%7;for(int i=0;i<gap;i++)grid.Children.Add(new Border());for(int i=1;i<=DateTime.DaysInMonth(month.Year,month.Month);i++){DateTime choice=month.AddDays(i-1);var button=Button(i.ToString(),delegate{calendarPopup.IsOpen=false;SetDay(choice);});button.Margin=new Thickness(1);button.Padding=new Thickness(4);if(choice==day)button.SetResourceReference(Control.BackgroundProperty,"Selection");grid.Children.Add(button);}calendarBody.Children.Add(grid);calendarBody.Children.Add(Button("今天",delegate{calendarPopup.IsOpen=false;SetDay(DateTime.Today);}));};render();calendarPopup=new Popup{Child=Card(calendarBody),PlacementTarget=date,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};}calendarPopup.IsOpen=true;}
 void CommitDate(){DateTime next;if(DateTime.TryParseExact(date.Text.Trim(),"yyyy-MM-dd",null,System.Globalization.DateTimeStyles.None,out next))SetDay(next);else{date.Text=day.ToString("yyyy-MM-dd");notice.Text="日期格式为 yyyy-MM-dd";}}
 void SetDay(DateTime next){if(busy){date.Text=day.ToString("yyyy-MM-dd");notice.Text="请先完成或取消 AI 请求";return;}ApplyDay(next.Date);}
 void UpdateDateNavigation(DateTime today){nextDay.IsEnabled=day<today.Date;}
 void ApplyDay(DateTime next){
  if(day!=next.Date){timelineHour=null;timelineMinute=null;}day=next.Date;
  date.Text=day.ToString("yyyy-MM-dd");UpdateDateNavigation(observedToday);Refresh();LoadReport();Navigate(selectedPage);
 }
 void CheckDayRollover(DateTime today){
  today=today.Date;
  if(observedToday!=today){bool follow=day==observedToday;observedToday=today;if(follow)ApplyDay(today);}
  UpdateDateNavigation(today);
 }
 void Refresh(){try{ApplyKnownCategories(day);var rows=recorder.Day(day);var selected=new HashSet<string>(activities.SelectedItems.Cast<ActivityRow>().Select(x=>x.Source.Id));activities.ItemsSource=rows.OrderByDescending(x=>x.Start).Select(x=>new ActivityRow{Source=x,Mode=classificationRules.IsWholeSoftware(x.App)?"整个软件":"窗口标题"}).ToList();foreach(ActivityRow row in activities.Items)if(selected.Contains(row.Source.Id))activities.SelectedItems.Add(row);overview.Children.Clear();var cards=new UniformGrid{Columns=3};cards.Children.Add(Card(Text("有效使用\n"+Duration(rows.Where(x=>x.App!="离开").Sum(x=>x.Seconds)),23,true)));cards.Children.Add(Card(Text("离开时间\n"+Duration(rows.Where(x=>x.App=="离开").Sum(x=>x.Seconds)),23,true)));cards.Children.Add(Card(Text("活动记录\n"+rows.Count+" 段",23,true)));for(int i=0;i<cards.Children.Count;i++)((FrameworkElement)cards.Children[i]).Margin=new Thickness(i==0?0:8,0,i==cards.Children.Count-1?0:8,16);overview.Children.Add(cards);overview.Children.Add(BuildTimeline(rows));foreach(var target in new[]{overview}){target.Children.Add(Text("应用使用排行",22,true));var stack=new StackPanel();var groups=rows.Where(x=>x.App!="离开").GroupBy(x=>x.App).OrderByDescending(x=>x.Sum(r=>r.Seconds)).ToList();double max=groups.Select(x=>x.Sum(r=>r.Seconds)).DefaultIfEmpty(1).Max();foreach(var g in groups){stack.Children.Add(Text(g.Key+"   "+Duration(g.Sum(x=>x.Seconds))));var track=new Grid{Height=6,Margin=new Thickness(0,0,0,15)};track.SetResourceReference(Grid.BackgroundProperty,"Line");var fill=new Border{HorizontalAlignment=HorizontalAlignment.Left};fill.SetResourceReference(Border.BackgroundProperty,"Accent");track.Children.Add(fill);double fraction=g.Sum(x=>x.Seconds)/Math.Max(1,max);track.SizeChanged+=delegate{fill.Width=track.ActualWidth*fraction;};stack.Children.Add(track);}if(groups.Count==0)stack.Children.Add(Text("暂无活动记录"));target.Children.Add(Card(stack));var cats=new StackPanel();var categoryGroups=rows.Where(x=>x.App!="离开").GroupBy(x=>x.Category).ToList();double sum=categoryGroups.Sum(x=>x.Sum(r=>r.Seconds));var donut=new Grid{Width=150,Height=150,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(8,10,8,16)};double angle=-90;foreach(var group in categoryGroups){double sweep=group.Sum(x=>x.Seconds)/Math.Max(1,sum)*359.999;double end=angle+sweep;var startPoint=new Point(75+60*Math.Cos(angle*Math.PI/180),75+60*Math.Sin(angle*Math.PI/180));var endPoint=new Point(75+60*Math.Cos(end*Math.PI/180),75+60*Math.Sin(end*Math.PI/180));var figure=new PathFigure{StartPoint=startPoint,IsClosed=false};figure.Segments.Add(new ArcSegment(endPoint,new Size(60,60),0,sweep>180,SweepDirection.Clockwise,true));var geometry=new PathGeometry();geometry.Figures.Add(figure);donut.Children.Add(new System.Windows.Shapes.Path{Data=geometry,Stroke=CategoryBrush(group.Key),StrokeThickness=18,ToolTip=group.Key});angle=end;}cats.Children.Add(donut);foreach(var g in categoryGroups)cats.Children.Add(CategorySummaryRow(g.Key,g.Sum(x=>x.Seconds)));target.Children.Add(Group(Text("活动分类",22,true),cats));}}catch(Exception ex){Error(ex);}}
 FrameworkElement BuildTimeline(List<Activity> rows){
  var timeline=new ActivityTimeline(day,rows){SelectedHour=timelineHour,SelectedMinute=timelineMinute};
  timeline.ZoomChanged+=delegate(int? hour){timelineHour=hour;timelineMinute=timeline.SelectedMinute;};
  var details=Text("将鼠标移到时间轴上查看具体时间与任务",16);
  details.Margin=new Thickness(10);details.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
  // Reserve a stable area below the axis instead of opening a floating popup near the pointer.
  var detailArea=new Border{MinHeight=120,MaxHeight=160,Margin=new Thickness(0,4,0,0),CornerRadius=new CornerRadius(5),Child=Scroll(details)};
  detailArea.SetResourceReference(Border.BackgroundProperty,"Field");
  timeline.HoverChanged+=delegate(string value){details.Text=value??"将鼠标移到时间轴上查看具体时间与任务";};
  return Group(Text("活动时间轴",22,true),timeline,BuildCategoryLegend(),detailArea);
 }
 static Brush CategoryBrush(string category){return ActivityColors.Brush(category);}
 static FrameworkElement CategorySummaryRow(string category,double seconds){
  var row=new DockPanel{Margin=new Thickness(0,2,0,2)};
  var marker=new Border{Background=CategoryBrush(category),Width=12,Height=12,CornerRadius=new CornerRadius(3),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,9,0)};
  DockPanel.SetDock(marker,Dock.Left);row.Children.Add(marker);
  row.Children.Add(Text(category+"   "+Duration(seconds)));return row;
 }
 static WrapPanel BuildCategoryLegend(){
  var legend=new WrapPanel();
  foreach(string label in ActivityColors.Categories){
   var item=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,20,6)};
   item.Children.Add(new Border{Background=CategoryBrush(label),Width=12,Height=12,CornerRadius=new CornerRadius(3),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,7,0)});
   item.Children.Add(Text(label,15));legend.Children.Add(item);
  }
  return legend;
 }
 string LookupCategory(string app,string caption){string known;return categoryCache.TryGetValue(classificationRules.Key(app,caption),out known)&&BatchAI.ValidCategory(known)?known:null;}
 void ApplyKnownCategories(DateTime dateValue){
  bool learned=false;
  // Seed missing cache entries from labels saved by earlier versions.
  foreach(var group in recorder.Day(dateValue).Where(x=>x.App!="离开"&&BatchAI.ValidCategory(x.Category)).GroupBy(x=>classificationRules.Key(x.App,x.Title))){
   if(categoryCache.ContainsKey(group.Key))continue;
   var labels=group.Select(x=>x.Category).Distinct().ToList();
   if(labels.Count==1){categoryCache[group.Key]=labels[0];learned=true;}
  }
  if(learned)SaveCategoryCache();
  bool changed=false;
  foreach(var row in recorder.Day(dateValue)){
   if(row.App=="离开"||BatchAI.ValidCategory(row.Category))continue;
   string known=LookupCategory(row.App,row.Title);if(known!=null){row.Category=known;changed=true;}
  }
  if(changed)recorder.Mark(dateValue);
 }
 FrameworkElement KeyEditor(){
  var root=new StackPanel();var field=new Grid();field.Children.Add(key);
  savedKeyHint.Margin=new Thickness(11,0,11,0);savedKeyHint.IsHitTestVisible=false;savedKeyHint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
  field.Children.Add(savedKeyHint);root.Children.Add(field);keyStatus.TextWrapping=TextWrapping.Wrap;root.Children.Add(keyStatus);
  baseUrl.TextChanged+=delegate{UpdateKeyStatus();};key.PasswordChanged+=delegate{UpdateKeyStatus();};return root;
 }
 void UpdateKeyStatus(){
  bool pending=key.Password.Length>0;bool saved=false,unavailable=false;
  try{Api.Endpoint(baseUrl.Text);saved=!string.IsNullOrEmpty(readCredential(baseUrl.Text));}
  catch(Exception){unavailable=true;}
  keyStatus.Text=pending?"新密钥待保存":unavailable?"请填写有效地址，或检查 Windows 凭据访问状态":saved?"此接口已保存 API Key":"此接口尚未保存 API Key";
  savedKeyHint.Text=saved?"已保存密钥 · 输入可替换":"请输入 API Key";
  savedKeyHint.Visibility=pending?Visibility.Collapsed:Visibility.Visible;
  keyStatus.Foreground=new SolidColorBrush(pending?Color.FromRgb(218,155,48):saved?Color.FromRgb(27,173,132):Color.FromRgb(146,150,162));
 }
 string CategoryCachePath {get{return Path.Combine(store.Root,"classification-cache.json");}}
 void LoadCategoryCache(){
  if(!File.Exists(CategoryCachePath))return;
  try{var saved=Json.Decode<Dictionary<string,string>>(File.ReadAllText(CategoryCachePath));if(saved!=null)foreach(var pair in saved)if(BatchAI.ValidCategory(pair.Value))categoryCache[pair.Key]=pair.Value;}
  catch(Exception){notice.Text="分类缓存无法读取，将根据活动重新分类";}
 }
 void SaveCategoryCache(){if(!demo)Storage.Atomic(CategoryCachePath,Json.Encode(categoryCache));}
 string RulesPath {get{return Path.Combine(store.Root,"classification-rules.json");}}
 List<ActivityRow> SelectedActivities(){return activities.SelectedItems.Cast<ActivityRow>().Where(x=>x.App!="离开").ToList();}
 void ApplyAIScope(){
  try{
   if(busy){notice.Text="请先完成或取消 AI 处理";return;}
   var selected=SelectedActivities();if(selected.Count==0){notice.Text="请先选择活动记录";return;}
   bool whole=categoryScope.SelectedIndex==1;
   foreach(var row in selected)classificationRules.WholeSoftware[ClassificationRules.App(row.App)]=whole;
   if(!demo)classificationRules.Save(RulesPath);
   Refresh();notice.Text=whole?"所选软件的 AI 分类将合并整个软件，不再发送每个标题":"所选软件的 AI 分类将逐个窗口标题处理";
  }catch(Exception ex){Error(ex);}
 }
 IEnumerable<DateTime> HistoryDays(){
  var days=new HashSet<DateTime>(recorder.Cache.Keys);days.Add(day);
  foreach(string path in Directory.GetFiles(store.Root,"*.json")){
   DateTime parsed;if(DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path),"yyyy-MM-dd",null,System.Globalization.DateTimeStyles.None,out parsed))days.Add(parsed.Date);
  }
  return days;
 }
 void ApplyManualCategory(){
  try{
   if(busy){notice.Text="请先完成或取消 AI 处理";return;}
   var selected=SelectedActivities();if(selected.Count==0){notice.Text="请先选择活动记录";return;}
   bool whole=categoryScope.SelectedIndex==1;string value=category.SelectedItem.ToString();
   var apps=new HashSet<string>(selected.Select(x=>ClassificationRules.App(x.App)));
   var titles=new HashSet<string>(selected.Select(x=>BatchAI.Key(x.App,x.Title)));
   foreach(string app in apps)classificationRules.WholeSoftware[app]=whole;
   int changed=0;
   foreach(DateTime dateValue in HistoryDays()){
    bool touched=false;
    foreach(var row in recorder.Day(dateValue)){
     if(row.App=="离开")continue;
     bool match=whole?apps.Contains(ClassificationRules.App(row.App)):titles.Contains(BatchAI.Key(row.App,row.Title));
     if(!match)continue;
     row.Category=value;changed++;touched=true;
     string titleKey=BatchAI.Key(row.App,row.Title);
     if(BatchAI.ValidCategory(value))categoryCache[titleKey]=value;else categoryCache.Remove(titleKey);
    }
    if(touched)recorder.Mark(dateValue);
   }
   foreach(var row in selected){string key=classificationRules.Key(row.App,row.Title);if(BatchAI.ValidCategory(value))categoryCache[key]=value;else categoryCache.Remove(key);}
   if(!demo)classificationRules.Save(RulesPath);
   recorder.Flush();SaveCategoryCache();Refresh();
   notice.Text="已按"+(whole?"整个软件":"窗口标题")+"更新 "+changed+" 条历史记录，新记录继续沿用该范围";
  }catch(Exception ex){Error(ex);}
 }
 void LoadSettings(){theme.SelectedIndex=settings.Theme=="light"?1:settings.Theme=="dark"?2:0;retention.Text=settings.RetentionDays.ToString();excluded.Text=settings.Excluded;continuous.Text=settings.Continuous;baseUrl.Text=settings.BaseUrl;model.Text=settings.Model;key.Clear();startup.IsChecked=!demo&&Startup.Enabled;UpdateKeyStatus();}
 void SaveSettings(){try{if(busy){notice.Text="请先完成或取消 AI 请求";return;}int days;if(!int.TryParse(retention.Text,out days)||days<0||days>36500)throw new Exception("保留天数应为 0 到 36500 的整数");Api.Endpoint(baseUrl.Text);if(string.IsNullOrWhiteSpace(model.Text))throw new Exception("请填写模型名称");if(days>0&&(settings.RetentionDays==0||days<settings.RetentionDays)&&!Confirm("缩短保留期限会删除过期记录和对话，继续？"))return;var next=new Settings{Theme=new[]{"system","light","dark"}[theme.SelectedIndex],RetentionDays=days,Excluded=excluded.Text,Continuous=continuous.Text,BaseUrl=baseUrl.Text.Trim().TrimEnd('/'),Model=model.Text.Trim()};if(!demo){if(!stopped)tracker.Feed(Sample());recorder.Flush();if(key.Password.Length>0)Credentials.Write(next.BaseUrl,key.Password);if(Startup.Enabled!=startup.IsChecked.Value)Startup.Set(startup.IsChecked.Value);store.SaveSettings(next);}settings=next;tracker.Settings=next;tracker.Reset();key.Clear();UpdateKeyStatus();ApplyTheme(next.Theme);LoadReport();if(!demo){store.Cleanup(days,DateTime.Today);recorder.Cache.Clear();}Refresh();notice.Text="设置已保存";}catch(Exception ex){Error(ex);}}
 string ReportPath(DateTime dateValue){return Path.Combine(store.Root,dateValue.ToString("yyyy-MM-dd")+".report.json");}
 void LoadReport(){
  string report="";string path=ReportPath(day);
  if(File.Exists(path))report=Json.Decode<DailyAnalysis>(File.ReadAllText(path)).Report;
  else{var legacy=store.ReadChat(day).Messages.LastOrDefault(x=>x.role=="assistant");if(legacy!=null)report=legacy.content;}
  var document=new FlowDocument{FontFamily=FontFamily,FontSize=18,PagePadding=new Thickness(8)};
  document.SetResourceReference(FlowDocument.ForegroundProperty,"Ink");
  if(string.IsNullOrWhiteSpace(report))report="尚未生成日报。\n点击“生成日报”后，会自动分批分析当天记录并汇总。";
  foreach(var line in report.Split('\n')){
   var paragraph=new Paragraph{Margin=new Thickness(0,0,0,8)};
   string clean=System.Text.RegularExpressions.Regex.Replace(line,@"^#{1,6}\s+","");
   if(line.StartsWith("#"))paragraph.FontSize=24;
   foreach(string part in System.Text.RegularExpressions.Regex.Split(clean,@"(\*\*[^*]+\*\*)"))
    if(part.StartsWith("**")&&part.EndsWith("**")&&part.Length>4)paragraph.Inlines.Add(new Bold(new Run(part.Substring(2,part.Length-4))));
    else paragraph.Inlines.Add(new Run(part));
   document.Blocks.Add(paragraph);
  }
  reportView.Document=document;reportView.ScrollToHome();
 }
 async Task RunAI(bool generateReport){
  if(busy||installing)return;
  DateTime target=day;
  try{
   if(demo){aiProgress.Text="演示模式，不调用真实接口";return;}
   string secret=Credentials.Read(settings.BaseUrl);
   if(string.IsNullOrEmpty(secret)){Navigate(3);notice.Text="请填写并保存 API Key";return;}
   recorder.Flush();
   var snapshot=recorder.Day(target).Select(x=>x.Copy()).ToList();
   if(snapshot.Count==0){aiProgress.Text="所选日期没有活动记录";return;}
   busy=true;request=new CancellationTokenSource();dateTools.IsEnabled=false;
   classifyButton.IsEnabled=reportButton.IsEnabled=false;cancelButton.IsEnabled=true;aiBusy.Visibility=Visibility.Visible;
   Navigate(2);notice.Text="";
   var cacheSnapshot=new Dictionary<string,string>(categoryCache);
   var rulesSnapshot=classificationRules.Copy();
   var engine=new BatchAI((messages,token)=>Api.Send(settings,messages,secret,token),message=>aiProgress.Text=message,cacheSnapshot,rulesSnapshot);
   var result=await engine.Run(snapshot,generateReport,request.Token);
   request.Token.ThrowIfCancellationRequested();if(quitting)return;
   // Commit only after every batch and the final summary succeeded.
   if(generateReport)Storage.Atomic(ReportPath(target),Json.Encode(new DailyAnalysis{Report=result.Report,GeneratedAt=DateTime.UtcNow,Records=snapshot.Count}));
   var original=snapshot.ToDictionary(x=>x.Id,x=>x.Category);int applied=0;foreach(var row in recorder.Day(target)){string value;if(result.Categories.TryGetValue(row.Id,out value)&&row.App!="离开"&&original.ContainsKey(row.Id)&&row.Category==original[row.Id]){row.Category=value;applied++;}}
   recorder.Mark(target);recorder.Flush();
   foreach(var pair in result.CachedCategories){
    string before=null,now=null;cacheSnapshot.TryGetValue(pair.Key,out before);categoryCache.TryGetValue(pair.Key,out now);
    if(before==now)categoryCache[pair.Key]=pair.Value;
   }
   ApplyKnownCategories(target);recorder.Flush();SaveCategoryCache();
   aiProgress.Text=generateReport?"日报已生成 · "+snapshot.Count+" 条记录合并为 "+result.Groups+" 个活动 · 调用 "+result.Requests+" 次":"分类完成 · 已更新 "+applied+" 条记录 · 调用 "+result.Requests+" 次";
   LoadReport();Refresh();
  }catch(OperationCanceledException){if(!quitting)aiProgress.Text=request!=null&&request.IsCancellationRequested?"已取消生成，原有结果已保留":"请求超时，请重试";}
  catch(Exception ex){if(!quitting)aiProgress.Text="生成未完成："+ex.Message+"。原有结果已保留。";}
  finally{
   busy=false;if(request!=null){request.Dispose();request=null;}
   if(!quitting){dateTools.IsEnabled=true;classifyButton.IsEnabled=reportButton.IsEnabled=true;cancelButton.IsEnabled=false;aiBusy.Visibility=Visibility.Collapsed;}
  }
 }
 void Export(){try{var d=new SaveFileDialog{Filter="CSV 文件|*.csv",FileName="拾时-"+day.ToString("yyyy-MM-dd")+".csv"};if(d.ShowDialog(this)!=true)return;var b=new StringBuilder("开始,结束,秒数,软件,窗口标题,分类\r\n");foreach(var r in recorder.Day(day))b.AppendLine(string.Join(",",new[]{Csv(r.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),Csv(r.End.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),Math.Round(r.Seconds).ToString(),Csv(r.App),Csv(r.Title),Csv(r.Category)}));File.WriteAllText(d.FileName,b.ToString(),new UTF8Encoding(true));notice.Text="已导出";}catch(Exception ex){Error(ex);}}
 static string Csv(string s){if(!string.IsNullOrEmpty(s)&&"=+-@\t\r\n".IndexOf(s[0])>=0)s="'"+s;return "\""+(s??"").Replace("\"","\"\"")+"\"";}
 void Delete(bool all){if(busy)return;if(!Confirm(all?"删除全部活动及对话？":"删除当天活动及对话？"))return;recorder.Clear(all?(DateTime?)null:day);if(all){categoryCache.Clear();if(File.Exists(CategoryCachePath))File.Delete(CategoryCachePath);}else if(File.Exists(ReportPath(day)))File.Delete(ReportPath(day));tracker.Reset();Refresh();LoadReport();}
 bool Confirm(string text){return MessageBox.Show(this,text,"拾时",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;}
 void Error(Exception ex){notice.Text=ex.Message;}
 Sample Sample(){var s=Native.Read();s.Locked|=locked;return s;}
 void Session(object sender,SessionSwitchEventArgs e){if(quitting)return;Dispatcher.BeginInvoke((Action)delegate{try{if(stopped)return;tracker.Feed(Sample());if(e.Reason==SessionSwitchReason.SessionLock)locked=true;if(e.Reason==SessionSwitchReason.SessionUnlock)locked=false;tracker.Feed(Sample());recorder.Flush();}catch(Exception ex){Error(ex);}});}
 void Power(object sender,PowerModeChangedEventArgs e){if(quitting)return;Dispatcher.BeginInvoke((Action)delegate{try{if(stopped)return;tracker.Feed(Sample());if(e.Mode==PowerModes.Suspend){var s=Sample();s.Locked=true;tracker.Feed(s);}recorder.Flush();}catch(Exception ex){Error(ex);}});}
 void TogglePause(){if(stopped)return;if(!demo){tracker.Feed(Sample());recorder.Flush();}tracker.Paused=!tracker.Paused;tracker.Reset();pause.Content=tracker.Paused?"继续记录":"暂停记录";UpdateRecordingStatus();if(tracker.Paused)tray.Icon=System.Drawing.SystemIcons.Warning;else using(var iconStream=typeof(Desktop).Assembly.GetManifestResourceStream("App.ico"))tray.Icon=new System.Drawing.Icon(iconStream);tray.Text="拾时 · "+status.Text;}
 void UpdateRecordingStatus(){
  status.Text=stopped?"记录已停止":demo?"演示数据":tracker!=null&&tracker.Paused?"已暂停":"正在记录";
  status.Foreground=new SolidColorBrush(stopped?Color.FromRgb(226,90,90):tracker!=null&&tracker.Paused?Color.FromRgb(218,155,48):Color.FromRgb(27,173,132));
 }
 internal bool ActivateExistingWindow(){if(quitting||exitPending||!IsLoaded)return false;Restore();if(IsVisible&&activationCheckLog!=null)File.AppendAllText(activationCheckLog,";activated");return IsVisible;}
 async void Restore(){trayMenu.IsOpen=false;Show();WindowState=WindowState.Normal;Activate();try{await CheckAutomaticUpdates(DateTime.Today);}catch(Exception ex){Error(ex);}}
 void Exit(){exitPending=true;if(busy&&!Confirm("取消当前 AI 操作并退出？")){exitPending=false;return;}if(exitCheckDelay>0)Thread.Sleep(exitCheckDelay);if(request!=null)request.Cancel();if(downloadRequest!=null)downloadRequest.Cancel();quitting=true;Close();}
 bool exitPending;
 internal int exitCheckDelay;
 string activationCheckLog;
 public async Task VerifyActivationLifecycle(string log){
  activationCheckLog=log;await Dispatcher.Yield(DispatcherPriority.Background);Hide();tray.Visible=true;File.WriteAllText(log,"ready");
  var end=new DispatcherTimer{Interval=TimeSpan.FromSeconds(8)};end.Tick+=delegate{end.Stop();Exit();};end.Start();
 }
 public async Task VerifyTrayExit(string log,bool hidden,bool nativeTray=false){
  await Dispatcher.Yield(DispatcherPriority.Background);
  File.WriteAllText(log,"opened");if(hidden)Hide();
  if(nativeTray){tray.Visible=true;NativeAppearance.TestTrayRightClick(tray);await Task.Delay(150);}else ShowTrayMenu();
  await Dispatcher.Yield(DispatcherPriority.Background);
  if(!trayMenu.IsOpen)throw new Exception("Tray menu did not open");
  ((MenuItem)trayMenu.Items[trayMenu.Items.Count-1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
  File.AppendAllText(log,"; clicked exit");
 }
 public async Task Verify(string folder){
  try{
   Directory.CreateDirectory(folder);var checks=new List<string>();
   Action<string,bool> check=delegate(string name,bool pass){if(!pass)throw new Exception("FAIL: "+name);checks.Add("PASS: "+name);};
   SingleInstanceChecks.Run(check);
   for(int repeat=0;repeat<6;repeat++){for(int i=0;i<5;i++)Navigate(i);ApplyTheme(repeat%2==0?"dark":"light");theme.IsDropDownOpen=true;await Dispatcher.Yield(DispatcherPriority.Background);theme.IsDropDownOpen=false;}
   string originalAppearance=settings.Theme;
   foreach(string appearance in new[]{"light","dark"}){
    settings.Theme=appearance;ApplyTheme(appearance);await Dispatcher.Yield(DispatcherPriority.Background);
    NativeAppearance.Capture(this,Path.Combine(folder,"window-frame-"+appearance+".png"));
    check(appearance+" native caption color accepted by Windows",NativeAppearance.LastCaptionResult==0);
    check(appearance+" native frame and caption text colors accepted by Windows",NativeAppearance.LastBorderResult==0&&NativeAppearance.LastTextResult==0);
    ShowTrayMenu();await Dispatcher.Yield(DispatcherPriority.Background);trayMenu.UpdateLayout();
    var menuImage=new RenderTargetBitmap((int)Math.Ceiling(trayMenu.ActualWidth),(int)Math.Ceiling(trayMenu.ActualHeight),96,96,PixelFormats.Pbgra32);menuImage.Render(trayMenu);var menuEncoder=new PngBitmapEncoder();menuEncoder.Frames.Add(BitmapFrame.Create(menuImage));using(var file=File.Create(Path.Combine(folder,"tray-menu-"+appearance+".png")))menuEncoder.Save(file);
    check(appearance+" tray menu uses current theme background",trayMenu.Background.ToString()==((Brush)FindResource("Surface")).ToString());
    check(appearance+" tray menu opens with compact readable rows",trayMenu.IsOpen&&trayMenu.ActualWidth>=200&&trayPauseItem.ActualHeight>=38&&trayPauseItem.FontSize>=16);
    trayMenu.IsOpen=false;
   }
   settings.Theme=originalAppearance;
   for(int repeat=0;repeat<12;repeat++){ShowTrayMenu();await Dispatcher.Yield(DispatcherPriority.Background);trayMenu.IsOpen=false;}
   ShowTrayMenu();trayPauseItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Dispatcher.Yield(DispatcherPriority.Background);ShowTrayMenu();
   check("tray pause action closes menu and next open shows continue",tracker.Paused&&trayPauseItem.Header.ToString()=="继续记录");
   trayPauseItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Dispatcher.Yield(DispatcherPriority.Background);check("tray continue action restores recording",!tracker.Paused&&!trayMenu.IsOpen);
   Navigate(4);
   check("navigation and dropdown lifecycle",content.Content==pages[4]);
   check("statistics page removed and about is the fifth page",pages.Count==5&&navigation.Count==5&&!navigation.Any(x=>x.Content.ToString().Contains("使用统计")));
   check("about hides date controls and follows settings",navigation[4].Content.ToString().Contains("关于")&&dateTools.Visibility==Visibility.Collapsed&&title.Text.Contains("关于"));
   check("about shows current version",Descendants<TextBlock>(pages[4]).Any(x=>x.Text=="版本 "+AppVersion.Current));
   var versionResult=await UpdateChecker.ReadPages((page,token)=>Task.FromResult("[{\"name\":\"v1.9.0\"},{\"name\":\"v1.10.0\"},{\"name\":\"v2.0.0-beta.1\"},{\"name\":\"notes\"}]"),CancellationToken.None);
   check("update check compares numeric stable versions",versionResult.Latest==new Version(1,10,0)&&versionResult.HasUpdate&&versionResult.PageUrl==UpdateChecker.Repository+"/tree/v1.10.0");
   int pagesRead=0;var pagedResult=await UpdateChecker.ReadPages((page,token)=>{pagesRead++;return Task.FromResult(page==1?Json.Encode(Enumerable.Range(0,100).Select(x=>new{name="v1.0.0"}).ToArray()):"[{\"name\":\"v3.0.0\"}]");},CancellationToken.None);
   check("update check reads subsequent tag pages",pagesRead==2&&pagedResult.Latest==new Version(3,0,0));
   var emptyResult=await UpdateChecker.ReadPages((page,token)=>Task.FromResult("[]"),CancellationToken.None);
   check("empty version list does not report latest incorrectly",emptyResult.Latest==null&&!emptyResult.HasUpdate);
   using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool stoppedCheck=false;try{await UpdateChecker.ReadPages((page,token)=>{throw new Exception("Should not fetch");},cancelled.Token);}catch(OperationCanceledException){stoppedCheck=true;}check("cancelled update check avoids another request",stoppedCheck);}
   var pending=new TaskCompletionSource<UpdateInfo>();fetchUpdate=token=>pending.Task;var checking=CheckForUpdates();
   check("checking update disables duplicate clicks",!checkUpdateButton.IsEnabled&&updateStatus.Text.Contains("正在检查"));
   pending.SetResult(new UpdateInfo{Latest=new Version(1,10,0),PageUrl=UpdateChecker.Repository+"/tree/v1.10.0"});await checking;
   check("new version exposes update action",checkUpdateButton.IsEnabled&&viewUpdateButton.Visibility==Visibility.Visible&&updateStatus.Text.Contains("1.10.0"));
   fetchUpdate=token=>Task.FromResult(new UpdateInfo{Latest=Version.Parse(AppVersion.Current)});await CheckForUpdates();
   check("current version reports no update",viewUpdateButton.Visibility==Visibility.Collapsed&&updateStatus.Text.Contains("最新版本"));
   fetchUpdate=token=>{throw new System.Net.Http.HttpRequestException("Test offline");};await CheckForUpdates();
   check("failed update check permits retry",checkUpdateButton.IsEnabled&&updateStatus.Text.Contains("失败")&&viewUpdateButton.Visibility==Visibility.Collapsed);
   fetchUpdate=token=>{throw new UpdateCheckException("GitHub 请求次数已达上限，请稍后重试，或通过版本记录查看。");};await CheckForUpdates();
   check("GitHub rate limit has a clear recoverable message",checkUpdateButton.IsEnabled&&updateStatus.Text.Contains("上限"));
   fetchUpdate=token=>Task.FromResult(new UpdateInfo{Latest=Version.Parse(AppVersion.Current)});await CheckForUpdates();fetchUpdate=UpdateChecker.Check;
   int automaticCalls=0,updateNotices=0;fetchUpdate=token=>{automaticCalls++;return Task.FromResult(new UpdateInfo{Latest=new Version(9,0,0)});};notifyUpdate=info=>updateNotices++;
   updatePreferences.Enabled=false;await CheckAutomaticUpdates(DateTime.Today,true);check("automatic update check stays off by default",automaticCalls==0);
   updatePreferences.Enabled=true;updatePreferences.LastAttempt="";await CheckAutomaticUpdates(DateTime.Today,true);await CheckAutomaticUpdates(DateTime.Today,true);
   check("automatic update check runs once per day and announces new version",automaticCalls==1&&updateNotices==1);
   await CheckAutomaticUpdates(DateTime.Today.AddDays(1),true);check("automatic update check runs again on next day",automaticCalls==2);
   fetchUpdate=token=>{automaticCalls++;throw new System.Net.Http.HttpRequestException("Test offline");};await CheckAutomaticUpdates(DateTime.Today.AddDays(2),true);await CheckAutomaticUpdates(DateTime.Today.AddDays(2),true);
   check("failed automatic check does not repeat throughout the day",automaticCalls==3);
   updatePreferences.Enabled=false;updatePreferences.LastAttempt="";notifyUpdate=NotifyUpdate;fetchUpdate=token=>Task.FromResult(new UpdateInfo{Latest=Version.Parse(AppVersion.Current)});await CheckForUpdates();fetchUpdate=UpdateChecker.Check;
   ApplyTheme("light");Navigate(4);await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"about-light.png"));
   ApplyTheme("dark");await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"about-dark.png"));Navigate(0);
   check("AI scope button has label marker and explanation",Descendants<Button>(pages[1]).Any(x=>Convert.ToString(x.Content)=="仅设置 AI 分类范围  ⓘ"&&Convert.ToString(x.ToolTip)=="只保存范围，具体分到哪类由下次 AI 分类决定。"));
   check("next day is disabled on today",!nextDay.IsEnabled);
   SetDay(DateTime.Today.AddDays(-1));check("next day is enabled on history",nextDay.IsEnabled);SetDay(DateTime.Today);
   var realToday=DateTime.Today;busy=true;CheckDayRollover(realToday.AddDays(1));busy=false;check("midnight follows current day",day==realToday.AddDays(1)&&!nextDay.IsEnabled);
   SetDay(realToday.AddDays(-2));CheckDayRollover(realToday.AddDays(2));check("midnight keeps historical selection",day==realToday.AddDays(-2));
   observedToday=realToday;SetDay(realToday);
   check("search box removed from header",!Descendants<TextBox>((DependencyObject)main.Children[0]).Any());
   string originalUrl=baseUrl.Text;readCredential=url=>url==originalUrl?"dummy-test-secret":"";
   key.Clear();UpdateKeyStatus();check("saved key visibly indicated without plaintext",keyStatus.Text.Contains("已保存")&&savedKeyHint.Visibility==Visibility.Visible&&key.Password.Length==0);
   key.Password="dummy-replacement";check("replacement key indicated as pending",keyStatus.Text=="新密钥待保存"&&savedKeyHint.Visibility==Visibility.Collapsed);key.Clear();
   baseUrl.Text="https://another-provider.invalid";check("key state isolated by API address",keyStatus.Text.Contains("尚未保存"));baseUrl.Text=originalUrl;
   var now=DateTime.Today.AddHours(12).ToUniversalTime();categoryCache[BatchAI.Key("known-app","known-title")]="学习";
   recorder.Add(now,now.AddSeconds(10),"known-app","known-title","未分类");
   check("newly captured activities reuse cached classification immediately",recorder.Day(day).Any(x=>x.App=="known-app"&&x.Category=="学习"));
   var backfill=new Activity{Start=now.AddSeconds(20),End=now.AddSeconds(25),App="known-app",Title="known-title"};recorder.Day(day).Add(backfill);Refresh();
   check("existing unlabeled activities inherit cached labels",backfill.Category=="学习");
   var legacy=new Activity{Start=now.AddMinutes(3),End=now.AddMinutes(4),App="legacy-app",Title="旧版分类",Category="工作"};recorder.Day(day).Add(legacy);var legacyNew=legacy.Copy();legacyNew.Id=Guid.NewGuid().ToString("N");legacyNew.Category="未分类";recorder.Day(day).Add(legacyNew);Refresh();check("classification migrates from old labeled history",legacyNew.Category=="工作");
   var endOfDay=DateTime.Today.AddDays(1).ToUniversalTime();
   var samples=new[]{new Activity{Start=DateTime.Today.AddHours(9).ToUniversalTime(),End=DateTime.Today.AddHours(10).ToUniversalTime(),App="Word",Title="测试方案",Category="工作"},new Activity{Start=endOfDay.AddMinutes(-1),End=endOfDay,App="浏览器",Title="午夜任务",Category="娱乐"}};
   var timelineTest=new ActivityTimeline(day,samples);timelineTest.Measure(new Size(800,100));timelineTest.Arrange(new Rect(0,0,800,100));
   check("timeline endpoints and midpoint use same coordinates as ticks",Math.Abs(timelineTest.Position(day.ToUniversalTime())-timelineTest.PlotLeft)<.01&&Math.Abs(timelineTest.Position(day.AddHours(12).ToUniversalTime())-(timelineTest.PlotLeft+timelineTest.PlotWidth/2))<.01&&Math.Abs(timelineTest.Position(endOfDay)-(timelineTest.PlotLeft+timelineTest.PlotWidth))<.01);
   check("timeline hover displays exact local time and task",timelineTest.Describe(timelineTest.Position(day.AddHours(9).AddMinutes(30).ToUniversalTime())).Contains("09:30:00")&&timelineTest.Describe(timelineTest.Position(day.AddHours(9).AddMinutes(30).ToUniversalTime())).Contains("测试方案"));
   check("timeline distinguishes gaps and midnight",timelineTest.Describe(timelineTest.Position(day.AddHours(4).ToUniversalTime())).Contains("没有活动记录")&&timelineTest.Describe(timelineTest.PlotLeft+timelineTest.PlotWidth).Contains("24:00:00"));
   check("all category legend colors are distinct",ActivityColors.Categories.Select(x=>ActivityColors.Brush(x).ToString()).Distinct().Count()==7);
   ApplyTheme("dark");Navigate(0);await Dispatcher.Yield(DispatcherPriority.Background);var liveTimeline=Descendants<ActivityTimeline>(pages[0]).First();
   liveTimeline.HoverAt(liveTimeline.Position(day.AddHours(9).AddMinutes(15).ToUniversalTime()),20);await Dispatcher.Yield(DispatcherPriority.Background);
   check("hover details are inline and contain task",liveTimeline.HoverDetails!=null&&liveTimeline.HoverDetails.Contains("项目方案")&&Descendants<TextBlock>(pages[0]).Any(x=>x.Text==liveTimeline.HoverDetails));
   check("timeline hover does not create a floating tooltip",liveTimeline.ToolTip==null);
   var outerScroll=(ScrollViewer)pages[0];var detailScroll=Descendants<ScrollViewer>(pages[0]).First(x=>x!=outerScroll&&x.Content is TextBlock);
   outerScroll.ScrollToTop();await Dispatcher.Yield(DispatcherPriority.Background);
   var wheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent};
   detailScroll.RaiseEvent(wheel);await Dispatcher.Yield(DispatcherPriority.Background);
   check("wheel over short timeline details scrolls the overview",wheel.Handled&&outerScroll.VerticalOffset>0);
   var detailText=(TextBlock)detailScroll.Content;string originalDetails=detailText.Text;detailText.Text=string.Join("\n",Enumerable.Repeat("很长的窗口标题详情",50));await Dispatcher.Yield(DispatcherPriority.Background);
   UpdateLayout();detailScroll.ScrollToBottom();outerScroll.ScrollToTop();await Dispatcher.Yield(DispatcherPriority.Background);UpdateLayout();
   var edgeWheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent};detailScroll.RaiseEvent(edgeWheel);await Dispatcher.Yield(DispatcherPriority.Background);
   check("wheel at details bottom continues scrolling the overview",detailScroll.ScrollableHeight>0&&edgeWheel.Handled&&outerScroll.VerticalOffset>0);
   detailScroll.ScrollToTop();await Dispatcher.Yield(DispatcherPriority.Background);
   var innerWheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=System.Windows.Input.Mouse.MouseWheelEvent};detailText.RaiseEvent(innerWheel);await Dispatcher.Yield(DispatcherPriority.Background);
   check("long timeline details retain their own wheel scrolling",detailScroll.VerticalOffset>0);
   detailText.Text=originalDetails;outerScroll.ScrollToTop();await Dispatcher.Yield(DispatcherPriority.Background);
   liveTimeline.HoverAt(0,0);check("hover details clear outside the plot",liveTimeline.HoverDetails==null);
   timelineTest.ToggleAt(timelineTest.Position(day.AddHours(9).AddMinutes(30).ToUniversalTime()),20);
   check("click expands the selected hour",timelineTest.SelectedHour==9&&timelineTest.ViewStart==day.AddHours(9).ToUniversalTime()&&timelineTest.ViewEnd==day.AddHours(10).ToUniversalTime());
   check("hour endpoints and midpoint remain aligned",Math.Abs(timelineTest.Position(day.AddHours(9).ToUniversalTime())-timelineTest.PlotLeft)<.01&&Math.Abs(timelineTest.Position(day.AddHours(9).AddMinutes(30).ToUniversalTime())-(timelineTest.PlotLeft+timelineTest.PlotWidth/2))<.01);
   check("zoomed hover finds the task at the expanded time",timelineTest.Describe(timelineTest.PlotLeft+timelineTest.PlotWidth/2).Contains("09:30:00")&&timelineTest.Describe(timelineTest.PlotLeft+timelineTest.PlotWidth/2).Contains("测试方案"));
   timelineTest.ToggleAt(timelineTest.PlotLeft+timelineTest.PlotWidth/2,20);
   check("second click expands two minutes",timelineTest.SelectedMinute==570&&(timelineTest.ViewEnd-timelineTest.ViewStart).TotalSeconds==120);
   check("two minute coordinates and hover stay aligned",Math.Abs(timelineTest.TimeAt(timelineTest.PlotLeft+timelineTest.PlotWidth/2).Subtract(day.AddHours(9).AddMinutes(31).ToUniversalTime()).TotalSeconds)<.01&&timelineTest.Describe(timelineTest.PlotLeft+timelineTest.PlotWidth/2).Contains("09:31:00"));
   timelineTest.ToggleAt(timelineTest.PlotLeft+5,20);check("third click returns to all-day view",timelineTest.SelectedHour==null&&timelineTest.ViewStart==day.ToUniversalTime());
   timelineTest.ToggleAt(timelineTest.PlotLeft+timelineTest.PlotWidth,20);check("day-end click expands last hour",timelineTest.SelectedHour==23&&timelineTest.ViewEnd==endOfDay);
   timelineTest.SelectedHour=null;timelineTest.ToggleAt(timelineTest.Position(day.AddHours(4).ToUniversalTime()),20);check("empty hour can be expanded",timelineTest.SelectedHour==4&&timelineTest.Describe(timelineTest.PlotLeft+20).Contains("没有活动记录"));
   liveTimeline.ToggleAt(liveTimeline.Position(day.AddHours(9).AddMinutes(15).ToUniversalTime()),20);liveTimeline.HoverAt(liveTimeline.PlotLeft+liveTimeline.PlotWidth/4,20);
   await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"hour-zoom.png"));
   Refresh();await Dispatcher.Yield(DispatcherPriority.Background);check("zoom survives periodic refresh",Descendants<ActivityTimeline>(pages[0]).First().SelectedHour==9);
   var zoomed=Descendants<ActivityTimeline>(pages[0]).First();zoomed.ToggleAt(zoomed.PlotLeft+10,20);
   Refresh();await Dispatcher.Yield(DispatcherPriority.Background);var minuteZoom=Descendants<ActivityTimeline>(pages[0]).First();
   check("two minute zoom survives periodic refresh",minuteZoom.SelectedMinute==540&&timelineMinute==540);
   minuteZoom.HoverAt(minuteZoom.PlotLeft+minuteZoom.PlotWidth/2,20);Snapshot(Path.Combine(folder,"two-minute-zoom.png"));
   minuteZoom.ToggleAt(minuteZoom.PlotLeft+10,20);check("zoom can be restored after refresh",timelineHour==null&&timelineMinute==null);
   timelineTest.SelectedHour=23;timelineTest.ToggleAt(timelineTest.PlotLeft+timelineTest.PlotWidth,20);
   check("last two minutes end exactly at midnight",timelineTest.SelectedMinute==1438&&timelineTest.ViewEnd==endOfDay);

   SetDay(day.AddDays(-1));SetDay(DateTime.Today);Refresh();
   var cards=(UniformGrid)overview.Children[0];UpdateLayout();
   var c0=(FrameworkElement)cards.Children[0];var c1=(FrameworkElement)cards.Children[1];
   check("overview cards have horizontal gap",c0.Margin.Right+c1.Margin.Left>=16);
   TogglePause();var pausedColor=((SolidColorBrush)status.Foreground).Color;TogglePause();
   check("recording and paused status colors differ",pausedColor!=((SolidColorBrush)status.Foreground).Color);
   check("AI page has no chat composer",!Descendants<TextBox>(pages[2]).Any());
   Navigate(3);ApplyTheme("dark");settingsScroll.ScrollToBottom();await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"key-dark.png"));settingsScroll.ScrollToTop();
   Width=MinWidth;Height=MinHeight;await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"compact.png"));Width=Math.Min(1320,SystemParameters.WorkArea.Width);Height=Math.Min(880,SystemParameters.WorkArea.Height);
   ApplyTheme("light");Navigate(0);await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"overview-light.png"));ApplyTheme("dark");await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"overview-dark.png"));((ScrollViewer)pages[0]).ScrollToBottom();await Dispatcher.Yield(DispatcherPriority.Background);UpdateLayout();Snapshot(Path.Combine(folder,"category-colors.png"));Navigate(2);await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"ai.png"));
   SetDay(DateTime.Today);var scopeStart=DateTime.Today.AddHours(14).ToUniversalTime();
   recorder.Add(scopeStart,scopeStart.AddSeconds(10),"scope-test","标题A","未分类");recorder.Add(scopeStart.AddSeconds(10),scopeStart.AddSeconds(20),"scope-test","标题B","未分类");
   recorder.Add(scopeStart.AddDays(-1),scopeStart.AddDays(-1).AddSeconds(10),"scope-test","标题B","未分类");
   Refresh();Navigate(1);activities.SelectedItems.Clear();activities.SelectedItem=activities.Items.Cast<ActivityRow>().First(x=>x.App=="scope-test"&&x.Title=="标题A");categoryScope.SelectedIndex=1;category.SelectedItem="娱乐";ApplyManualCategory();
   check("whole-software manual category updates all titles and history",recorder.Day(day).Where(x=>x.App=="scope-test").All(x=>x.Category=="娱乐")&&recorder.Day(day.AddDays(-1)).Single(x=>x.App=="scope-test").Category=="娱乐");
   recorder.Add(scopeStart.AddMinutes(1),scopeStart.AddMinutes(1).AddSeconds(10),"scope-test","标题C","未分类");check("whole-software manual cache applies to new titles",recorder.Day(day).Single(x=>x.App=="scope-test"&&x.Title=="标题C").Category=="娱乐");
   Refresh();activities.SelectedItems.Clear();activities.SelectedItem=activities.Items.Cast<ActivityRow>().First(x=>x.App=="scope-test"&&x.Title=="标题A");categoryScope.SelectedIndex=0;category.SelectedItem="学习";ApplyManualCategory();
   check("title-only manual category leaves other titles unchanged",recorder.Day(day).Single(x=>x.App=="scope-test"&&x.Title=="标题A").Category=="学习"&&recorder.Day(day).Single(x=>x.App=="scope-test"&&x.Title=="标题B").Category=="娱乐"&&!classificationRules.IsWholeSoftware("scope-test"));
   activities.SelectedItems.Clear();activities.SelectedItem=activities.Items.Cast<ActivityRow>().First(x=>x.App=="scope-test"&&x.Title=="标题A");categoryScope.SelectedIndex=1;ApplyAIScope();check("AI-only scope changes grouping without rewriting categories",classificationRules.IsWholeSoftware("scope-test")&&recorder.Day(day).Single(x=>x.App=="scope-test"&&x.Title=="标题A").Category=="学习");
   await Dispatcher.Yield(DispatcherPriority.Background);Snapshot(Path.Combine(folder,"classification-scope.png"));
   await UpdateChecks.Run(check,folder);await BatchChecks.Run(check);File.WriteAllLines(Path.Combine(folder,"checks.txt"),checks);
  }catch(Exception ex){File.WriteAllText(Path.Combine(folder,"checks.txt"),ex.ToString());Environment.ExitCode=1;}
  finally{quitting=true;Close();}
 }
 static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject{
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T)yield return (T)child;foreach(var nested in Descendants<T>(child))yield return nested;}
 }
 void Snapshot(string path){UpdateLayout();var image=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(this);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var f=File.Create(path))encoder.Save(f);}
}
}
