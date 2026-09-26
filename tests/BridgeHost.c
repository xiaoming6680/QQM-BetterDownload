#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#include <windows.h>
#include <shellapi.h>
#include <wchar.h>
static WCHAR root[2048];
static HWND main_window, startup_window;
static HINSTANCE instance;
static LRESULT CALLBACK procedure(HWND w,UINT m,WPARAM a,LPARAM b){
    if(m==WM_APP+7){ShowWindow(w,SW_SHOWNOACTIVATE);return 0;}
    if(m==WM_APP+8){SetWindowPos(w,NULL,0,0,(int)a,(int)b,SWP_NOMOVE|SWP_NOACTIVATE|SWP_NOZORDER);return 0;}
    if(m==WM_APP+9){ShowWindow(w,SW_HIDE);return 0;}
    if(m==WM_APP+11){ShowWindow(w,SW_SHOWMINNOACTIVE);return 0;}
    if(m==WM_APP+10){
        main_window=CreateWindowW(L"BetterDownload.TestHost",L"BetterDownload Test Host",WS_OVERLAPPEDWINDOW,-20000,-20000,1100,740,NULL,NULL,instance,NULL);
        ShowWindow(main_window,SW_SHOWNOACTIVATE);DestroyWindow(w);return (LRESULT)main_window;
    }
    if(m>=WM_APP+1&&m<=WM_APP+6){
        WCHAR src[2048],dst[2048];
        _snwprintf(src,2048,L"%ls\\source.dat",root);
        _snwprintf(dst,2048,L"%ls\\VipSongsDownload\\test%u.mflac",root,m-WM_APP);
        BOOL ok=FALSE;
        if(m==WM_APP+1)ok=CopyFileW(src,dst,TRUE);
        if(m==WM_APP+2)ok=CopyFileExW(src,dst,NULL,NULL,NULL,COPY_FILE_FAIL_IF_EXISTS);
        if(m==WM_APP+3)ok=MoveFileW(src,dst);
        if(m==WM_APP+4)ok=MoveFileExW(src,dst,0);
        if(m==WM_APP+5){wcscat(src,L".missing");ok=CopyFileW(src,dst,TRUE);}
        if(m==WM_APP+6){_snwprintf(dst,2048,L"%ls\\plain.txt",root);ok=CopyFileW(src,dst,TRUE);}
        SetPropW(w,L"Test.Error",(HANDLE)(ULONG_PTR)GetLastError());
        SetPropW(w,L"Test.Completed",(HANDLE)(ULONG_PTR)(ok?m:0xffff));return 0;
    }
    if(m==WM_CLOSE){DestroyWindow(w);return 0;}
    if(m==WM_DESTROY && w==main_window){if(startup_window)DestroyWindow(startup_window);PostQuitMessage(0);return 0;}
    return DefWindowProcW(w,m,a,b);
}
int WINAPI wWinMain(HINSTANCE h,HINSTANCE previous,PWSTR cmd,int show){
    (void)previous;(void)cmd;(void)show;
    instance=h;
    int argc;WCHAR **argv=CommandLineToArgvW(GetCommandLineW(),&argc);if(argc!=2&&argc!=3)return 1;
    BOOL lifecycle=argc==3&&!wcscmp(argv[2],L"--lifecycle");
    wcsncpy(root,argv[1],2047);LocalFree(argv);
    WNDCLASSW c={0};c.hInstance=h;c.lpfnWndProc=procedure;c.lpszClassName=L"BetterDownload.TestHost";c.hbrBackground=(HBRUSH)(COLOR_WINDOW+1);RegisterClassW(&c);
    main_window=CreateWindowW(c.lpszClassName,L"BetterDownload Test Host",WS_OVERLAPPEDWINDOW,-20000,-20000,1100,740,NULL,NULL,h,NULL);
    if(lifecycle){startup_window=CreateWindowW(L"STATIC",L"BetterDownload Startup Helper",WS_OVERLAPPEDWINDOW,-20000,-20000,220,120,NULL,NULL,h,NULL);ShowWindow(startup_window,SW_SHOWNOACTIVATE);}
    MSG msg;while(GetMessageW(&msg,NULL,0,0)>0){TranslateMessage(&msg);DispatchMessageW(&msg);}return 0;
}
