// G920 Emulator — DirectInput OEM force-feedback driver (IDirectInputEffectDriver)
#pragma once
#include <windows.h>

// {A920FFB0-E7DB-4329-8C13-A966D84A289F}
DEFINE_GUID(ClassID_G920FFB,
	0xA920FFB0, 0xE7DB, 0x4329, 0x8C, 0x13, 0xA9, 0x66, 0xD8, 0x4A, 0x28, 0x9F);

extern CRITICAL_SECTION CriticalSection;

// COM inproc lifetime — see DllCanUnloadNow / LockServer in extension.cpp + com.cpp.
extern LONG g_cObjects;
extern LONG g_cLocks;
void G920FfbAddObject(void);
void G920FfbReleaseObject(void);

// Shared memory: virtual-G920 OEM effects <-> G920Emulator bridge.
// Contract: anything downloaded against our OEM CLSID must reach the base.
// Game process owns Torque.*; Steam/overlay may only fill Aux* (rumble layer)
// so they cannot zero-out the game channel.
// v6: new map name + AuxTorque fields.
#define G920FFB_SHM_NAME L"Local\\G920Emulator.FfbTorque.v6"
#define G920FFB_MAGIC 0x46463947u /* 'G9FF' */
#define G920FFB_VERSION 6u
#define G920FFB_TYPE_GAIN_COUNT 16

// Bit in TypesSeen / TypesPlaying: (1u << effectTypeId)
#pragma pack(push, 1)
struct G920FfbSharedState
{
	UINT32 Magic;
	UINT32 Version;
	volatile UINT32 Sequence;
	float Torque;       // game OEM mix -> app, -1 .. +1
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
	// Per DI effect-type gains (v4), written by the emulator. 0..10000 (10000 = 100%).
	// Index matches DIEFT type id (Constant=0 … Custom=11). Unused slots ignored.
	UINT16 TypeGain[G920FFB_TYPE_GAIN_COUNT];
	// v6: non-game host contribution (Steam Input, etc.) — never replaces Torque.
	float AuxTorque;
	UINT32 AuxPlaying;
	UINT32 AuxTypesPlaying;
	UINT64 AuxTickMs;
	// Emulator mix options (app-written; defaults = Raw / pass-through).
	// MixFlags bit0 = InvertConstantForce.
	UINT32 MixFlags;
	UINT16 DamperVelScale;      // 10000 = 1.0, 20000 = 2.0
	UINT16 DamperDeadbandScale; // 10000 = 1.0, ~3333 = 1/3
};
#pragma pack(pop)

#define G920FFB_MIX_INVERT_CONSTANT 0x1u

void G920FfbLogSession();
void G920FfbLogEffect(DWORD effectType, DWORD flags, DWORD handle, LONG extra);
void G920FfbLogSpringDetail(DWORD handle, DWORD flags, DWORD condCount, DWORD pick,
	LONG offset, LONG posCoeff, LONG negCoeff, DWORD posSat, DWORD negSat, LONG deadBand, LONG dirSign);
void G920FfbLogCall(const char* format, ...);
void G920FfbLogMix(LONG axisPos, LONG axisVel, LONG cf, LONG periodic, LONG spring,
	LONG damper, LONG other, LONG total, UINT32 typesPlaying);
