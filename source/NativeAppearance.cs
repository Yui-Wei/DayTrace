using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
namespace DayTrace {
 public static class NativeAppearance {
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out NativeRect bounds);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
  [StructLayout(LayoutKind.Sequential)] struct NativeRect {public int Left,Top,Right,Bottom;}
  public static int ColorValue(Brush brush){
   var solid=brush as SolidColorBrush;var gradient=brush as GradientBrush;
   Color color=solid!=null?solid.Color:gradient!=null?gradient.GradientStops.OrderBy(x=>x.Offset).First().Color:Colors.Black;
   return color.R|(color.G<<8)|(color.B<<16);
  }
  public static int LastCaptionResult,LastBorderResult,LastTextResult;
  public static void Apply(Window window,bool dark){
   var handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;
   try{
    Set(handle,20,dark?1:0);Set(handle,33,2);
    LastBorderResult=Set(handle,34,ColorValue((Brush)window.FindResource("Line")));
    LastCaptionResult=Set(handle,35,ColorValue((Brush)window.FindResource("Canvas")));
    LastTextResult=Set(handle,36,ColorValue((Brush)window.FindResource("Ink")));
   }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
  }
  static int Set(IntPtr handle,int attribute,int value){return DwmSetWindowAttribute(handle,attribute,ref value,4);}
  public static void FocusMenu(ContextMenu menu){var source=PresentationSource.FromVisual(menu) as HwndSource;if(source!=null)SetForegroundWindow(source.Handle);menu.Focus();}
  public static void Capture(Window window,string path){
   var handle=new WindowInteropHelper(window).Handle;NativeRect bounds;if(!GetWindowRect(handle,out bounds))return;
   using(var bitmap=new System.Drawing.Bitmap(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top))using(var graphics=System.Drawing.Graphics.FromImage(bitmap)){
    IntPtr dc=graphics.GetHdc();bool captured;try{captured=PrintWindow(handle,dc,2);}finally{graphics.ReleaseHdc(dc);}
    if(captured)bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
   }
  }
 }
}
