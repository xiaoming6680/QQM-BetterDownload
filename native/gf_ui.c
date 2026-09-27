#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#include "gf_ui.h"
#include <oleauto.h>
#include <stdio.h>
#include <wchar.h>
#include <math.h>

/* Tencent GF's x86 COM interfaces, verified against the tested GF.dll and
   Common.dll code. No code offsets, XML resources, or client code are bundled. */
static const GUID frame_id={0xa5dff81a,0xb003,0x4967,{0xa2,0x86,0x87,0xeb,0x38,0x04,0x1c,0x7c}};
static const GUID texture_id={0x40c0a0b4,0x33e3,0x4091,{0xae,0x36,0x91,0x8f,0x9c,0x09,0xe3,0x50}};
typedef HRESULT (__stdcall *Query)(void*,const GUID*,void**);
typedef ULONG (__stdcall *Release)(void*);
typedef HRESULT (__stdcall *Find)(void*,BSTR,void**);
typedef HRESULT (__stdcall *Init)(void*,void*,void*);
typedef HRESULT (__stdcall *Make)(void*,const GUID*,const GUID*,void**);
typedef HRESULT (__stdcall *PutString)(void*,BSTR);
typedef HRESULT (__stdcall *PutInt)(void*,int);
typedef HRESULT (__stdcall *PutSize)(void*,SIZE);
typedef HRESULT (__stdcall *PutRect)(void*,RECT);
typedef HRESULT (__stdcall *PutObject)(void*,void*);
typedef HRESULT (__stdcall *NoArgs)(void*);
typedef HRESULT (__stdcall *GetRect)(void*,RECT*);
typedef HRESULT (__cdecl *GetGfWindow)(HWND,void**);
typedef int (__cdecl *GetCore)(void**);
typedef HRESULT (__cdecl *Attribute)(void*,const WCHAR*,const WCHAR*);
typedef void (__cdecl *PointToWindow)(void*,POINT*);
typedef void (__thiscall *BrowserLife)(void*);
typedef int (__thiscall *BrowserCreate)(void*,void*,int,int,int,int,int);
typedef void (__thiscall *BrowserNavigate)(void*,WCHAR*,int,int);
static HWND host,owner;
static DWORD owner_pid;
static void *core,*root,*entry,*panel,*card,*card_back,*browser;
static Attribute attribute;
static PointToWindow point_to_window;
static BrowserLife browser_ctor,browser_destroy,browser_dtor;
static BrowserCreate browser_create;
static BrowserNavigate browser_navigate;
static WCHAR assets[2048],url[2048];
static BOOL settings_visible,card_visible,leaving,hovered,trusted_wrapper;
static BOOL entry_down,suppress_entry_up,timer_on;
static int entry_state;
static RECT folder_hit;
static int card_width,card_height,stay;
static ULONGLONG deadline,animation,busy_until;
static UINT callback_message;
static void **vt(void *p){return *(void***)p;}
static void drop(void *p){if(p)((Release)vt(p)[2])(p);}
static void destroy_frame(void **p){if(*p){((NoArgs)vt(*p)[0x24/4])(*p);drop(*p);*p=NULL;}}
static void string(void *p,int slot,const WCHAR *text){BSTR b=SysAllocString(text);if(b){((PutString)vt(p)[slot/4])(p,b);SysFreeString(b);}}
static void number(void *p,int slot,int n){((PutInt)vt(p)[slot/4])(p,n);}
static void *find(void *p,const WCHAR *name){void *out=NULL;BSTR b=SysAllocString(name);if(b){((Find)vt(p)[0x58/4])(p,b,&out);SysFreeString(b);}return out;}
static void size(void *p,int w,int h){SIZE value={w,h};((PutSize)vt(p)[0x1ec/4])(p,value);}
static void margin(void *p,int right,int bottom){RECT r={0,0,right,bottom};((PutRect)vt(p)[0x20c/4])(p,r);}
static void *new_frame(void *parent,const WCHAR *name){
    void *p=NULL;
    if(FAILED(((Make)vt(core)[0x1c/4])(core,&frame_id,&frame_id,&p))||!p)return NULL;
    if(FAILED(((Init)vt(p)[0x1c/4])(p,parent,NULL))){drop(p);return NULL;}
    string(p,0xa8,name);return p;
}
static BOOL picture(void *frame,const WCHAR *file){
    WCHAR full[4096];if(_snwprintf(full,4096,L"%ls\\%ls",assets,file)<0)return FALSE;
    void *texture=NULL;HRESULT hr=((Make)vt(core)[0x1c/4])(core,&texture_id,&texture_id,&texture);
    if(FAILED(hr)||!texture)return FALSE;
    BSTR name=SysAllocString(full);if(!name){drop(texture);return FALSE;}
    hr=((PutString)vt(texture)[0xec/4])(texture,name);SysFreeString(name);
    if(SUCCEEDED(hr))hr=((PutObject)vt(frame)[0x188/4])(frame,texture);
    drop(texture);return SUCCEEDED(hr);
}
static BOOL bounds(void *frame,RECT *r){
    RECT local;if(!frame||FAILED(((GetRect)vt(frame)[0x118/4])(frame,&local)))return FALSE;
    /* GF's helper adds the parent origin; WindowRect is parent-relative. */
    POINT p={local.left,local.top};point_to_window(frame,&p);
    *r=(RECT){p.x,p.y,p.x+local.right-local.left,p.y+local.bottom-local.top};return TRUE;
}
static UINT dpi(void){typedef UINT(WINAPI *GetDpi)(HWND);GetDpi get=(GetDpi)(void*)GetProcAddress(GetModuleHandleW(L"user32.dll"),"GetDpiForWindow");UINT value=get?get(host):96;return value?value:96;}
static POINT logical(POINT p){UINT d=dpi();p.x=MulDiv(p.x,96,(int)d);p.y=MulDiv(p.y,96,(int)d);return p;}
/* The glyph's box inside entry.svg's 30x30 frame, stroke included. Hover, the
   hand cursor and clicks react only on the icon, like QQ's own top-bar icons. */
static const RECT entry_icon={7,5,26,24};
static BOOL on_entry(POINT p){RECT r;if(!entry||!bounds(entry,&r))return FALSE;RECT icon={r.left+entry_icon.left,r.top+entry_icon.top,r.left+entry_icon.right,r.top+entry_icon.bottom};return PtInRect(&icon,p);}
BOOL bd_ui_entry_hit(HWND window,POINT p){if(window!=host||!entry)return FALSE;ScreenToClient(host,&p);return on_entry(logical(p));}
/* Drive the 16ms frame clock only while there is something to animate: a
   visible or sliding card, or an entry hover to track. When nothing moves the
   timer is stopped, so an idle QQ Music does no per-frame work on its UI thread. */
static void pump(void){if(host&&!timer_on&&SetTimer(host,BD_UI_TIMER,16,NULL))timer_on=TRUE;}
void bd_ui_wake(HWND window){if(window==host)pump();}
static void update_entry(void){
    POINT p;BOOL over=FALSE;
    if(GetCursorPos(&p)){HWND under=WindowFromPoint(p);over=(under==host||IsChild(host,under))&&bd_ui_entry_hit(host,p);}
    if(!(GetKeyState(VK_LBUTTON)&0x8000))entry_down=FALSE;
    int next=over?(entry_down?2:1):0;
    if(next!=entry_state&&picture(entry,next==2?L"entry-pressed.svg":next==1?L"entry-hover.svg":L"entry.svg"))entry_state=next;
}
static int sender_status(HWND sender,HMODULE bridge){
    DWORD pid=0;GetWindowThreadProcessId(sender,&pid);if(!pid)return -20;
    WCHAR expected[2048],actual[2048];DWORD count=2048;
    if(!GetModuleFileNameW(bridge,expected,2048))return -21;
    WCHAR *name=wcsrchr(expected,L'\\');if(!name||name-expected>2000)return -22;wcscpy(name+1,L"BetterDownload.exe");
    HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!p)return -23;
    BOOL ok=QueryFullProcessImageNameW(p,0,actual,&count);CloseHandle(p);if(!ok)return -24;
    /* Hook loading can retain an 8.3 path while the sender reports a long path.
       Compare the actual files; never weaken this to an executable name check. */
    HANDLE expected_file=CreateFileW(expected,0,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,0,NULL);
    if(expected_file==INVALID_HANDLE_VALUE)return -26;
    HANDLE actual_file=CreateFileW(actual,0,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,0,NULL);
    if(actual_file==INVALID_HANDLE_VALUE){CloseHandle(expected_file);return -27;}
    BY_HANDLE_FILE_INFORMATION a,b;
    ok=GetFileInformationByHandle(expected_file,&a)&&GetFileInformationByHandle(actual_file,&b)&&
       a.dwVolumeSerialNumber==b.dwVolumeSerialNumber&&a.nFileIndexHigh==b.nFileIndexHigh&&a.nFileIndexLow==b.nFileIndexLow;
    CloseHandle(expected_file);CloseHandle(actual_file);return ok?1:-28;
}
static BOOL readable(const void *p,SIZE_T bytes){
    MEMORY_BASIC_INFORMATION m;
    if(!p||VirtualQuery(p,&m,sizeof(m))!=sizeof(m)||m.State!=MEM_COMMIT||!(m.Protect&0xEE)||(m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return FALSE;
    return (ULONG_PTR)p+bytes<=(ULONG_PTR)m.BaseAddress+m.RegionSize;
}
static BOOL code_in(HMODULE module,const void *p){
    MEMORY_BASIC_INFORMATION m;HMODULE found=NULL;
    if(!module||!p||VirtualQuery(p,&m,sizeof(m))!=sizeof(m)||m.State!=MEM_COMMIT||!(m.Protect&0xF0))return FALSE;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,(LPCWSTR)p,&found)&&found==module;
}
/* A rebuilt wrapper keeps its decorated exports, which encode the signatures,
   but not necessarily its private layout. Use its browser element only when it
   is a live object whose methods we call are code in GF.dll or the wrapper. */
static BOOL gf_object(void *element){
    HMODULE gf=GetModuleHandleW(L"GF.dll"),wrapper=GetModuleHandleW(L"QQMusic_GFWrapper.dll");
    if(!readable(element,sizeof(void*)))return FALSE;
    void **table=*(void***)element;if(!readable(table,0x228))return FALSE;
    const int slots[]={0x00,0x170,0x214,0x224};
    for(unsigned i=0;i<sizeof(slots)/sizeof(slots[0]);i++){void *f=table[slots[i]/4];if(!code_in(gf,f)&&!code_in(wrapper,f))return FALSE;}
    return TRUE;
}
static void release_browser(void){
    if(!browser)return;
    browser_destroy(browser);browser_dtor(browser);HeapFree(GetProcessHeap(),0,browser);browser=NULL;
}
/* 4 = the page cannot be shown inside QQ; the worker opens its own window. */
static void settings_unavailable(void){if(owner)PostMessageW(owner,callback_message,4,0);}
static void show_settings(BOOL show){
    if(!panel)return;
    if(show&&!browser){
        /* The tested wrapper object is 24 bytes; leave room for a larger build. */
        browser=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,4096);if(!browser){settings_unavailable();return;}browser_ctor(browser);
        /* OSR=1 is essential: QQ's DirectComposition window cannot display a
           foreign GDI/browser child. Let GF composite its own browser surface. */
        if(!browser_create(browser,panel,0,1,0,0,0)){release_browser();settings_unavailable();return;}
        void *native=((void**)browser)[1];RECT padding={0};
        if(!trusted_wrapper&&!gf_object(native)){release_browser();settings_unavailable();return;}
        ((PutRect)vt(native)[0x214/4])(native,padding);number(native,0x224,1);number(native,0x170,1);
        browser_navigate(browser,url,0,0);
    }
    BOOL changed=show!=settings_visible;
    settings_visible=show;number(panel,0x15c,!show);
    if(show)SetPropW(host,L"BetterDownload.NativeSettings",(HANDLE)1);else RemovePropW(host,L"BetterDownload.NativeSettings");
    /* 2 = opened, 3 = closed. The page blanks itself while hidden so a quick
       reopen fades in fresh instead of flashing the previous frame. */
    if(changed)PostMessageW(owner,callback_message,show?2:3,0);
}
void bd_ui_close(HWND window){
    if(host&&window!=host)return;
    if(host){KillTimer(host,BD_UI_TIMER);RemovePropW(host,L"BetterDownload.NativeUi");RemovePropW(host,L"BetterDownload.NativeOwner");RemovePropW(host,L"BetterDownload.NativeSettings");RemovePropW(host,L"BetterDownload.NativeCard");}
    release_browser();
    destroy_frame(&card);destroy_frame(&card_back);destroy_frame(&panel);destroy_frame(&entry);drop(root);drop(core);root=core=NULL;
    settings_visible=card_visible=leaving=hovered=timer_on=trusted_wrapper=FALSE;host=owner=NULL;owner_pid=0;assets[0]=url[0]=0;busy_until=0;
    entry_down=suppress_entry_up=FALSE;entry_state=0;
}
static int initialize(HWND window,HWND sender,const WCHAR *text){
    /* url \n asset folder \n 1 if the wrapper is a tested build, else 0 */
    const WCHAR *end=wcschr(text,L'\n'),*flag=end?wcschr(end+1,L'\n'):NULL;
    if(!end||!flag||end-text>=2048||flag-(end+1)>=2048||(flag[1]!=L'0'&&flag[1]!=L'1')||flag[2])return -3;
    if(wcsncmp(text,L"http://127.0.0.1:",17)||end-text<9||wcsncmp(end-9,L"/settings",9))return -3;
    if((end+1)[1]!=L':'||(end+1)[2]!=L'\\')return -3;
    int failure=-4;
    bd_ui_close(host);host=window;owner=sender;GetWindowThreadProcessId(owner,&owner_pid);
    wcsncpy(url,text,end-text);url[end-text]=0;wcsncpy(assets,end+1,flag-(end+1));assets[flag-(end+1)]=0;trusted_wrapper=flag[1]==L'1';
    HMODULE gf=GetModuleHandleW(L"GF.dll"),wrapper=GetModuleHandleW(L"QQMusic_GFWrapper.dll"),common=GetModuleHandleW(L"Common.dll");
    if(!gf||!wrapper||!common)goto fail;
    GetGfWindow get=(GetGfWindow)(void*)GetProcAddress(gf,"?GetWindowByHWnd@GFSpyFuncHelper@@YAJPAUHWND__@@PAPAUIGFPopupWin@@@Z");
    GetCore get_core=(GetCore)(void*)GetProcAddress(common,"?GetPlatformCore@Core@Util@@YAHPAPAUITXCore@@@Z");
    attribute=(Attribute)(void*)GetProcAddress(gf,"?SetFrameAttribute@GFSpyFuncHelper@@YAJPAUIGFFrame@@PB_W1@Z");
    point_to_window=(PointToWindow)(void*)GetProcAddress(gf,"?FramePoint2WindowPoint@GF@Util@@YAXPAUIGFFrame@@AAUtagPOINT@@@Z");
    browser_ctor=(BrowserLife)(void*)GetProcAddress(wrapper,"??0CMMGFIEBrowser2@@QAE@XZ");
    browser_destroy=(BrowserLife)(void*)GetProcAddress(wrapper,"?Destroy@CMMGFIEBrowser2@@QAEXXZ");
    browser_dtor=(BrowserLife)(void*)GetProcAddress(wrapper,"??1CMMGFIEBrowser2@@QAE@XZ");
    browser_create=(BrowserCreate)(void*)GetProcAddress(wrapper,"?CreateBrowser@CMMGFIEBrowser2@@QAEHPAUIGFElement@@HHHHH@Z");
    browser_navigate=(BrowserNavigate)(void*)GetProcAddress(wrapper,"?Navigate@CMMGFIEBrowser2@@QAEXPA_WHW4BackOrForwardOpration@@@Z");
    failure=-5;
    if(!get||!get_core||!attribute||!point_to_window||!browser_ctor||!browser_destroy||!browser_dtor||!browser_create||!browser_navigate)goto fail;
    failure=-6;
    void *popup=NULL;if(FAILED(get(window,&popup))||!popup)goto fail;
    ((Query)vt(popup)[0])(popup,&frame_id,&root);drop(popup);get_core(&core);if(!root||!core)goto fail;
    failure=-7;
    void *nav=find(root,L"NavigationBar"),*content=find(root,L"RightFrame_Frame");
    if(nav&&content){entry=new_frame(nav,L"BetterDownload.Entry");panel=new_frame(content,L"BetterDownload.Settings");card=new_frame(content,L"BetterDownload.Card");card_back=new_frame(content,L"BetterDownload.CardBack");}
    if(nav&&content)failure=-8;
    drop(nav);drop(content);if(!entry||!panel||!card||!card_back)goto fail;
    failure=-9;
    size(entry,30,30);string(entry,0x21c,L"RIGHTCENTER");margin(entry,197,0);if(!picture(entry,L"entry.svg"))goto fail;
    attribute(panel,L"zOrder",L"-100");number(panel,0x224,1);number(panel,0x15c,1);
    attribute(card,L"zOrder",L"-200");string(card,0x21c,L"BOTTOMRIGHT");number(card,0x15c,1);
    attribute(card_back,L"zOrder",L"-200");string(card_back,0x21c,L"BOTTOMRIGHT");number(card_back,0x15c,1);
    callback_message=RegisterWindowMessageW(L"BetterDownload.NativeAction.v1");
    /* Start idle: the frame clock runs on demand, not for the whole session. */
    SetPropW(host,L"BetterDownload.NativeOwner",owner);SetPropW(host,L"BetterDownload.NativeUi",(HANDLE)1);return TRUE;
fail:bd_ui_close(window);return failure;
}
LRESULT bd_ui_command(HWND window,HWND sender,const COPYDATASTRUCT *packet,HMODULE bridge){
    if(!packet||packet->dwData!=BD_UI_PACKET||packet->cbData<4||packet->cbData>16384||(packet->cbData%2)||!packet->lpData)return 0;
    const WCHAR *text=(const WCHAR*)packet->lpData;size_t count=packet->cbData/2;
    if(text[count-1]||wcsnlen(text,count)!=count-1||text[1]!=L'\n')return -1;
    int sender_result=sender_status(sender,bridge);if(sender_result!=1)return sender_result;
    int command=text[0]-L'0';text+=2;
    if(command==0)return initialize(window,sender,text);
    DWORD pid=0;GetWindowThreadProcessId(sender,&pid);if(window!=host||sender!=owner||pid!=owner_pid)return 0;
    if(command==1){show_settings(!settings_visible);return settings_visible?2:1;}
    if(command==2){show_settings(FALSE);return 1;}
    if(command==4){if(card_visible){leaving=TRUE;animation=GetTickCount64();pump();}return 1;}
    if(command==5){bd_ui_close(window);return 1;}
    if(command==6){show_settings(TRUE);return settings_visible?1:0;}
    /* 7 = the verified worker opens a window for the user's click on our page. */
    if(command==7)return AllowSetForegroundWindow(owner_pid)?1:0;
    if(command==3){
        int w,h,x,y,fw,fh,duration,flags;unsigned seq;WCHAR extra;
        if(swscanf(text,L"%d,%d,%d,%d,%d,%d,%d,%d\ncard-%u.png%lc",&w,&h,&x,&y,&fw,&fh,&duration,&flags,&seq,&extra)!=9)return 0;
        if(w<100||w>500||h<30||h>500||x<0||y<0||fw<0||fh<0||x+fw>w||y+fh>h||(duration!=2000&&duration!=4000&&duration!=6000)||flags<0||flags>7)return 0;
        /* Prepare the next size and texture while hidden. Reusing the visible
           frame lets GF briefly stretch the old texture during a style swap. */
        WCHAR file[64];swprintf(file,64,L"card-%u.png",seq);size(card_back,w,h);if(!picture(card_back,file))return 0;
        void *previous=card;card=card_back;card_back=previous;number(card_back,0x15c,1);
        card_width=w;card_height=h;folder_hit=(RECT){x,y,x+fw,y+fh};stay=duration;
        /* Flag 1 shows the card; flag 2 marks a conversion still running and
           flag 4 a round whose next song is on its way. The countdown waits for
           the final frame, or resumes once the worker has been silent for a
           minute (ten seconds between songs). Neither reopens a card the user
           dismissed. */
        ULONGLONG now=GetTickCount64();busy_until=(flags&2)?now+60000:(flags&4)?now+10000:0;
        if(flags&1){if(!card_visible||leaving)animation=now;leaving=FALSE;card_visible=TRUE;deadline=now+stay;number(card,0x15c,0);SetPropW(host,L"BetterDownload.NativeCard",(HANDLE)1);}
        if(card_visible){pump();bd_ui_tick(host);number(card,0x15c,!card_visible);}return 1;
    }
    return 0;
}
void bd_ui_tick(HWND window){
    if(window!=host)return;
    DWORD pid=0;GetWindowThreadProcessId(owner,&pid);if(!IsWindow(owner)||pid!=owner_pid){bd_ui_close(window);return;}
    update_entry();
    // Stop the clock once nothing needs animating; a lingering entry hover keeps
    // it alive so the highlight and its release still repaint.
    if(!card_visible){if(entry_state==0){KillTimer(host,BD_UI_TIMER);timer_on=FALSE;}return;}
    ULONGLONG now=GetTickCount64();RECT rect;POINT p;BOOL over=FALSE;
    if(GetCursorPos(&p)){HWND under=WindowFromPoint(p);if(under==host||IsChild(host,under)){ScreenToClient(host,&p);p=logical(p);over=bounds(card,&rect)&&PtInRect(&rect,p);}}
    if(over)deadline=now+stay;else if(hovered)deadline=now+stay;hovered=over;
    if(now<busy_until)deadline=now+stay;
    if(!leaving&&now>=deadline){leaving=TRUE;animation=now;}
    double t=(double)(now-animation)/320.0;if(t>1)t=1;
    double eased=1-pow(1-t,3);int offset=(int)((leaving?eased:1-eased)*(leaving?card_width+24:24));
    margin(card,2-offset,2);WCHAR alpha[16];swprintf(alpha,16,L"%d",(int)(255*(leaving?1-eased:eased)));attribute(card,L"alpha",alpha);
    if(leaving&&t>=1){number(card,0x15c,1);card_visible=leaving=FALSE;RemovePropW(host,L"BetterDownload.NativeCard");}
}
/* QQ's left column: the list, and below it the bottom-left options (main menu
   with 设置, skin, sidebar toggle). The buttons are named too in case a build
   hosts them outside the options panel. */
static BOOL in_sidebar(POINT p){
    static const WCHAR *const names[]={L"GroupList",L"LeftBottomOpt",L"Button_MainMenu",L"Button_Face",L"Button_OpenLeft"};
    for(unsigned i=0;i<sizeof(names)/sizeof(names[0]);i++){void *frame=find(root,names[i]);RECT r;BOOL hit=frame&&bounds(frame,&r)&&PtInRect(&r,p);drop(frame);if(hit)return TRUE;}
    return FALSE;
}
BOOL bd_ui_message(const MSG *message){
    if(!host||!root||(message->hwnd!=host&&!IsChild(host,message->hwnd)))return FALSE;
    if(message->message==WM_KEYDOWN&&message->wParam==VK_ESCAPE&&settings_visible){show_settings(FALSE);return TRUE;}
    UINT m=message->message;BOOL nonclient=m==WM_NCLBUTTONDOWN||m==WM_NCLBUTTONUP||m==WM_NCLBUTTONDBLCLK;
    if(m!=WM_LBUTTONDOWN&&m!=WM_LBUTTONUP&&m!=WM_LBUTTONDBLCLK&&m!=WM_RBUTTONUP&&!nonclient)return FALSE;
    POINT p={(short)LOWORD(message->lParam),(short)HIWORD(message->lParam)};
    if(nonclient)ScreenToClient(host,&p);else MapWindowPoints(message->hwnd,host,&p,1);p=logical(p);RECT r;
    if(on_entry(p)){
        if(m==WM_LBUTTONDOWN||m==WM_NCLBUTTONDOWN){entry_down=TRUE;suppress_entry_up=FALSE;}
        else if(m==WM_LBUTTONDBLCLK||m==WM_NCLBUTTONDBLCLK){entry_down=TRUE;suppress_entry_up=TRUE;}
        else if(m==WM_LBUTTONUP||m==WM_NCLBUTTONUP){entry_down=FALSE;if(!suppress_entry_up)show_settings(!settings_visible);suppress_entry_up=FALSE;}
        pump();update_entry();return m!=WM_RBUTTONUP;
    }
    /* Pressing QQ's own navigation hides the page on the way down, before a
       menu there (bottom-left main menu, skin) can take the mouse. */
    if(settings_visible&&(m==WM_LBUTTONDOWN||m==WM_LBUTTONUP)&&in_sidebar(p))show_settings(FALSE);
    if(m!=WM_LBUTTONUP&&m!=WM_RBUTTONUP)return FALSE;
    if(card_visible&&bounds(card,&r)&&PtInRect(&r,p)){
        if(m==WM_RBUTTONUP){leaving=TRUE;animation=GetTickCount64();return TRUE;}
        p.x-=r.left;p.y-=r.top;if(PtInRect(&folder_hit,p))PostMessageW(owner,callback_message,1,0);return TRUE;
    }
    return FALSE;
}
