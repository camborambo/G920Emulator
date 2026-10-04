#include "extension.h"
#include <stdio.h>

static const char* EffectName(DWORD type)
{
	switch (type)
	{
	case 0x00: return "ConstantForce";
	case 0x01: return "RampForce";
	case 0x02: return "Square";
	case 0x03: return "Sine";
	case 0x04: return "Triangle";
	case 0x05: return "SawtoothUp";
	case 0x06: return "SawtoothDown";
	case 0x07: return "Spring";
	case 0x08: return "Damper";
	case 0x09: return "Inertia";
	case 0x0A: return "Friction";
	case 0x0B: return "CustomForce";
	default: return "Unknown";
	}
}

void G920FfbLogEffect(DWORD effectType, DWORD flags, DWORD handle, LONG extra)
{
	wchar_t path[MAX_PATH];
	if (GetTempPathW(MAX_PATH, path) == 0)
		return;
	wcscat_s(path, L"g920ffb-effects.log");

	HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if (file == INVALID_HANDLE_VALUE)
		return;

	SYSTEMTIME st;
	GetLocalTime(&st);
	char line[256];
	int n = sprintf_s(line,
		"%02u:%02u:%02u.%03u type=%lu(%s) handle=%lu flags=0x%08lX extra=%ld\r\n",
		st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
		(unsigned long)effectType, EffectName(effectType),
		(unsigned long)handle, (unsigned long)flags, (long)extra);
	if (n > 0)
	{
		DWORD written = 0;
		WriteFile(file, line, (DWORD)n, &written, NULL);
	}
	CloseHandle(file);
}
