param([ValidateRange(10,120)][int]$Seconds = 45)
$ErrorActionPreference = 'Stop'
# 独立 STA 探针；不加载 Glint，不读取正文，不修改注册表，不接管键盘。
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class WeTypeTsfProbe
{
    // 全部 GUID、方法顺序和继承槽来自 Windows SDK 10.0.26100.0 um/msctf.h。
    [ComImport, Guid("aa80e801-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IThreadMgr {
        [PreserveSig] int Activate(out uint client);
        [PreserveSig] int Deactivate();
    }
    [ComImport, Guid("ea1ea135-19df-11d7-a6d2-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IElementMgr {
        [PreserveSig] int BeginUIElement(IElement element, [MarshalAs(UnmanagedType.Bool)] ref bool show, out uint id);
        [PreserveSig] int UpdateUIElement(uint id);
        [PreserveSig] int EndUIElement(uint id);
        [PreserveSig] int GetUIElement(uint id, out IElement element);
        [PreserveSig] int EnumUIElements(out IEnumElements elements);
    }
    [ComImport, Guid("ea1ea137-19df-11d7-a6d2-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IElement {
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string description);
        [PreserveSig] int GetGUID(out Guid guid);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
        [PreserveSig] int IsShown([MarshalAs(UnmanagedType.Bool)] out bool shown);
    }
    // 显式展开基接口的四个槽，避免 COM 继承导致 GetCount 等方法错位。
    [ComImport, Guid("ea1ea138-19df-11d7-a6d2-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICandidates {
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string description);
        [PreserveSig] int GetGUID(out Guid guid);
        [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
        [PreserveSig] int IsShown([MarshalAs(UnmanagedType.Bool)] out bool shown);
        [PreserveSig] int GetUpdatedFlags(out uint flags);
        [PreserveSig] int GetDocumentMgr(out IntPtr document);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetSelection(out uint selection);
        [PreserveSig] int GetString(uint index, [MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int GetPageIndex([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] uint[] indexes, uint size, out uint pages);
        [PreserveSig] int SetPageIndex([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex=1)] uint[] indexes, uint size);
        [PreserveSig] int GetCurrentPage(out uint page);
    }
    [ComImport, Guid("887aa91e-acba-4931-84da-3c5208cf543f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumElements {
        [PreserveSig] int Clone(out IEnumElements clone);
        [PreserveSig] int Next(uint count, out IElement element, out uint fetched);
        [PreserveSig] int Reset();
        [PreserveSig] int Skip(uint count);
    }
    [ComImport, Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISource {
        [PreserveSig] int AdviseSink(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] object sink, out uint cookie);
        [PreserveSig] int UnadviseSink(uint cookie);
    }
    // 事件接收器是本探针实现的真实 CCW，不伪造 vtable。
    [ComVisible(true), Guid("ea1ea136-19df-11d7-a6d2-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISink {
        [PreserveSig] int BeginUIElement(uint id, [MarshalAs(UnmanagedType.Bool)] ref bool show);
        [PreserveSig] int UpdateUIElement(uint id);
        [PreserveSig] int EndUIElement(uint id);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Sink : ISink {
        public IElementMgr Manager;
        public int Begins, Updates, Ends;
        public int BeginUIElement(uint id, ref bool show) { Begins++; show=true; Log("BeginUIElement"); Read(id); return 0; }
        public int UpdateUIElement(uint id) { Updates++; Log("UpdateUIElement"); Read(id); return 0; }
        public int EndUIElement(uint id) { Ends++; Log("EndUIElement"); return 0; }
        private void Read(uint id) {
            try { IElement element; int hr=Manager.GetUIElement(id,out element); Hr("GetUIElement",hr); if(hr>=0 && element!=null) Inspect(element); }
            catch(Exception error) { Hr("callback",error.HResult); }
        }
    }
    private static Stopwatch timer;
    private static void Log(string value) { Console.WriteLine("elapsedMs="+timer.ElapsedMilliseconds+" "+value); }
    private static void Hr(string step,int hr) { Log(step+" hr=0x"+hr.ToString("X8")); }
    private static object Query(object value, Guid iid, string step) {
        IntPtr unknown=Marshal.GetIUnknownForObject(value), result=IntPtr.Zero;
        try { int hr=Marshal.QueryInterface(unknown,in iid,out result); Hr(step,hr); return hr>=0 ? Marshal.GetObjectForIUnknown(result) : null; }
        finally { if(result!=IntPtr.Zero) Marshal.Release(result); Marshal.Release(unknown); }
    }
    private static void Inspect(IElement element) {
        Query(element,typeof(IElement).GUID,"QI.ITfUIElement");
        Query(element,new Guid("85fad185-58ce-497a-9460-355366b64b9a"),"QI.ITfCandidateListUIElementBehavior");
        var candidate=Query(element,typeof(ICandidates).GUID,"QI.ITfCandidateListUIElement") as ICandidates;
        if(candidate==null) return;
        uint count,selection,flags;
        int countHr=candidate.GetCount(out count), selectionHr=candidate.GetSelection(out selection);
        Hr("GetCount",countHr); Hr("GetSelection",selectionHr); Hr("GetUpdatedFlags",candidate.GetUpdatedFlags(out flags));
        Log("candidateCount="+count+" selection="+selection+" flags="+flags);
        if(countHr==0 && selectionHr==0 && selection<count) {
            string text; int hr=candidate.GetString(selection,out text); Hr("GetString",hr);
            // 只输出选中项的汉字数量和是否全为 CJK，绝不输出原文。
            if(hr==0 && text!=null) { int cjk=0; foreach(char c in text) if(c>=0x3400 && c<=0x9fff) cjk++; Log("cjkCount="+cjk+" allCjk="+(cjk==text.Length)); }
        }
    }
    public static void Run(int seconds) {
        var thread=new Thread(()=>RunSta(seconds)); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
    private static void RunSta(int seconds) {
        timer=Stopwatch.StartNew(); IThreadMgr threadMgr=null; ISource source=null; Sink sink=null; uint cookie=0; bool advised=false,activated=false;
        int foregroundSamples=0,visibleSamples=0,enumerated=0;
        try {
            // 这里只能创建调用线程的管理器；不把它冒充前台应用的管理器。
            Guid clsid=new Guid("529a9e6b-6587-4f23-ab9e-9c7d683e3c50"), iid=typeof(IThreadMgr).GUID;
            object manager; int hr=CoCreateInstance(ref clsid,IntPtr.Zero,1,ref iid,out manager); Hr("CoCreateInstance.ITfThreadMgr",hr); if(hr<0) return;
            threadMgr=(IThreadMgr)manager; uint client; hr=threadMgr.Activate(out client); Hr("Activate",hr); activated=hr>=0; if(!activated) return;
            // TF_GetThreadMgr 没有前台线程参数，只返回调用线程的管理器。
            IThreadMgr callingManager; Hr("TF_GetThreadMgr.callingThread",TF_GetThreadMgr(out callingManager));
            if(callingManager!=null) Marshal.ReleaseComObject(callingManager);
            var mgr=Query(manager,typeof(IElementMgr).GUID,"QI.ITfUIElementMgr") as IElementMgr;
            source=Query(manager,typeof(ISource).GUID,"QI.ITfSource") as ISource;
            if(mgr==null || source==null) return;
            sink=new Sink { Manager=mgr }; Guid sinkIid=typeof(ISink).GUID;
            hr=source.AdviseSink(ref sinkIid,sink,out cookie); Hr("AdviseSink.ITfUIElementSink",hr); advised=hr>=0; if(!advised) return;
            long nextPoll=0; bool? previousForeign=null,previousCandidate=null;
            while(timer.Elapsed.TotalSeconds<seconds) {
                MSG msg; while(PeekMessage(out msg,IntPtr.Zero,0,0,1)) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                if(timer.ElapsedMilliseconds>=nextPoll) {
                    nextPoll=timer.ElapsedMilliseconds+250;
                    uint pid; uint foregroundThread=GetWindowThreadProcessId(GetForegroundWindow(),out pid);
                    var info=new GUI { size=(uint)Marshal.SizeOf(typeof(GUI)) };
                    bool focus=foregroundThread!=0 && GetGUIThreadInfo(foregroundThread,ref info) && info.focus!=IntPtr.Zero;
                    bool foreign=focus && pid!=(uint)Process.GetCurrentProcess().Id;
                    if(foreign) foregroundSamples++;
                    bool visible=false;
                    EnumWindows((h,p)=> { var name=new System.Text.StringBuilder(128); GetClassName(h,name,name.Capacity); if(name.ToString()=="WeType.NativeCandidate.Decoration.v2" && IsWindowVisible(h)) { uint windowPid; GetWindowThreadProcessId(h,out windowPid); try { using(var proc=Process.GetProcessById((int)windowPid)) if(proc.ProcessName=="wetype_renderer") visible=true; } catch(ArgumentException) {} } return true; },IntPtr.Zero);
                    if(visible && foreign) visibleSamples++;
                    if(previousForeign!=foreign || previousCandidate!=visible) { Log("focusPresent="+focus+" foreignThread="+foreign+" weTypeCandidateVisible="+visible); previousForeign=foreign; previousCandidate=visible; }
                    IEnumElements list; hr=mgr.EnumUIElements(out list);
                    if(nextPoll<600) Hr("EnumUIElements",hr);
                    if(hr>=0 && list!=null) {
                        try { for(int i=0;i<32;i++) { IElement element; uint fetched; int next=list.Next(1,out element,out fetched); if(next!=0 || fetched==0) { if(nextPoll<600) Hr("IEnumTfUIElements.Next",next); break; } enumerated++; try { Inspect(element); } finally { Marshal.ReleaseComObject(element); } } }
                        finally { Marshal.ReleaseComObject(list); }
                    }
                }
                Thread.Sleep(10);
            }
        } catch(Exception error) { Hr("probe.exception",error.HResult); }
        finally {
            if(advised) Hr("UnadviseSink",source.UnadviseSink(cookie));
            if(activated) Hr("Deactivate",threadMgr.Deactivate());
            Log("summary Begin="+(sink==null?0:sink.Begins)+" Update="+(sink==null?0:sink.Updates)+" End="+(sink==null?0:sink.Ends)+" enumerated="+enumerated+" foreignFocusSamples="+foregroundSamples+" visibleCandidateSamples="+visibleSamples);
            GC.KeepAlive(sink);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left,top,right,bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GUI { public uint size,flags; public IntPtr active,focus,capture,menu,move,caret; public RECT caretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wparam; public IntPtr lparam; public uint time; public int x,y; public uint extra; }
    private delegate bool EnumProc(IntPtr hwnd,IntPtr parameter);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid,IntPtr outer,uint context,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out object result);
    [DllImport("msctf.dll")] private static extern int TF_GetThreadMgr(out IThreadMgr manager);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GUI info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder name,int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg,IntPtr hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
}
'@
[WeTypeTsfProbe]::Run($Seconds)
