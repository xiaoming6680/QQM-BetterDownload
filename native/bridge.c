// Thread-specific WH_GETMESSAGE bridge. Only file-operation imports in QQ's
// two audio modules are observed; keyboard/message contents are never recorded.
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#define UNICODE
#include <windows.h>
#include <commctrl.h>
#include <shlobj.h>
#include <wchar.h>
#include <stdio.h>
#include <string.h>
#include "gf_ui.h"

#define CAP 2048
#define QCAP 1024
#define SUBCLASS_ID 0x4244
#define MENU_ID 0x1bd0
static HINSTANCE instance;
static HWND main_window;
static UINT attach_message, detach_message;
static volatile LONG started, enabled;
static CRITICAL_SECTION gate;
static HANDLE wake;
static WCHAR *queue[QCAP], spool[CAP];
static unsigned head, tail, count;
static LONG sequence;
static BOOL (WINAPI *real_move)(LPCWSTR,LPCWSTR);
static BOOL (WINAPI *real_move_ex)(LPCWSTR,LPCWSTR,DWORD);
static BOOL (WINAPI *real_copy)(LPCWSTR,LPCWSTR,BOOL);
static BOOL (WINAPI *real_copy_ex)(LPCWSTR,LPCWSTR,LPPROGRESS_ROUTINE,LPVOID,LPBOOL,DWORD);
typedef struct { void *volatile *slot; void *original; void *hook; HMODULE module; } PATCH;
static PATCH patches[256];
static unsigned patch_count;
static LRESULT CALLBACK main_proc(HWND,UINT,WPARAM,LPARAM,UINT_PTR,DWORD_PTR);

static BOOL candidate(LPCWSTR path) {
    if(!path || !path[0])return FALSE;
    size_t length=wcsnlen(path,CAP);if(length>=CAP)return FALSE;
    const WCHAR *ext=wcsrchr(path,L'.');if(!ext)return FALSE;
    const WCHAR *types[]={L".mflac",L".mflac0",L".mflach",L".mgg",L".mgg0",L".mgg1",L".mggl",L".mmp4"};
    BOOL ok=FALSE;for(unsigned i=0;i<sizeof(types)/sizeof(types[0]);i++)if(!_wcsicmp(ext,types[i]))ok=TRUE;
    if(!ok)return FALSE;
    WCHAR lower[CAP];memcpy(lower,path,(length+1)*sizeof(WCHAR));CharLowerBuffW(lower,(DWORD)length);
    for(size_t i=0;i<length;i++)if(lower[i]==L'/')lower[i]=L'\\';
    return wcsstr(lower,L"\\vipsongsdownload\\") && !wcsstr(lower,L"\\unlock\\");
}
static void enqueue(LPCWSTR path) {
    if(!InterlockedCompareExchange(&enabled,0,0) || !candidate(path))return;
    size_t bytes=(wcslen(path)+1)*sizeof(WCHAR);WCHAR *copy=HeapAlloc(GetProcessHeap(),0,bytes);if(!copy)return;
    memcpy(copy,path,bytes);EnterCriticalSection(&gate);
    if(count<QCAP){queue[tail]=copy;tail=(tail+1)%QCAP;count++;copy=NULL;}
    LeaveCriticalSection(&gate);
    if(copy){HeapFree(GetProcessHeap(),0,copy);SetPropW(main_window,L"BetterDownload.QueueOverflow",(HANDLE)1);}else SetEvent(wake);
}
static BOOL WINAPI copy_file(LPCWSTR a,LPCWSTR b,BOOL fail){BOOL ok=real_copy(a,b,fail);DWORD e=GetLastError();if(ok)enqueue(b);SetLastError(e);return ok;}
static BOOL WINAPI copy_ex(LPCWSTR a,LPCWSTR b,LPPROGRESS_ROUTINE p,LPVOID d,LPBOOL c,DWORD f){BOOL ok=real_copy_ex(a,b,p,d,c,f);DWORD e=GetLastError();if(ok)enqueue(b);SetLastError(e);return ok;}
static BOOL WINAPI move_file(LPCWSTR a,LPCWSTR b){BOOL ok=real_move(a,b);DWORD e=GetLastError();if(ok)enqueue(b);SetLastError(e);return ok;}
static BOOL WINAPI move_ex(LPCWSTR a,LPCWSTR b,DWORD f){BOOL ok=real_move_ex(a,b,f);DWORD e=GetLastError();if(ok&&!(f&MOVEFILE_DELAY_UNTIL_REBOOT))enqueue(b);SetLastError(e);return ok;}

static BOOL range(DWORD size,DWORD at,SIZE_T bytes){return at<size && bytes<=size-at;}
static void patch(HMODULE module) {
    BYTE *base=(BYTE*)module;if(!base)return;
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER*)base;
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE || dos->e_lfanew<0 || dos->e_lfanew>0x100000)return;
    IMAGE_NT_HEADERS32 *nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);
    if(nt->Signature!=IMAGE_NT_SIGNATURE || nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386 || nt->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC)return;
    DWORD size=nt->OptionalHeader.SizeOfImage,at=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    DWORD imports=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].Size;
    if(!at || !range(size,at,imports))return;
    for(DWORD n=0;range(imports,n,sizeof(IMAGE_IMPORT_DESCRIPTOR));n+=sizeof(IMAGE_IMPORT_DESCRIPTOR)){
        IMAGE_IMPORT_DESCRIPTOR *d=(IMAGE_IMPORT_DESCRIPTOR*)(base+at+n);if(!d->Name)break;
        if(!d->OriginalFirstThunk || !d->FirstThunk)continue;
        for(DWORD i=0;i<size/sizeof(IMAGE_THUNK_DATA32);i++){
            DWORD step=i*sizeof(IMAGE_THUNK_DATA32);
            if(d->OriginalFirstThunk>size || d->FirstThunk>size || !range(size-d->OriginalFirstThunk,step,4) || !range(size-d->FirstThunk,step,4))break;
            IMAGE_THUNK_DATA32 *name=(IMAGE_THUNK_DATA32*)(base+d->OriginalFirstThunk+step),*slot=(IMAGE_THUNK_DATA32*)(base+d->FirstThunk+step);
            if(!name->u1.AddressOfData)break;if(IMAGE_SNAP_BY_ORDINAL32(name->u1.Ordinal))continue;
            DWORD nrva=name->u1.AddressOfData;
            if(!range(size,nrva,3))continue;
            char *text=(char*)(base+nrva+2);if(!memchr(text,0,size-nrva-2))continue;
            void *original=NULL,*replacement=NULL;
            if(!strcmp(text,"MoveFileW")){original=(void*)real_move;replacement=(void*)move_file;}
            else if(!strcmp(text,"MoveFileExW")){original=(void*)real_move_ex;replacement=(void*)move_ex;}
            else if(!strcmp(text,"CopyFileW")){original=(void*)real_copy;replacement=(void*)copy_file;}
            else if(!strcmp(text,"CopyFileExW")){original=(void*)real_copy_ex;replacement=(void*)copy_ex;}
            if(!replacement || (void*)(ULONG_PTR)slot->u1.Function!=original || patch_count>=256)continue;
            DWORD protect;if(!VirtualProtect(&slot->u1.Function,4,PAGE_READWRITE,&protect))continue;
            void *was=InterlockedCompareExchangePointer((void*volatile*)&slot->u1.Function,replacement,original);
            DWORD unused;VirtualProtect(&slot->u1.Function,4,protect,&unused);
            if(was==original)patches[patch_count++]=(PATCH){(void*volatile*)&slot->u1.Function,original,replacement,module};
        }
    }
}
static void unpatch(void) {
    for(unsigned i=0;i<patch_count;i++){
        PATCH *p=&patches[i];HMODULE retained=NULL;
        if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,(LPCWSTR)p->slot,&retained))continue;
        if(retained==p->module){DWORD old;if(VirtualProtect((void*)p->slot,4,PAGE_READWRITE,&old)){InterlockedCompareExchangePointer(p->slot,p->original,p->hook);DWORD unused;VirtualProtect((void*)p->slot,4,old,&unused);}}
        FreeLibrary(retained);
    }
    patch_count=0;
}
static BOOL write_event(LPCWSTR path) {
    WCHAR tmp[CAP],dest[CAP];FILETIME t;GetSystemTimeAsFileTime(&t);LONG id=InterlockedIncrement(&sequence);
    _snwprintf(tmp,CAP,L"%ls\\%08lx%08lx-%08lx.tmp",spool,(unsigned long)t.dwHighDateTime,(unsigned long)t.dwLowDateTime,(unsigned long)id);
    _snwprintf(dest,CAP,L"%ls\\%08lx%08lx-%08lx.evt",spool,(unsigned long)t.dwHighDateTime,(unsigned long)t.dwLowDateTime,(unsigned long)id);
    HANDLE file=CreateFileW(tmp,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);if(file==INVALID_HANDLE_VALUE)return FALSE;
    DWORD size=(DWORD)(wcslen(path)*sizeof(WCHAR)),written=0;BOOL ok=WriteFile(file,path,size,&written,NULL)&&written==size;
    if(ok)ok=FlushFileBuffers(file);CloseHandle(file);if(ok)ok=real_move(tmp,dest);if(!ok)DeleteFileW(tmp);return ok;
}
static DWORD WINAPI worker(LPVOID ignored) {
    (void)ignored;
    for(;;){
        if(InterlockedCompareExchange(&enabled,0,0)){
            const WCHAR *modules[]={L"QQMusic.dll",L"QQMusicCommon.dll"};
#ifdef BETTERDOWNLOAD_TEST
            patch(GetModuleHandleW(NULL));
#endif
            for(unsigned i=0;i<2;i++){HMODULE m=NULL;if(GetModuleHandleExW(0,modules[i],&m)){patch(m);FreeLibrary(m);}}
            SetPropW(main_window,L"BetterDownload.Imports",(HANDLE)(ULONG_PTR)patch_count);
        }else if(patch_count)unpatch();
        for(;;){
            EnterCriticalSection(&gate);WCHAR *item=count?queue[head]:NULL;LeaveCriticalSection(&gate);if(!item)break;
            if(!write_event(item))break; // retain the head for retry, never discard a disk-write failure
            EnterCriticalSection(&gate);head=(head+1)%QCAP;count--;LeaveCriticalSection(&gate);HeapFree(GetProcessHeap(),0,item);
        }
        WaitForSingleObject(wake,1000);
    }
}
static void open_settings(void) {
    HWND settings=FindWindowW(NULL,L"BetterDownload");DWORD pid=0;if(settings)GetWindowThreadProcessId(settings,&pid);if(pid)AllowSetForegroundWindow(pid);
    HANDLE evt=OpenEventW(EVENT_MODIFY_STATE,FALSE,L"Local\\QQM-BetterDownload.Settings");if(evt){SetEvent(evt);CloseHandle(evt);}
}
static void ui_attach(HWND w,BOOL show_icon) {
    (void)show_icon; // The worker initializes the fingerprint-gated GF adapter.
    main_window=w;SetWindowSubclass(w,main_proc,SUBCLASS_ID,0);
    HMENU menu=GetSystemMenu(w,FALSE);if(menu && GetMenuState(menu,MENU_ID,MF_BYCOMMAND)==(UINT)-1)AppendMenuW(menu,MF_STRING,MENU_ID,L"BetterDownload 设置");
    SetPropW(w,L"BetterDownload.Bridge",(HANDLE)1);InterlockedExchange(&enabled,1);SetEvent(wake);
}
static void detach(HWND w) {
    bd_ui_close(w);
    InterlockedExchange(&enabled,0);SetEvent(wake);
    HMENU menu=GetSystemMenu(w,FALSE);if(menu)DeleteMenu(menu,MENU_ID,MF_BYCOMMAND);
    RemovePropW(w,L"BetterDownload.Bridge");RemovePropW(w,L"BetterDownload.Imports");RemovePropW(w,L"BetterDownload.QueueOverflow");RemovePropW(w,L"BetterDownload.Entry");RemovePropW(w,L"BetterDownload.IconError");
    RemoveWindowSubclass(w,main_proc,SUBCLASS_ID);InvalidateRect(w,NULL,FALSE);
}
static LRESULT CALLBACK main_proc(HWND w,UINT m,WPARAM a,LPARAM b,UINT_PTR id,DWORD_PTR ref) {
    (void)id;(void)ref;
    if(m==detach_message){detach(w);return 0;}
    if(m==WM_COPYDATA && b && ((COPYDATASTRUCT*)b)->dwData==BD_UI_PACKET)return bd_ui_command(w,(HWND)a,(COPYDATASTRUCT*)b,instance);
    if(m==WM_TIMER && a==BD_UI_TIMER){bd_ui_tick(w);return 0;}
    // Wake the on-demand frame clock only while the pointer is actually moving;
    // it idles itself again once there is no hover or card to animate.
    if(m==WM_MOUSEMOVE||m==WM_NCMOUSEMOVE)bd_ui_wake(w);
    if(m==WM_NCHITTEST){POINT p={(short)LOWORD(b),(short)HIWORD(b)};if(bd_ui_entry_hit(w,p))return HTCLIENT;}
    if(m==WM_SETCURSOR){POINT p;if(GetCursorPos(&p)&&bd_ui_entry_hit(w,p)){SetCursor(LoadCursorW(NULL,IDC_HAND));return TRUE;}}
    if(m==WM_SYSCOMMAND && (a&0xfff0)==MENU_ID){open_settings();return 0;}
    if(m==WM_NCDESTROY)detach(w);
    LRESULT result=DefSubclassProc(w,m,a,b);
    return result;
}
__declspec(dllexport) LRESULT CALLBACK BetterDownloadHook(int code,WPARAM a,LPARAM b) {
    if(!attach_message){attach_message=RegisterWindowMessageW(L"BetterDownload.Attach.v1");detach_message=RegisterWindowMessageW(L"BetterDownload.Detach.v1");}
    if(code==HC_ACTION && a==PM_REMOVE){
        MSG *msg=(MSG*)b;
        if(bd_ui_message(msg)){msg->message=WM_NULL;msg->wParam=msg->lParam=0;}
        if(msg->message==attach_message){
            WCHAR path[CAP];GetModuleFileNameW(NULL,path,CAP);WCHAR *name=wcsrchr(path,L'\\');
            BOOL valid=name&&!_wcsicmp(name+1,L"QQMusic.exe");
#ifdef BETTERDOWNLOAD_TEST
            valid=valid||(name&&!_wcsicmp(name+1,L"BridgeHost.exe"));
#endif
            DWORD pid=0;GetWindowThreadProcessId(msg->hwnd,&pid);
            if(valid && pid==GetCurrentProcessId()){
                if(InterlockedCompareExchange(&started,1,0)==0){
                    HMODULE pin;GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,(LPCWSTR)BetterDownloadHook,&pin);
                    HMODULE k=GetModuleHandleW(L"kernel32.dll");real_move=(void*)GetProcAddress(k,"MoveFileW");real_move_ex=(void*)GetProcAddress(k,"MoveFileExW");real_copy=(void*)GetProcAddress(k,"CopyFileW");real_copy_ex=(void*)GetProcAddress(k,"CopyFileExW");
                    WCHAR local[CAP];SHGetFolderPathW(NULL,CSIDL_LOCAL_APPDATA,NULL,SHGFP_TYPE_CURRENT,local);
#ifdef BETTERDOWNLOAD_TEST
                    const WCHAR *product=L"QQM-BetterDownload-Test";
#else
                    const WCHAR *product=L"QQM-BetterDownload";
#endif
                    _snwprintf(spool,CAP,L"%ls\\%ls",local,product);CreateDirectoryW(spool,NULL);
                    _snwprintf(spool,CAP,L"%ls\\%ls\\events",local,product);CreateDirectoryW(spool,NULL);
                    _snwprintf(spool,CAP,L"%ls\\%ls\\events\\%lu",local,product,GetCurrentProcessId());CreateDirectoryW(spool,NULL);
                    InitializeCriticalSection(&gate);wake=CreateEventW(NULL,FALSE,FALSE,NULL);
                    HANDLE thread=CreateThread(NULL,0,worker,NULL,0,NULL);if(thread)CloseHandle(thread);
                }
                ui_attach(msg->hwnd,msg->lParam!=0);
            }
        }
    }
    return CallNextHookEx(NULL,code,a,b);
}
BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID reserved){(void)reserved;if(reason==DLL_PROCESS_ATTACH){instance=h;DisableThreadLibraryCalls(h);}return TRUE;}
