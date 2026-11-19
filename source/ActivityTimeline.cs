using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DayTrace {
public static class ActivityColors {
 public static readonly string[] Categories={"工作","学习","娱乐","沟通","其他","未分类","离开"};
 public static Brush Brush(string category) {
  string color;
  switch(category) {
   case "工作":color="#159B89";break;
   case "学习":color="#4B85DE";break;
   case "娱乐":color="#9C71D7";break;
   case "沟通":color="#E2A344";break;
   case "其他":color="#5998AC";break;
   case "离开":color="#636B78";break;
   default:color="#A7B0BC";break;
  }
  var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));brush.Freeze();return brush;
 }
}

// Bars, ticks and pointer queries all share the same visible interval and plot rectangle.
public sealed class ActivityTimeline:FrameworkElement {
 readonly DateTime day,start,end;
 readonly List<Activity> rows;
 DateTime viewStart,viewEnd;
 int? selectedHour,selectedMinute;
 double? pointer;
 public event Action<string> HoverChanged;
 public event Action<int?> ZoomChanged;
 public string HoverDetails {get;private set;}
 public ActivityTimeline(DateTime selectedDay,IEnumerable<Activity> activities) {
  day=selectedDay.Date;start=day.ToUniversalTime();end=day.AddDays(1).ToUniversalTime();viewStart=start;viewEnd=end;
  rows=activities.OrderBy(x=>x.Start).ToList();Height=116;MinWidth=100;Cursor=Cursors.Hand;
  SetResourceReference(TextElement.ForegroundProperty,"Ink");
  MouseMove+=delegate(object sender,MouseEventArgs args){var p=args.GetPosition(this);HoverAt(p.X,p.Y);};
  MouseLeave+=delegate{ClearHover();};
  MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs args){var p=args.GetPosition(this);if(ToggleAt(p.X,p.Y)){args.Handled=true;HoverAt(p.X,p.Y);}};
  Unloaded+=delegate{ClearHover();};
  SizeChanged+=delegate{InvalidateVisual();};
 }
 public double PlotLeft {get{return 27;}}
 public double PlotWidth {get{return Math.Max(1,ActualWidth-54);}}
 public DateTime ViewStart {get{return viewStart;}}
 public DateTime ViewEnd {get{return viewEnd;}}
 public int? SelectedHour {get{return selectedHour;}set{
  if(value.HasValue&&(value.Value<0||value.Value>23))throw new ArgumentOutOfRangeException("value");
  selectedHour=value;selectedMinute=null;UpdateView();
 }}
 public int? SelectedMinute {get{return selectedMinute;}set{
  if(value.HasValue&&(value.Value<0||value.Value>1438||value.Value%2!=0))throw new ArgumentOutOfRangeException("value");
  selectedMinute=value;if(value.HasValue)selectedHour=value.Value/60;UpdateView();
 }}
 void UpdateView(){
  viewStart=selectedMinute.HasValue?day.AddMinutes(selectedMinute.Value).ToUniversalTime():selectedHour.HasValue?day.AddHours(selectedHour.Value).ToUniversalTime():start;
  viewEnd=selectedMinute.HasValue?viewStart.AddMinutes(2):selectedHour.HasValue?day.AddHours(selectedHour.Value+1).ToUniversalTime():end;
  if(viewEnd<=viewStart)viewEnd=viewStart.AddHours(1);
  ClearHover();InvalidateVisual();
 }
 public double Position(DateTime utc){return PlotLeft+Math.Max(0,Math.Min(1,(utc-viewStart).TotalSeconds/(viewEnd-viewStart).TotalSeconds))*PlotWidth;}
 public DateTime TimeAt(double x){double ratio=Math.Max(0,Math.Min(1,(x-PlotLeft)/PlotWidth));return viewStart.AddSeconds(ratio*(viewEnd-viewStart).TotalSeconds);}
 internal bool ToggleAt(double x,double y){
  if(x<PlotLeft||x>PlotLeft+PlotWidth||y<4||y>78)return false;
  if(selectedMinute.HasValue)SelectedHour=null;
  else if(selectedHour.HasValue){DateTime time=TimeAt(x);if(time>=viewEnd)time=viewEnd.AddTicks(-1);var local=time.ToLocalTime();SelectedMinute=local.Hour*60+local.Minute/2*2;}
  else {DateTime time=TimeAt(x);if(time>=end)time=end.AddTicks(-1);SelectedHour=time.ToLocalTime().Hour;}
  if(ZoomChanged!=null)ZoomChanged(selectedHour);
  return true;
 }
 protected override void OnRender(DrawingContext dc) {
  base.OnRender(dc);dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,ActualWidth,ActualHeight));
  var ink=TryFindResource("Muted") as Brush??Brushes.Gray;
  var track=TryFindResource("Line") as Brush??Brushes.Gray;
  dc.DrawRoundedRectangle(track,null,new Rect(PlotLeft,12,PlotWidth,32),4,4);
  foreach(var row in rows) {
   if(row.End<=viewStart||row.Start>=viewEnd||row.End<=row.Start)continue;
   double left=Position(row.Start),right=Position(row.End);
   dc.DrawRectangle(ActivityColors.Brush(row.App=="离开"?"离开":row.Category),null,new Rect(left,12,Math.Max(0,right-left),32));
  }
  for(int tick=0;tick<=6;tick++) {
   DateTime time=selectedHour.HasValue?viewStart.AddSeconds((viewEnd-viewStart).TotalSeconds*tick/6):day.AddHours(tick*4).ToUniversalTime();
   double x=Position(time);
   dc.DrawLine(new Pen(ink,1),new Point(x,47),new Point(x,53));
   var text=Label(time>=end?"24:00":time.ToLocalTime().ToString(selectedMinute.HasValue?"HH:mm:ss":"HH:mm"),14,ink);
   dc.DrawText(text,new Point(x-text.Width/2,57));
  }
  string instruction=selectedHour.HasValue?viewStart.ToLocalTime().ToString("HH:mm")+"—"+(viewEnd>=end?"24:00":viewEnd.ToLocalTime().ToString("HH:mm"))+(selectedMinute.HasValue?" · 再点一次恢复全天":" · 点击展开对应两分钟"):"全天 · 点击时间轴展开对应小时";
  dc.DrawText(Label(instruction,14,ink),new Point(PlotLeft,88));
  if(pointer.HasValue&&pointer>=PlotLeft&&pointer<=PlotLeft+PlotWidth)
   dc.DrawLine(new Pen(TryFindResource("Ink") as Brush??Brushes.White,1),new Point(pointer.Value,6),new Point(pointer.Value,49));
 }
 FormattedText Label(string value,double size,Brush brush) {
  return new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
 }
 internal void HoverAt(double x,double y) {
  if(x<PlotLeft||x>PlotLeft+PlotWidth||y<4||y>78){ClearHover();return;}
  pointer=x;HoverDetails=Describe(x);
  if(HoverChanged!=null)HoverChanged(HoverDetails);
  InvalidateVisual();
 }
 void ClearHover(){pointer=null;HoverDetails=null;if(HoverChanged!=null)HoverChanged(null);InvalidateVisual();}
 public string Describe(double x) {
  DateTime time=TimeAt(x);
  string label=time>=end?"24:00:00":time.ToLocalTime().ToString("HH:mm:ss");
  var task=rows.LastOrDefault(row=>row.Start<=time&&row.End>time);
  if(task==null)return "时间："+label+"\n此时间没有活动记录";
  string finish=task.End.ToLocalTime().Date>day?"24:00:00":task.End.ToLocalTime().ToString("HH:mm:ss");
  return "时间："+label+" · 分类："+(task.App=="离开"?"离开":task.Category)+" · 时长："+DesktopWindow.Duration(task.Seconds)+"\n程序："+task.App+"\n任务："+task.Title+"\n时段："+task.Start.ToLocalTime().ToString("HH:mm:ss")+"—"+finish;
 }
}
}
