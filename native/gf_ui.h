#ifndef BETTERDOWNLOAD_GF_UI_H
#define BETTERDOWNLOAD_GF_UI_H
#include <windows.h>
/* All methods run on the client's UI thread. The worker enables this adapter
   only for tested GF.dll/Common.dll code; a rebuilt, Tencent-signed wrapper
   is used through its decorated exports plus the runtime object checks. */
#define BD_UI_PACKET 0x42444746
#define BD_UI_TIMER 0x42444746
LRESULT bd_ui_command(HWND window, HWND sender, const COPYDATASTRUCT *packet, HMODULE bridge);
BOOL bd_ui_message(const MSG *message);
BOOL bd_ui_entry_hit(HWND window, POINT screen);
void bd_ui_tick(HWND window);
void bd_ui_wake(HWND window);
void bd_ui_close(HWND window);
#endif
