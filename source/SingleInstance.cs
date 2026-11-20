// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Yui-Wei

using System;
using System.Threading;
namespace DayTrace {
 public sealed class SingleInstance:IDisposable {
  readonly string name;
  readonly Mutex mutex;
  EventWaitHandle activation,acknowledged;
  RegisteredWaitHandle listener;
  bool owned,disposed;
  public SingleInstance(string instanceName){name=instanceName;mutex=new Mutex(false,name);}
  public bool TryAcquire(int milliseconds){
   if(owned)return true;
   try{owned=mutex.WaitOne(milliseconds);}catch(AbandonedMutexException){owned=true;}
   return owned;
  }
  public void Listen(Action activate){
   if(!owned)throw new InvalidOperationException("Only the active instance can listen");
   activation=new EventWaitHandle(false,EventResetMode.AutoReset,name+".Activate");
   acknowledged=new EventWaitHandle(false,EventResetMode.AutoReset,name+".Activated");
   listener=ThreadPool.RegisterWaitForSingleObject(activation,delegate(object state,bool timedOut){if(!disposed)try{activate();}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
  }
  public bool PeerUsesAcknowledgements {get;private set;}
  public void AcknowledgeActivation(){if(!disposed&&acknowledged!=null)acknowledged.Set();}
  public bool ActivateExisting(int timeout=1500){
   try{using(var handle=EventWaitHandle.OpenExisting(name+".Activate"))using(var response=EventWaitHandle.OpenExisting(name+".Activated")){PeerUsesAcknowledgements=true;while(response.WaitOne(0)){}return handle.Set()&&response.WaitOne(timeout);}}
   catch(WaitHandleCannotBeOpenedException){return false;}catch(UnauthorizedAccessException){return false;}
  }
  public void Dispose(){
   if(disposed)return;disposed=true;
   if(listener!=null)listener.Unregister(null);
   if(activation!=null)activation.Dispose();
   if(acknowledged!=null)acknowledged.Dispose();
   if(owned){mutex.ReleaseMutex();owned=false;}mutex.Dispose();
  }
 }
 public static class SingleInstanceChecks {
  public static void Run(Action<string,bool> check){
   string name="Local\\DayTrace.Verify."+Guid.NewGuid().ToString("N");
   using(var owner=new SingleInstance(name))using(var signal=new ManualResetEvent(false)){
    check("single instance acquires first ownership",owner.TryAcquire(0));owner.Listen(()=>{signal.Set();owner.AcknowledgeActivation();});
    bool blocked=false,activated=false;var second=new Thread(()=>{using(var duplicate=new SingleInstance(name)){blocked=!duplicate.TryAcquire(0);activated=duplicate.ActivateExisting();}});second.Start();second.Join();
    check("active owner prevents duplicate instance",blocked);check("second launch can activate existing instance",activated&&signal.WaitOne(2000));
   }
   using(var unresponsive=new SingleInstance(name+".Unresponsive")){
    unresponsive.TryAcquire(0);unresponsive.Listen(()=>{});bool rejected=false;
    var request=new Thread(()=>{using(var duplicate=new SingleInstance(name+".Unresponsive"))rejected=!duplicate.ActivateExisting(150);});request.Start();request.Join();
    check("unresponsive activation is not silently reported as success",rejected);
   }
   using(var reopened=new SingleInstance(name))check("clean exit releases instance ownership",reopened.TryAcquire(0));
   string abandonedName=name+".Abandoned";Mutex abandoned=null;
   var thread=new Thread(()=>{abandoned=new Mutex(false,abandonedName);abandoned.WaitOne();});thread.Start();thread.Join();
   try{using(var recovered=new SingleInstance(abandonedName))check("abandoned instance lock does not falsely block startup",recovered.TryAcquire(0));}finally{if(abandoned!=null)abandoned.Dispose();}
  }
}
}
