#include "extension.h"
#include <stdio.h>
#include <stdarg.h>

static const DWORD kMaxLogBytes = 4 * 1024 * 1024;
// Cap DownloadEffect spam — Forza can hit ~600 lines/s; open+write+close per line
// on the game's FFB thread stalls input/FFB (seen as "game stopped seeing the G920").
static const LONG kMaxEffectLinesPerSecond = 40;

// Created by the emulator while a Debug session is active (DiagnosticsDebugSession).
#define G920FFB_DEBUG_EVENT L"Local\\G920Emulator.FfbDebug"

static CRITICAL_SECTION g_logCs;
static bool g_logCsInit = false;
static HANDLE g_logFile = INVALID_HANDLE_VALUE;
static DWORD g_effectWindowTick = 0;
static LONG g_effectLinesInWindow = 0;

static void EnsureLogCs()
{
	if (!g_logCsInit)
	{
		InitializeCriticalSection(&g_logCs);
		g_logCsInit = true;
	}
}

static bool LoggingEnabled()
{
	HANDLE h = OpenEventW(SYNCHRONIZE, FALSE, G920FFB_DEBUG_EVENT);
	if (!h)
		return false;
	CloseHandle(h);
	return true;
}

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

static bool LogPath(wchar_t* path, size_t cch)
{
	if (GetTempPathW((DWORD)cch, path) == 0)
		return false;
	return wcscat_s(path, cch, L"emuffb-effects.log") == 0;
}

static void CloseLogFile_NoLock()
{
	if (g_logFile != INVALID_HANDLE_VALUE)
	{
		CloseHandle(g_logFile);
		g_logFile = INVALID_HANDLE_VALUE;
	}
}

static void RotateIfNeeded_NoLock(const wchar_t* path)
{
	WIN32_FILE_ATTRIBUTE_DATA info;
	if (!GetFileAttributesExW(path, GetFileExInfoStandard, &info))
		return;
	if (info.nFileSizeHigh == 0 && info.nFileSizeLow <= kMaxLogBytes)
		return;

	CloseLogFile_NoLock();
	wchar_t old[MAX_PATH];
	wcscpy_s(old, path);
	wcscat_s(old, L".old");
	MoveFileExW(path, old, MOVEFILE_REPLACE_EXISTING);
}

static bool AllowEffectLog_NoLock()
{
	const DWORD now = GetTickCount();
	if (now - g_effectWindowTick >= 1000)
	{
		g_effectWindowTick = now;
		g_effectLinesInWindow = 0;
	}
	if (g_effectLinesInWindow >= kMaxEffectLinesPerSecond)
		return false;
	g_effectLinesInWindow++;
	return true;
}

static void AppendLine(const char* body)
{
	if (!LoggingEnabled())
	{
		EnsureLogCs();
		EnterCriticalSection(&g_logCs);
		CloseLogFile_NoLock();
		LeaveCriticalSection(&g_logCs);
		return;
	}

	EnsureLogCs();
	EnterCriticalSection(&g_logCs);

	wchar_t path[MAX_PATH];
	if (!LogPath(path, MAX_PATH))
	{
		LeaveCriticalSection(&g_logCs);
		return;
	}

	RotateIfNeeded_NoLock(path);

	if (g_logFile == INVALID_HANDLE_VALUE)
	{
		g_logFile = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
			OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
		if (g_logFile == INVALID_HANDLE_VALUE)
		{
			LeaveCriticalSection(&g_logCs);
			return;
		}
	}

	SYSTEMTIME st;
	GetLocalTime(&st);
	char line[400];
	int n = sprintf_s(line, "%02u:%02u:%02u.%03u %s\r\n",
		st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, body);
	if (n > 0)
	{
		DWORD written = 0;
		if (!WriteFile(g_logFile, line, (DWORD)n, &written, NULL))
			CloseLogFile_NoLock();
	}

	LeaveCriticalSection(&g_logCs);
}

void G920FfbLogSession()
{
	if (!LoggingEnabled())
		return;

	EnsureLogCs();
	EnterCriticalSection(&g_logCs);
	wchar_t path[MAX_PATH];
	if (LogPath(path, MAX_PATH))
		RotateIfNeeded_NoLock(path);
	LeaveCriticalSection(&g_logCs);

	wchar_t exe[MAX_PATH] = L"?";
	GetModuleFileNameW(NULL, exe, MAX_PATH);
	const wchar_t* name = wcsrchr(exe, L'\\');
	name = name ? name + 1 : exe;

	char body[300];
	sprintf_s(body, "SESSION emuffb loaded pid=%lu exe=%ls",
		(unsigned long)GetCurrentProcessId(), name);
	AppendLine(body);
}

void G920FfbLogCall(const char* format, ...)
{
	char body[300];
	va_list args;
	va_start(args, format);
	vsprintf_s(body, format, args);
	va_end(args);
	AppendLine(body);
}

void G920FfbLogEffect(DWORD effectType, DWORD flags, DWORD handle, LONG extra)
{
	if (!LoggingEnabled())
		return;

	EnsureLogCs();
	EnterCriticalSection(&g_logCs);
	const bool allow = AllowEffectLog_NoLock();
	LeaveCriticalSection(&g_logCs);
	if (!allow)
		return;

	char body[200];
	sprintf_s(body, "type=%lu(%s) handle=%lu flags=0x%08lX extra=%ld",
		(unsigned long)effectType, EffectName(effectType),
		(unsigned long)handle, (unsigned long)flags, (long)extra);
	AppendLine(body);
}

void G920FfbLogSpringDetail(DWORD handle, DWORD flags, DWORD condCount, DWORD pick,
	LONG offset, LONG posCoeff, LONG negCoeff, DWORD posSat, DWORD negSat, LONG deadBand, LONG dirSign)
{
	if (!LoggingEnabled())
		return;

	EnsureLogCs();
	EnterCriticalSection(&g_logCs);
	const bool allow = AllowEffectLog_NoLock();
	LeaveCriticalSection(&g_logCs);
	if (!allow)
		return;

	char body[320];
	sprintf_s(body,
		"SPRING_DETAIL handle=%lu flags=0x%08lX conds=%lu pick=%lu "
		"off=%ld pos=%ld neg=%ld sat+=%lu sat-=%lu dead=%ld dir=%ld",
		(unsigned long)handle, (unsigned long)flags,
		(unsigned long)condCount, (unsigned long)pick,
		(long)offset, (long)posCoeff, (long)negCoeff,
		(unsigned long)posSat, (unsigned long)negSat,
		(long)deadBand, (long)dirSign);
	AppendLine(body);
}

void G920FfbLogMix(LONG axisPos, LONG axisVel, LONG cf, LONG periodic, LONG spring,
	LONG damper, LONG other, LONG total, UINT32 typesPlaying)
{
	char body[260];
	sprintf_s(body,
		"MIX axis=%ld vel=%ld cf=%ld periodic=%ld spring=%ld damper=%ld other=%ld total=%ld playing=0x%04lX",
		(long)axisPos, (long)axisVel, (long)cf, (long)periodic, (long)spring,
		(long)damper, (long)other, (long)total, (unsigned long)typesPlaying);
	AppendLine(body);
}
