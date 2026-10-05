#include "effect.h"

CEffect::CEffect()
{
	Type = 0;
	ZeroMemory(&DiEffect, sizeof(DIEFFECT));
	ZeroMemory(&DiEnvelope, sizeof(DIENVELOPE));
	ZeroMemory(&DiConstantForce, sizeof(DICONSTANTFORCE));
	ZeroMemory(&DiPeriodic, sizeof(DIPERIODIC));
	ZeroMemory(&DiRampforce, sizeof(DIRAMPFORCE));
	ZeroMemory(&DiCondition, sizeof(DICONDITION));
	Handle = 0;
	Status = 0;
	PlayCount = 0;
	StartTime = 0;
	DirectionSign = 1;
	HasEnvelope = FALSE;
	DiEffect.dwGain = 10000;
	LastLogTick = 0;
	LastLoggedExtra = 0;
	LastReportedStatus = 0xFFFFFFFF;
}

LONG CEffect::EvalCondition(const DICONDITION& Cond, LONG Metric)
{
	// Standard DirectInput condition evaluation (Wine / PID-FF style).
	const LONG x = Metric - Cond.lOffset;
	// NFS Unbound (and some Logitech SDK titles) download Spring with
	// lDeadBand=10000. DI units are 0..10000, so that disables the entire
	// axis range and the spring never produces force. Treat a full-scale
	// deadband as "no deadband" when the game also set a coefficient.
	LONG dead = Cond.lDeadBand;
	if (dead >= 10000 &&
		(Cond.lPositiveCoefficient != 0 || Cond.lNegativeCoefficient != 0))
	{
		dead = 0;
	}

	LONG coeff = 0;
	LONG sat = 10000;
	LONG axis = 0;

	if (x > dead)
	{
		coeff = Cond.lPositiveCoefficient;
		sat = (LONG)Cond.dwPositiveSaturation;
		axis = x - dead;
	}
	else if (x < -dead)
	{
		coeff = Cond.lNegativeCoefficient;
		sat = (LONG)Cond.dwNegativeSaturation;
		axis = x + dead;
	}
	else
	{
		return 0;
	}

	if (sat <= 0) sat = 10000;

	LONGLONG force = -((LONGLONG)coeff * (LONGLONG)axis) / 10000;
	if (force > sat) force = sat;
	if (force < -sat) force = -sat;
	return (LONG)force;
}

VOID CEffect::CalcTorque(LONG* Torque, LONG AxisPos, LONG AxisVel)
{
	// Condition effects (arcade Spring etc.) stay alive while PLAYING even when the
	// game downloaded a finite duration; other effects honor duration x PlayCount.
	const bool isCondition =
		Type == SPRING || Type == DAMPER || Type == INERTIA || Type == FRICTION;
	const bool infinite =
		DiEffect.dwDuration == 0 ||
		DiEffect.dwDuration == INFINITE;

	if (Status != DIEGES_PLAYING)
		return;

	const ULONG BeginTime = StartTime + (DiEffect.dwStartDelay / 1000);
	const ULONG CurrentTime = GetTickCount();
	if ((LONG)(CurrentTime - BeginTime) < 0)
		return;
	const ULONG Elapsed = CurrentTime - BeginTime;

	// Duration 0 = infinite; periodic phase then runs off elapsed time.
	ULONG Duration = 0;
	ULONG CurrentPos = Elapsed;
	if (!infinite)
	{
		Duration = max(1UL, DiEffect.dwDuration / 1000);
		const bool loops = PlayCount == 0 || PlayCount == (DWORD)-1;
		if (!isCondition && !loops && (ULONGLONG)Elapsed >= (ULONGLONG)Duration * PlayCount)
			return;
		CurrentPos = Elapsed % Duration;
	}

	LONG NormalLevel = 0;
	CalcForce(Duration, CurrentPos, AxisPos, AxisVel, &NormalLevel);

	// Per-effect gain (DIEFFECT.dwGain, 0..10000) — game-authored, not an emulator tweak.
	LONG effectGain = (LONG)min(DiEffect.dwGain, (DWORD)10000);
	NormalLevel = (LONG)(((LONGLONG)NormalLevel * effectGain) / 10000);

	// Original mix: direction applies to all effect types, including Spring.
	*Torque += NormalLevel * DirectionSign;
}

LONG CEffect::ApplyEnvelope(LONG Magnitude, ULONG Duration, ULONG CurrentPos) const
{
	if (!HasEnvelope)
		return Magnitude;

	// DirectInput envelopes scale the absolute magnitude; the sign is preserved.
	const LONG sign = Magnitude < 0 ? -1 : 1;
	LONG level = Magnitude * sign;

	const ULONG AttackTime = DiEnvelope.dwAttackTime / 1000;
	const ULONG FadeTime = DiEnvelope.dwFadeTime / 1000;

	if (AttackTime > 0 && CurrentPos < AttackTime)
	{
		const LONG from = (LONG)min(DiEnvelope.dwAttackLevel, (DWORD)10000);
		level = from + (LONG)(((LONGLONG)(level - from) * CurrentPos) / AttackTime);
	}
	else if (Duration > 0 && FadeTime > 0 && CurrentPos + FadeTime > Duration)
	{
		const LONG to = (LONG)min(DiEnvelope.dwFadeLevel, (DWORD)10000);
		const ULONG intoFade = CurrentPos + FadeTime - Duration;
		level = level + (LONG)(((LONGLONG)(to - level) * intoFade) / FadeTime);
	}

	return level * sign;
}

VOID CEffect::CalcForce(ULONG Duration, ULONG CurrentPos, LONG AxisPos, LONG AxisVel, LONG* NormalLevel)
{
	LONG Magnitude = 0;
	LONG Period;
	LONG R;

	switch (Type)
	{
	case SPRING:
		Magnitude = EvalCondition(DiCondition, AxisPos);
		break;

	case DAMPER:
		Magnitude = EvalCondition(DiCondition, AxisVel);
		break;

	case INERTIA:
		// Approximate accel with velocity metric when accel is unavailable.
		Magnitude = EvalCondition(DiCondition, AxisVel);
		break;

	case FRICTION:
	{
		// Friction opposes motion: sign(velocity) * coefficient. The deadband keeps
		// sensor noise at rest from flipping the force back and forth.
		const LONG kFrictionDeadband = 100;
		LONG metric = 0;
		if (AxisVel > kFrictionDeadband) metric = 10000;
		else if (AxisVel < -kFrictionDeadband) metric = -10000;
		Magnitude = EvalCondition(DiCondition, metric);
		break;
	}

	case CONSTANT_FORCE:
		Magnitude = ApplyEnvelope(DiConstantForce.lMagnitude, Duration, CurrentPos);
		break;

	case RAMP_FORCE:
	{
		LONG begin = DiRampforce.lStart;
		LONG end = DiRampforce.lEnd;
		Magnitude = Duration > 0
			? begin + (LONG)(((LONGLONG)(end - begin) * CurrentPos) / Duration)
			: begin;
		Magnitude = ApplyEnvelope(Magnitude, Duration, CurrentPos);
		break;
	}

	case CUSTOM_FORCE:
		break;

	case SQUARE:
		Period = max(1L, (LONG)(DiPeriodic.dwPeriod / 1000));
		R = ((LONG)CurrentPos % Period) * 360 / Period;
		R = (R + (LONG)(DiPeriodic.dwPhase / 100)) % 360;
		Magnitude = ApplyEnvelope((LONG)DiPeriodic.dwMagnitude, Duration, CurrentPos);
		if (180 <= R) Magnitude = -Magnitude;
		Magnitude += DiPeriodic.lOffset;
		break;

	case SINE:
		Period = max(1L, (LONG)(DiPeriodic.dwPeriod / 1000));
		R = ((LONG)CurrentPos % Period) * 360 / Period;
		R = (R + (LONG)(DiPeriodic.dwPhase / 100)) % 360;
		Magnitude = ApplyEnvelope((LONG)DiPeriodic.dwMagnitude, Duration, CurrentPos);
		Magnitude = (LONG)(Magnitude * sin(R * 3.14159265 / 180.0));
		Magnitude += DiPeriodic.lOffset;
		break;

	case TRIANGLE:
	case SAWTOOTH_UP:
	case SAWTOOTH_DOWN:
		Period = max(1L, (LONG)(DiPeriodic.dwPeriod / 1000));
		R = ((LONG)CurrentPos % Period) * 360 / Period;
		R = (R + (LONG)(DiPeriodic.dwPhase / 100)) % 360;
		Magnitude = ApplyEnvelope((LONG)DiPeriodic.dwMagnitude, Duration, CurrentPos);
		if (Type == SAWTOOTH_UP)
			Magnitude = -Magnitude + (Magnitude * 2 * R / 360);
		else if (Type == SAWTOOTH_DOWN)
			Magnitude = Magnitude - (Magnitude * 2 * R / 360);
		else
		{
			if (R < 180) Magnitude = -Magnitude + (Magnitude * 2 * R / 180);
			else Magnitude = Magnitude - (Magnitude * 2 * (R - 180) / 180);
		}
		Magnitude += DiPeriodic.lOffset;
		break;

	default:
		break;
	}

	*NormalLevel = Magnitude;
}
