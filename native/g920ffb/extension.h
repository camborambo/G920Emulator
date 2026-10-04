// G920 Emulator — DirectInput OEM force-feedback driver (IDirectInputEffectDriver)
#pragma once
#include <windows.h>

// {A920FFB0-E7DB-4329-8C13-A966D84A289F}
DEFINE_GUID(ClassID_G920FFB,
	0xA920FFB0, 0xE7DB, 0x4329, 0x8C, 0x13, 0xA9, 0x66, 0xD8, 0x4A, 0x28, 0x9F);

extern CRITICAL_SECTION CriticalSection;

// Shared memory: game effects (via this DLL) <-> G920Emulator bridge.
#define G920FFB_SHM_NAME L"Local\\G920Emulator.FfbTorque"
#define G920FFB_MAGIC 0x46463947u /* 'G9FF' */
#define G920FFB_VERSION 3u

// Bit in TypesSeen / TypesPlaying: (1u << effectTypeId)
#pragma pack(push, 1)
struct G920FfbSharedState
{
	UINT32 Magic;
	UINT32 Version;
	volatile UINT32 Sequence;
	float Torque;       // DLL -> app, -1 .. +1 (DI force / 10000)
	UINT32 Playing;
	UINT64 TickMs;
	float Steering;     // app -> DLL, -1 .. +1 (physical rim for conditions)
	float SteeringVel;  // app -> DLL
	// Probe / diagnostics (v3)
	UINT32 TypesSeen;       // bitmask of effect types ever downloaded
	UINT32 TypesPlaying;    // bitmask of types currently contributing
	UINT32 DownloadCount;
	UINT32 LastEffectType;
	UINT32 LastFlags;
};
#pragma pack(pop)

void G920FfbLogEffect(DWORD effectType, DWORD flags, DWORD handle, LONG extra);
