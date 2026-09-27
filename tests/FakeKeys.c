/* Unsigned stand-ins for QQ Music's key interface (export ordinal 12, stdcall,
   32-character key and store name). Built only for tests.exe. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>

void __stdcall GenerateKey(char *key, char *name) {
#if defined(FAKE_CRASH)
    (void)key; (void)name;
    *(volatile int *)(ULONG_PTR)16 = 1;
#elif defined(FAKE_HANG)
    (void)key; (void)name;
    Sleep(INFINITE);
#elif defined(FAKE_INVALID)
    strcpy(key, "not a key");
    strcpy(name, "..\\escape.cah");
#else
    strcpy(key, "0123456789abcdef0123456789ABCDEF");
    strcpy(name, "FakeStore.cah");
#endif
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) { (void)instance; (void)reason; (void)reserved; return TRUE; }
